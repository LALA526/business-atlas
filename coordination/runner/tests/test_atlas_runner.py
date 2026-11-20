from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import atlas_runner as ar


PASS_REVIEW = json.dumps({"verdict": "pass", "findings": [], "evidence": ["diff reviewed"]})
PASS_QA = json.dumps({"verdict": "pass", "findings": [], "evidence": ["candidate inspected"]})


class FakeAdapter:
    def __init__(self, handler=None):
        self.handler = handler
        self.calls = []

    def run(self, role, prompt, cwd, log_dir, timeout_seconds):
        self.calls.append({"role": role, "prompt": prompt, "cwd": cwd, "timeout": timeout_seconds})
        if self.handler:
            return self.handler(role, prompt, cwd, len(self.calls))
        if role == "atlas-leader":
            return ar.RoleResult(True, text="persisted plan")
        if role == "atlas-builder":
            (cwd / "implementation.txt").write_text("implemented", encoding="utf-8")
            return ar.RoleResult(True, text="implemented with tests and knowledge candidate")
        if role == "atlas-reviewer":
            return ar.RoleResult(True, text=PASS_REVIEW)
        if role == "atlas-qa":
            return ar.RoleResult(True, text=PASS_QA)
        raise AssertionError(role)


class SequenceAdapter:
    def __init__(self, results):
        self.results = list(results)
        self.calls = 0

    def run(self, role, prompt, cwd, log_dir, timeout_seconds):
        self.calls += 1
        return self.results.pop(0)


class FakeIntegration:
    def __init__(self, statuses=None):
        self.statuses = list(statuses or ["merged"])
        self.calls = []

    def preflight(self):
        return {"ready": True, "requiredCheckCount": 1}

    def integrate(self, task, candidate_sha, evidence_dir):
        self.calls.append((task["taskId"], candidate_sha))
        status = self.statuses.pop(0) if self.statuses else self.statuses[-1]
        if status == "merged":
            return ar.IntegrationResult(
                "merged", candidate_sha, merge_sha="m" * 40,
                deployment_id="deployment-1", pr_id="17",
            )
        return ar.IntegrationResult(status, candidate_sha, detail=status)


class RunnerFixture(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="atlas-runner-test-")
        self.addCleanup(self.temporary.cleanup)
        root = Path(self.temporary.name)
        self.repo = root / "repo"
        self.state = root / "state"
        self.baseline = self.repo / "docs" / "baselines" / "T1.0"
        self.baseline.mkdir(parents=True)
        (self.baseline / "README.md").write_text("test baseline\n", encoding="utf-8")
        (self.baseline / "contract.md").write_text("frozen contract\n", encoding="utf-8")
        self.dag_path = self.baseline / "03_开发执行" / "task-dag.json"
        self.dag_path.parent.mkdir(parents=True)
        self.dag = {
            "schemaVersion": "1.0.0",
            "project": "test",
            "status": "planned",
            "tasks": [self.task("T01", []), self.task("T02", ["T01"])],
        }
        self.dag_path.write_text(json.dumps(self.dag, indent=2), encoding="utf-8")
        self.policy_path = self.repo / "coordination" / "runner" / "policy.json"
        self.policy_path.parent.mkdir(parents=True)
        self.write_policy()
        ar.git(self.repo, "init", "-b", "main")
        ar.git(self.repo, "config", "user.name", "Runner Test")
        ar.git(self.repo, "config", "user.email", "runner@example.invalid")
        ar.git(self.repo, "add", "-A")
        ar.git(self.repo, "commit", "-m", "test base")
        self.base_sha = ar.git_head(self.repo)

    @staticmethod
    def task(task_id, dependencies):
        return {
            "taskId": task_id,
            "title": "Task " + task_id,
            "ownerRole": "Owner",
            "modules": ["M"],
            "dependsOn": dependencies,
            "repoPaths": ["implementation.txt"],
            "implementation": "implement",
            "acceptance": "verified",
            "testIds": ["AT-" + task_id],
            "productIds": [],
            "workpackageIds": [],
            "resourceLocks": [],
            "requiredOutputs": [],
            "status": "planned",
            "plannedBranch": "task/" + task_id,
            "actualEvidence": None,
            "requirementIds": [],
            "knowledgeOutputs": [],
            "contractRefs": ["contract.md"],
            "operationIds": [],
        }

    def write_policy(self, include_task=True):
        verification = (
            "from pathlib import Path; "
            "assert Path('implementation.txt').read_text(encoding='utf-8') == 'implemented'"
        )
        document = {
            "version": 1,
            "productVerify": "scripts/ci/verify",
            "taskVerify": {"T01": [sys.executable, "-c", verification]} if include_task else {},
        }
        self.policy_path.write_text(json.dumps(document), encoding="utf-8")

    def controls(self, **updates):
        controls = {
            "enabled": False,
            "maxRetries": 2,
            "timeoutSeconds": 30,
            "leaseSeconds": 100,
            "retryBackoffSeconds": 0,
            "loopPollSeconds": 0,
            "maxLoopIterations": 1,
            "maxConcurrentTasks": 1,
            "policyPath": "coordination/runner/policy.json",
            "worktreeRoot": str(self.state / "worktrees"),
            "evidenceRoot": str(self.state / "evidence"),
            "model": {"id": ar.DEFAULT_MODEL, "verified": False},
            "integration": {},
        }
        controls.update(updates)
        return controls

    def make_runner(self, adapter=None, integration=None, **control_updates):
        runner = ar.AtlasRunner(
            self.repo,
            self.baseline,
            self.state,
            self.controls(**control_updates),
            adapter or FakeAdapter(),
            integration or FakeIntegration(),
            sleep=lambda _seconds: None,
        )
        self.addCleanup(runner.close)
        runner.ledger.initialize(self.dag_path, self.baseline, 100)
        return runner


