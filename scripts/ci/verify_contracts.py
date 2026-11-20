#!/usr/bin/env python3
"""Deterministic AT-T01 machine-contract lint; this is not product acceptance."""
from __future__ import annotations

import argparse
import csv
import hashlib
import io
import json
import os
from pathlib import Path
import tempfile
from typing import Any, Iterator

import jsonschema
import yaml

ROOT = Path(__file__).resolve().parents[2]
CONTRACTS = ROOT / "contracts"
MANIFEST_NAME = "manifest.json"


class ContractError(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ContractError(message)


def load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ContractError(f"invalid JSON {path}: {exc}") from exc


def load_yaml(path: Path) -> Any:
    try:
        return yaml.safe_load(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, yaml.YAMLError) as exc:
        raise ContractError(f"invalid YAML {path}: {exc}") from exc


def walk(value: Any) -> Iterator[Any]:
    yield value
    if isinstance(value, dict):
        for child in value.values():
            yield from walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from walk(child)


def json_pointer(document: Any, pointer: str, source: Path) -> Any:
    current = document
    if pointer:
        for raw_part in pointer.split("/")[1:]:
            part = raw_part.replace("~1", "/").replace("~0", "~")
            try:
                current = current[int(part)] if isinstance(current, list) else current[part]
            except (KeyError, IndexError, ValueError) as exc:
                raise ContractError(f"unresolved ref {pointer} in {source}") from exc
    return current


def validate_refs(document: Any, source: Path, seen: set[tuple[Path, str]] | None = None) -> None:
    """Resolve local/external refs and reject reference cycles."""
    seen = set() if seen is None else seen
    for node in walk(document):
        if not isinstance(node, dict) or "$ref" not in node:
            continue
        ref = node["$ref"]
        require(isinstance(ref, str) and ref, f"invalid ref in {source}")
        target_path, separator, pointer = ref.partition("#")
        if "://" in target_path:
            raise ContractError(f"non-local ref forbidden: {ref}")
        target = (source.parent / target_path).resolve() if target_path else source.resolve()
        require(target.is_file(), f"missing ref target {target}")
        key = (target, pointer)
        require(key not in seen, f"reference cycle at {ref} in {source}")
        if target_path:
            target_document = load_json(target) if target.suffix == ".json" else load_yaml(target)
            json_pointer(target_document, pointer, target)
            validate_refs(target_document, target, seen | {key})
        else:
            json_pointer(document, pointer, source)


def operations(document: dict[str, Any]) -> list[dict[str, Any]]:
    result = []
    for path, path_item in document.get("paths", {}).items():
        for method, operation in path_item.items():
            if method.lower() in {"get", "post", "put", "patch", "delete", "head", "options", "trace"}:
                result.append({"path": path, "method": method.upper(), **operation})
    return result


def validate_openapi(document: dict[str, Any]) -> None:
    require(document.get("openapi") == "3.1.0", "OpenAPI must be frozen at 3.1.0")
    ops = operations(document)
    ids = [op.get("operationId") for op in ops]
    require(all(isinstance(item, str) and item for item in ids), "every operation needs operationId")
    require(len(ids) == len(set(ids)), "duplicate operationId")
    for op in ops:
        require(bool(op.get("x-owner")), f"missing owner: {op['operationId']}")
        require(bool(op.get("x-required-scope")), f"missing scope: {op['operationId']}")


def validate_catalog(root: Path, document: dict[str, Any]) -> None:
    with (root / "api-catalog.csv").open(encoding="utf-8-sig", newline="") as handle:
        rows = list(csv.DictReader(handle))
    require(bool(rows) and set(rows[0]) == {"operationId", "method", "path", "owner", "scope", "write", "ifMatch", "cognitionService"}, "invalid API catalog columns")
    catalog = {(row["method"], row["path"]): row for row in rows}
    require(len(catalog) == len(rows), "duplicate API catalog operation")
    openapi = {(op["method"], op["path"]): op for op in operations(document)}
    require(catalog.keys() == openapi.keys(), "API catalog/OpenAPI operation drift")
    for key, row in catalog.items():
        op = openapi[key]
        require(row["operationId"] == op["operationId"], f"operationId drift: {key}")
        require(row["owner"] == op["x-owner"] and row["scope"] == op["x-required-scope"], f"ownership/scope drift: {row['operationId']}")


def validate_registry(registry: dict[str, Any], domain: dict[str, Any]) -> None:
    require(registry.get("version") == "atlas-registry/1.0", "registry version drift")
    types = registry.get("types", [])
    predicates = registry.get("predicates", [])
    kinds = [item.get("kind") for item in types]
    keys = [item.get("key") for item in predicates]
    require(len(kinds) == len(set(kinds)), "duplicate registry type")
    require(len(keys) == len(set(keys)), "duplicate registry predicate")
    definitions = domain.get("$defs", {})
    for item in types:
        require(item.get("schemaVersion") == "1.0", f"type version drift: {item.get('kind')}")
        require(item.get("strictBodySchema") in definitions, f"missing strict body schema: {item.get('kind')}")


def validate_mcp(mcp: dict[str, Any], openapi: dict[str, Any]) -> None:
    require(mcp.get("transport") == "streamable-http", "MCP transport drift")
    tools = mcp.get("tools", [])
    names = [tool.get("name") for tool in tools]
    require(len(names) == len(set(names)), "duplicate MCP tool")
    operation_ids = {op["operationId"] for op in operations(openapi)}
    for tool in tools:
        require(tool.get("operationId") in operation_ids, f"unmapped MCP tool: {tool.get('name')}")
        lowered = " ".join(str(tool.get(key, "")) for key in ("name", "description", "policy")).lower()
        require("arbitrary shell" not in lowered and "direct approval" not in lowered, f"unsafe MCP capability: {tool.get('name')}")
        jsonschema.Draft202012Validator.check_schema(tool["inputSchema"])
        jsonschema.Draft202012Validator.check_schema(tool["outputSchema"])
        validate_refs(tool["inputSchema"], root_path(CONTRACTS / "mcp-tools.json"))
        validate_refs(tool["outputSchema"], root_path(CONTRACTS / "mcp-tools.json"))


def root_path(path: Path) -> Path:
    return path


def manifest_entries(root: Path) -> list[dict[str, str | int]]:
    entries = []
    for path in sorted(root.rglob("*"), key=lambda item: item.relative_to(root).as_posix()):
        if not path.is_file() or path.name == MANIFEST_NAME or path.name.startswith(MANIFEST_NAME + "."):
            continue
        relative = path.relative_to(root).as_posix()
        entries.append({"path": relative, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "bytes": path.stat().st_size})
    require(bool(entries), "empty contract directory")
    return entries


def tree_digest(entries: list[dict[str, str | int]]) -> str:
    digest = hashlib.sha256()
    for entry in entries:
        digest.update(str(entry["path"]).encode("utf-8"))
        digest.update(b"\0")
        digest.update(str(entry["sha256"]).encode("ascii"))
        digest.update(b"\n")
    return digest.hexdigest()


def build_manifest(root: Path) -> dict[str, Any]:
    entries = manifest_entries(root)
    return {
        "schemaVersion": "1.0.0",
        "algorithm": "sha256(path UTF-8 || NUL || lowercase-content-sha256 || LF), sorted by relative POSIX path; manifest excluded",
        "fileCount": len(entries),
        "treeSha256": tree_digest(entries),
        "files": entries,
    }


def write_manifest(root: Path) -> dict[str, Any]:
    document = build_manifest(root)
    payload = (json.dumps(document, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    fd, temporary = tempfile.mkstemp(prefix=MANIFEST_NAME + ".", dir=root)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, root / MANIFEST_NAME)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    return document


def validate_manifest(root: Path) -> None:
    actual = build_manifest(root)
    expected = load_json(root / MANIFEST_NAME)
    require(actual == expected, "contract manifest missing, stale, or contains unregistered/changed files")


def validate_freeze(root: Path = ROOT) -> None:
    freeze = load_json(root / "docs/decisions/T01-contract-freeze.json")
    dag_path = root / "docs/baselines/T1.0/03_开发执行/task-dag.json"
    dag = load_json(dag_path)
    require(freeze.get("inputDagSha256") == hashlib.sha256(dag_path.read_bytes()).hexdigest(), "DAG digest mismatch")
    require(freeze.get("technicalBaseline") == "T1.0", "baseline mismatch")
    require(freeze.get("scope", {}).get("textFirst") is True, "text-first scope required")
    require(freeze.get("scope", {}).get("dedicatedDrawing") is False, "dedicated drawing is not approved")
    require(freeze.get("approval", {}).get("kind") == "delegated-ai", "approval must identify AI delegation")
    require(isinstance(freeze.get("unknowns"), list) and bool(freeze["unknowns"]), "unknowns must be explicit")
    require(len(dag.get("tasks", [])) == 44 and freeze.get("baselineCounts") == {"tasks": 44, "requirements": 154, "invariants": 14}, "full baseline must remain")
    expected_ids = {"BA-VIS-001", "BA-VIS-004", "BA-VIS-007", "BA-VIS-008", "BA-QA-003", "ENG-ARCH-001", "ENG-ARCH-005", "ENG-DEL-001", "ENG-DEL-002", "ENG-DEL-003", "ENG-DEL-006", "PD-36", "PD-37", "PD-38"}
    require(set(freeze.get("requirementIds", [])) == expected_ids, "T01 requirement scope drift")
    task = next(item for item in dag["tasks"] if item["taskId"] == "T01")
    digest = hashlib.sha256()
    digest.update(json.dumps(task, ensure_ascii=False, sort_keys=True).encode("utf-8"))
    for reference in sorted(task["contractRefs"]):
        digest.update(reference.encode("utf-8"))
        digest.update(b"\0")
        digest.update((root / "docs/baselines/T1.0" / reference).read_bytes())
    require(freeze.get("inputContractDigest") == digest.hexdigest(), "coordinator input contract digest mismatch")


def validate_examples(root: Path) -> None:
    direct = {
        "agent-result-v1.example.json": "agent-result-v1.schema.json",
        "impact-v1.example.json": "impact-v1.schema.json",
        "rule-v1.example.json": "rule-v1.schema.json",
        "task-context-v1.example.json": "task-context-v1.schema.json",
        "knowledge-change-proposal-v1.3.example.json": "knowledge-change-proposal-v1.3.schema.json",
        "knowledge-delivery-manifest-v1.3.example.json": "knowledge-delivery-manifest-v1.3.schema.json",
        "agent-task.example.json": "agent-task-v1.2.schema.json",
        "deployment-reconciliation.example.json": "deployment-reconciliation-v1.2.schema.json",
        "domain-event.example.json": "domain-event-v1.2.schema.json",
        "error-pattern.example.json": "error-pattern-v1.2.schema.json",
        "mcp-tool-contract.example.json": "mcp-tool-contract-v1.2.schema.json",
        "knowledge-change-proposal.example.json": "knowledge-change-proposal-v1.2.schema.json",
        "knowledge-delivery-manifest.example.json": "knowledge-delivery-manifest-v1.2.schema.json",
    }
    for example_name, schema_name in direct.items():
        schema = load_json(root / "schemas" / schema_name)
        example = load_json(root / "examples" / example_name)
        jsonschema.Draft202012Validator(schema).validate(example)
    domain = load_json(root / "schemas/domain-v1.schema.json")
    revision_schema = {"$ref": "#/$defs/RevisionInput", "$defs": domain["$defs"]}
    jsonschema.Draft202012Validator(revision_schema).validate(load_json(root / "examples/revision-v1.example.json"))


def verify(root: Path = ROOT) -> None:
    contracts = root / "contracts"
    source = root / "docs/baselines/T1.0/02_机器合同"
    validate_freeze(root)
    validate_manifest(contracts)
    source_files = {p.relative_to(source).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in source.rglob("*") if p.is_file()}
    contract_files = {entry["path"]: entry["sha256"] for entry in manifest_entries(contracts)}
    require(source_files == contract_files, "frozen contracts differ from read-only baseline bytes")
    openapi = load_yaml(contracts / "openapi.yaml")
    require(isinstance(openapi, dict), "OpenAPI root must be object")
    validate_openapi(openapi)
    validate_refs(openapi, contracts / "openapi.yaml")
    validate_catalog(contracts, openapi)
    schemas = sorted((contracts / "schemas").glob("*.json"))
    require(len(schemas) == 17, "schema set drift")
    for schema_path in schemas:
        schema = load_json(schema_path)
        jsonschema.Draft202012Validator.check_schema(schema)
        validate_refs(schema, schema_path)
    validate_registry(load_json(contracts / "registry.json"), load_json(contracts / "schemas/domain-v1.schema.json"))
    validate_mcp(load_json(contracts / "mcp-tools.json"), openapi)
    validate_examples(contracts)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--write-manifest", action="store_true")
    args = parser.parse_args()
    if args.write_manifest:
        document = write_manifest(CONTRACTS)
        print(f"AT-T01 manifest: {document['fileCount']} files, sha256={document['treeSha256']}")
    else:
        verify()
        print("AT-T01 machine-contract lint passed; static validation only, not domain/security/deployment acceptance")


if __name__ == "__main__":
    try:
        main()
    except (ContractError, jsonschema.ValidationError, jsonschema.SchemaError) as exc:
        raise SystemExit(f"AT-T01 contract lint failed: {exc}")
