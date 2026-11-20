"""Durable quota recovery. No model is trusted as available before a real probe."""
from pathlib import Path
import contextlib,copy,fcntl,json,os,re,subprocess,time,urllib.request,urllib.error,uuid
from atlas_runner import RoleResult,RunnerError,ProcessSupervisor,parse_opencode_jsonl,classify_role_failure,write_json
from multica_adapter import MulticaAdapter

class ProviderHealth:
    def __init__(self,path,providers,clock=time.time):
        self.path=Path(path);self.providers=providers;self.clock=clock
        self.path.parent.mkdir(parents=True,exist_ok=True)
    @contextlib.contextmanager
    def transaction(self):
        with self.path.with_suffix('.lock').open('a+') as lock:
            fcntl.flock(lock,fcntl.LOCK_EX)
            state=json.loads(self.path.read_text()) if self.path.exists() else {'version':1,'providers':{}}
            state.setdefault('retirements',[])
            for name,config in self.providers.items():
                state['providers'].setdefault(name,{'verified':bool(config.get('verified')),'status':'available' if config.get('verified') else 'unverified','nextProbeAt':0,'model':config['model']})
                if state['providers'][name].get('model')!=config['model']:
                    state['providers'][name]={'verified':False,'status':'unverified','nextProbeAt':0,'model':config['model']}
            yield state
            write_json(self.path,state);self.path.chmod(0o600)
    def read(self):
        with self.transaction() as state:return copy.deepcopy(state)
    def available(self):
        state=self.read()
        return [name for name in self.providers if state['providers'][name].get('verified') and state['providers'][name]['status']=='available']
    def quota(self,name,retry_seconds=1800,retirement=None):
        with self.transaction() as state:
            state['providers'][name].update(status='quota_wait',verified=False,nextProbeAt=int(self.clock()+max(60,retry_seconds)),lastFailureAt=int(self.clock()))
            if retirement is not None and retirement not in state['retirements']:state['retirements'].append(retirement)
    def probe_result(self,name,verified,category,retry_seconds=1800):
        with self.transaction() as state:
            state['providers'][name].update(verified=verified,status='available' if verified else category,nextProbeAt=0 if verified else int(self.clock()+max(60,retry_seconds)),lastProbeAt=int(self.clock()))

class ProviderRouter:
    def __init__(self,primary_binding,supervisor,config,state_dir,clock=time.time,adapters=None):
        self.binding=primary_binding;self.supervisor=supervisor;self.config=config;self.clock=clock
        self.health=ProviderHealth(Path(state_dir)/'provider-health.json',config['providers'],clock)
        self.adapters=adapters or {name:MulticaAdapter(json.loads(Path(p['bindingPath']).read_text()),supervisor) for name,p in config['providers'].items()}
        self.command=self.adapters['kimi'].command
    def preflight(self):
        result=self.adapters['kimi'].preflight()
        result['providerRecovery']={'available':self.health.available(),'providers':self.health.read()['providers']}
        return result
    def sync_status(self,status):return self.adapters['kimi'].sync_status(status)
    def settle_pending(self):
        for item in self.health.read()['retirements']:
            if not self.adapters[item['provider']].settle_quota_run(RoleResult(False,category='blocked_quota',log_paths=tuple(item['logs']))):return False
            with self.health.transaction() as state:
                state['retirements']=[r for r in state['retirements'] if r!=item]
        return True
    def run(self,role,prompt,cwd,log_dir,timeout_seconds):
        if self.supervisor.stop_event.is_set():return RoleResult(False,category='stopped')
        if not self.settle_pending():return RoleResult(False,category='provider_wait')
        deadline=time.monotonic()+timeout_seconds
        last=None
        for name in self.health.available():
            if self.supervisor.stop_event.is_set():return RoleResult(False,category='stopped')
            if time.monotonic()>=deadline:break
            last=self.adapters[name].run(role,prompt,cwd,Path(log_dir)/name,max(1,int(deadline-time.monotonic())))
            if last.ok:return last
            if last.category!='blocked_quota':return last
            native=hasattr(self.adapters[name],'settle_quota_run')
            self.health.quota(name,self.config.get('retrySeconds',1800),retirement={'provider':name,'logs':list(last.log_paths)} if native else None)
            if native:
                if not self.settle_pending():return RoleResult(False,category='provider_wait',log_paths=last.log_paths)
            write_json(Path(log_dir)/('quota-'+name+'.json'),{'provider':name,'model':self.config['providers'][name]['model'],'category':'blocked_quota','role':role,'cwd':str(cwd),'logs':list(last.log_paths),'at':int(self.clock())})
            # Do not start the standby while a failed native run still owns its brief.
            file=Path(cwd)/'AGENTS.md'
            for _ in range(30):
                if not file.exists() or '<!-- BEGIN MULTICA-RUNTIME (auto-managed; do not edit) -->' not in file.read_text():break
                if self.supervisor.stop_event.wait(1):return RoleResult(False,category='provider_wait')
            else:return RoleResult(False,category='provider_wait')
        return RoleResult(False,category='provider_wait',log_paths=last.log_paths if last else ())