class DagAndLedgerTests(RunnerFixture):
    def test_dag_ordering_and_original_bytes(self):
        original = self.dag_path.read_bytes()
        runner = self.make_runner()
        first = runner.ledger.claim("one", 100, 50, 1, self.base_sha, self.state / "evidence", lambda row: None)
        self.assertEqual(first["task_id"], "T01")
        self.assertEqual(runner.ledger.get("T02")["attempt"], 0)
        self.assertEqual(self.dag_path.read_bytes(), original)
        repeated = runner.ledger.initialize(self.dag_path, self.baseline, 101)
        self.assertTrue(repeated["unchanged"])
        self.assertEqual(len(runner.status()["tasks"]), 2)

    def test_dag_rejects_cycles_and_unknown_dependencies(self):
        document, _, _ = ar.load_dag(self.dag_path)
        document["tasks"][0]["dependsOn"] = ["T02"]
        self.dag_path.write_text(json.dumps(document), encoding="utf-8")
        with self.assertRaises(ar.RunnerError) as caught:
            ar.load_dag(self.dag_path)
        self.assertEqual(caught.exception.category, "dag_cycle")
        document["tasks"][0]["dependsOn"] = ["T99"]
        self.dag_path.write_text(json.dumps(document), encoding="utf-8")
        with self.assertRaises(ar.RunnerError):
            ar.load_dag(self.dag_path)

    def test_lease_contention_and_expired_lease_fencing(self):
        runner = self.make_runner()
        other = ar.Ledger(self.state)
        self.addCleanup(other.close)
        first = runner.ledger.claim("owner-one", 100, 10, 1, self.base_sha, self.state / "evidence", lambda row: None)
        self.assertIsNotNone(first)
        contested = other.claim("owner-two", 105, 10, 1, self.base_sha, self.state / "evidence", lambda row: None)
        self.assertIsNone(contested)
        reclaimed = other.claim("owner-two", 111, 10, 1, self.base_sha, self.state / "evidence", lambda row: None)
        self.assertEqual(reclaimed["task_id"], "T01")
        self.assertEqual(reclaimed["fence"], first["fence"] + 1)
        with self.assertRaises(ar.RunnerError) as caught:
            runner.ledger.accept_stage(
                "T01", "leader", "developer", self.state / "old-plan",
                "owner-one", first["fence"], 112,
            )
        self.assertEqual(caught.exception.category, "lease_fenced")
        other.accept_stage(
            "T01", "leader", "developer", self.state / "new-plan",
            "owner-two", reclaimed["fence"], 112,
        )
        self.assertEqual(other.get("T01")["phase"], "developer")

    def test_expired_lease_requires_candidate_git_reconciliation(self):
        runner = self.make_runner()
        row = runner.ledger.claim("owner", 100, 10, 1, self.base_sha, self.state / "evidence", lambda row: None)
        with runner.ledger.connection:
            runner.ledger.connection.execute(
                "UPDATE tasks SET phase='review',candidate_sha=?,lease_expires_at=? WHERE task_id='T01'",
                ("f" * 40, 105),
            )
        result = runner.run_once()
        self.assertEqual(result["status"], "recovery_blocked")
        self.assertNotEqual(runner.ledger.get("T01")["phase"], "done")
        self.assertEqual(row["task_id"], "T01")

    def test_process_lock_is_single_writer(self):
        first = ar.ProcessLock(self.state / "runner.lock")
        self.addCleanup(first.release)
        with self.assertRaises(ar.RunnerError) as caught:
            ar.ProcessLock(self.state / "runner.lock")
        self.assertEqual(caught.exception.category, "writer_locked")


