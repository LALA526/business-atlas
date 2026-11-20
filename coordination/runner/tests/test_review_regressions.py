import json, os, sys, tempfile, urllib.error
from pathlib import Path
import unittest
from unittest import mock
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import atlas_runner as ar
from azure_gate_backend import AzureGateBackend
from test_atlas_runner import RunnerFixture, FakeAdapter, PASS_QA

class ReviewRegressionTests(RunnerFixture):
    def test_untracked_qa_poisoning_cannot_pass(self):
        default = FakeAdapter()
        def handler(role, prompt, cwd, count):
            if role == 'atlas-qa':
                (cwd/'uncommitted_test_dependency.py').write_text('poisoned=true')
                return ar.RoleResult(True, text=PASS_QA)
            return default.run(role, prompt, cwd, self.state, 1)
        runner = self.make_runner(adapter=FakeAdapter(handler))
        result = runner.run_once()
        self.assertEqual(result.get('category'), 'source_mutated')
        self.assertNotEqual(runner.ledger.get('T01')['phase'], 'done')

    def test_child_has_correct_pwd_and_no_integration_credentials(self):
        out, err = self.state/'child.out', self.state/'child.err'
        self.state.mkdir(exist_ok=True)
        command = [sys.executable, '-c', 'import os,json; print(json.dumps({"hasPat": "AZURE_DEVOPS_PAT" in os.environ, "hasAdo": "ADO_MCP_AUTH_TOKEN" in os.environ, "pwdMatches": os.environ.get("PWD")==os.getcwd()}))']
        with mock.patch.dict(os.environ, {'AZURE_DEVOPS_PAT': 'not-a-real-secret', 'ADO_MCP_AUTH_TOKEN': 'also-not-real', 'PWD': '/incorrect-parent'}):
            ar.ProcessSupervisor().execute(command, self.repo, 10, out, err)
        self.assertEqual(json.loads(out.read_text()), {'hasPat': False, 'hasAdo': False, 'pwdMatches': True})

    def test_legacy_backend_is_always_disabled(self):
        backend = ar.AzureIntegrationBackend({'organization':'x','project':'x','repositoryId':'x','requiredContexts':['x']})
        self.assertFalse(backend.preflight()['ready'])

    def test_unexpected_cli_server_error_has_bounded_retry_classification(self):
        category, retry = ar.classify_role_failure('Unexpected server error. Check server logs.')
        self.assertEqual(category, 'transient_provider')
        self.assertTrue(retry)

    @mock.patch('azure_gate_backend.subprocess.run')
    def test_api_credential_failure_is_pending_without_leased_crash(self, run):
        run.return_value.returncode = 0
        def api(*args):
            raise urllib.error.HTTPError('https://dev.azure.com/example', 403, 'Forbidden', {}, None)
        backend = AzureGateBackend({'project':'Business Atlas','repositoryId':'r'}, self.repo, api)
        result = backend.integrate({'taskId':'T01','plannedBranch':'task/T01'}, 'a'*40, self.state)
        self.assertEqual(result.status, 'pending')
        self.assertEqual(result.detail, 'blocked_credential')

if __name__ == '__main__': unittest.main()
