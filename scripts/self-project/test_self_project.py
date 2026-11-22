from __future__ import annotations

import copy
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

import self_project
import build_case


class SelfProjectTests(unittest.TestCase):
    def test_current_repository_bootstrap_and_writeback_bind_exact_git_sources(self) -> None:
        root = self_project.ROOT
        head = self_project.revision(root, "HEAD")
        initial = self_project.build_package(root, head, generated_at="2026-11-22T00:00:00Z")
        self_project.verify_package(root, initial)
        ids = {item["stableExternalId"] for item in initial["entities"]}
        self.assertTrue(self_project.SELF_REQUIREMENTS <= ids)
        self.assertIn("T38", ids)
        self.assertTrue(any(item["typeId"] == "Implementation" for item in initial["entities"]))
        self.assertEqual(head, initial["manifest"]["sourceRevision"])
        self.assertTrue(all(item["captureMethod"].startswith("git show ") for item in initial["sources"]))

        parent = self_project.revision(root, "HEAD^")
        change = self_project.build_package(root, head, parent, generated_at="2026-11-22T00:00:01Z")
        observation = next(item for item in change["entities"] if item["typeId"] == "Observation")
        self.assertEqual(parent, observation["properties"]["baseCommitSha"])
        self.assertEqual(head, observation["properties"]["headCommitSha"])
        self.assertGreater(observation["properties"]["changeCount"], 0)
        self.assertNotEqual(initial["manifest"]["packageId"], change["manifest"]["packageId"])

    def test_tampered_hash_identity_and_non_ancestor_change_are_rejected(self) -> None:
        root = self_project.ROOT
        package = self_project.build_package(root, "HEAD", generated_at="2026-11-22T00:00:00Z")
        tampered = copy.deepcopy(package)
        tampered["sources"][0]["contentHash"] = "0" * 64
        with self.assertRaisesRegex(self_project.SelfProjectError, "hash mismatch"):
            self_project.verify_package(root, tampered)
        unresolved = copy.deepcopy(package)
        unresolved["entities"][0]["sourceIds"] = ["SRC-NOT-PRESENT"]
        with self.assertRaisesRegex(self_project.SelfProjectError, "Unresolved source"):
            self_project.verify_package(root, unresolved)
        with self.assertRaisesRegex(self_project.SelfProjectError, "ancestor"):
            self_project.build_package(root, "HEAD^", "HEAD")

    def test_atomic_output_does_not_overwrite_another_candidate(self) -> None:
        package = self_project.build_package(self_project.ROOT, "HEAD", generated_at="2026-11-22T00:00:00Z")
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "self.json"
            self_project.write_atomic(path, package)
            original = path.read_bytes()
            self_project.write_atomic(path, package)
            self.assertEqual(original, path.read_bytes())
            changed = copy.deepcopy(package)
            changed["manifest"]["packageId"] = "AKP-SELF-OTHER"
            with self.assertRaisesRegex(self_project.SelfProjectError, "Refusing"):
                self_project.write_atomic(path, changed)

    def test_sources_are_content_addressed_and_bounded_to_repository(self) -> None:
        self.assertEqual("SRC-" + hashlib.sha256(b"README.md").hexdigest()[:20], self_project.source_id("README.md"))
        for path in ["../secrets", "/etc/passwd", "docs\\private.json"]:
            with self.subTest(path=path), self.assertRaises(self_project.SelfProjectError):
                self_project.safe_path(path)

    def test_public_case_and_import_candidate_trace_all_requirements_and_tasks(self) -> None:
        revision = self_project.revision(self_project.ROOT, "HEAD")
        public_view, package = build_case.build(revision, generated_at="2026-11-22T00:00:00Z")
        repeated_view, repeated_package = build_case.build(revision, generated_at="2026-11-22T00:00:00Z")
        self.assertEqual(154, public_view["counts"]["requirements"])
        self.assertEqual(44, public_view["counts"]["tasks"])
        self.assertEqual(154, len([item for item in package["entities"] if item["typeId"] == "Requirement"]))
        self.assertEqual(44, len([item for item in package["entities"] if item["typeId"] == "Task"]))
        self.assertTrue(all(source["contentHash"] for source in package["sources"]))
        self.assertTrue(all(document["contentHash"] for document in public_view["documents"]))
        self.assertEqual("not_ready", public_view["design"]["currentAcceptance"]["decision"])
        self.assertEqual(public_view, repeated_view)
        self.assertEqual(package, repeated_package)


if __name__ == "__main__":
    unittest.main()
