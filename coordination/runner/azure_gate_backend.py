"""Concrete Azure PR/build/deployment gate for the Atlas preparation runner."""
from pathlib import Path
import hashlib, json, subprocess, time, urllib.parse, urllib.error
from atlas_runner import IntegrationResult, RunnerError

class AzureGateBackend:
    def __init__(self, config, repo, api, sleep=time.sleep):
        self.config, self.repo, self.api, self.sleep = config, Path(repo), api, sleep

    def path(self, suffix):
        return '/' + urllib.parse.quote(self.config['project'], safe='') + suffix

    def pr_path(self, suffix=''):
        return self.path('/_apis/git/repositories/' + self.config['repositoryId'] + '/pullrequests' + suffix)

    def policies(self, pr_id):
        artifact = 'vstfs:///CodeReview/CodeReviewId/' + self.config['projectId'] + '/' + str(pr_id)
        return self.api(self.path('/_apis/policy/evaluations?' + urllib.parse.urlencode({'artifactId': artifact, 'api-version': '7.1-preview.1'})))['value']

    def preflight(self):
        try:
            repo = self.api(self.path('/_apis/git/repositories/' + self.config['repositoryId'] + '?api-version=7.1'))
            policies = self.api(self.path('/_apis/policy/configurations?api-version=7.1'))['value']
            policy_ok = any(p.get('isEnabled') and p.get('isBlocking') and
                p.get('settings', {}).get('buildDefinitionId') == self.config['pipelineId'] and
                any(s.get('repositoryId') == self.config['repositoryId'] and s.get('refName') == 'refs/heads/main'
                    for s in p.get('settings', {}).get('scope', [])) for p in policies)
            agents = self.api('/_apis/distributedtask/pools/' + str(self.config['buildPoolId']) + '/agents?api-version=7.1')['value']
            deploy_agents = self.api('/_apis/distributedtask/pools/1/agents?api-version=7.1')['value']
            ready = bool(repo.get('defaultBranch')) and policy_ok and any(a.get('status') == 'online' and a['name'] == 'atlas-mac16-build' for a in agents) and any(a.get('status') == 'online' and a['name'] == 'mac-mini-m1-deploy' for a in deploy_agents)
            return {'ready': ready, 'branchPolicyVerified': policy_ok, 'requiredCheckCount': 1,
                    'authentication': 'verified', 'buildPoolChecked': True, 'deploymentAgentChecked': True}
        except Exception:
            return {'ready': False, 'reason': 'Azure live preflight failed; no readiness assumed'}

    def successful_deployment(self, sha, require_product=False):
        query = urllib.parse.urlencode({'definitions': self.config['pipelineId'], 'branchName': 'refs/heads/main', '$top': 10, 'api-version': '7.1'})
        builds = self.api(self.path('/_apis/build/builds?' + query))['value']
        for build in builds:
            if build.get('sourceVersion') != sha or build.get('status') != 'completed':
                continue
            if build.get('result') != 'succeeded':
                return 'failed', str(build['id'])
            timeline = self.api(self.path('/_apis/build/builds/' + str(build['id']) + '/timeline?api-version=7.1'))
            if any(r.get('type') == 'Stage' and r.get('identifier') == self.config.get('deployStage', 'DeployPreparationMac8') and r.get('result') == 'succeeded' for r in timeline.get('records', [])):
                if require_product:
                    artifacts = self.api(self.path('/_apis/build/builds/' + str(build['id']) + '/artifacts?api-version=7.1'))['value']
                    if not any(a.get('name') == 'atlas-deployment-receipt' and a.get('resource', {}).get('type') == 'PipelineArtifact' and a.get('resource', {}).get('data') for a in artifacts):
                        return 'failed', str(build['id'])
                return 'succeeded', str(build['id'])
        return 'pending', ''

    def prepare_candidate(self,task,worktree,base_sha=None):
        """Bind real PR identity before review, without approving or merging it."""
        worktree=Path(worktree)
        branch=task['plannedBranch']
        if not task.get('taskId','').startswith('T'):return None
        query=urllib.parse.urlencode({'searchCriteria.sourceRefName':'refs/heads/'+branch,'searchCriteria.targetRefName':'refs/heads/main','searchCriteria.status':'active','api-version':'7.1'})
        prs=self.api(self.pr_path('?'+query))['value']
        if len(prs)>1:raise RunnerError('delivery_setup_failed','Ambiguous active task PR')
        if prs and not prs[0].get('isDraft'):
            self.api(self.pr_path('/'+str(prs[0]['pullRequestId'])+'?api-version=7.1'),'PATCH',{'isDraft':True})
        subprocess.run(['git','push','origin','HEAD:refs/heads/'+branch],cwd=worktree,check=True,capture_output=True)
        pr=prs[0] if prs else self.api(self.pr_path('?api-version=7.1'),'POST',{'sourceRefName':'refs/heads/'+branch,'targetRefName':'refs/heads/main','title':task['taskId']+': '+task.get('title','Requirement delivery'),'description':'Requirements: '+', '.join(task.get('requirementIds',[]))+'\nDraft identity reservation. Independent review, QA and required CI remain mandatory.','isDraft':True})
        pr_id=str(pr['pullRequestId']);changed=False
        for file in (worktree/'knowledge/proposals').rglob('*.json'):
            document=json.loads(file.read_text())
            if document.get('taskId')!=task['taskId']:continue
            before=json.dumps(document,sort_keys=True)
            repo=document['repo']
            if base_sha and document.get('schemaVersion')=='1.3.0':
                repo['baseSha']=base_sha
                output=subprocess.check_output(['git','diff','--no-renames','--name-status',base_sha,'HEAD'],cwd=worktree,text=True)
                facts=[]
                for line in output.splitlines():
                    kind,path=line.split('\t')
                    if path.startswith('knowledge/proposals/'):continue
                    fact={'path':path,'changeType':{'A':'added','M':'modified','D':'removed'}[kind]}
                    if kind!='D':fact['contentSha256']=hashlib.sha256(subprocess.check_output(['git','show','HEAD:'+path],cwd=worktree)).hexdigest()
                    facts.append(fact)
                document['changedFiles']=facts
                for anchor in document.get('sourceAnchors',[]):
                    if 'contentSha256' in anchor:anchor['contentSha256']=hashlib.sha256(subprocess.check_output(['git','show','HEAD:'+anchor['path']],cwd=worktree)).hexdigest()
                    if 'blobSha' in anchor:anchor['blobSha']=subprocess.check_output(['git','rev-parse','HEAD:'+anchor['path']],cwd=worktree,text=True).strip()
            if repo.get('branch')!=branch:raise RunnerError('delivery_setup_failed','Proposal branch mismatch')
            if repo.get('pullRequestId')!=pr_id:
                if str(repo.get('pullRequestId','')).upper()!='PENDING':raise RunnerError('delivery_setup_failed','Proposal already references another real PR')
                repo['pullRequestId']=pr_id
            if json.dumps(document,sort_keys=True)!=before:
                file.write_text(json.dumps(document,ensure_ascii=False,indent=2)+'\n');changed=True
        if changed:
            subprocess.run(['git','add','knowledge/proposals'],cwd=worktree,check=True,capture_output=True)
            subprocess.run(['git','commit','-m','Bind '+task['taskId']+' knowledge to actual draft PR '+pr_id],cwd=worktree,check=True,capture_output=True)
            subprocess.run(['git','push','origin','HEAD:refs/heads/'+branch],cwd=worktree,check=True,capture_output=True)
        return pr_id

    def current_build_policy(self,evaluation,pr,candidate_sha):
        if evaluation.get('configuration',{}).get('settings',{}).get('buildDefinitionId')!=self.config['pipelineId']:return True
        context=evaluation.get('context',{})
        target=pr.get('lastMergeTargetCommit',{}).get('commitId')
        return bool(target and context.get('buildId') and context.get('lastMergeSourceCommitId')==candidate_sha and context.get('lastMergeTargetCommitId')==target and not context.get('isExpired',False) and not context.get('buildIsNotCurrent',False))

    def integrate(self, task, candidate_sha, evidence_dir):
        try:
            return self._integrate(task, candidate_sha, evidence_dir)
        except urllib.error.HTTPError as error:
            reason = "blocked_credential" if error.code in (401, 403) else "Azure HTTP request pending"
            return IntegrationResult("pending", candidate_sha, detail=reason)
        except (OSError, ValueError, subprocess.TimeoutExpired):
            return IntegrationResult("pending", candidate_sha, detail="Azure transport or process temporarily unavailable")

    def _integrate(self, task, candidate_sha, evidence_dir):
        evidence_dir = Path(evidence_dir)
        branch = task['plannedBranch']
        result = subprocess.run(['git', 'push', 'origin', candidate_sha + ':refs/heads/' + branch], cwd=self.repo,
                                capture_output=True, text=True, timeout=120)
        if result.returncode:
            return IntegrationResult('failed', candidate_sha, detail='candidate push failed')
        query = urllib.parse.urlencode({'searchCriteria.sourceRefName': 'refs/heads/' + branch,
            'searchCriteria.targetRefName': 'refs/heads/main', 'searchCriteria.status': 'all', 'api-version': '7.1'})
        prs = self.api(self.pr_path('?' + query))['value']
        pr = next((p for p in prs if p.get('lastMergeSourceCommit', {}).get('commitId') == candidate_sha), None)
        if pr is None:
            pr = self.api(self.pr_path('?api-version=7.1'), 'POST', {
                'sourceRefName': 'refs/heads/' + branch, 'targetRefName': 'refs/heads/main',
                'title': task['taskId'] + ': ' + task.get('title', 'Atlas task'),
                'description': 'Scoped T1.0 requirement delivery. Task: ' + task['taskId'] + '\nRequirement IDs: ' + ', '.join(task.get('requirementIds', [])) + '\nAcceptance: ' + task.get('acceptance', '') + '\nIndependent implementation, review and QA evidence is bound to source ' + candidate_sha})
        pr_id = str(pr['pullRequestId'])
        deadline = time.monotonic() + self.config.get('timeoutSeconds', 1800)
        requeued=set()
        while time.monotonic() < deadline:
            pr = self.api(self.pr_path('/' + pr_id + '?api-version=7.1'))
            if pr.get('lastMergeSourceCommit', {}).get('commitId') != candidate_sha:
                return IntegrationResult('failed', candidate_sha, pr_id=pr_id, detail='PR source changed after QA')
            if pr.get('status') == 'abandoned':
                return IntegrationResult('failed', candidate_sha, pr_id=pr_id, detail='PR abandoned')
            if pr.get('status') == 'active':
                if pr.get('mergeStatus')=='conflicts':
                    return IntegrationResult('failed',candidate_sha,pr_id=pr_id,detail='Merge approved origin/main into the task branch and resolve Git conflicts')
                if pr.get('isDraft'):
                    self.api(self.pr_path('/' + pr_id + '?api-version=7.1'), 'PATCH', {'isDraft': False})
                evaluations = self.policies(pr_id)
                required = [e for e in evaluations if e.get('configuration', {}).get('isEnabled') and e.get('configuration', {}).get('isBlocking')]
                own_build = any(e.get('configuration', {}).get('settings', {}).get('buildDefinitionId') == self.config['pipelineId'] for e in required)
                # Draft/source changes can leave a prior generation's rejected or
                # approved evaluation visible. Neither is evidence for this SHA.
                for evaluation in required:
                    own=evaluation.get('configuration',{}).get('settings',{}).get('buildDefinitionId')==self.config['pipelineId']
                    if own and not self.current_build_policy(evaluation,pr,candidate_sha):
                        key=(evaluation.get('evaluationId'),candidate_sha,pr.get('lastMergeTargetCommit',{}).get('commitId'))
                        if key[0] and key not in requeued and evaluation.get('status') not in ('queued','running'):
                            self.api(self.path('/_apis/policy/evaluations/'+key[0]+'?api-version=7.1-preview.1'),'PATCH',{'status':'queued'})
                            requeued.add(key)
                all_current=all(self.current_build_policy(e,pr,candidate_sha) for e in required)
                if own_build and all_current and all(e.get('status') == 'approved' for e in required):
                    self.api(self.pr_path('/' + pr_id + '?api-version=7.1'), 'PATCH', {
                        'status': 'completed', 'lastMergeSourceCommit': {'commitId': candidate_sha},
                        'completionOptions': {'deleteSourceBranch': False, 'mergeStrategy': 'squash', 'transitionWorkItems': False}})
                elif any(e.get('status') in ('rejected','broken') and self.current_build_policy(e,pr,candidate_sha) for e in required):
                    evidence_dir.mkdir(parents=True,exist_ok=True)
                    failure=evidence_dir/'azure-policy-failure.json'
                    failure.write_text(json.dumps([{'status':e.get('status'),'context':e.get('context',{})} for e in required],indent=2))
                    return IntegrationResult('failed',candidate_sha,pr_id=pr_id,detail='Current candidate Azure policy failed; inspect '+str(failure))
            elif pr.get('status') == 'completed':
                merge_sha = pr.get('lastMergeCommit', {}).get('commitId', '')
                if not merge_sha:
                    return IntegrationResult('failed', candidate_sha, pr_id=pr_id, detail='merge SHA missing')
                product_required = task.get('taskId') in {'T39', 'T40', 'T42', 'T43', 'T44'}
                state, build_id = self.successful_deployment(merge_sha, product_required)
                if state == 'succeeded':
                    evidence_dir.mkdir(parents=True, exist_ok=True)
                    (evidence_dir / 'azure-gates.json').write_text(json.dumps({'prId': pr_id, 'candidateSha': candidate_sha,
                        'mergeSha': merge_sha, 'deploymentBuildId': build_id, 'stage': self.config.get('deployStage', 'DeployPreparationMac8')}, indent=2))
                    subprocess.run(['git', 'fetch', 'origin', 'main'], cwd=self.repo, check=True, capture_output=True)
                    return IntegrationResult('merged', candidate_sha, merge_sha, build_id, pr_id)
                if state == 'failed':
                    diagnostic = evidence_dir / 'azure-deployment-failure.json'
                    diagnostic.write_text(json.dumps({'buildId': build_id, 'mergeSha': merge_sha, 'requireProductReceipt': product_required}, indent=2))
                    return IntegrationResult('failed', candidate_sha, merge_sha=merge_sha, pr_id=pr_id, detail='Build/deployment/product receipt failed. Read ' + str(diagnostic))
            self.sleep(self.config.get('pollSeconds', 15))
        return IntegrationResult('pending', candidate_sha, pr_id=pr_id, detail='Awaiting required Azure checks or deployment')
