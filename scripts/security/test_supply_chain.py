#!/usr/bin/env python3
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("atlas_supply_chain", ROOT / "scripts/security/supply_chain.py")
supply_chain = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(supply_chain)


class SupplyChainTests(unittest.TestCase):
    def test_secret_findings_are_redacted_and_examples_are_allowed(self):
        with tempfile.TemporaryDirectory(prefix="atlas-t42-secret-") as temporary:
            root = Path(temporary)
            subprocess.run(["git", "init", "-q"], cwd=root, check=True)
            secret = "actual-" + "private-token-938475610293847561"
            (root / "settings.json").write_text(json.dumps({"apiKey": secret, "password": "example-only-password"}) + "\n")
            (root / "private.pem").write_text("-----BEGIN " + "PRIVATE KEY-----\nnot scanned further\n")
            subprocess.run(["git", "add", "."], cwd=root, check=True)
            findings = supply_chain.scan_secrets(root)
            serialized = json.dumps(findings)
            self.assertEqual(2, len(findings))
            self.assertNotIn(secret, serialized)
            self.assertEqual({"settings.json", "private.pem"}, {item["path"] for item in findings})

    def test_sbom_has_valid_serial_and_locked_component(self):
        with tempfile.TemporaryDirectory(prefix="atlas-t42-sbom-") as temporary:
            output = Path(temporary) / "sbom.json"
            supply_chain.write_sbom(ROOT, output, [supply_chain.component("npm", "example", "1.2.3", "MIT")])
            document = json.loads(output.read_text())
            self.assertEqual("CycloneDX", document["bomFormat"])
            self.assertEqual("pkg:npm/example@1.2.3", document["components"][0]["purl"])
            self.assertEqual(document["serialNumber"], "urn:uuid:" + str(uuid.UUID(document["serialNumber"].removeprefix("urn:uuid:"))))
    def test_offline_dependency_audit_is_explicitly_not_run(self):
        findings, evidence = supply_chain.dependency_audits(ROOT, skip_network=True)
        self.assertEqual([], findings)
        self.assertEqual("not_run", evidence[0]["status"])


if __name__ == "__main__":
    unittest.main()
