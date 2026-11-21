import json,subprocess
from pathlib import Path
from unittest.mock import patch
import atlas_runner as ar
from azure_gate_backend import AzureGateBackend
from test_atlas_runner import RunnerFixture

class DeliveryIdentityTests(RunnerFixture):
 def test_prompt_supplies_exact_candidate_never_issue_suffix(self):
  r=self.make_runner();self.addCleanup(r.close)
  r.ledger.connection.execute("update tasks set candidate_sha=? where task_id='T01'",(self.base_sha,))
  self.assertIn('Candidate SHA: '+self.base_sha,r._prompt_context(r.ledger.get('T01')))
 def test_draft_pr_binding_precedes_review_and_preserves_source_hash(self):
  remote=self.repo.parent/'remote.git';subprocess.run(['git','init','--bare','-q',str(remote)],check=True)
  ar.git(self.repo,'remote','add','origin',str(remote));ar.git(self.repo,'push','-u','origin','main');ar.git(self.repo,'checkout','-b','task/T02')
  folder=self.repo/'knowledge/proposals';folder.mkdir(parents=True)
  proposal=folder/'T02.json';proposal.write_text(json.dumps({'taskId':'T02','repo':{'branch':'task/T02','pullRequestId':'PENDING'},'changedFiles':[{'path':'source.txt','contentSha256':'known'}]}))
  ar.git(self.repo,'add','.');ar.git(self.repo,'commit','-m','candidate')
  calls=[]
  def api(path,method='GET',payload=None):
   calls.append((method,payload))
   return {'pullRequestId':77,'isDraft':True} if method=='POST' else {'value':[]}
  backend=AzureGateBackend({'project':'P','repositoryId':'R'},self.repo,api)
  self.assertEqual(backend.prepare_candidate({'taskId':'T02','plannedBranch':'task/T02'},self.repo),'77')
  self.assertTrue(next(payload for method,payload in calls if method=='POST')['isDraft'])
  data=json.loads(proposal.read_text());self.assertEqual(data['repo']['pullRequestId'],'77');self.assertEqual(data['changedFiles'][0]['contentSha256'],'known')
  self.assertEqual(ar.git(self.repo,'ls-remote','origin','refs/heads/task/T02').stdout.split()[0],ar.git_head(self.repo))

 def test_empty_implementation_never_publishes_draft(self):
  from test_atlas_runner import FakeAdapter,FakeIntegration
  adapter=FakeAdapter()
  normal=adapter.run
  def no_op(role,prompt,cwd,log_dir,timeout):
   return ar.RoleResult(True,text='no changes') if role=='atlas-builder' else normal(role,prompt,cwd,log_dir,timeout)
  adapter.run=no_op
  integration=FakeIntegration();integration.prepare_candidate=lambda *a:self.fail('invalid candidate must not touch Azure')
  r=self.make_runner(adapter=adapter,integration=integration);self.addCleanup(r.close)
  self.assertEqual(r.run_once()['category'],'candidate_empty')

 def test_approved_base_refresh_normalizes_only_factual_git_metadata(self):
  import hashlib
  remote=self.repo.parent/'remote.git';subprocess.run(['git','init','--bare','-q',str(remote)],check=True)
  ar.git(self.repo,'remote','add','origin',str(remote));ar.git(self.repo,'push','-u','origin','main');ar.git(self.repo,'checkout','-b','task/T02')
  (self.repo/'source.txt').write_text('real implementation')
  folder=self.repo/'knowledge/proposals';folder.mkdir(parents=True);f=folder/'T02.json'
  f.write_text(json.dumps({'schemaVersion':'1.3.0','taskId':'T02','repo':{'branch':'task/T02','pullRequestId':'PENDING','baseSha':'stale'},'changedFiles':[],'sourceAnchors':[{'path':'source.txt','contentSha256':'stale'}],'unknowns':['real business fact remains unknown']}))
  ar.git(self.repo,'add','.');ar.git(self.repo,'commit','-m','candidate')
  def api(path,method='GET',payload=None):return {'value':[{'pullRequestId':77,'isDraft':True}]}
  backend=AzureGateBackend({'project':'P','repositoryId':'R'},self.repo,api)
  backend.prepare_candidate({'taskId':'T02','plannedBranch':'task/T02'},self.repo,self.base_sha)
  d=json.loads(f.read_text());self.assertEqual(d['repo']['baseSha'],self.base_sha);self.assertEqual(d['repo']['pullRequestId'],'77')
  self.assertEqual(d['changedFiles'],[{'path':'source.txt','changeType':'added','contentSha256':hashlib.sha256(b'real implementation').hexdigest()}])
  self.assertEqual(d['unknowns'],['real business fact remains unknown'])
