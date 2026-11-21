import json,sys,tempfile,unittest
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from azure_gate_backend import AzureGateBackend

class PolicyGenerationTests(unittest.TestCase):
 @patch('azure_gate_backend.subprocess.run')
 def test_old_failure_old_approval_and_old_target_are_requeued_not_used(self,run):
  run.return_value.returncode=0
  for old_status,old_source,old_target in [('rejected','d'*40,'c'*40),('approved','d'*40,'c'*40),('approved','a'*40,'e'*40)]:
   with self.subTest(status=old_status,source=old_source,target=old_target),tempfile.TemporaryDirectory() as tmp:
    current=False;merged=False;queues=[];merges=[]
    def api(path,method='GET',body=None):
     nonlocal current,merged
     if 'searchCriteria' in path:return {'value':[{'pullRequestId':11,'lastMergeSourceCommit':{'commitId':'a'*40}}]}
     if method=='PATCH' and '/policy/evaluations/' in path:
      self.assertEqual(body,{'status':'queued'});queues.append(1);current=True;return {}
     if '/policy/evaluations?' in path:
      return {'value':[{'evaluationId':'E','status':'approved' if current else old_status,'configuration':{'isEnabled':True,'isBlocking':True,'settings':{'buildDefinitionId':3}},'context':{'buildId':34 if current else 30,'lastMergeSourceCommitId':'a'*40 if current else old_source,'lastMergeTargetCommitId':'c'*40 if current else old_target}}]}
     if method=='PATCH':
      self.assertTrue(current);self.assertEqual(body['lastMergeSourceCommit']['commitId'],'a'*40);merges.append(1);merged=True;return {}
     if '/timeline?' in path:return {'records':[{'type':'Stage','identifier':'DeployMac8','result':'succeeded'}]}
     if '/build/builds?' in path:return {'value':[{'id':35,'sourceVersion':'b'*40,'status':'completed','result':'succeeded'}]}
     return {'pullRequestId':11,'status':'completed' if merged else 'active','lastMergeSourceCommit':{'commitId':'a'*40},'lastMergeTargetCommit':{'commitId':'c'*40},'lastMergeCommit':{'commitId':'b'*40}}
    cfg={'project':'P','projectId':'P','repositoryId':'R','pipelineId':3,'deployStage':'DeployMac8','timeoutSeconds':2,'pollSeconds':0}
    result=AzureGateBackend(cfg,tmp,api).integrate({'taskId':'T02','plannedBranch':'task/T02'},'a'*40,tmp)
    self.assertEqual(result.status,'merged');self.assertEqual(queues,[1]);self.assertEqual(merges,[1])
 def test_missing_context_or_expired_evidence_cannot_be_current(self):
  b=AzureGateBackend({'pipelineId':3},'.',lambda *a:None);pr={'lastMergeTargetCommit':{'commitId':'c'*40}}
  e={'configuration':{'settings':{'buildDefinitionId':3}},'context':{'buildId':34,'lastMergeSourceCommitId':'a'*40,'lastMergeTargetCommitId':'c'*40}}
  self.assertTrue(b.current_build_policy(e,pr,'a'*40));e['context']['isExpired']=True;self.assertFalse(b.current_build_policy(e,pr,'a'*40));e['context']={};self.assertFalse(b.current_build_policy(e,pr,'a'*40))
