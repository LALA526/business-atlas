from __future__ import annotations

import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest

import delivery


class ProposalTests(unittest.TestCase):
    def setUp(self) -> None:
        source = delivery.ROOT / "global.json"
        self.document = {
            "schemaVersion": "1.3.0", "proposalId": "KCP-T02-TEST", "projectId": "PRJ-SELF", "taskId": "T02",
            "requirementIds": ["ENG-ARCH-001", "ENG-ARCH-002", "ENG-DEL-003", "PD-37"],
            "changeKind": "feature", "repo": {"url": "https://example.invalid/repo.git", "branch": "task/T02", "pullRequestId": "123", "baseSha": "a" * 40},
            "changedFiles": [{"path": "global.json", "changeType": "modified", "contentSha256": hashlib.sha256(source.read_bytes()).hexdigest()}],
            "entityChanges": [], "assertionChanges": [], "apiChanges": [], "dataChanges": [],
            "testEvidence": [{"testId": "AT-T02", "result": "passed", "artifactUri": "ci://test/logs"}],
            "sourceAnchors": [{"path": "global.json", "locator": "sdk.version", "contentSha256": hashlib.sha256(source.read_bytes()).hexdigest()}],
            "knowledgeStatus": "proposed", "unknowns": ["PR does not exist in this unit test"], "errorsObserved": [],
        }

    def write(self, directory: Path, document: dict) -> Path:
        path = directory / "proposal.json"
        path.write_text(json.dumps(document), encoding="utf-8")
        return path

    def manifest_inputs(self, root: Path) -> dict:
        verification = root / "downloaded-verification"
        verification.mkdir(exist_ok=True)
        (verification / "verify.log").write_text("AT-T02 verification passed\n", encoding="utf-8")
        attestation = root / "candidate-build-attestation.json"
        attestation.write_text(json.dumps({
            "schemaVersion": "1.0.0", "verifiedBy": "Azure DevOps Builds API",
            "candidateBuildId": "candidate-run", "candidateSha": "b" * 40,
            "pullRequestId": "123", "sourceBranch": "task/T02", "reason": "pullRequest",
            "status": "completed", "result": "succeeded", "responseSha256": "a" * 64,
        }), encoding="utf-8")
        return dict(
            candidate_sha="b" * 40, merge_sha="c" * 40, merge_strategy="merge",
            repo_url=self.document["repo"]["url"], branch=self.document["repo"]["branch"], pull_request_id=self.document["repo"]["pullRequestId"],
            build_id="run-1", build_digest=delivery.directory_digest(verification),
            ci_run_id="run-1", artifact_uri="ci://run-1/manifest",
            verification_root=verification, verification_artifact_uri="ci://candidate-run/at-t02-verification-candidate-run",
            candidate_build_id="candidate-run", post_merge_build_id="run-1", attestation_path=attestation,
        )

    def test_valid_v13(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = self.write(Path(tmp), self.document)
            document, raw = delivery.parse(path)
            self.assertEqual("1.3.0", delivery.validate_schema(document))
            self.assertEqual(raw, path.read_bytes())

    def test_legacy_v12_remains_legacy(self) -> None:
        document, _ = delivery.parse(delivery.ROOT / "contracts/examples/knowledge-change-proposal.example.json")
        self.assertEqual("1.2.0", delivery.validate_schema(document))
        self.assertIn("headSha", document["repo"])

    def test_unknown_approval_and_head_sha_are_rejected(self) -> None:
        for key, value in [("approval", {"kind": "human"}), ("headSha", "b" * 40)]:
            with self.subTest(key=key), tempfile.TemporaryDirectory() as tmp:
                candidate = copy.deepcopy(self.document)
                if key == "headSha": candidate["repo"][key] = value
                else: candidate[key] = value
                document, _ = delivery.parse(self.write(Path(tmp), candidate))
                with self.assertRaises(delivery.DeliveryError): delivery.validate_schema(document)

    def test_tampered_source_and_path_traversal_are_rejected(self) -> None:
        with self.assertRaises(delivery.DeliveryError): delivery.safe_path("../outside")
        candidate = copy.deepcopy(self.document)
        candidate["sourceAnchors"][0]["contentSha256"] = "0" * 64
        # Schema accepts a well-formed lie; candidate Git validation is responsible for rejecting it.
        self.assertEqual("1.3.0", delivery.validate_schema(candidate))

    def test_manifest_concurrency_idempotency_and_conflict_recovery(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            proposal = self.write(root, self.document)
            common = self.manifest_inputs(root)
            output = root / "run-1"
            errors: list[Exception] = []
            def create() -> None:
                try: delivery.create_manifest(proposal, output, **common)
                except Exception as exc: errors.append(exc)
            threads = [threading.Thread(target=create) for _ in range(2)]
            for thread in threads: thread.start()
            for thread in threads: thread.join()
            self.assertEqual([], errors)
            first = (output / "knowledge-delivery-manifest-v1.3.json").read_bytes()
            delivery.create_manifest(proposal, output, **common)
            self.assertEqual(first, (output / "knowledge-delivery-manifest-v1.3.json").read_bytes())
            with self.assertRaises(delivery.DeliveryError):
                delivery.create_manifest(proposal, output, **{**common, "merge_sha": "e" * 40})
            other = root / "run-2"
            delivery.create_manifest(proposal, other, **{**common, "merge_sha": "e" * 40, "build_id": "run-2", "ci_run_id": "run-2", "post_merge_build_id": "run-2"})
            self.assertNotEqual(first, (other / "knowledge-delivery-manifest-v1.3.json").read_bytes())

    def test_manifest_recovers_after_interrupted_second_write(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            proposal = self.write(root, self.document)
            common = self.manifest_inputs(root)
            output = root / "run-1"
            delivery.create_manifest(proposal, output, **common)
            (output / "candidate-binding.json").unlink()
            delivery.create_manifest(proposal, output, **common)
            self.assertTrue((output / "candidate-binding.json").is_file())
            (output / "knowledge-delivery-manifest-v1.3.json").unlink()
            delivery.create_manifest(proposal, output, **common)
            self.assertTrue((output / "knowledge-delivery-manifest-v1.3.json").is_file())

    def test_candidate_git_scope_and_only_changed_proposals(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            def run(*args: str) -> str:
                return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True, text=True).stdout.strip()
            run("init", "--quiet")
            run("config", "user.name", "AT-T02 Test")
            run("config", "user.email", "at-t02@example.invalid")
            (root / "knowledge/proposals").mkdir(parents=True)
            (root / "docs/decisions").mkdir(parents=True)
            (root / "docs/baselines/T1.0/03_开发执行").mkdir(parents=True)
            (root / "contracts").mkdir()
            dag_bytes = (json.dumps({"tasks": [{"taskId": "T02", "requirementIds": self.document["requirementIds"]}]}) + "\n").encode()
            (root / "docs/baselines/T1.0/03_开发执行/task-dag.json").write_bytes(dag_bytes)
            contract_bytes = b"frozen contract\n"
            (root / "contracts/README.md").write_bytes(contract_bytes)
            contract_hash = hashlib.sha256(contract_bytes).hexdigest()
            tree_digest = hashlib.sha256(b"README.md\0" + contract_hash.encode("ascii") + b"\n").hexdigest()
            (root / "contracts/manifest.json").write_text(json.dumps({"fileCount": 1, "treeSha256": tree_digest, "files": [{"path": "README.md", "sha256": contract_hash}]}), encoding="utf-8")
            (root / "docs/decisions/T01-contract-freeze.json").write_text(json.dumps({"inputDagSha256": hashlib.sha256(dag_bytes).hexdigest(), "machineContractFreeze": {"treeSha256": tree_digest}}), encoding="utf-8")
            self.document["contractDigests"] = [f"sha256:{tree_digest}"]
            (root / "knowledge/proposals/old.json").write_text("{}", encoding="utf-8")
            (root / "global.json").write_text('{"sdk":{"version":"10.0.404"}}\n', encoding="utf-8")
            run("add", ".")
            run("commit", "--quiet", "-m", "base")
            base = run("rev-parse", "HEAD")
            (root / "global.json").write_text('{"sdk":{"version":"10.0.405"}}\n', encoding="utf-8")
            (root / "knowledge/proposals/new.json").write_text("{}", encoding="utf-8")
            run("add", ".")
            run("commit", "--quiet", "-m", "candidate")
            candidate_sha = run("rev-parse", "HEAD")
            self.assertEqual([root / "knowledge/proposals/new.json"], delivery.changed_proposals(root, base, candidate_sha))
            candidate = copy.deepcopy(self.document)
            candidate["repo"]["baseSha"] = base
            candidate["changedFiles"] = [{"path": "global.json", "changeType": "modified", "contentSha256": hashlib.sha256((root / "global.json").read_bytes()).hexdigest()}]
            candidate["sourceAnchors"] = [{"path": "global.json", "locator": "sdk.version", "contentSha256": hashlib.sha256((root / "global.json").read_bytes()).hexdigest()}]
            common = dict(root=root, task_id="T02", requirements=set(self.document["requirementIds"]), repo_url=self.document["repo"]["url"], branch="task/T02", pull_request_id="123", base_sha=base, candidate_sha=candidate_sha)
            delivery.validate_candidate(candidate, b"{}", **common)
            for key, value in [("task_id", "T03"), ("requirements", {"PD-37"}), ("repo_url", "https://example.invalid/other.git"), ("branch", "task/other"), ("pull_request_id", "456"), ("pull_request_id", "PENDING"), ("pull_request_id", " pending ")]:
                with self.subTest(key=key), self.assertRaises(delivery.DeliveryError):
                    delivery.validate_candidate(candidate, b"{}", **{**common, key: value})
            for digests in [None, [], ["sha256:" + "0" * 64], candidate["contractDigests"] * 2]:
                with self.subTest(contractDigests=digests), self.assertRaisesRegex(delivery.DeliveryError, "contractDigests"):
                    invalid = copy.deepcopy(candidate)
                    if digests is None: invalid.pop("contractDigests")
                    else: invalid["contractDigests"] = digests
                    delivery.validate_candidate(invalid, b"{}", **common)
            unknown_task = copy.deepcopy(candidate)
            unknown_task["taskId"] = "T99"
            with self.assertRaisesRegex(delivery.DeliveryError, "controlled task"):
                delivery.validate_candidate(unknown_task, b"{}", **{**common, "task_id": "T99"})
            unknown_requirement = copy.deepcopy(candidate)
            unknown_requirement["requirementIds"] = ["ENG-ARCH-999"]
            with self.assertRaisesRegex(delivery.DeliveryError, "controlled task trace"):
                delivery.validate_candidate(unknown_requirement, b"{}", **{**common, "requirements": {"ENG-ARCH-999"}})
            tampered = copy.deepcopy(candidate)
            tampered["changedFiles"][0]["contentSha256"] = "0" * 64
            with self.assertRaises(delivery.DeliveryError): delivery.validate_candidate(tampered, b"{}", **common)

    def test_normal_merge_discovers_candidate_against_target_parent(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            def run(*args: str) -> str:
                return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True, text=True).stdout.strip()
            run("init", "--quiet")
            run("config", "user.name", "AT-T02 Test")
            run("config", "user.email", "at-t02@example.invalid")
            (root / "knowledge/proposals").mkdir(parents=True)
            (root / "global.json").write_text("base\n", encoding="utf-8")
            run("add", ".")
            run("commit", "--quiet", "-m", "base")
            base = run("rev-parse", "HEAD")
            run("checkout", "--quiet", "-b", "candidate")
            (root / "global.json").write_text("candidate\n", encoding="utf-8")
            (root / "knowledge/proposals/new.json").write_text("{}\n", encoding="utf-8")
            run("add", ".")
            run("commit", "--quiet", "-m", "candidate")
            candidate_sha = run("rev-parse", "HEAD")
            run("checkout", "--quiet", "-b", "target", base)
            (root / "target.txt").write_text("target\n", encoding="utf-8")
            run("add", ".")
            run("commit", "--quiet", "-m", "target")
            run("merge", "--quiet", "--no-ff", "candidate", "-m", "merge")
            merge_sha = run("rev-parse", "HEAD")
            self.assertEqual("", run("diff", "--name-only", candidate_sha, merge_sha, "--", "knowledge/proposals/new.json"))
            self.assertEqual([root / "knowledge/proposals/new.json"], delivery.merged_proposals(root, candidate_sha, merge_sha, "merge", base))
            with self.assertRaisesRegex(delivery.DeliveryError, "exactly one target parent"):
                delivery.merged_proposals(root, candidate_sha, merge_sha, "squash", base)

    def test_squash_and_rebase_discover_final_bytes_without_candidate_ancestry(self) -> None:
        for strategy in ["squash", "rebase"]:
            with self.subTest(strategy=strategy), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                def run(*args: str) -> str:
                    return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True, text=True).stdout.strip()
                run("init", "--quiet")
                run("config", "user.name", "AT-T02 Test")
                run("config", "user.email", "at-t02@example.invalid")
                (root / "knowledge/proposals").mkdir(parents=True)
                (root / "global.json").write_text("base\n", encoding="utf-8")
                run("add", ".")
                run("commit", "--quiet", "-m", "base")
                base = run("rev-parse", "HEAD")
                run("checkout", "--quiet", "-b", "candidate")
                (root / "global.json").write_text("candidate\n", encoding="utf-8")
                (root / "knowledge/proposals/new.json").write_text("{}\n", encoding="utf-8")
                run("add", ".")
                run("commit", "--quiet", "-m", "candidate")
                candidate_sha = run("rev-parse", "HEAD")
                run("checkout", "--quiet", "-b", "target", base)
                (root / "target.txt").write_text("target\n", encoding="utf-8")
                run("add", ".")
                run("commit", "--quiet", "-m", "target")
                if strategy == "squash":
                    run("merge", "--quiet", "--squash", "candidate")
                    run("commit", "--quiet", "-m", "squash candidate")
                else:
                    run("checkout", "--quiet", "candidate")
                    run("rebase", "--quiet", "target")
                merge_sha = run("rev-parse", "HEAD")
                ancestry = subprocess.run(["git", "-C", str(root), "merge-base", "--is-ancestor", candidate_sha, merge_sha], check=False).returncode
                self.assertNotEqual(0, ancestry)
                self.assertEqual([root / "knowledge/proposals/new.json"], delivery.merged_proposals(root, candidate_sha, merge_sha, strategy, base))
                with self.assertRaisesRegex(delivery.DeliveryError, "requires exactly one target"):
                    delivery.merged_proposals(root, candidate_sha, merge_sha, "merge", base)

    def test_multicommit_rebase_binds_early_proposal_and_complete_patch(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            def run(*args):
                return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True, text=True).stdout.strip()
            run("init", "--quiet")
            run("config", "user.name", "Test")
            run("config", "user.email", "test@example.invalid")
            (root / "base.txt").write_text("base\n")
            run("add", "."); run("commit", "--quiet", "-m", "base")
            base = run("rev-parse", "HEAD")
            run("checkout", "--quiet", "-b", "candidate")
            (root / "knowledge/proposals").mkdir(parents=True)
            proposal = root / "knowledge/proposals/early.json"
            proposal.write_text("{}\n")
            run("add", "."); run("commit", "--quiet", "-m", "early proposal")
            (root / "implementation.bin").write_bytes(b"\0complete candidate\xff")
            run("add", "."); run("commit", "--quiet", "-m", "later implementation")
            candidate = run("rev-parse", "HEAD")
            run("branch", "reviewed", candidate)
            run("checkout", "--quiet", "-b", "target", base)
            (root / "target.txt").write_text("unrelated target advance\n")
            run("add", "."); run("commit", "--quiet", "-m", "target advance")
            run("checkout", "--quiet", "candidate"); run("rebase", "--quiet", "target")
            merged = run("rev-parse", "HEAD")
            self.assertEqual([proposal], delivery.merged_proposals(root, candidate, merged, "rebase", base))
            # An extra delivered change must not be attributed to the reviewed SHA.
            (root / "unreviewed.txt").write_text("not reviewed\n")
            run("add", "."); run("commit", "--quiet", "-m", "unreviewed")
            with self.assertRaises(delivery.DeliveryError):
                delivery.merged_proposals(root, candidate, run("rev-parse", "HEAD"), "rebase", base)

    def test_candidate_from_another_pr_or_build_cannot_bind_to_current_delivery(self) -> None:
        for strategy in ["merge", "squash", "rebase"]:
            with self.subTest(strategy=strategy), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                def run(*args: str) -> str:
                    return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True, text=True).stdout.strip()
                run("init", "--quiet")
                run("config", "user.name", "AT-T02 Test")
                run("config", "user.email", "at-t02@example.invalid")
                (root / "knowledge/proposals").mkdir(parents=True)
                (root / "global.json").write_text("base\n", encoding="utf-8")
                run("add", ".")
                run("commit", "--quiet", "-m", "base")
                base = run("rev-parse", "HEAD")
                run("checkout", "--quiet", "-b", "delivered-pr", base)
                (root / "global.json").write_text("delivered PR\n", encoding="utf-8")
                (root / "knowledge/proposals/delivered.json").write_text("{}\n", encoding="utf-8")
                run("add", ".")
                run("commit", "--quiet", "-m", "delivered candidate")
                delivered_candidate = run("rev-parse", "HEAD")
                run("checkout", "--quiet", "-b", "unrelated-pr", base)
                (root / "knowledge/proposals").mkdir(parents=True)
                (root / "unrelated.txt").write_text("another PR/build\n", encoding="utf-8")
                (root / "knowledge/proposals/unrelated.json").write_text("{}\n", encoding="utf-8")
                run("add", ".")
                run("commit", "--quiet", "-m", "unrelated candidate")
                unrelated_candidate = run("rev-parse", "HEAD")
                run("checkout", "--quiet", "-b", "target", base)
                (root / "target.txt").write_text("target\n", encoding="utf-8")
                run("add", ".")
                run("commit", "--quiet", "-m", "target")
                if strategy == "merge":
                    run("merge", "--quiet", "--no-ff", "delivered-pr", "-m", "merge delivered PR")
                elif strategy == "squash":
                    run("merge", "--quiet", "--squash", "delivered-pr")
                    run("commit", "--quiet", "-m", "squash delivered PR")
                else:
                    run("checkout", "--quiet", "delivered-pr")
                    run("rebase", "--quiet", "target")
                merge_sha = run("rev-parse", "HEAD")
                self.assertEqual(
                    [root / "knowledge/proposals/delivered.json"],
                    delivery.merged_proposals(root, delivered_candidate, merge_sha, strategy, base),
                )
                expected = "source parent" if strategy == "merge" else "exactly the reviewed candidate patch"
                with self.assertRaisesRegex(delivery.DeliveryError, expected):
                    delivery.merged_proposals(root, unrelated_candidate, merge_sha, strategy, base)

    def test_squash_with_extra_unreviewed_change_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            def run(*args: str) -> str:
                return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True, text=True).stdout.strip()
            run("init", "--quiet")
            run("config", "user.name", "AT-T02 Test")
            run("config", "user.email", "at-t02@example.invalid")
            (root / "knowledge/proposals").mkdir(parents=True)
            (root / "global.json").write_text("base\n", encoding="utf-8")
            run("add", ".")
            run("commit", "--quiet", "-m", "base")
            base = run("rev-parse", "HEAD")
            run("checkout", "--quiet", "-b", "candidate")
            (root / "global.json").write_text("reviewed\n", encoding="utf-8")
            (root / "knowledge/proposals/new.json").write_text("{}\n", encoding="utf-8")
            run("add", ".")
            run("commit", "--quiet", "-m", "candidate")
            candidate_sha = run("rev-parse", "HEAD")
            run("checkout", "--quiet", "-b", "target", base)
            run("merge", "--quiet", "--squash", "candidate")
            (root / "unreviewed.txt").write_text("not in candidate build\n", encoding="utf-8")
            run("add", ".")
            run("commit", "--quiet", "-m", "squash with unreviewed change")
            merge_sha = run("rev-parse", "HEAD")
            with self.assertRaisesRegex(delivery.DeliveryError, "exactly the reviewed candidate patch"):
                delivery.merged_proposals(root, candidate_sha, merge_sha, "squash", base)

    def test_manifest_binds_actual_downloaded_verification_evidence(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            proposal = self.write(root, self.document)
            common = self.manifest_inputs(root)
            manifest = delivery.create_manifest(proposal, root / "out", **common)
            evidence = manifest["testEvidence"][0]
            self.assertEqual(common["verification_artifact_uri"], evidence["artifactUri"])
            self.assertEqual(common["build_digest"], evidence["artifactSha256"])
            self.assertEqual("matched", manifest["anchorResolutions"][0]["verification"])
            self.assertEqual("123", manifest["repo"]["pullRequestId"])
            binding = json.loads((root / "out/candidate-binding.json").read_text(encoding="utf-8"))
            self.assertEqual("task/T02", binding["branch"])
            self.assertEqual("123", binding["pullRequestId"])
            self.assertEqual("candidate-run", binding["candidateBuildId"])
            self.assertEqual("run-1", binding["postMergeBuildId"])
            self.assertEqual(hashlib.sha256(common["attestation_path"].read_bytes()).hexdigest(), binding["candidateBuildAttestationSha256"])

    def test_manifest_rejects_tampered_or_wrong_candidate_build_attestation(self) -> None:
        for field, value in [("candidateSha", "d" * 40), ("candidateBuildId", "other-build"), ("pullRequestId", "456"), ("sourceBranch", "task/other"), ("result", "failed")]:
            with self.subTest(field=field), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                proposal = self.write(root, self.document)
                common = self.manifest_inputs(root)
                attestation = json.loads(common["attestation_path"].read_text(encoding="utf-8"))
                attestation[field] = value
                common["attestation_path"].write_text(json.dumps(attestation), encoding="utf-8")
                with self.assertRaisesRegex(delivery.DeliveryError, "attestation mismatch"):
                    delivery.create_manifest(proposal, root / "out", **common)
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            common = self.manifest_inputs(root)
            common["attestation_path"].write_text("not json", encoding="utf-8")
            with self.assertRaisesRegex(delivery.DeliveryError, "invalid candidate-build attestation"):
                delivery.create_manifest(self.write(root, self.document), root / "out", **common)

    def test_manifest_rejects_unvalidated_pr_identity(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            proposal = self.write(root, self.document)
            common = self.manifest_inputs(root)
            for key, value in [("repo_url", "https://example.invalid/other.git"), ("branch", "task/other"), ("pull_request_id", "456"), ("pull_request_id", "PENDING"), ("pull_request_id", " pending ")]:
                with self.subTest(key=key, value=value), self.assertRaisesRegex(delivery.DeliveryError, "PR identity|pull request ID"):
                    delivery.create_manifest(proposal, root / key / str(value).replace("/", "_"), **{**common, key: value})

    def test_placeholder_empty_and_tampered_verification_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            candidate = copy.deepcopy(self.document)
            candidate["testEvidence"][0]["artifactUri"] = "ci://verification pending"
            proposal = self.write(root, candidate)
            common = self.manifest_inputs(root)
            with self.assertRaisesRegex(delivery.DeliveryError, "placeholder"):
                delivery.create_manifest(proposal, root / "out", **common)
            with self.assertRaisesRegex(delivery.DeliveryError, "does not match"):
                delivery.create_manifest(self.write(root, self.document), root / "out-2", **{**common, "build_digest": "0" * 64})
            empty = root / "empty"
            empty.mkdir()
            with self.assertRaisesRegex(delivery.DeliveryError, "no files"):
                delivery.create_manifest(self.write(root, self.document), root / "out-3", **{**common, "verification_root": empty, "build_digest": "d" * 64})

    def test_source_hash_mismatch_cannot_be_remapped_to_verified(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            candidate = copy.deepcopy(self.document)
            candidate["sourceAnchors"][0]["contentSha256"] = "0" * 64
            proposal = self.write(root, candidate)
            with self.assertRaisesRegex(delivery.DeliveryError, "source hash mismatch"):
                delivery.create_manifest(proposal, root / "out", **self.manifest_inputs(root))

    def test_failed_evidence_cannot_be_verified(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            candidate = copy.deepcopy(self.document)
            candidate["testEvidence"][0]["result"] = "failed"
            proposal = self.write(root, candidate)
            with self.assertRaises(delivery.DeliveryError):
                delivery.create_manifest(proposal, root / "out", **self.manifest_inputs(root))


class VerificationContextTests(unittest.TestCase):
    def invoke(self, function: str, overrides: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
        environment = os.environ.copy()
        environment.pop("SYSTEM_PULLREQUEST_SOURCECOMMITID", None)
        environment.pop("ATLAS_PYTHON", None)
        environment.update(overrides or {})
        context = delivery.ROOT / "scripts/ci/verify_context.sh"
        return subprocess.run(
            ["bash", "-c", f'source "$1"; {function}', "bash", str(context)],
            cwd=delivery.ROOT, env=environment, check=False, capture_output=True, text=True,
        )

    def test_pr_rejects_synthetic_checkout_head_mislabeled_as_candidate(self) -> None:
        checkout_head = subprocess.check_output(["git", "-C", str(delivery.ROOT), "rev-parse", "HEAD"], text=True).strip()
        candidate = "b" * 40
        self.assertNotEqual(candidate, checkout_head)
        result = self.invoke("atlas_verification_candidate", {"SYSTEM_PULLREQUEST_SOURCECOMMITID": candidate})
        self.assertNotEqual(0, result.returncode)
        self.assertIn("does not match reviewed candidate", result.stderr)

    def test_local_verification_falls_back_to_actual_head(self) -> None:
        expected = subprocess.check_output(["git", "-C", str(delivery.ROOT), "rev-parse", "HEAD"], text=True).strip()
        result = self.invoke("atlas_verification_candidate")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(expected, result.stdout.strip())

    def test_invalid_candidate_identity_is_rejected_before_artifact_path_use(self) -> None:
        for candidate in ["../escape", "B" * 40, "abc123"]:
            with self.subTest(candidate=candidate):
                result = self.invoke("atlas_verification_candidate", {"SYSTEM_PULLREQUEST_SOURCECOMMITID": candidate})
                self.assertNotEqual(0, result.returncode)
                self.assertIn("full lowercase Git SHA", result.stderr)

    def test_python_default_is_portable_and_override_is_honored(self) -> None:
        default = self.invoke("atlas_verification_python")
        self.assertEqual(0, default.returncode, default.stderr)
        self.assertEqual("python3", default.stdout.strip())
        override = self.invoke("atlas_verification_python", {"ATLAS_PYTHON": sys.executable})
        self.assertEqual(0, override.returncode, override.stderr)
        self.assertEqual(sys.executable, override.stdout.strip())
        missing = self.invoke("atlas_verification_python", {"ATLAS_PYTHON": "/nonexistent/atlas/python"})
        self.assertNotEqual(0, missing.returncode)
        self.assertIn("unavailable", missing.stderr)

    def test_pipeline_download_path_matches_pr_verification_artifact_identity(self) -> None:
        verify = (delivery.ROOT / "scripts/ci/verify").read_text(encoding="utf-8")
        pipeline = (delivery.ROOT / "azure-pipelines.yml").read_text(encoding="utf-8")
        self.assertIn('CANDIDATE_SHA=$(atlas_verification_candidate)', verify)
        self.assertIn('LOG_DIR="$ROOT/artifacts/verification/$CANDIDATE_SHA/$RUN_ID"', verify)
        self.assertIn('VERIFY_DIR="$PIPELINE_WORKSPACE/at-t02-verification/$ATLAS_CANDIDATE_SHA/$ATLAS_CANDIDATE_BUILD_ID"', pipeline)
        self.assertIn('export ATLAS_PYTHON=/Users/_atlasbuild/toolchains/verify-venv/bin/python', pipeline)
        self.assertIn('--branch "$SOURCE_BRANCH" --pull-request-id "$PR_ID"', pipeline)
        self.assertIn('--merge-strategy "$ATLAS_MERGE_STRATEGY"', pipeline)
        self.assertIn('list-merged-proposals --base-sha "$BASE" --candidate-sha "$ATLAS_CANDIDATE_SHA"', pipeline)
        self.assertIn('d["buildId"]==os.environ["ATLAS_CANDIDATE_BUILD_ID"]', pipeline)
        self.assertNotIn('--merge-strategy merge --build-id', pipeline)
        self.assertIn('"pullRequestId":sys.argv[5]', pipeline)
        self.assertIn('artifactName: knowledge-candidate-$(ATLAS_CANDIDATE_BUILD_ID)', pipeline)
        self.assertIn('scripts/knowledge/candidate_build.py --output', pipeline)
        self.assertIn('SYSTEM_ACCESSTOKEN: $(System.AccessToken)', pipeline)
        self.assertIn('--candidate-build-id "$ATLAS_CANDIDATE_BUILD_ID" --post-merge-build-id "$BUILD_BUILDID"', pipeline)
        self.assertLess(pipeline.index('Verify reviewed candidate build with Azure DevOps'), pipeline.index("Download the reviewed candidate's actual verification logs"))


if __name__ == "__main__":
    unittest.main()
