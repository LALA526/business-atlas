#!/usr/bin/env python3
"""Build an evidence-bound T1.0 release manifest and operator handoff.

This utility deliberately separates implementation evidence from acceptance. It never
invents approvals, deployment records, or requirement completion.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import re
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TRACE = ROOT / "docs/baselines/T1.0/03_开发执行/requirements-trace.csv"
STATES = {
    "not_started", "in_progress", "blocked", "implemented", "verified",
    "accepted", "deferred_approved",
}
EVIDENCE_KEYS = (
    "implementationRefs", "operationIds", "databaseRefs", "eventRefs",
    "testRefs", "knowledgeDeliveryRefs", "deploymentRefs",
)
HEX40 = re.compile(r"^[0-9a-f]{40}$")


class ReleaseError(ValueError):
    pass


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def load_json(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ReleaseError(f"{path} must contain a JSON object")
    return value


def safe_ref(value: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ReleaseError("Evidence references must be non-empty strings")
    if "\x00" in value or "\n" in value or "\r" in value:
        raise ReleaseError("Evidence references must be single-line values")
    return value


def file_record(root: Path, relative: str) -> dict:
    path = root / relative
    if not path.is_file():
        raise ReleaseError(f"Required release input is missing: {relative}")
    return {"path": relative, "sha256": sha256(path), "bytes": path.stat().st_size}


def load_trace() -> list[dict]:
    with TRACE.open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    if len(rows) != 154:
        raise ReleaseError(f"Expected 154 requirements, found {len(rows)}")
    ids = [row["requirement_id"] for row in rows]
    if len(ids) != len(set(ids)):
        raise ReleaseError("Requirement IDs must be unique")
    return rows


def requirement_record(source: dict, override: dict) -> dict:
    requirement_id = source["requirement_id"]
    status = override.get("status", "not_started")
    if status not in STATES:
        raise ReleaseError(f"{requirement_id}: invalid status {status!r}")
    unknown = set(override) - {"status", "notes", "ownerDecisionRef", *EVIDENCE_KEYS}
    if unknown:
        raise ReleaseError(f"{requirement_id}: unknown evidence fields {sorted(unknown)}")
    evidence = {
        key: [safe_ref(ref) for ref in override.get(key, [])]
        for key in EVIDENCE_KEYS
    }
    if status in {"implemented", "verified", "accepted", "deferred_approved"}:
        if not evidence["implementationRefs"] or not evidence["testRefs"]:
            raise ReleaseError(
                f"{requirement_id}: {status} requires implementation and test refs"
            )
    if status in {"verified", "accepted", "deferred_approved"}:
        if not evidence["knowledgeDeliveryRefs"]:
            raise ReleaseError(f"{requirement_id}: {status} requires knowledge evidence")
    if status == "accepted" and not evidence["deploymentRefs"]:
        raise ReleaseError(f"{requirement_id}: accepted requires deployment evidence")
    if status == "deferred_approved" and not override.get("ownerDecisionRef"):
        raise ReleaseError(f"{requirement_id}: deferral requires an owner decision")
    return {
        "requirementId": requirement_id,
        "title": source["title"],
        "priority": source["priority"],
        "workpackageId": source["workpackage"],
        "taskIds": [part for part in source["implementation_tasks"].split(";") if part],
        "testIds": [part for part in source["planned_tests"].split(";") if part],
        "status": status,
        "evidence": evidence,
        "notes": override.get("notes", ""),
        "ownerDecisionRef": override.get("ownerDecisionRef"),
    }


def validate_approvals(approvals: object) -> list[dict]:
    if not isinstance(approvals, list):
        raise ReleaseError("approvals must be a list")
    result = []
    for approval in approvals:
        if not isinstance(approval, dict) or set(approval) != {
            "role", "identity", "decision", "signedAt", "evidenceRef"
        }:
            raise ReleaseError("Each approval needs role, identity, decision, signedAt and evidenceRef")
        if approval["role"] not in {"QA", "BA", "Leader", "ProductOwner"}:
            raise ReleaseError("Unknown approval role")
        if approval["decision"] not in {"approved", "rejected"}:
            raise ReleaseError("Approval decision must be approved or rejected")
        for key in ("role", "identity", "decision", "signedAt", "evidenceRef"):
            safe_ref(approval[key])
        # Human identities are deliberately supplied as evidence, never generated here.
        result.append({**approval, "actorType": "human"})
    return result


def knowledge_archive(output: Path, records: list[dict]) -> dict:
    """Create a deterministic, secret-free archive of versioned project knowledge."""
    archive_path = output / "knowledge-archive.zip"
    files = []
    for base in (ROOT / "knowledge", ROOT / "contracts", ROOT / "docs/adr", ROOT / "docs/decisions"):
        if base.is_dir():
            files.extend(path for path in base.rglob("*") if path.is_file())
    files.extend(path for path in (ROOT / "docs/baselines/T1.0").rglob("*") if path.is_file())
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(set(files), key=lambda item: item.as_posix()):
            relative = path.relative_to(ROOT).as_posix()
            info = zipfile.ZipInfo(relative, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, path.read_bytes())
    record = file_record(output, archive_path.name)
    records.append(record)
    return record


def render_handoff(manifest: dict) -> str:
    counts = {state: 0 for state in sorted(STATES)}
    for row in manifest["requirements"]:
        counts[row["status"]] += 1
    lines = [
        "# Business Atlas T1.0 handoff",
        "",
        f"Release: `{manifest['releaseId']}`  ",
        f"Source: `{manifest['source']['commit']}`  ",
        f"Decision: **{manifest['checkout']['decision']}**",
        "",
        "## Access and operation",
        "",
    ]
    deployments = manifest["deployments"]
    if deployments:
        for deployment in deployments:
            lines.append(f"- {deployment['environment']}: {deployment['url']}")
    else:
        lines.append("- No verified deployment URL has been supplied.")
    lines.extend([
        "- Credentials remain in the approved private login configuration; they are not archived.",
        "- Start from the project home, then use import, reading, review, change, delivery and assistant journeys with project-scoped permissions.",
        "- Operations, backup and recovery: `deploy/ops/RUNBOOK.md` and `deploy/backup/README.md`.",
        "",
        "## Requirement traceability",
        "",
        " | ".join(["Status", "Count"]),
        " | ".join(["---", "---:"]),
    ])
    lines.extend(f" | ".join([state, str(count)]) for state, count in counts.items())
    lines.extend(["", "## Gates", ""])
    for gate in manifest["gates"]:
        lines.append(f"- {gate['gate']}: **{gate['status']}** — {gate.get('notes') or 'Evidence remains required.'}")
    lines.extend(["", "## Risks and unfinished work", ""])
    risks = manifest["risks"]
    lines.extend(f"- {risk['severity']}: {risk['description']} (owner: {risk['owner']})" for risk in risks)
    if not risks:
        lines.append("- None recorded.")
    lines.extend([
        "",
        "`implemented` and `verified` are not human acceptance. Only explicit QA/BA/Leader/ProductOwner approvals can authorize final checkout.",
        "",
    ])
    return "\n".join(lines)


def build(args: argparse.Namespace) -> dict:
    evidence_path = Path(args.evidence).resolve()
    evidence = load_json(evidence_path)
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    source = evidence.get("source", {})
    commit = source.get("commit") or subprocess.check_output(
        ["git", "-C", str(ROOT), "rev-parse", "HEAD"], text=True
    ).strip()
    if not HEX40.fullmatch(commit):
        raise ReleaseError("A real 40-character source commit is required")
    trace = load_trace()
    overrides = evidence.get("requirements", {})
    if not isinstance(overrides, dict) or set(overrides) - {row["requirement_id"] for row in trace}:
        raise ReleaseError("Requirement evidence contains unknown IDs")
    requirements = [requirement_record(row, overrides.get(row["requirement_id"], {})) for row in trace]
    approvals = validate_approvals(evidence.get("approvals", []))
    approved_roles = {row["role"] for row in approvals if row["decision"] == "approved"}
    accepted = all(row["status"] in {"accepted", "deferred_approved"} for row in requirements)
    required_roles = {"QA", "BA", "Leader", "ProductOwner"}
    has_blocker = any(risk.get("severity") == "blocking" for risk in evidence.get("risks", [])) or any(
        row["decision"] == "rejected" for row in approvals
    )
    decision = "accepted" if accepted and approved_roles >= required_roles and not has_blocker else "not_ready"
    gates = evidence.get("gates", [])
    if not isinstance(gates, list):
        raise ReleaseError("gates must be a list")
    by_gate = {gate.get("gate"): gate for gate in gates if isinstance(gate, dict)}
    gate_records = []
    for number in range(8):
        gate_id = f"G{number}"
        gate = by_gate.get(gate_id, {})
        if set(gate) - {"gate", "status", "evidenceRefs", "notes"}:
            raise ReleaseError(f"{gate_id}: unknown fields")
        status = gate.get("status", "not_started")
        if status not in {"not_started", "in_progress", "blocked", "verified", "accepted"}:
            raise ReleaseError(f"{gate_id}: invalid status")
        refs = [safe_ref(ref) for ref in gate.get("evidenceRefs", [])]
        if status in {"verified", "accepted"} and not refs:
            raise ReleaseError(f"{gate_id}: {status} requires evidence")
        gate_records.append({"gate": gate_id, "status": status, "evidenceRefs": refs, "notes": gate.get("notes", "")})
    if decision == "accepted" and any(gate["status"] != "accepted" for gate in gate_records):
        decision = "not_ready"
    deployments = evidence.get("deployments", [])
    for deployment in deployments:
        if not isinstance(deployment, dict) or not {"environment", "url", "deployedAt", "evidenceRef"} <= set(deployment):
            raise ReleaseError("Deployments require environment, URL, time and evidence")
        if not deployment["url"].startswith("https://"):
            raise ReleaseError("Deployment access URLs must use HTTPS")
        for key in ("environment", "url", "deployedAt", "evidenceRef"):
            safe_ref(deployment[key])
    risks = evidence.get("risks", [])
    for risk in risks:
        if not isinstance(risk, dict) or not {"severity", "description", "owner"} <= set(risk):
            raise ReleaseError("Risks require severity, description and owner")
    artifacts = []
    archive = knowledge_archive(output, artifacts)
    contract_files = [file_record(ROOT, f"contracts/{path.relative_to(ROOT / 'contracts').as_posix()}")
                      for path in sorted((ROOT / "contracts").rglob("*")) if path.is_file()]
    source_records = [file_record(ROOT, relative) for relative in (
        "docs/baselines/T1.0/README.md",
        "docs/baselines/T1.0/03_开发执行/requirements-trace.csv",
        "docs/baselines/T1.0/03_开发执行/task-dag.json",
        "docs/ui-reference/source-manifest.json",
    )]
    manifest = {
        "schemaVersion": "1.0.0",
        "releaseId": evidence.get("releaseId", f"T1.0-{commit[:12]}"),
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "source": {"repository": source.get("repository", "Business Atlas authorized project"), "commit": commit},
        "inputs": {"evidenceSha256": sha256(evidence_path), "sourceFiles": source_records, "contracts": contract_files},
        "schemaMigrationConfigVersions": evidence.get("schemaMigrationConfigVersions", {}),
        "build": evidence.get("build", {}),
        "deployments": deployments,
        "selfProject": evidence.get("selfProject", {}),
        "knowledge": {"archive": archive, "baselines": evidence.get("knowledgeBaselines", [])},
        "requirements": requirements,
        "gates": gate_records,
        "approvals": approvals,
        "risks": risks,
        "checkout": {
            "decision": decision,
            "blockerCount": (
                sum(row["status"] == "blocked" for row in requirements)
                + sum(risk.get("severity") == "blocking" for risk in risks)
                + sum(row["decision"] == "rejected" for row in approvals)
            ),
            "unacceptedCount": sum(row["status"] not in {"accepted", "deferred_approved"} for row in requirements),
            "requiredHumanRoles": sorted(required_roles),
        },
        "artifacts": artifacts,
    }
    manifest_path = output / "release-manifest.json"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (output / "HANDOFF.md").write_text(render_handoff(manifest), encoding="utf-8")
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence", required=True, help="Actual evidence JSON; no sample values are accepted implicitly")
    parser.add_argument("--output", required=True, help="Output directory for manifest, handoff and knowledge archive")
    args = parser.parse_args()
    try:
        manifest = build(args)
    except (ReleaseError, OSError, json.JSONDecodeError, subprocess.CalledProcessError) as error:
        print(f"release handoff failed: {error}", file=sys.stderr)
        return 2
    print(json.dumps(manifest["checkout"], ensure_ascii=False))
    return 0 if manifest["checkout"]["decision"] == "accepted" else 1


if __name__ == "__main__":
    raise SystemExit(main())
