"""Derive delivery context from frozen DAG and authenticated Azure service facts."""
import json,os,re,sys,subprocess,urllib.parse,urllib.request
from pathlib import Path

def require(ok,message):
 if not ok:raise RuntimeError(message)

def task_context(root,branch):
 dag=json.loads((Path(root)/'docs/baselines/T1.0/03_开发执行/task-dag.json').read_text())
 tasks=[t for t in dag['tasks'] if t['plannedBranch']==branch.removeprefix('refs/heads/')]
 require(len(tasks)==1,'PR branch must map to exactly one controlled task')
 return {'ATLAS_TASK_ID':tasks[0]['taskId'],'ATLAS_REQUIREMENT_IDS':','.join(tasks[0]['requirementIds'])}

def merge_context(api,environment):
 repo=environment['BUILD_REPOSITORY_ID'];sha=environment['BUILD_SOURCEVERSION'];definition=int(environment['SYSTEM_DEFINITIONID'])
 prs=[]
 for offset in range(0,2000,100):
  page=api('/_apis/git/repositories/'+repo+'/pullrequests?searchCriteria.status=completed&$top=100&$skip='+str(offset)+'&api-version=7.1')['value']
  prs.extend(p for p in page if p.get('lastMergeCommit',{}).get('commitId')==sha)
  if len(page)<100:break
 require(len(prs)==1,'Actual merge must identify exactly one completed PR')
 pr=prs[0];pid=str(pr['pullRequestId']);candidate=pr['lastMergeSourceCommit']['commitId']
 require(re.fullmatch('[0-9a-f]{40}',candidate) is not None,'Invalid reviewed source SHA')
 artifact='vstfs:///CodeReview/CodeReviewId/'+environment['SYSTEM_TEAMPROJECTID']+'/'+pid
 evaluations=api('/_apis/policy/evaluations?'+urllib.parse.urlencode({'artifactId':artifact,'api-version':'7.1-preview.1'}))['value']
 own=[e for e in evaluations if e.get('status')=='approved' and e.get('configuration',{}).get('isEnabled') and e['configuration'].get('isBlocking') and e['configuration'].get('settings',{}).get('buildDefinitionId')==definition and e.get('context',{}).get('lastMergeSourceCommitId')==candidate]
 if len(own)==1 and own[0]['context'].get('buildId'):
  build_id=str(own[0]['context']['buildId'])
 else:
  # Completed-PR policy evaluations are not durable on every Azure DevOps
  # version. Recover the same trusted evidence from Builds, then let
  # candidate_build.py authenticate the complete response before use.
  query=urllib.parse.urlencode({'definitions':definition,'reasonFilters':'pullRequest','branchName':'refs/pull/'+pid+'/merge','$top':100,'api-version':'7.1'})
  builds=api('/_apis/build/builds?'+query)['value'];matching=[]
  for build in builds:
   if build.get('status')!='completed' or build.get('result')!='succeeded':continue
   parameters=build.get('parameters') or {}
   try:parameters=json.loads(parameters) if isinstance(parameters,str) else parameters
   except ValueError:continue
   if not isinstance(parameters,dict):continue
   if parameters.get('system.pullRequest.sourceCommitId')==candidate and str(parameters.get('system.pullRequest.pullRequestId'))==pid:
    matching.append(build)
  require(bool(matching),'No approved exact-candidate build policy or successful exact-candidate build evidence')
  build_id=str(max(matching,key=lambda build:int(build['id']))['id'])
 strategy=pr.get('completionOptions',{}).get('mergeStrategy');strategy={'noFastForward':'merge'}.get(strategy,strategy)
 require(strategy in ('merge','squash','rebase'),'Unsupported actual merge strategy')
 return {'ATLAS_CANDIDATE_SHA':candidate,'ATLAS_CANDIDATE_BUILD_ID':build_id,'ATLAS_MERGE_STRATEGY':strategy,'ATLAS_PULL_REQUEST_ID':pid,'ATLAS_SOURCE_BRANCH':pr['sourceRefName'].removeprefix('refs/heads/')}

def canonical_repo_url(url):
 parsed=urllib.parse.urlsplit(url)
 require(parsed.scheme=='https' and parsed.hostname=='dev.azure.com','Unexpected Azure Git repository URL')
 return urllib.parse.urlunsplit((parsed.scheme,parsed.hostname,parsed.path,'',''))

def main():
 mode=sys.argv[1]
 if mode=='repo-url':
  print(canonical_repo_url(subprocess.check_output(['git','remote','get-url','origin'],text=True).strip()));return
 if mode=='pr':values=task_context(Path(__file__).resolve().parents[2],os.environ['SYSTEM_PULLREQUEST_SOURCEBRANCH'])
 else:
  env=os.environ;token=env['SYSTEM_ACCESSTOKEN'];base=env['SYSTEM_COLLECTIONURI'].rstrip('/')+'/'+env['SYSTEM_TEAMPROJECTID']
  def api(path):
   request=urllib.request.Request(base+path,headers={'Authorization':'Bearer '+token,'Accept':'application/json','Cache-Control':'no-cache'})
   with urllib.request.urlopen(request,timeout=40) as response:return json.load(response)
  values=merge_context(api,env)
 for key,value in values.items():
  require('\n' not in value and '\r' not in value,'Context value contains a newline')
  print('##vso[task.setvariable variable='+key+']'+value.replace('%','%AZP25'))
if __name__=='__main__':main()