def normalize_legacy_quota_waits(runner,health):
    """Upgrade a terminal quota checkpoint, never a source/test/review failure."""
    now=runner.clock()
    for row in runner.ledger.rows():
        if row['failure_category']!='blocked_quota' or row['lease_owner'] is not None:continue
        phase=row['phase']
        if phase=='blocked':
            phase='leader'
            for stage,next_phase in [('leader','developer'),('developer','review'),('review','qa'),('qa','integration')]:
                accepted=runner.ledger.latest_accepted(row['task_id'],stage)
                if accepted and (stage=='leader' or accepted['candidate_sha']==row['candidate_sha']):phase=next_phase
                else:break
        receipts=[]
        for file in Path(row['evidence_dir']).rglob('*.multica.json'):
            try:
                data=json.loads(file.read_text());run=data.get('run',{})
                category,_=classify_role_failure(str(run.get('error'))+' '+str((run.get('result') or {}).get('output','')))
                if run.get('status')=='failed' and category=='blocked_quota':receipts.append(file)
            except (OSError,ValueError):continue
        receipt=max(receipts,key=lambda p:p.stat().st_mtime) if receipts else None
        health.quota('kimi',retirement={'provider':'kimi','logs':[str(receipt)] if receipt else []})
        with runner.ledger.connection:
            runner.ledger.connection.execute("UPDATE tasks SET phase=?,failure_category='provider_wait',lease_owner=NULL,lease_expires_at=NULL WHERE task_id=?",(phase,row['task_id']))
            runner.ledger._event(row['task_id'],'provider_wait',row['fence'],'legacy Kimi quota checkpoint',now)

