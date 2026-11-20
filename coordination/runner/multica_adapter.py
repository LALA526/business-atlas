"""Native Multica dispatch on a pinned Mac16 runtime, with coordinator-owned gates."""
from pathlib import Path
import dataclasses, hashlib, json, os, re, subprocess, time, uuid, fcntl
from atlas_runner import RoleResult, RunnerError, StopRequested, classify_role_failure, write_json

class MulticaAdapter:
    def __init__(self, binding, supervisor, command=None, sleep=time.sleep):
        self.binding=binding
        self.supervisor=supervisor
        self.command=command or self._command
        self.sleep=sleep

    def _command(self,*args):
        control_dir=Path.home()/'Workspace/projects/business-atlas-preflight/multica-control'
        control_dir.mkdir(parents=True,exist_ok=True)
        control_dir.chmod(0o700)
        result=subprocess.run(['multica','--profile',self.binding['profile'],'--workspace-id',self.binding['workspaceId'],*args],cwd=control_dir,capture_output=True,text=True,timeout=60,env={k:v for k,v in os.environ.items() if not k.startswith('MULTICA_') and k not in ('AZURE_DEVOPS_PAT','PERSONAL_ACCESS_TOKEN','ADO_MCP_AUTH_TOKEN','AZURE_DEVOPS_EXT_PAT')})
        if result.returncode: raise RunnerError('multica_api_failed',result.stderr[:300])
        return json.loads(result.stdout)

    def preflight(self):
        runtimes=self.command('runtime','list','--output','json')
        runtime=next((r for r in runtimes if r['id']==self.binding['runtimeId']),None)
        ready=bool(runtime and runtime.get('daemon_id')==self.binding['daemonId'] and runtime.get('provider')=='opencode' and runtime.get('status')=='online')
        agents={a['id']:a for a in self.command('agent','list','--output','json')}
        ready=ready and all(agents.get(r['agentId'],{}).get('runtime_id')==self.binding['runtimeId'] for r in self.binding['roles'].values())
        return {'ready':ready,'runtimeId':self.binding['runtimeId'],'daemonId':self.binding['daemonId'],'workspaceId':self.binding['workspaceId']}

    def run(self,role,prompt,cwd,log_dir,timeout_seconds):
        if self.supervisor.stop_event.is_set(): raise StopRequested()
        if not self.preflight()['ready']: return RoleResult(False,category='runtime_not_ready')
        cwd=Path(cwd).resolve()
        permitted=Path.home()/'Workspace/projects/business-atlas-preflight'
        if not cwd.is_dir() or not (cwd/'.git').exists() or not (cwd==Path.home()/'Workspace/projects/business-atlas' or cwd.is_relative_to(permitted)):
            raise RunnerError('worktree_invalid','Multica requires an explicitly allowed Mac16 checkout')
        role_config=self.binding['roles'][role]
        log_dir.mkdir(parents=True,exist_ok=True)
        receipt=log_dir/(role+'-'+uuid.uuid4().hex[:12]+'.multica.json')
        match=re.search(r'"taskId"\s*:\s*"([^"]+)"',prompt) or re.search(r'\b(T\d\d|PREP-[A-Z0-9-]+)\b',prompt)
        task_id=match.group(1) if match else 'PREP-SMOKE'
        state=Path.home()/'Workspace/projects/business-atlas-preflight/runner-state-multica/native-dispatch'
        state.mkdir(parents=True,exist_ok=True)
        state.chmod(0o700)
        active=state/(hashlib.sha256((role+'\0'+task_id).encode()).hexdigest()+'.json')
        issue=None
        try:
            saved=json.loads(active.read_text()) if active.exists() else None
            if saved and (saved['cwd']!=str(cwd) or saved['prompt']!=prompt):
                raise RunnerError('dispatch_conflict','Unfinished native dispatch has different context; refuse duplicate execution')
            with (state/'source-ignore.lock').open('a+') as ignore_lock:
                fcntl.flock(ignore_lock,fcntl.LOCK_EX)
                resolved=subprocess.run(['git','rev-parse','--git-common-dir'],cwd=cwd,capture_output=True,text=True,check=True).stdout.strip()
                common=Path(resolved) if Path(resolved).is_absolute() else (cwd/resolved).resolve()
                exclude=common/'info/exclude';exclude.parent.mkdir(parents=True,exist_ok=True)
                content=exclude.read_text() if exclude.exists() else ''
                if '.atlas-runtime/' not in content.splitlines():exclude.write_text(content.rstrip('\n')+'\n.atlas-runtime/\n')
            inputs=cwd/'.atlas-runtime'
            inputs.mkdir(parents=True,exist_ok=True);inputs.chmod(0o700)
            input_file=inputs/'task-input.json'
            prompt_file=inputs/'task-input.txt'
            temporary=prompt_file.with_suffix('.tmp')
            temporary.write_text(prompt,encoding='utf-8')
            temporary.chmod(0o600)
            temporary.replace(prompt_file)
            write_json(input_file,{'workspaceId':self.binding['workspaceId'],'cwd':str(cwd),'role':role,'prompt':'Use the read tool to read the complete plain-text task at '+str(prompt_file)+'. Execute that task and return only its required response.','promptSha256':hashlib.sha256(prompt.encode()).hexdigest()})
            input_file.chmod(0o600)
            input_digest=hashlib.sha256(input_file.read_bytes()).hexdigest()
            if not saved:
                title=task_id+' · '+role+' · '+uuid.uuid4().hex[:12]
                saved={'cwd':str(cwd),'prompt':prompt,'title':title,'issue':None}
                write_json(active,saved)
            issue=saved.get('issue')
            if not issue:
                # The intent is durable before POST. Recover a response lost after creation.
                offset=0
                while True:
                    page=self.command('issue','list','--project',role_config['projectId'],'--limit','100','--offset',str(offset),'--output','json')
                    items=page if isinstance(page,list) else page.get('issues',[])
                    issue=next((i for i in items if i['title']==saved['title']),None)
                    if issue or isinstance(page,list) or not page.get('has_more'):break
                    offset+=len(items)
                    if not items:raise RunnerError('dispatch_unresolved','Native issue pagination did not advance')
            if not issue:
                self.command('project','resource','update',role_config['projectId'],role_config['resourceId'],'--daemon-id',self.binding['daemonId'],'--local-path',str(cwd),'--execution-mode','in_place','--output','json')
                item=self.binding['tasks'].get(task_id)
                if item:
                    self.command('issue','status',item['id'],'in_progress','--no-start','--output','json')
                desc='MAC16 NATIVE EXECUTION. Exact working directory: '+str(cwd)+'\nNever dispatch other agents, alter issue status, use Windows or reuse APP/ARC issues. Return your result in the response; coordinator owns all acceptance and PR state.\n\n'+prompt
                issue=self.command('issue','create','--title',saved['title'],'--project',role_config['projectId'],'--assignee-id',role_config['agentId'],'--status','in_progress','--pr-auto-complete','off','--description',desc,'--output','json')
            saved['issue']=issue
            write_json(active,saved)
            deadline=time.monotonic()+timeout_seconds
            while time.monotonic()<deadline:
                runs=self.command('issue','runs',issue['id'],'--output','json')
                run=max((r for r in runs if r.get('agent_id')==role_config['agentId']),key=lambda r:(r.get('created_at') or '',r.get('attempt') or 0),default=None)
                if self.supervisor.stop_event.is_set():
                    if run and run['status'] not in ('completed','failed','cancelled'):
                        self.command('issue','cancel-task',run['id'],'--issue',issue['id'],'--output','json')
                    raise StopRequested()
                if run and run.get('runtime_id')!=self.binding['runtimeId']:
                    if run['status'] not in ('completed','failed','cancelled'):
                        self.command('issue','cancel-task',run['id'],'--issue',issue['id'],'--output','json')
                    raise RunnerError('wrong_runtime','Native run is not bound to the approved Mac16 runtime')
                if run and run['status'] in ('completed','failed','cancelled'):
                    receipt.write_text(json.dumps({'issueId':issue['id'],'identifier':issue['identifier'],'cwd':str(cwd),'role':role,'run':run,'dispatchJournal':str(active)},ensure_ascii=False,indent=2))
                    result=run.get('result') or {}
                    text=result.get('output') or ''
                    if run['status']=='completed' and text.strip():
                        actual=result.get('work_dir') or run.get('work_dir')
                        if not actual or Path(actual).resolve()!=cwd: raise RunnerError('wrong_worktree','Daemon worktree evidence is missing or does not match the approved worktree')
                        # Server completion may precede daemon cleanup of its temporary brief.
                        agents_file=cwd/'AGENTS.md'
                        for _ in range(30):
                            if not agents_file.exists() or '<!-- BEGIN MULTICA-RUNTIME (auto-managed; do not edit) -->' not in agents_file.read_text():break
                            self.sleep(1)
                        else:
                            write_json(active,saved)
                            return RoleResult(False,category='runtime_cleanup_pending',retryable=True,log_paths=(str(receipt),))
                        if not input_file.is_file() or not prompt_file.is_file() or hashlib.sha256(input_file.read_bytes()).hexdigest()!=input_digest or prompt_file.read_text()!=prompt:
                            raise RunnerError('input_mutated','Native role changed its coordinator-owned input')
                        active.unlink(missing_ok=True)
                        self.command('issue','status',issue['id'],'done','--no-start','--output','json')
                        return RoleResult(True,text=text,log_paths=(str(receipt),))
                    category,retryable=classify_role_failure(str(run.get('error'))+' '+text)
                    if category=='blocked_quota':
                        saved['quotaFailureReceipt']=str(receipt)
                        write_json(active,saved)
                    else:
                        active.unlink(missing_ok=True)
                    return RoleResult(False,category=category,retryable=retryable,log_paths=(str(receipt),))
                self.sleep(3)
            if run and run['status'] not in ('completed','failed','cancelled'):
                self.command('issue','cancel-task',run['id'],'--issue',issue['id'],'--output','json')
            active.unlink(missing_ok=True)
            return RoleResult(False,category='timeout',log_paths=(str(receipt),))
        except RunnerError as error:
            write_json(receipt,{'category':error.category,'detail':str(error),'issueId':issue.get('id') if issue else None})
            return RoleResult(False,category=error.category,log_paths=(str(receipt),))

    def settle_quota_run(self,result):
        """Retire native retries before another provider may own this stage."""
        try:
            if not result.log_paths:return False
            receipt=json.loads(Path(result.log_paths[0]).read_text())
            if receipt.get('run',{}).get('status')!='failed' or not receipt.get('issueId'):return False
            issue_id=receipt['issueId']
            self.command('issue','status',issue_id,'blocked','--no-start','--output','json')
            for _ in range(10):
                runs=self.command('issue','runs',issue_id,'--output','json')
                active=[r for r in runs if r['status'] not in ('completed','failed','cancelled')]
                if not active:
                    if not receipt.get('cwd'):return False
                    brief=Path(receipt['cwd'])/'AGENTS.md'
                    for _ in range(30):
                        if not brief.exists() or '<!-- BEGIN MULTICA-RUNTIME (auto-managed; do not edit) -->' not in brief.read_text():break
                        self.sleep(1)
                    else:return False
                    if receipt.get('dispatchJournal'):
                        journal=Path(receipt['dispatchJournal']).resolve()
                        folder=Path.home()/'Workspace/projects/business-atlas-preflight/runner-state-multica/native-dispatch'
                        if not journal.is_relative_to(folder.resolve()):return False
                        try:
                            saved=json.loads(journal.read_text())
                            if (saved.get('issue') or {}).get('id')==issue_id:
                                archive=folder.parent/'dispatch-archive';archive.mkdir(exist_ok=True)
                                journal.rename(archive/(journal.stem+'-'+str(time.time_ns())+'.json'))
                        except FileNotFoundError:pass
                    return True
                for run in active:self.command('issue','cancel-task',run['id'],'--issue',issue_id,'--output','json')
                self.sleep(1)
            return False
        except (OSError,ValueError,RunnerError):
            return False

    def sync_status(self,status):
        for task in status['tasks']:
            item=self.binding['tasks'].get(task['taskId'])
            if not item or task['state']=='planned': continue
            if task['state']=='done':
                receipt=task.get('integration') or {}
                if receipt.get('status')!='merged' or not all(receipt.get(k) for k in ('pr_id','merge_sha','deployment_id')) or receipt.get('candidate_sha')!=task.get('candidateSha'):
                    raise RunnerError('completion_gate','Ledger completion lacks PR/candidate/deployment receipt')
                state='done'
            else:
                state='blocked' if task['state'] in ('blocked','waiting_provider') else ('in_review' if task['phase'] in ('review','qa','integration','pending_integration') else 'in_progress')
            self.command('issue','status',item['id'],state,'--no-start','--output','json')

class MulticaIntegration:
    def __init__(self, backend, adapter): self.backend,self.adapter=backend,adapter
    def preflight(self):
        azure=self.backend.preflight()
        native=self.adapter.preflight()
        return {**azure,'ready':bool(azure['ready'] and native['ready']),'multica':native}
    def integrate(self,task,candidate_sha,evidence_dir):
        result=self.backend.integrate(task,candidate_sha,evidence_dir)
        item=self.adapter.binding['tasks'].get(task['taskId'])
        if item and result.pr_id:
            if result.status=='merged' and not(result.merge_sha and result.deployment_id):
                raise RunnerError('completion_gate','Requirement cannot finish without merged PR and deployment')
            url='https://dev.azure.com/2717243207/Business%20Atlas/_git/Business%20Atlas/pullrequest/'+str(result.pr_id)
            desc=item['description']+'\n\nActual PR: '+url+'\nCandidate: '+candidate_sha+'\nIntegration evidence:\n'+json.dumps(dataclasses.asdict(result),ensure_ascii=False,indent=2)
            # Only sync_status may publish done, after the ledger accepts all gates.
            self.adapter.command('issue','update',item['id'],'--description',desc,'--status','in_review','--no-start','--pr-auto-complete','off','--output','json')
        return result
