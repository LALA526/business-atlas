"""User-authorized delivery: builder, PR, single build, deployment. No review lanes."""
import json,time,urllib.parse
from pathlib import Path
import atlas_runner as ar
from parallel_executor import ParallelAtlasRunner
from azure_api import request,project_path
REPO='5993b3bf-248b-41c6-850a-06c83e0fa87a'
GIT='/_apis/git/repositories/'+REPO
class FastRunner(ParallelAtlasRunner):
 def preflight(self):
  return {'ready':True,'mode':'functional-delivery','removed':['independent-review','independent-qa','knowledge-manifest','repeated-integration-tests']}
 def _prompt_context(self,row):
  return 'Implement exactly this Business Atlas functional task. Latest user policy: fast functional delivery; no separate planning/reviewer/QA agents, no knowledge manifests or SHA acceptance gates. Read only relevant baseline references and UI references. Full functional scope and business invariants remain. '+json.dumps(self.ledger.task(row),ensure_ascii=False)
 def _phase(self,row,phase):
  self.ledger._owned(row['task_id'],self.owner,row['fence'],self.clock())
  self.ledger.connection.execute('UPDATE tasks SET phase=? WHERE task_id=?',(phase,row['task_id']))
 def _run_leader(self,row):self._phase(row,'developer')
 def _run_review(self,row):self._phase(row,'integration');return 'pass'
 def _run_qa(self,row):self._phase(row,'integration');return 'pass'
 def _run_developer(self,row):
  worktree=self._development_worktree(row)
  ar.git(worktree,'fetch','origin','main')
  merged=ar.git(worktree,'merge','--no-edit','origin/main',check=False)
  prompt=self._prompt_context(row)+'\nContinue existing code; implement the task fully, resolve any merge conflicts. Do not run the old global verification scripts, knowledge suites, or independent AI reviews. Run a focused check only if needed. Host creates the PR and builds/deploys once. Do not dispatch other agents. Previous execution/build feedback: '+str(row['failure_detail'] or '')+'\n'+merged.stdout[-2000:]
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
      detail+=json.dumps([x.get('issues') for x in timeline.get('records',[]) if x.get('issues')],ensure_ascii=False)[-3500:]
      self._phase(row,'developer');raise ar.RunnerError('integration_failed',detail)
     payload={'status':'merged','candidate_sha':row['candidate_sha'],'pr_id':saved,'merge_sha':commit,'deployment_id':str(b['id']),'detail':'Single product build and deployment succeeded; independent review/QA not required by latest user instruction.'}
     self.ledger.connection.execute("UPDATE tasks SET phase='done',integration_json=?,lease_owner=NULL,lease_expires_at=NULL,failure_category=NULL,failure_detail=NULL,updated_at=? WHERE task_id=?",(json.dumps(payload),self.clock(),row['task_id']))
     self.ledger._event(row['task_id'],'integration_merged',row['fence'],'PR '+saved+' deployed build '+str(b['id']),self.clock())
     return 'done'
   self.sleep(10)
  raise ar.RunnerError('integration_pending','Build/deployment pending; resume without rerunning model')
 def run_once(self):
  result=super().run_once()
  if result.get('status')=='failed' and result.get('category')!='provider_wait':self.sleep(30)
  return result
