"""User-authorized delivery: builder, PR, single build, deployment. No review lanes."""
import json,time,urllib.parse,hashlib,subprocess,traceback
from pathlib import Path
import atlas_runner as ar
from parallel_executor import ParallelAtlasRunner
from azure_api import request,project_path
REPO='5993b3bf-248b-41c6-850a-06c83e0fa87a'
GIT='/_apis/git/repositories/'+REPO
class FastRunner(ParallelAtlasRunner):
 def preflight(self):
  return {'ready':True,'mode':'functional-delivery','removed':['independent-review','independent-qa','knowledge-manifest','repeated-integration-tests']}
 def reconcile(self,row):
  # Resuming delivery does not require the removed review/QA receipts.
  if row['phase'] in ('integration','pending_integration') and not row['candidate_sha']:
   self.ledger.connection.execute("UPDATE tasks SET phase='developer' WHERE task_id=?",(row['task_id'],))
 def _prompt_context(self,row):
  return 'Implement exactly this Business Atlas functional task. Latest user policy: fast functional delivery; no separate planning/reviewer/QA agents, no knowledge manifests or SHA acceptance gates. Read only relevant baseline references and UI references. Full functional scope and business invariants remain. '+json.dumps({k:v for k,v in self.ledger.task(row).items() if k not in ('knowledgeOutputs','requiredOutputs')},ensure_ascii=False)
 def _phase(self,row,phase):
  self.ledger._owned(row['task_id'],self.owner,row['fence'],self.clock())
  self.ledger.connection.execute('UPDATE tasks SET phase=? WHERE task_id=?',(phase,row['task_id']))
 def _run_leader(self,row):self._phase(row,'developer')
 def _run_review(self,row):self._phase(row,'integration');return 'pass'
 def _run_qa(self,row):self._phase(row,'integration');return 'pass'
 def _run_developer(self,row):
  worktree=self._development_worktree(row)
  journal=self.state_dir/'native-dispatch'/(hashlib.sha256(('atlas-builder'+'\0'+row['task_id']).encode()).hexdigest()+'.json')
  if journal.exists():
   # Reattach the exact native job after process/network failure; never merge into a live actor's tree.
   saved=json.loads(journal.read_text());prompt=saved['prompt']
  else:
   ar.git(worktree,'fetch','origin','main')
   merged=ar.git(worktree,'merge','--no-edit','origin/main',check=False)
   failure=self.ledger.get_metadata('deliveryFailure:'+row['task_id']) or row['failure_detail'] or ''
   prompt=self._prompt_context(row)+'\nContinue existing code; implement this task fully and resolve ordinary conflicts. Check deployment configuration for new dependencies, connection strings, authentication, routing and migrations. Do not leave newly required infrastructure unconfigured. No global verification or independent review agents. Host owns PR/build/deploy. Previous actual failure log (repair the reported cause): '+str(failure)[-14000:]+'\n'+merged.stdout[-1000:]
  result=self.invoke_role('atlas-builder',prompt,worktree,Path(row['evidence_dir']))
  if not result.ok:raise ar.RunnerError(result.category,'Builder could not complete: '+result.text[-2000:])
  self._persist_output(row,'developer-result.md',result.text)
  if ar.git(worktree,'status','--porcelain').stdout.strip():
   ar.git(worktree,'add','-A');ar.git(worktree,'commit','-m',row['task_id']+' implement functional requirement')
  candidate=ar.git_head(worktree)
  if candidate==row['base_sha']:raise ar.RunnerError('candidate_empty','No implementation changes; implement the requested requirement')
  self.ledger.connection.execute('UPDATE tasks SET candidate_sha=?,phase=? WHERE task_id=?',(candidate,'integration',row['task_id']))
 def _run_integration(self,row):
  with self.leased_lock(self.state_dir/'integration.lock',row):return self._deliver(row)
 def _deliver(self,row):
  task=self.ledger.task(row);branch=task['plannedBranch'];tree=self._development_worktree(row)
  ar.git(tree,'push','-u','origin',branch)
  key='fastPR:'+row['task_id'];saved=self.ledger.get_metadata(key)
  if saved:
   pr=request(project_path(GIT+'/pullrequests/'+saved+'?api-version=7.1'))
   if pr['status']=='completed' and pr.get('lastMergeSourceCommit',{}).get('commitId')!=row['candidate_sha']:saved=None
  if not saved:
   pr=request(project_path(GIT+'/pullrequests?api-version=7.1'),'POST',{'sourceRefName':'refs/heads/'+branch,'targetRefName':'refs/heads/main','title':row['task_id']+': '+str(task.get('title',task.get('name','Functional delivery'))),'description':'Functional delivery for '+row['task_id']+'\n'+json.dumps(task.get('requirementIds',task.get('requirements',[])),ensure_ascii=False)+'\nUser-authorized process: one build and deployment; independent review/QA and manifest gates removed.'})
   saved=str(pr['pullRequestId']);self.ledger.set_metadata(key,saved)
  if pr['status']!='completed':
   request(project_path(GIT+'/pullrequests/'+saved+'?api-version=7.1'),'PATCH',{'status':'completed','lastMergeSourceCommit':{'commitId':row['candidate_sha']},'completionOptions':{'mergeStrategy':'squash','deleteSourceBranch':False}})
  for _ in range(120):
   self._check_stop();self.ledger.heartbeat(row['task_id'],self.owner,row['fence'],self.clock(),self.lease_seconds)
   pr=request(project_path(GIT+'/pullrequests/'+saved+'?api-version=7.1'))
   if pr['status']=='completed':break
   if pr.get('mergeStatus') in ('conflicts','rejectedByPolicy','failure'):
    self._phase(row,'developer');raise ar.RunnerError('integration_failed','Merge conflict or service error: '+str(pr.get('mergeFailureMessage',pr.get('mergeStatus'))))
   self.sleep(5)
  else:raise ar.RunnerError('integration_pending','PR merge still pending')
  commit=pr['lastMergeCommit']['commitId']
  for tick in range(240):
   self._check_stop();self.ledger.heartbeat(row['task_id'],self.owner,row['fence'],self.clock(),self.lease_seconds)
   builds=request(project_path('/_apis/build/builds?definitions=3&branchName=refs/heads/main&$top=30&api-version=7.1'))['value']
   builds=[b for b in builds if b.get('sourceVersion')==commit]
   if builds:
    b=builds[0]
    if b['status']=='completed':
     if b.get('result')!='succeeded':
      detail='Build/deploy '+str(b['id'])+' failed. '
      timeline=request(project_path('/_apis/build/builds/'+str(b['id'])+'/timeline?api-version=7.1'))
      for record in timeline.get('records',[]):
       if record.get('type')!='Task' or record.get('result')!='failed':continue
       log=(record.get('log') or {}).get('id')
       detail+='\n'+record.get('name','')+' '+json.dumps(record.get('issues'),ensure_ascii=False)
       if log:
        try:detail+='\n'+request(project_path('/_apis/build/builds/'+str(b['id'])+'/logs/'+str(log)+'?api-version=7.1'),as_text=True)[-12000:]
        except Exception as error:detail+='\nLog temporarily unavailable: '+type(error).__name__
      self.ledger.set_metadata('deliveryFailure:'+row['task_id'],detail)
      self._persist_output(row,'actual-delivery-failure.txt',detail)
      self._phase(row,'developer');raise ar.RunnerError('integration_failed',detail)
     payload={'status':'merged','candidate_sha':row['candidate_sha'],'pr_id':saved,'merge_sha':commit,'deployment_id':str(b['id']),'detail':'Single product build and deployment succeeded; independent review/QA not required by latest user instruction.'}
     self.ledger.connection.execute("UPDATE tasks SET phase='done',integration_json=?,lease_owner=NULL,lease_expires_at=NULL,failure_category=NULL,failure_detail=NULL,updated_at=? WHERE task_id=?",(json.dumps(payload),self.clock(),row['task_id']))
     self.ledger._event(row['task_id'],'integration_merged',row['fence'],'PR '+saved+' deployed build '+str(b['id']),self.clock())
     return 'done'
   self.sleep(10)
  raise ar.RunnerError('integration_pending','Build/deployment pending; resume without rerunning model')
 def run_once(self):
  try:result=super().run_once()
  except ar.StopRequested:raise
  except Exception as error:
   task_id=getattr(self,'active_task',None)
   detail=type(error).__name__+': '+str(error)[:1000]
   if task_id:
    row=self.ledger.get(task_id)
    self._persist_output(row,'coordinator-error.txt',traceback.format_exc())
    if row['lease_owner']==self.owner:
     self.ledger.fail(task_id,self.owner,row['fence'],'transient_delivery_error',detail,self.clock())
   result={'status':'failed','taskId':task_id,'category':'transient_delivery_error'}
  if result.get('status')=='failed' and result.get('category')!='provider_wait':self.sleep(30)
  return result