def probe_provider(name,config,prep):
    """Routin precheck avoids OpenCode's long automatic retries during 402."""
    model=config['model'];folder=Path(prep)/'provider-probes'/name
    folder.mkdir(parents=True,exist_ok=True);folder.chmod(0o700)
    overlay={'enabled_providers':[model.split('/')[0]],'model':model,'small_model':model,'autoupdate':False}
    if name=='routin':
        global_config=json.loads((Path.home()/'.config/opencode/opencode.json').read_text())
        provider=global_config['provider']['routinai'];key=provider['options']['apiKey']
        match=re.fullmatch(r'\{file:(.+)\}',key)
        key=Path(match.group(1)).expanduser().read_text().strip() if match else key
        payload={'model':model.split('/',1)[1],'input':'Reply exactly OK.','max_output_tokens':16}
        req=urllib.request.Request(provider['options']['baseURL'].rstrip('/')+'/responses',data=json.dumps(payload).encode(),headers={'Authorization':'Bearer '+key,'Content-Type':'application/json'})
        try:
            with urllib.request.urlopen(req,timeout=30) as response:
                data=json.load(response)
                if data.get('error'):
                    category,_=classify_role_failure(json.dumps(data))
                    return {'verified':False,'category':category}
        except urllib.error.HTTPError as error:
            if error.code==402:return {'verified':False,'category':'quota_wait'}
            if error.code in (401,403):return {'verified':False,'category':'auth_error'}
            return {'verified':False,'category':'http_'+str(error.code)}
        except (OSError,ValueError):return {'verified':False,'category':'transport_error'}
        template=copy.deepcopy(next(iter(provider['models'].values())))
        template['name']=model.split('/',1)[1]
        overlay['provider']={'routinai':{'models':{model.split('/',1)[1]:template}}}
    nonce=uuid.uuid4().hex
    (folder/'provider-probe-sentinel.txt').write_text(nonce)
    target=folder/'provider-probe-result.txt';target.unlink(missing_ok=True)
    overlay['agent']={'quota-probe':{'mode':'primary','model':model,'tools':{'bash':False,'webfetch':False},'prompt':'You are an isolated provider readiness probe. Only read provider-probe-sentinel.txt and write provider-probe-result.txt. Do not inspect any other file.'}}
    (folder/'opencode.json').write_text(json.dumps(overlay))
    out=folder/'probe.stdout.jsonl';err=folder/'probe.stderr.log'
    try:
        prompt='Read provider-probe-sentinel.txt using the read tool. Write its exact content to provider-probe-result.txt using the write tool. Then reply exactly ATLAS_PROVIDER_READY. Do not use shell commands or inspect other files.'
        environment={k:v for k,v in os.environ.items() if not k.startswith(('MULTICA_','OPENCODE_'))}
        environment['OPENCODE_CONFIG_CONTENT']=json.dumps(overlay)
        code=ProcessSupervisor().execute(['opencode','run',prompt,'--auto','--format','json','--agent','quota-probe','-m',model],folder,120,out,err,environment=environment)
        text=out.read_text(errors='replace');errors=err.read_text(errors='replace')
        try:ok=code==0 and 'ATLAS_PROVIDER_READY' in parse_opencode_jsonl(text) and target.is_file() and target.read_text()==nonce
        except RunnerError:ok=False
        category,_=classify_role_failure(text+'\n'+errors)
        return {'verified':ok,'category':'available' if ok else ('quota_wait' if category=='blocked_quota' else category),'model':model}
    except RunnerError:return {'verified':False,'category':'probe_timeout','model':model}

def wake(runner,state_dir,prep,probe=probe_provider,clock=time.time,start=None):
    """Deterministic native/launchd wakeup; never overrides an explicit user stop."""
    state_dir=Path(state_dir)
    current=json.loads((state_dir/'controls.json').read_text())
    if not current.get('enabled') or current.get('stopRequested') or (state_dir/'STOP').exists():return {'status':'paused','resumed':False}
    config=current.get('providerRecovery',{})
    if not config.get('enabled'):return {'status':'disabled','resumed':False}
    rows=runner.ledger.rows()
    if rows and all(r['phase']=='done' for r in rows):return {'status':'complete','resumed':False}
    health=ProviderHealth(state_dir/'provider-health.json',config['providers'],clock)
    changed=[]
    # Separate probe lock: only one timer/native wakeup may spend probe tokens.
    with (state_dir/'provider-probe.lock').open('a+') as lock:
        try:fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
        except BlockingIOError:return {'status':'probe_already_running','resumed':False}
        for name,info in health.read()['providers'].items():
            if info['status']=='available' or info.get('nextProbeAt',0)>clock():continue
            result=probe(name,config['providers'][name],Path(prep))
            health.probe_result(name,result['verified'],result['category'],config.get('retrySeconds',1800))
            changed.append({'provider':name,**result})
    available=health.available();resumed=False
    waiting=any(r['failure_category'] in ('provider_wait','blocked_quota') for r in rows)
    if available and waiting:
        from atlas_runner import ProcessLock
        try:
            with ProcessLock(state_dir/'runner.lock'):
                normalize_legacy_quota_waits(runner,health)
                controls=json.loads((state_dir/'controls.json').read_text())
                permitted=controls.get('enabled') and not controls.get('stopRequested') and not (state_dir/'STOP').exists()
            if permitted and health.available():
                # launchctl without -k leaves an already-running coordinator alone.
                (start or (lambda:subprocess.run(['sudo','-n','launchctl','kickstart','system/local.businessatlas.runner'],check=True)))()
                resumed=True
        except RunnerError as error:
            if error.category!='writer_locked':raise
    available=health.available()
    result={'status':'available' if available else 'waiting_quota','available':available,'probes':changed,'resumed':resumed,'at':int(clock())}
    write_json(state_dir/'provider-wakeup-status.json',result)
    return result
