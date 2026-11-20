import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from azure_gate_backend import AzureGateBackend
import atlas_runner as ar
from test_atlas_runner import RunnerFixture, FakeAdapter, PASS_QA

class OperationalRunnerTests(RunnerFixture):
    def test_repair_attempts_exhaust_without_claiming_done(self):
        default = FakeAdapter()
        def handler(role, prompt, cwd, count):
            if role == 'atlas-reviewer':
                return ar.RoleResult(True, text=json.dumps({'verdict': 'needs_changes', 'findings': ['missing behavior'], 'evidence': ['actual candidate']}))
            return default.run(role, prompt, cwd, self.state, 1)
        runner = self.make_runner(adapter=FakeAdapter(handler), maxTaskAttempts=2)
        self.assertEqual(runner.run_once()['status'], 'needs_changes')
        self.assertEqual(runner.run_once()['status'], 'needs_changes')
        self.assertEqual(runner.run_once()['status'], 'idle')
        self.assertEqual(runner.ledger.get('T01')['phase'], 'blocked')

    def test_developer_receives_accepted_plan_and_current_worktree(self):
        seen = []
        default = FakeAdapter()
        def handler(role, prompt, cwd, count):
            if role == 'atlas-builder':
                seen.append(prompt)
                self.assertIn('persisted plan', prompt)
            return default.run(role, prompt, cwd, self.state, 1)
        runner = self.make_runner(adapter=FakeAdapter(handler))
        self.assertEqual(runner.run_once()['status'], 'done')
        self.assertEqual(len(seen), 1)

    def test_runtime_controls_can_be_outside_repository(self):
        controls_path = self.state / 'controls.json'
        controls_path.parent.mkdir(exist_ok=True)
        controls_path.write_text(json.dumps({'enabled': False}))
        controls = ar.load_controls(self.repo, controls_path, self.state)
        self.assertEqual(controls['controlsPath'], str(controls_path.resolve()))
        self.assertFalse(controls['enabled'])

class AzureOperationalGateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.config = {'project': 'Business Atlas', 'projectId': 'p', 'repositoryId': 'r',
            'pipelineId': 4, 'buildPoolId': 2, 'timeoutSeconds': 2, 'pollSeconds': 0}
        self.sha, self.merge = 'a' * 40, 'b' * 40

    def test_deployment_requires_exact_sha_and_successful_stage(self):
        def api(path, *args):
            if '/timeline?' in path:
                return {'records': [{'type': 'Stage', 'identifier': 'DeployPreparationMac8', 'result': 'skipped'}]}
            return {'value': [{'id': 9, 'sourceVersion': self.merge, 'status': 'completed', 'result': 'succeeded'}]}
        backend = AzureGateBackend(self.config, self.tmp.name, api)
        self.assertEqual(backend.successful_deployment(self.merge), ('pending', ''))

    def test_product_tasks_cannot_accept_preparation_artifact(self):
        def api(path, *args):
            if '/timeline?' in path:
                return {'records': [{'type': 'Stage', 'identifier': 'DeployPreparationMac8', 'result': 'succeeded'}]}
            if '/artifacts?' in path:
                return {'value': [{'name': 'atlas-preparation-release', 'resource': {'type': 'PipelineArtifact', 'data': 'actual-preparation-artifact'}}]}
            return {'value': [{'id': 9, 'sourceVersion': self.merge, 'status': 'completed', 'result': 'succeeded'}]}
        backend = AzureGateBackend(self.config, self.tmp.name, api)
        self.assertEqual(backend.successful_deployment(self.merge, require_product=True), ('failed', '9'))

    @mock.patch('azure_gate_backend.subprocess.run')
    def test_changed_pr_source_is_not_merged(self, run):
        run.return_value.returncode = 0
        calls = []
        def api(path, method='GET', body=None):
            calls.append(method)
            if 'searchCriteria' in path:
                return {'value': [{'pullRequestId': 3, 'lastMergeSourceCommit': {'commitId': self.sha}}]}
            return {'pullRequestId': 3, 'status': 'active', 'lastMergeSourceCommit': {'commitId': 'c' * 40}}
        result = AzureGateBackend(self.config, self.tmp.name, api).integrate({'taskId': 'T01', 'plannedBranch': 'task/T01'}, self.sha, self.tmp.name)
        self.assertEqual(result.status, 'failed')
        self.assertNotIn('PATCH', calls)

    @mock.patch('azure_gate_backend.subprocess.run')
    def test_required_policy_failure_cannot_merge(self, run):
        run.return_value.returncode = 0
        calls = []
        def api(path, method='GET', body=None):
            calls.append(method)
            if 'searchCriteria' in path:
                return {'value': [{'pullRequestId': 3, 'lastMergeSourceCommit': {'commitId': self.sha}}]}
            if '/policy/evaluations?' in path:
                return {'value': [{'status': 'rejected', 'configuration': {'isEnabled': True, 'isBlocking': True, 'settings': {'buildDefinitionId': 4}}}]}
            return {'pullRequestId': 3, 'status': 'active', 'lastMergeSourceCommit': {'commitId': self.sha}}
        result = AzureGateBackend(self.config, self.tmp.name, api).integrate({'taskId': 'T01', 'plannedBranch': 'task/T01'}, self.sha, self.tmp.name)
        self.assertEqual(result.status, 'failed')
        self.assertNotIn('PATCH', calls)

    @mock.patch('azure_gate_backend.subprocess.run')
    def test_only_policy_merge_and_deployment_produce_completion(self, run):
        run.return_value.returncode = 0
        merged = False
        def api(path, method='GET', body=None):
            nonlocal merged
            if 'searchCriteria' in path:
                return {'value': [{'pullRequestId': 3, 'lastMergeSourceCommit': {'commitId': self.sha}}]}
            if '/policy/evaluations?' in path:
                return {'value': [{'status': 'approved', 'configuration': {'isEnabled': True, 'isBlocking': True, 'settings': {'buildDefinitionId': 4}}}]}
            if method == 'PATCH':
                self.assertEqual(body['lastMergeSourceCommit']['commitId'], self.sha)
                merged = True
                return {}
            if '/timeline?' in path:
                return {'records': [{'type': 'Stage', 'identifier': 'DeployPreparationMac8', 'result': 'succeeded'}]}
            if '/build/builds?' in path:
                return {'value': [{'id': 9, 'sourceVersion': self.merge, 'status': 'completed', 'result': 'succeeded'}]}
            return {'pullRequestId': 3, 'status': 'completed' if merged else 'active', 'lastMergeSourceCommit': {'commitId': self.sha}, 'lastMergeCommit': {'commitId': self.merge}}
        result = AzureGateBackend(self.config, self.tmp.name, api).integrate({'taskId': 'T01', 'plannedBranch': 'task/T01'}, self.sha, self.tmp.name)
        self.assertEqual(result.status, 'merged')
        self.assertEqual(result.merge_sha, self.merge)
        self.assertEqual(result.deployment_id, '9')
        self.assertTrue((Path(self.tmp.name)/'azure-gates.json').is_file())

if __name__ == '__main__': unittest.main()