def supervise_fast(runner):
 """Cheap deterministic wakeup; never spends model tokens or interrupts active work."""
 ctl=json.loads((runner.state_dir/'controls.json').read_text())
 if not ctl.get('enabled') or ctl.get('stopRequested') or (runner.state_dir/'STOP').exists():return {'status':'disabled'}
 try:
  with ar.ProcessLock(runner.state_dir/'runner.lock'):
   from autonomous_recovery import REPAIRABLE
   for row in runner.ledger.rows():
    if row['phase']=='blocked' and row['failure_category'] in REPAIRABLE:
     runner.ledger.connection.execute("UPDATE tasks SET phase='developer',failure_category='technical_retry',lease_owner=NULL,lease_expires_at=NULL WHERE task_id=?",(row['task_id'],))
   pending=[row for row in runner.ledger.rows() if row['phase']!='done' and row['failure_category'] not in ('provider_wait','blocked_quota')]
   if not pending:return {'status':'complete_or_provider_wait'}
   # No lock owner means no coordinator can renew these leases. Reattach native jobs on restart.
   runner.ledger.connection.execute("UPDATE tasks SET lease_expires_at=0 WHERE lease_owner IS NOT NULL AND phase!='done'")
   subprocess.run(['sudo','-n','launchctl','kickstart','system/local.businessatlas.runner'],check=True,timeout=15)
   return {'status':'restarted_without_model_call'}
 except ar.RunnerError as error:
  if error.category=='writer_locked':return {'status':'running'}
  raise