class OpenCodeProtocolTests(RunnerFixture):
    def test_jsonl_text_and_error_detection(self):
        output = "\n".join([
            json.dumps({"type": "step_start", "part": {"id": "1"}}),
            json.dumps({"type": "text", "part": {"text": "hello"}}),
            json.dumps({"type": "text", "part": {"text": " world"}}),
        ])
        self.assertEqual(ar.parse_opencode_jsonl(output), "hello world")
        with self.assertRaises(ar.RunnerError) as caught:
            ar.parse_opencode_jsonl('{"type":"text","part":{"text":"partial"}}\n{"type":"error","error":"bad"}')
        self.assertEqual(caught.exception.category, "model_error")

    def test_empty_and_malformed_output_fail(self):
        for output in ("", "not-json\n", '{"type":"text","part":{}}\n'):
            with self.subTest(output=output), self.assertRaises(ar.RunnerError):
                ar.parse_opencode_jsonl(output)

    def test_quota_and_credentials_have_no_blind_retry(self):
        for category in ("blocked_quota", "blocked_credential"):
            adapter = SequenceAdapter([ar.RoleResult(False, category=category, retryable=False)])
            # Use a dedicated runner to inject the deterministic test-only adapter.
            test_runner = self.make_runner(adapter=adapter)
            result = test_runner.invoke_role("atlas-leader", "prompt", self.repo, self.state, 1)
            self.assertFalse(result.ok)
            self.assertEqual(result.category, category)
            self.assertEqual(adapter.calls, 1)

    def test_bounded_transient_retries(self):
        adapter = SequenceAdapter([
            ar.RoleResult(False, category="transient_provider", retryable=True),
            ar.RoleResult(False, category="transient_provider", retryable=True),
            ar.RoleResult(True, text="ok"),
        ])
        runner = self.make_runner(adapter=adapter)
        result = runner.invoke_role("atlas-leader", "prompt", self.repo, self.state, 1)
        self.assertTrue(result.ok)
        self.assertEqual(adapter.calls, 3)
        exhausted = SequenceAdapter([
            ar.RoleResult(False, category="transient_provider", retryable=True),
            ar.RoleResult(False, category="transient_provider", retryable=True),
            ar.RoleResult(False, category="transient_provider", retryable=True),
            ar.RoleResult(True, text="must not run"),
        ])
        runner = self.make_runner(adapter=exhausted)
        self.assertFalse(runner.invoke_role("atlas-leader", "prompt", self.repo, self.state, 1).ok)
        self.assertEqual(exhausted.calls, 3)

    def test_failure_classification(self):
        self.assertEqual(ar.classify_role_failure("HTTP 402 payment required")[0], "blocked_quota")
        self.assertEqual(ar.classify_role_failure("HTTP 401")[0], "blocked_credential")
        self.assertEqual(ar.classify_role_failure("HTTP 503"), ("transient_provider", True))
        self.assertEqual(ar.classify_role_failure("HTTP 429"), ("transient_provider", True))

    def test_real_command_is_explicit_and_never_continue(self):
        adapter = ar.OpenCodeAdapter()
        command = adapter.command("atlas-builder", "do work")
        self.assertEqual(command[:8], [
            "opencode", "run", "--format", "json", "--agent", "atlas-builder", "-m", ar.DEFAULT_MODEL,
        ])
        self.assertNotIn("--continue", command)
        self.assertEqual(command[-1], "do work")

    def test_plain_self_claim_is_not_a_verdict(self):
        runner = self.make_runner()
        for text in ("done", "```json\n{}\n```", '{"verdict":"pass","findings":[],"evidence":[]}'):
            with self.subTest(text=text), self.assertRaises(ar.RunnerError):
                runner.parse_verdict(text)


