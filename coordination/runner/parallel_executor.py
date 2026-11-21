"""Two leased task workers; candidate validation fans out without weakening gates."""
from pathlib import Path
import concurrent.futures,contextlib,fcntl,json,re,threading,time
import atlas_runner as ar

def binding_for_slot(binding,slot):
    slot=str(slot or 1)
    if slot=='1':roles=binding.get('slots',{}).get(slot,{}).get('roles',binding['roles'])
    else:
        roles=binding.get('slots',{}).get(slot,{}).get('roles')
        if not roles:raise ar.RunnerError('slot_binding_missing','No isolated native role projects for slot '+slot)
    return {**binding,'roles':roles}

def resources(task):
    return {str(s).replace('\\','/').strip('/').casefold() for s in task.get('resourceLocks',[])+task.get('repoPaths',[]) if str(s).strip('/')}

def conflicts(a,b):
    return any(x==y or x.startswith(y+'/') or y.startswith(x+'/') for x in resources(a) for y in resources(b))

def eligible_rows(ledger,now,max_tasks,slot=None):
    if max_tasks not in (1,2):raise ar.RunnerError('concurrency_invalid','Mac16 task concurrency is capped at two')
    rows=ledger.rows()
    active=[r for r in rows if r['lease_owner'] is not None and r['lease_expires_at']>now and r['phase']!='done']
    if len(active)>=max_tasks:return []
    result=[]
    for row in rows:
        if row['phase'] in ('done','blocked') or (row['lease_owner'] is not None and row['lease_expires_at']>now):continue
        assigned=ledger.get_metadata('workerSlot:'+row['task_id'])
        if slot is not None and assigned is not None and assigned!=str(slot):continue
        if not ledger._dependencies_done(row):continue
        if any(conflicts(ledger.task(row),ledger.task(a)) for a in active):continue
        result.append(row)
    return result

@contextlib.contextmanager
def file_lock(path):
    path=Path(path);path.parent.mkdir(parents=True,exist_ok=True)
    with path.open('a+') as handle:
        fcntl.flock(handle,fcntl.LOCK_EX)
        yield

class ParallelAtlasRunner(ar.AtlasRunner):
    def __init__(self,*args,**kwargs):
        super().__init__(*args,**kwargs)
        self._thread=threading.get_ident();self._validation_cache=None
    def current_base(self):
        with file_lock(self.state_dir/'repo-git.lock'):return super().current_base()
    def _development_worktree(self,row):
        with file_lock(self.state_dir/'repo-git.lock'):return super()._development_worktree(row)
    def _detached_worktree(self,row,stage):
        with file_lock(self.state_dir/'repo-git.lock'):return super()._detached_worktree(row,stage)
    @contextlib.contextmanager
    def leased_lock(self,path,row):
        ledger=ar.Ledger(self.state_dir)
        try:
            with Path(path).open('a+') as handle:
                while True:
                    self._check_stop()
                    ledger.heartbeat(row['task_id'],self.owner,row['fence'],self.clock(),self.lease_seconds)
                    try:fcntl.flock(handle,fcntl.LOCK_EX|fcntl.LOCK_NB);break
                    except BlockingIOError:self.sleep(1)
                ledger.heartbeat(row['task_id'],self.owner,row['fence'],self.clock(),self.lease_seconds)
                yield
        finally:ledger.close()
    def _run_integration(self,row):
        with self.leased_lock(self.state_dir/'integration.lock',row):return super()._run_integration(row)
    def _run_verification(self,row,worktree):
        cached=self._validation_cache
        if cached and cached['candidate']==row['candidate_sha'] and cached['attempt']==row['attempt']:
            if isinstance(cached['verification'],Exception):raise cached['verification']
            return cached['verification']
        with self.leased_lock(self.state_dir/'heavy-verification.lock',row):return super()._run_verification(row,worktree)
    def _invoke(self,role,prompt,cwd,log_dir,timeout_seconds=None):
        ledger=self.ledger if threading.get_ident()==self._thread else ar.Ledger(self.state_dir)
        try:
            last=ar.RoleResult(False,category='model_not_called')
            for retry in range(self.max_retries+1):
                self._check_stop()
                if getattr(self,'active_task',None):
                    row=ledger.get(self.active_task)
                    ledger.heartbeat(self.active_task,self.owner,row['fence'],self.clock(),self.lease_seconds)
                last=self.adapter.run(role,prompt,cwd,log_dir,timeout_seconds or self.timeout_seconds)
                if last.ok or not last.retryable or retry>=self.max_retries:break
                self.sleep(float(self.controls.get('retryBackoffSeconds',2))*(2**retry))
            return last
        finally:
            if ledger is not self.ledger:ledger.close()
    def invoke_role(self,role,prompt,cwd,log_dir,timeout_seconds=None):
        cached=self._validation_cache
        if role=='atlas-qa' and cached:
            row=self.ledger.get(self.active_task) if threading.get_ident()==self._thread else None
            if row and cached['candidate']==row['candidate_sha'] and cached['attempt']==row['attempt']:
                if isinstance(cached['qa'],Exception):raise cached['qa']
                return cached['qa']
        if role!='atlas-reviewer' or not self.controls.get('parallelValidation'):
            return self._invoke(role,prompt,cwd,log_dir,timeout_seconds)
        row=self.ledger.get(self.active_task)
        qa_tree=self._detached_worktree(row,'qa');verification_tree=self._detached_worktree(row,'verification')
        policy=ar.VerificationPolicy.from_committed(self.repo,row['base_sha'],self.policy_path)
        policy.command_for(row['task_id'],qa_tree)
        qa_prompt=self._prompt_context(row)+'\nIndependently inspect this exact committed candidate and acceptance criteria. Do not modify source. Coordinator-owned deterministic verification runs separately. Do not launch scripts/ci/verify, Docker or server smoke processes in this QA lane; inspect and run focused unit tests only, using /Users/fox/Workspace/projects/business-atlas-preflight/.venv/bin/python for Python dependencies. The host verifier supplies mandatory full-build evidence. Return ONLY strict JSON with exactly verdict/findings/evidence. A pass requires actual evidence and does not replace review, CI or deployment.'
        self._validation_cache=None;started=time.time()
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            qa_future=pool.submit(self._invoke,'atlas-qa',qa_prompt,qa_tree,log_dir,timeout_seconds)
            verify_future=pool.submit(self._run_verification,row,verification_tree)
            try:review=self._invoke(role,prompt,cwd,log_dir,timeout_seconds)
            finally:
                outcomes=[]
                for future in (qa_future,verify_future):
                    try:outcomes.append(future.result())
                    except Exception as error:outcomes.append(error)
        self._validation_cache={'candidate':row['candidate_sha'],'attempt':row['attempt'],'qa':outcomes[0],'verification':outcomes[1]}
        ar.write_json(Path(log_dir)/'parallel-validation.json',{'candidateSha':row['candidate_sha'],'startedAt':started,'joinedAt':time.time(),'allJoined':True,'qaOk':getattr(outcomes[0],'ok',False),'verificationOk':not isinstance(outcomes[1],Exception)})
        return review

