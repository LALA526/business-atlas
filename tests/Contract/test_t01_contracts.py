from __future__ import annotations

import concurrent.futures
import copy
import importlib.util
import json
from pathlib import Path
import shutil
import tempfile
import unittest

import jsonschema

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("verify_contracts", ROOT / "scripts/ci/verify_contracts.py")
contracts = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(contracts)


class T01ContractTests(unittest.TestCase):
    def test_positive_frozen_contracts_and_examples(self) -> None:
        contracts.verify(ROOT)
        proposal_schema = contracts.load_json(ROOT / "contracts/schemas/knowledge-change-proposal-v1.3.schema.json")
        proposal = contracts.load_json(ROOT / "docs/decisions/T01-knowledge-change.candidate.json")
        jsonschema.Draft202012Validator(proposal_schema).validate(proposal)
        self.assertTrue(proposal["repo"]["pullRequestId"].startswith("PENDING-"))

    def test_reject_duplicate_operation_and_owner_drift(self) -> None:
        document = contracts.load_yaml(ROOT / "contracts/openapi.yaml")
        operations = contracts.operations(document)
        duplicate = copy.deepcopy(operations[0])
        duplicate["path"] = "/duplicate"
        duplicate["method"] = "GET"
        with self.assertRaisesRegex(contracts.ContractError, "duplicate operationId"):
            # A minimal valid OpenAPI document with two paths sharing an ID.
            first_path = operations[0]["path"]
            second = copy.deepcopy(document["paths"][first_path])
            altered = {"openapi": "3.1.0", "paths": {first_path: document["paths"][first_path], "/duplicate": second}}
            contracts.validate_openapi(altered)

    def test_reject_missing_and_cyclic_refs(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "schema.json"
            path.write_text(json.dumps({"$ref": "#/missing"}), encoding="utf-8")
            with self.assertRaisesRegex(contracts.ContractError, "unresolved ref"):
                contracts.validate_refs(contracts.load_json(path), path)
            path.write_text(json.dumps({"$ref": "./other.json"}), encoding="utf-8")
            other = path.with_name("other.json")
            other.write_text(json.dumps({"$ref": "./schema.json"}), encoding="utf-8")
            with self.assertRaisesRegex(contracts.ContractError, "reference cycle"):
                contracts.validate_refs(contracts.load_json(path), path)

    def test_reject_invalid_example_and_missing_draft_unknown(self) -> None:
        schema = contracts.load_json(ROOT / "contracts/schemas/knowledge-change-proposal-v1.3.schema.json")
        example = contracts.load_json(ROOT / "contracts/examples/knowledge-change-proposal-v1.3.example.json")
        example["knowledgeStatus"] = "approved"
        with self.assertRaises(jsonschema.ValidationError):
            jsonschema.Draft202012Validator(schema).validate(example)
        revision_schema = contracts.load_json(ROOT / "contracts/schemas/domain-v1.schema.json")["$defs"]["RevisionInput"]
        revision = contracts.load_json(ROOT / "contracts/examples/revision-v1.example.json")
        revision["body"].pop("purpose")
        revision["body"]["evaluationMode"] = "structured"
        # Structural incompleteness is detectable when the strict publish body is applied.
        domain = contracts.load_json(ROOT / "contracts/schemas/domain-v1.schema.json")
        strict = {"$ref": "#/$defs/RuleBody", "$defs": domain["$defs"]}
        with self.assertRaises(jsonschema.ValidationError):
            jsonschema.Draft202012Validator(strict).validate(revision["body"])

    def test_manifest_rejects_changed_missing_and_extra_bytes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            shutil.copytree(ROOT / "contracts", root, dirs_exist_ok=True)
            contracts.write_manifest(root)
            contracts.validate_manifest(root)
            target = root / "registry.json"
            original = target.read_bytes()
            target.write_bytes(original + b"\n")
            with self.assertRaisesRegex(contracts.ContractError, "manifest"):
                contracts.validate_manifest(root)
            target.write_bytes(original)
            extra = root / "unexpected.json"
            extra.write_text("{}", encoding="utf-8")
            with self.assertRaisesRegex(contracts.ContractError, "manifest"):
                contracts.validate_manifest(root)
            extra.unlink()
            (root / "registry.json").unlink()
            with self.assertRaisesRegex(contracts.ContractError, "manifest"):
                contracts.validate_manifest(root)

    def test_freeze_rejects_digest_unknowns_and_approval_tampering(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            shutil.copytree(ROOT / "docs", root / "docs")
            freeze_path = root / "docs/decisions/T01-contract-freeze.json"
            original = freeze_path.read_bytes()
            for field, value in (("inputDagSha256", "0" * 64), ("unknowns", [])):
                document = json.loads(original)
                document[field] = value
                freeze_path.write_text(json.dumps(document), encoding="utf-8")
                with self.assertRaises(contracts.ContractError):
                    contracts.validate_freeze(root)
            document = json.loads(original)
            document["approval"]["kind"] = "human-owner"
            freeze_path.write_text(json.dumps(document), encoding="utf-8")
            with self.assertRaisesRegex(contracts.ContractError, "AI delegation"):
                contracts.validate_freeze(root)

    def test_concurrent_digest_readers_are_identical_and_isolated(self) -> None:
        # Independent readers exercise deterministic ordering without mutating frozen bytes.
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            results = list(pool.map(lambda _: contracts.tree_digest(contracts.manifest_entries(ROOT / "contracts")), range(32)))
        self.assertEqual(len(set(results)), 1)
        self.assertEqual(results[0], contracts.load_json(ROOT / "contracts/manifest.json")["treeSha256"])

    def test_recovery_from_interrupted_or_corrupt_manifest_write(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            shutil.copytree(ROOT / "contracts", root, dirs_exist_ok=True)
            orphan = root / "manifest.json.interrupted"
            orphan.write_text('{"partial":', encoding="utf-8")
            (root / "manifest.json").write_text('{"corrupt":true}', encoding="utf-8")
            rebuilt = contracts.write_manifest(root)
            contracts.validate_manifest(root)
            self.assertTrue(orphan.exists(), "test retains the simulated interrupted write")
            self.assertEqual(rebuilt["treeSha256"], contracts.build_manifest(root)["treeSha256"])


if __name__ == "__main__":
    unittest.main()
