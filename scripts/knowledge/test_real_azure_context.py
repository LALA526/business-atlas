import copy,json,unittest
import candidate_build,ci_context

class RealAzureContextTests(unittest.TestCase):
 def test_real_azure_merge_ref_build_attests_source_from_server_parameters(self):
  candidate='a'*40
  d={'id':23,'project':{'id':'P'},'definition':{'id':3},'sourceVersion':'b'*40,'sourceBranch':'refs/pull/9/merge','reason':'pullRequest','status':'completed','result':'succeeded','triggerInfo':{'pr.number':'9'},'parameters':json.dumps({'system.pullRequest.sourceCommitId':candidate,'system.pullRequest.pullRequestId':'9','system.pullRequest.sourceBranch':'refs/heads/task/T02'})}
  args=dict(collection_uri='https://dev.azure.com/O',project_id='P',definition_id='3',build_id='23',candidate_sha=candidate,pull_request_id='9',source_branch='task/T02')
  self.assertEqual(candidate,candidate_build.attest_build(d,json.dumps(d).encode(),'https://dev.azure.com/O/P/_apis/build/builds/23',**args)['candidateSha'])
  bad=copy.deepcopy(d);p=json.loads(bad['parameters']);p['system.pullRequest.sourceCommitId']='c'*40;bad['parameters']=json.dumps(p)
  with self.assertRaises(candidate_build.AttestationError):candidate_build.attest_build(bad,json.dumps(bad).encode(),'https://dev.azure.com/O/P/_apis/build/builds/23',**args)
 def test_merge_context_selects_actual_completed_pr_and_exact_approved_policy(self):
  env={'BUILD_REPOSITORY_ID':'R','BUILD_SOURCEVERSION':'b'*40,'SYSTEM_DEFINITIONID':'3','SYSTEM_TEAMPROJECTID':'P'}
  pr={'pullRequestId':9,'lastMergeCommit':{'commitId':'b'*40},'lastMergeSourceCommit':{'commitId':'a'*40},'sourceRefName':'refs/heads/task/T02','completionOptions':{'mergeStrategy':'squash'}}
  policy={'status':'approved','configuration':{'isEnabled':True,'isBlocking':True,'settings':{'buildDefinitionId':3}},'context':{'lastMergeSourceCommitId':'a'*40,'buildId':23}}
  def api(path):return {'value':[policy]} if '/policy/' in path else {'value':[pr]}
  d=ci_context.merge_context(api,env);self.assertEqual(d['ATLAS_CANDIDATE_BUILD_ID'],'23');self.assertEqual(d['ATLAS_MERGE_STRATEGY'],'squash')
  policy['context']['lastMergeSourceCommitId']='c'*40
  with self.assertRaises(RuntimeError):ci_context.merge_context(api,env)
