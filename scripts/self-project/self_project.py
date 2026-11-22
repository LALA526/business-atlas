#!/usr/bin/env python3
"""Build auditable AKP candidates from the real Business Atlas Git repository.

The output is always an unapproved import candidate. Publication remains a normal
Governance operation performed by an authorized human reviewer.
"""
from __future__ import annotations

import argparse
import csv
from datetime import datetime, timezone
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
SHA = re.compile(r"^[0-9a-f]{40}$")
SELF_REQUIREMENTS = {
    "BA-QA-005", "ENG-KNW-001", "ENG-KNW-002", "ENG-KNW-004",
    "PD-32", "PD-33", "PD-38",
}
CORE_SOURCES = [
    "README.md",
    "docs/baselines/T1.0/03_开发执行/requirements-trace.csv",
    "docs/baselines/T1.0/03_开发执行/task-dag.json",
    "docs/baselines/T1.0/01_技术实施/08_导入与工程知识交付.md",
]


class SelfProjectError(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SelfProjectError(message)


def git(root: Path, *args: str, text: bool = True) -> Any:
    result = subprocess.run(["git", "-C", str(root), *args], capture_output=True, check=False, text=text)
    require(result.returncode == 0, f"Git command failed: {' '.join(args)}")
    return result.stdout.strip() if text else result.stdout


def safe_path(value: str) -> str:
    path = PurePosixPath(value)
    require(value and not path.is_absolute() and "\\" not in value and not any(part in {".", ".."} for part in path.parts), f"Unsafe source path: {value}")
    return path.as_posix()


def revision(root: Path, value: str) -> str:
    result = git(root, "rev-parse", f"{value}^{{commit}}")
    require(SHA.fullmatch(result), "Source revision must resolve to a full Git commit SHA")
    return result


def source_bytes(root: Path, commit: str, path: str) -> bytes:
    return git(root, "show", f"{commit}:{safe_path(path)}", text=False)


def read_json(root: Path, commit: str, path: str) -> Any:
    return json.loads(source_bytes(root, commit, path))


def source_id(path: str) -> str:
    return "SRC-" + hashlib.sha256(path.encode()).hexdigest()[:20]


def source(root: Path, commit: str, path: str) -> dict[str, Any]:
    content = source_bytes(root, commit, path)
    blob = git(root, "rev-parse", f"{commit}:{path}")
    return {
        "sourceId": source_id(path), "sourceType": "git", "path": path,
        "uri": f"git:{commit}:{path}", "revision": commit,
        "contentHash": hashlib.sha256(content).hexdigest(),
        "captureMethod": f"git show {commit}:{path}; git blob {blob}",
    }


def load_proposal_sources(root: Path, commit: str) -> list[str]:
    listing = git(root, "ls-tree", "-r", "--name-only", commit, "--", "knowledge/proposals")
    paths: set[str] = set(CORE_SOURCES)
    for name in listing.splitlines():
        if not name.endswith(".json"):
            continue
        proposal = read_json(root, commit, name)
        for anchor in proposal.get("sourceAnchors", []):
            path = safe_path(anchor["path"])
            # A historical proposal may refer to a file retired before this snapshot.
            exists = subprocess.run(["git", "-C", str(root), "cat-file", "-e", f"{commit}:{path}"], capture_output=True).returncode == 0
            if exists:
                paths.add(path)
    return sorted(paths)


def entities(root: Path, commit: str, source_paths: list[str], base: str | None) -> list[dict[str, Any]]:
    trace_path = "docs/baselines/T1.0/03_开发执行/requirements-trace.csv"
    rows = csv.DictReader(io.StringIO(source_bytes(root, commit, trace_path).decode("utf-8-sig")))
    result = []
    for row in rows:
        if row["requirement_id"] not in SELF_REQUIREMENTS:
            continue
        result.append({
            "stableExternalId": row["requirement_id"], "typeId": "Requirement",
            "name": row["title"], "description": row["behavior"],
            "properties": {
                "origin": row["source"], "priority": row["priority"],
                "acceptanceId": row["source_test_id"], "disposition": row["scope_disposition"],
                "taskIds": row["implementation_tasks"], "testIds": row["planned_tests"],
                "acceptance": row["acceptance"], "sourceRevision": commit,
            },
            "categoryAssignments": ["SelfProject"], "applicableContext": {"project": "BusinessAtlas-Self"},
            "verificationStatus": "documented_intent", "sourceIds": [source_id(trace_path)],
        })
    require({item["stableExternalId"] for item in result} == SELF_REQUIREMENTS, "Controlled Self Project requirements are missing or renamed")

    dag_path = "docs/baselines/T1.0/03_开发执行/task-dag.json"
    tasks = read_json(root, commit, dag_path)["tasks"]
    task = next((item for item in tasks if item.get("taskId") == "T38"), None)
    require(task is not None, "T38 is absent from the controlled task DAG")
    result.append({
        "stableExternalId": "T38", "typeId": "Milestone", "name": task["title"],
        "description": task["implementation"],
        "properties": {"acceptance": task["acceptance"], "dependsOn": task["dependsOn"], "requirementIds": task["requirementIds"], "sourceRevision": commit},
        "categoryAssignments": ["SelfProject"], "applicableContext": {"project": "BusinessAtlas-Self"},
        "verificationStatus": "documented_intent", "sourceIds": [source_id(dag_path)],
    })
    for path in source_paths:
        if path in CORE_SOURCES or not path.startswith(("src/", "apps/", "scripts/", "contracts/", "tests/")):
            continue
        result.append({
            "stableExternalId": "IMPL-" + hashlib.sha256(path.encode()).hexdigest()[:20],
            "typeId": "Implementation", "name": path,
            "description": "Exact source file observed in the Business Atlas repository; this import does not claim human approval.",
            "properties": {"path": path, "commitSha": commit, "contentSha256": hashlib.sha256(source_bytes(root, commit, path)).hexdigest(), "gitBlobSha": git(root, "rev-parse", f"{commit}:{path}")},
            "categoryAssignments": ["SelfProject"], "applicableContext": {"project": "BusinessAtlas-Self"},
            "verificationStatus": "observed", "sourceIds": [source_id(path)],
        })
    if base is not None:
        changed = git(root, "diff", "--name-only", "--no-renames", base, commit).splitlines()
        changed = [safe_path(path) for path in changed]
        result.append({
            "stableExternalId": f"SELF-CHANGE-{base[:12]}-{commit[:12]}", "typeId": "Observation",
            "name": f"Self Project change {base[:12]} → {commit[:12]}",
            "description": "A later real repository change derived from the exact Git commit interval. It requires independent review before publication.",
            "properties": {"baseCommitSha": base, "headCommitSha": commit, "changedPaths": changed, "changeCount": len(changed)},
            "categoryAssignments": ["SelfProject"], "applicableContext": {"project": "BusinessAtlas-Self"},
            "verificationStatus": "observed", "sourceIds": [source_id(path) for path in changed if path in source_paths] or [source_id(trace_path)],
        })
    return result


def build_package(root: Path, commit_value: str, base_value: str | None = None, *, generated_at: str | None = None) -> dict[str, Any]:
    commit = revision(root, commit_value)
    base = revision(root, base_value) if base_value else None
    if base:
        require(git(root, "merge-base", base, commit) == base, "The prior Self Project baseline must be an ancestor of the change")
        require(base != commit, "A change writeback requires a later, different commit")
    paths = load_proposal_sources(root, commit)
    if base:
        paths = sorted(set(paths) | {safe_path(path) for path in git(root, "diff", "--name-only", "--diff-filter=ACMR", base, commit).splitlines() if path})
    timestamp = generated_at or datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    package_id = f"AKP-SELF-{base[:12]}-{commit[:12]}" if base else f"AKP-SELF-{commit[:12]}"
    return {
        "manifest": {
            "formatVersion": "AKP/0.1", "packageId": package_id,
            "sourceProjectId": "BusinessAtlas-Self", "projectName": "Business Atlas",
            "generatedAt": timestamp, "producer": {"kind": "tool", "identity": "scripts/self-project/self_project.py"},
            "sourceRevision": commit,
            "importPurpose": "Traceable Self Project change candidate; human review and publication are still required" if base else "Traceable initial Self Project candidate; human review and publication are still required",
            "declaredScope": ["requirements", "implementation", "source provenance", "change writeback" if base else "bootstrap"],
        },
        "entities": entities(root, commit, paths, base), "assertions": [],
        "businessStructures": {}, "technicalStructures": {"codeRevision": commit, "architectureRevision": commit},
        "sources": [source(root, commit, path) for path in paths],
        "unknownsAndConflicts": [{
            "id": f"SELF-APPROVAL-{commit[:12]}", "kind": "unverified-inference",
            "title": "Candidate requires human review", "description": "Import and observed source facts do not constitute approval or a published baseline.",
            "relatedExternalIds": ["T38"], "status": "open", "blocksFormal": False,
        }],
        "importProvenance": {
            "agentRunId": "self-project-git-collector", "toolExecutionRefs": [f"git rev-parse {commit}", f"git ls-tree {commit}", f"git diff {base} {commit}" if base else "initial source snapshot"],
            "sourceFileRevisions": {path: commit for path in paths}, "extractedAt": timestamp,
            "extractionStrategy": "Exact Git object reads with SHA-256 and Git blob identities; no synthetic business facts",
        },
    }


def verify_package(root: Path, document: dict[str, Any]) -> None:
    manifest = document["manifest"]
    require(manifest["formatVersion"] == "AKP/0.1", "Unsupported package format")
    commit = revision(root, manifest["sourceRevision"])
    require(manifest["sourceProjectId"] == "BusinessAtlas-Self", "Not a Business Atlas Self Project package")
    ids = set()
    for item in document["sources"]:
        path = safe_path(item["path"])
        content = source_bytes(root, commit, path)
        require(item["revision"] == commit and item["contentHash"] == hashlib.sha256(content).hexdigest(), f"Source hash mismatch: {path}")
        require(item["sourceId"] == source_id(path), f"Unstable source identity: {path}")
        require(item["sourceId"] not in ids, f"Duplicate source: {path}")
        ids.add(item["sourceId"])
    entity_ids = set()
    for item in document["entities"]:
        require(item["stableExternalId"] not in entity_ids, f"Duplicate entity: {item['stableExternalId']}")
        entity_ids.add(item["stableExternalId"])
        require(set(item.get("sourceIds", [])) <= ids, f"Unresolved source: {item['stableExternalId']}")
        require(item["verificationStatus"] != "approved" if "approved" in item else True, "Approval cannot be imported as a source observation")
    require(SELF_REQUIREMENTS <= entity_ids and "T38" in entity_ids, "Self Project trace is incomplete")


def write_atomic(path: Path, document: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = (json.dumps(document, ensure_ascii=False, indent=2) + "\n").encode()
    if path.exists():
        require(path.read_bytes() == payload, f"Refusing to overwrite different candidate bytes: {path}")
        return
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_bytes(payload)
    temporary.replace(path)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    sub = parser.add_subparsers(dest="command", required=True)
    build = sub.add_parser("build")
    build.add_argument("--revision", default="HEAD")
    build.add_argument("--base-revision")
    build.add_argument("--generated-at")
    build.add_argument("--output", type=Path, required=True)
    verify = sub.add_parser("verify")
    verify.add_argument("package", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    if args.command == "build":
        document = build_package(root, args.revision, args.base_revision, generated_at=args.generated_at)
        verify_package(root, document)
        write_atomic(args.output, document)
        print(f"Self Project candidate {document['manifest']['packageId']}: {len(document['entities'])} entities, {len(document['sources'])} exact sources")
    else:
        document = json.loads(args.package.read_text(encoding="utf-8"))
        verify_package(root, document)
        print(f"Verified Self Project candidate {document['manifest']['packageId']} against {document['manifest']['sourceRevision']}")


if __name__ == "__main__":
    try:
        main()
    except (SelfProjectError, KeyError, ValueError, json.JSONDecodeError) as exc:
        raise SystemExit(f"Self Project candidate failed: {exc}")