def run_parallel(parent,controls_path,factory=None):
    limit=int(parent.controls.get('maxConcurrentTasks',1))
    if limit!=2:raise ar.RunnerError('concurrency_invalid','Parallel manager supports exactly two task slots')
    manifest=parent.preflight()
    if not manifest['ready']:return {'status':'preflight_failed'}
    if hasattr(parent.adapter,'sync_status'):parent.adapter.sync_status(parent.status())
    active={};registry={};guard=threading.Lock();completed=0;last=[];quiescent=set();errors=[]
    def worker(slot):
        runner=factory(slot) if factory else ar.build_real_runner(parent.repo,parent.state_dir,controls_path,overrides={'workerSlot':slot,'maxConcurrentTasks':2})
        with guard:registry[slot]=runner
        try:
            try:result=runner.run_once()
            except ar.StopRequested:result={'status':'stopped'}
            if hasattr(runner.adapter,'sync_status') and result.get('taskId'):
                runner.adapter.sync_status({'tasks':[t for t in runner.status()['tasks'] if t['taskId']==result['taskId']]})
            return result
        finally:
            with guard:registry.pop(slot,None)
            runner.close()
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        while True:
            stopping=parent.stop_event.is_set() or (parent.state_dir/'STOP').exists()
            if stopping:
                with guard:
                    for runner in registry.values():runner.request_stop()
            for slot,future in list(active.items()):
                if not future.done():continue
                try:result=future.result()
                except Exception as error:
                    errors.append(type(error).__name__);parent.request_stop();result={'status':'worker_error'}
                last.append(result);completed+=1;del active[slot]
                if result.get('category') in ('provider_wait','blocked_quota'):quiescent.add(slot)
            stopping=parent.stop_event.is_set() or (parent.state_dir/'STOP').exists()
            if stopping and not active:return {'status':'worker_error' if errors else 'stopped','iterations':completed,'errors':errors}
            if not stopping:
                for slot in (1,2):
                    if slot not in active and slot not in quiescent and eligible_rows(parent.ledger,parent.clock(),2,slot):active[slot]=pool.submit(worker,slot)
            if not active:
                rows=parent.ledger.rows()
                waiting=any(r['failure_category'] in ('provider_wait','blocked_quota') for r in rows)
                live=any(r['lease_owner'] is not None and r['lease_expires_at']>parent.clock() for r in rows)
                if not live:return {'status':'provider_wait' if waiting else 'idle','iterations':completed,'last':last[-2:]}
            time.sleep(.2)

def prepare_stopped_dispatches(parent):
    """Explicit restart only: archive cancelled receipts; never discard source or approvals."""
    stopped={r['task_id']:r for r in parent.ledger.rows() if r['failure_category']=='stopped' and r['lease_owner'] is None}
    if not stopped:return
    folder=parent.state_dir/'native-dispatch';archive=parent.state_dir/'dispatch-archive';archive.mkdir(exist_ok=True)
    for file in folder.glob('*.json'):
        data=json.loads(file.read_text());match=re.search(r'"taskId"\s*:\s*"([^"]+)"',data.get('prompt',''))
        if not match or match.group(1) not in stopped:continue
        issue=data.get('issue')
        if not issue:raise ar.RunnerError('dispatch_unresolved','Stopped native creation lacks an issue receipt')
        for _ in range(20):
            runs=parent.adapter.command('issue','runs',issue['id'],'--output','json')
            unfinished=[r for r in runs if r['status'] not in ('completed','failed','cancelled')]
            if not unfinished:break
            for run in unfinished:parent.adapter.command('issue','cancel-task',run['id'],'--issue',issue['id'],'--output','json')
            time.sleep(1)
        else:raise ar.RunnerError('dispatch_unresolved','Old native writers have not stopped')
        file.rename(archive/(file.stem+'-'+str(time.time_ns())+'.json'))
    for row in stopped.values():
        detail='attempt='+str(row['attempt'])
        if not parent.ledger.connection.execute("SELECT 1 FROM events WHERE task_id=? AND event_type='pause_wait' AND detail=?",(row['task_id'],detail)).fetchone():
            with parent.ledger.connection:parent.ledger._event(row['task_id'],'pause_wait',row['fence'],detail,parent.clock())
