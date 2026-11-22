"""Focused tests for the real T44 handoff generator (synthetic approvals stay in temp dirs)."""
import copy
import csv
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/release/handoff.py"
EVIDENCE = ROOT / "docs/release/t1.0-evidence.json"


class HandoffTests(unittest.TestCase):
    def run_handoff(self, evidence: Path, output: Path):
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--evidence", str(evidence), "--output", str(output)],
            cwd=ROOT, text=True, capture_output=True,
        )

    def test_current_evidence_produces_complete_trace_without_false_acceptance(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp)
            result = self.run_handoff(EVIDENCE, output)
            self.assertEqual(1, result.returncode, result.stderr)
            manifest = json.loads((output / "release-manifest.json").read_text())
            self.assertEqual(154, len(manifest["requirements"]))
            self.assertEqual(154, manifest["checkout"]["unacceptedCount"])
            self.assertEqual("not_ready", manifest["checkout"]["decision"])
            self.assertEqual([], manifest["approvals"])
            self.assertEqual(8, len(manifest["gates"]))
            self.assertTrue((output / "knowledge-archive.zip").is_file())
            handoff = (output / "HANDOFF.md").read_text()
            self.assertIn("https://atlas.lalapilot.com", handoff)
            self.assertIn("not_ready", handoff)

    def test_acceptance_requires_every_role_gate_and_requirement(self):
        evidence = json.loads(EVIDENCE.read_text())
        with (ROOT / "docs/baselines/T1.0/03_开发执行/requirements-trace.csv").open(encoding="utf-8-sig") as stream:
            trace = list(csv.DictReader(stream))
        evidence["requirements"] = {
            row["requirement_id"]: {
                "status": "accepted",
                "implementationRefs": ["test-implementation"],
                "testRefs": ["test-run"],
                "knowledgeDeliveryRefs": ["test-knowledge"],
                "deploymentRefs": ["test-deployment"],
            }
            for row in trace
        }
        evidence["gates"] = [
            {"gate": f"G{number}", "status": "accepted", "evidenceRefs": ["test-gate"]}
            for number in range(8)
        ]
        evidence["approvals"] = [
            {
                "role": role, "identity": f"synthetic-test-{role.lower()}",
                "decision": "approved", "signedAt": "2026-11-22T00:00:00Z",
                "evidenceRef": "synthetic-unit-test-only",
            }
            for role in ("QA", "BA", "Leader", "ProductOwner")
        ]
        evidence["risks"] = []
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp)
            evidence_path = output / "evidence.json"
            evidence_path.write_text(json.dumps(evidence))
            result = self.run_handoff(evidence_path, output / "release")
            self.assertEqual(0, result.returncode, result.stderr)
            manifest = json.loads((output / "release/release-manifest.json").read_text())
            self.assertEqual("accepted", manifest["checkout"]["decision"])
            self.assertEqual(0, manifest["checkout"]["unacceptedCount"])
            self.assertTrue(all(row["actorType"] == "human" for row in manifest["approvals"]))

            rejected = copy.deepcopy(evidence)
            rejected["approvals"] = rejected["approvals"][:-1]
            evidence_path.write_text(json.dumps(rejected))
            result = self.run_handoff(evidence_path, output / "rejected")
            self.assertEqual(1, result.returncode)
            manifest = json.loads((output / "rejected/release-manifest.json").read_text())
            self.assertEqual("not_ready", manifest["checkout"]["decision"])

    def test_status_cannot_be_claimed_without_evidence(self):
        evidence = json.loads(EVIDENCE.read_text())
        evidence["requirements"] = {"ENG-DEL-008": {"status": "accepted"}}
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            path = root / "evidence.json"
            path.write_text(json.dumps(evidence))
            result = self.run_handoff(path, root / "release")
            self.assertEqual(2, result.returncode)
            self.assertIn("requires implementation and test refs", result.stderr)


if __name__ == "__main__":
    unittest.main()