class WorkflowTests(RunnerFixture):
    def test_successful_flow_has_independent_roles_and_real_completion_gate(self):
        adapter = FakeAdapter()
        integration = FakeIntegration()
        runner = self.make_runner(adapter=adapter, integration=integration)
        result = runner.run_once()
        self.assertEqual(result["status"], "done", result)
        self.assertEqual([call["role"] for call in adapter.calls], [
            "atlas-leader", "atlas-builder", "atlas-reviewer", "atlas-qa",
        ])
        row = runner.ledger.get("T01")
        self.assertEqual(row["phase"], "done")
        self.assertNotEqual(row["candidate_sha"], self.base_sha)
        self.assertEqual(integration.calls[0][1], row["candidate_sha"])
        self.assertEqual(json.loads(self.dag_path.read_text())["tasks"][0]["status"], "planned")
        self.assertTrue((Path(row["evidence_dir"]) / "leader-plan.md").is_file())
        self.assertTrue((Path(row["evidence_dir"]) / "review-verdict.json").is_file())
        self.assertTrue((Path(row["evidence_dir"]) / "verification-result.json").is_file())

    def test_review_rejection_returns_to_developer_without_self_approval(self):
        reviews = 0

        def handler(role, prompt, cwd, serial):
            nonlocal reviews
            if role == "atlas-reviewer":
                reviews += 1
                if reviews == 1:
                    return ar.RoleResult(True, text=json.dumps({
                        "verdict": "needs_changes", "findings": ["missing negative test"], "evidence": ["diff"],
                    }))
                return ar.RoleResult(True, text=PASS_REVIEW)
            return FakeAdapter().run(role, prompt, cwd, self.state, 1)

        adapter = FakeAdapter(handler)
        runner = self.make_runner(adapter=adapter)
        first = runner.run_once()
        self.assertEqual(first["status"], "needs_changes")
        self.assertEqual(runner.ledger.get("T01")["phase"], "developer")
        second = runner.run_once()
        self.assertEqual(second["status"], "done", second)
        self.assertEqual(reviews, 2)
        developers = [call for call in adapter.calls if call["role"] == "atlas-builder"]
        self.assertEqual(len(developers), 2)
        self.assertIn("missing negative test", developers[1]["prompt"])

    def test_missing_verification_gate_blocks_completion(self):
        self.write_policy(include_task=False)
        ar.git(self.repo, "add", "-A")
        ar.git(self.repo, "commit", "-m", "remove task verification")
        self.base_sha = ar.git_head(self.repo)
        integration = FakeIntegration()
        runner = self.make_runner(integration=integration)
        result = runner.run_once()
        self.assertEqual(result["status"], "failed")
        self.assertEqual(result["category"], "verification_missing")
        self.assertNotEqual(runner.ledger.get("T01")["phase"], "done")
        self.assertEqual(integration.calls, [])

    def test_qa_source_change_rejects_candidate(self):
        def handler(role, prompt, cwd, serial):
            if role == "atlas-qa":
                (cwd / "implementation.txt").write_text("tampered", encoding="utf-8")
                return ar.RoleResult(True, text=PASS_QA)
            return FakeAdapter().run(role, prompt, cwd, self.state, 1)

        runner = self.make_runner(adapter=FakeAdapter(handler))
        result = runner.run_once()
        self.assertEqual(result["category"], "source_mutated")
        self.assertNotEqual(runner.ledger.get("T01")["phase"], "done")

    def test_review_source_change_rejects_candidate(self):
        def handler(role, prompt, cwd, serial):
            if role == "atlas-reviewer":
                (cwd / "implementation.txt").write_text("tampered", encoding="utf-8")
                return ar.RoleResult(True, text=PASS_REVIEW)
            return FakeAdapter().run(role, prompt, cwd, self.state, 1)

        runner = self.make_runner(adapter=FakeAdapter(handler))
        result = runner.run_once()
        self.assertEqual(result["category"], "source_mutated")
        self.assertNotEqual(runner.ledger.get("T01")["phase"], "done")

    def test_restart_resumes_without_rerunning_accepted_leader(self):
        first_adapter = SequenceAdapter([
            ar.RoleResult(True, text="plan"),
            ar.RoleResult(False, category="model_error", retryable=False),
        ])
        first = self.make_runner(adapter=first_adapter)
        failed = first.run_once()
        self.assertEqual(failed["status"], "failed")
        self.assertEqual(first.ledger.get("T01")["phase"], "developer")
        second_adapter = FakeAdapter()
        second = self.make_runner(adapter=second_adapter)
        result = second.run_once()
        self.assertEqual(result["status"], "done", result)
        self.assertNotIn("atlas-leader", [call["role"] for call in second_adapter.calls])
        self.assertEqual(second.ledger.get("T01")["attempt"], 2)

    def test_pending_integration_resumes_only_integration(self):
        integration = FakeIntegration(["pending", "merged"])
        first_adapter = FakeAdapter()
        first = self.make_runner(adapter=first_adapter, integration=integration)
        result = first.run_once()
        self.assertEqual(result["status"], "pending_integration")
        self.assertNotEqual(first.ledger.get("T01")["phase"], "done")
        second_adapter = FakeAdapter()
        second = self.make_runner(adapter=second_adapter, integration=integration)
        result = second.run_once()
        self.assertEqual(result["status"], "done", result)
        self.assertEqual(second_adapter.calls, [])
        self.assertEqual(len(integration.calls), 2)

    def test_merged_without_deployment_cannot_complete(self):
        class MissingDeployment(FakeIntegration):
            def integrate(self, task, candidate_sha, evidence_dir):
                return ar.IntegrationResult(
                    "merged", candidate_sha, merge_sha="m" * 40,
                    deployment_id="", pr_id="17",
                )

        runner = self.make_runner(integration=MissingDeployment())
        result = runner.run_once()
        self.assertEqual(result["category"], "completion_gate")
        self.assertNotEqual(runner.ledger.get("T01")["phase"], "done")

    def test_stop_checkpoints_without_repeating_accepted_stage(self):
        runner_holder = {}

        def handler(role, prompt, cwd, serial):
            if role == "atlas-leader":
                runner_holder["runner"].stop_event.set()
            return FakeAdapter().run(role, prompt, cwd, self.state, 1)

        adapter = FakeAdapter(handler)
        runner = self.make_runner(adapter=adapter)
        runner_holder["runner"] = runner
        result = runner.run_once()
        self.assertEqual(result["status"], "stopped")
        self.assertEqual(runner.ledger.get("T01")["phase"], "developer")
        self.assertIsNotNone(runner.ledger.latest_accepted("T01", "leader"))

    def test_continuous_loop_is_disabled_by_default(self):
        adapter = FakeAdapter()
        runner = self.make_runner(adapter=adapter)
        self.assertEqual(runner.loop()["status"], "disabled")
        self.assertEqual(adapter.calls, [])


