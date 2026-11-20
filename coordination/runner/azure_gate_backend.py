"""Concrete Azure PR/build/deployment gate for the Atlas preparation runner."""
from pathlib import Path
import json, subprocess, time, urllib.parse
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

    def successful_deployment(self, sha):
        query = urllib.parse.urlencode({'definitions': self.config['pipelineId'], 'branchName': 'refs/heads/main', '$top': 10, 'api-version': '7.1'})
        builds = self.api(self.path('/_apis/build/builds?' + query))['value']
        for build in builds:
            if build.get('sourceVersion') != sha or build.get('status') != 'completed':
                continue
            if build.get('result') != 'succeeded':
                return 'failed', str(build['id'])
            timeline = self.api(self.path('/_apis/build/builds/' + str(build['id']) + '/timeline?api-version=7.1'))
            if any(r.get('type') == 'Stage' and r.get('identifier') == self.config.get('deployStage', 'DeployPreparationMac8') and r.get('result') == 'succeeded' for r in timeline.get('records', [])):
                return 'succeeded', str(build['id'])
        return 'pending', ''

    def integrate(self, task, candidate_sha, evidence_dir):
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
                'description': 'Candidate from independent implementation, review and QA sessions. Coordinator evidence is bound to source ' + candidate_sha})
        pr_id = str(pr['pullRequestId'])
        deadline = time.monotonic() + self.config.get('timeoutSeconds', 1800)
        while time.monotonic() < deadline:
            pr = self.api(self.pr_path('/' + pr_id + '?api-version=7.1'))
            if pr.get('lastMergeSourceCommit', {}).get('commitId') != candidate_sha:
                return IntegrationResult('failed', candidate_sha, pr_id=pr_id, detail='PR source changed after QA')
            if pr.get('status') == 'abandoned':
                return IntegrationResult('failed', candidate_sha, pr_id=pr_id, detail='PR abandoned')
            if pr.get('status') == 'active':
                evaluations = self.policies(pr_id)
                required = [e for e in evaluations if e.get('configuration', {}).get('isEnabled') and e.get('configuration', {}).get('isBlocking')]
                own_build = any(e.get('configuration', {}).get('settings', {}).get('buildDefinitionId') == self.config['pipelineId'] for e in required)
                if own_build and all(e.get('status') == 'approved' for e in required):
                    self.api(self.pr_path('/' + pr_id + '?api-version=7.1'), 'PATCH', {
                        'status': 'completed', 'lastMergeSourceCommit': {'commitId': candidate_sha},
                        'completionOptions': {'deleteSourceBranch': False, 'mergeStrategy': 'squash', 'transitionWorkItems': False}})
                elif any(e.get('status') in ('rejected', 'broken') for e in required):
                    return IntegrationResult('failed', candidate_sha, pr_id=pr_id, detail='Azure required policy failed')
            elif pr.get('status') == 'completed':
                merge_sha = pr.get('lastMergeCommit', {}).get('commitId', '')
                if not merge_sha:
                    return IntegrationResult('failed', candidate_sha, pr_id=pr_id, detail='merge SHA missing')
                state, build_id = self.successful_deployment(merge_sha)
                if state == 'succeeded':
                    evidence_dir.mkdir(parents=True, exist_ok=True)
                    (evidence_dir / 'azure-gates.json').write_text(json.dumps({'prId': pr_id, 'candidateSha': candidate_sha,
                        'mergeSha': merge_sha, 'deploymentBuildId': build_id, 'stage': self.config.get('deployStage', 'DeployPreparationMac8')}, indent=2))
                    subprocess.run(['git', 'fetch', 'origin', 'main'], cwd=self.repo, check=True, capture_output=True)
                    return IntegrationResult('merged', candidate_sha, merge_sha, build_id, pr_id)
                if state == 'failed':
                    return IntegrationResult('failed', candidate_sha, merge_sha=merge_sha, pr_id=pr_id, detail='Post-merge build or deployment failed')
            self.sleep(self.config.get('pollSeconds', 15))
        return IntegrationResult('pending', candidate_sha, pr_id=pr_id, detail='Awaiting required Azure checks or deployment')