class PreflightTests(RunnerFixture):
    def test_uncommitted_git_base_and_unverified_model_are_not_green(self):
        empty = Path(self.temporary.name) / "empty"
        empty.mkdir()
        ar.git(empty, "init", "-b", "main")
        runner = ar.AtlasRunner(
            empty, self.baseline, Path(self.temporary.name) / "empty-state",
            self.controls(), FakeAdapter(), FakeIntegration(), sleep=lambda _seconds: None,
        )
        self.addCleanup(runner.close)
        runner.ledger.initialize(self.dag_path, self.baseline, 100)
        manifest = runner.preflight()
        self.assertFalse(manifest["ready"])
        self.assertFalse(manifest["checks"]["git"]["ready"])
        self.assertFalse(manifest["checks"]["model"]["ready"])
        self.assertFalse(manifest["checks"]["credentials"]["valuesIncluded"])

    def test_real_azure_preflight_requires_checks_credentials_and_https_deployment(self):
        backend = ar.AzureIntegrationBackend({})
        with mock.patch.dict(os.environ, {}, clear=True):
            result = backend.preflight()
        self.assertFalse(result["ready"])
        self.assertIn("requiredContexts", result["missing"])
        self.assertFalse(result["credentialPresent"])


if __name__ == "__main__":
    unittest.main()
