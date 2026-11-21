#!/usr/bin/env python3
"""Validate original Git knowledge proposals and create trusted post-merge artifacts."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import tempfile
from datetime import datetime, timezone
from typing import Any

import jsonschema

ROOT = Path(__file__).resolve().parents[2]
SCHEMAS = ROOT / "contracts" / "schemas"
SHA = re.compile(r"^[0-9a-f]{40}$")
DIGEST = re.compile(r"^(sha256:)?[0-9a-f]{64}$")


class DeliveryError(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise DeliveryError(message)


def sha256(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def require_actual_pull_request_id(value: str) -> None:
    require(value == value.strip() and bool(value) and value.casefold() != "pending", "actual pull request ID is required")


def unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        require(key not in result, f"duplicate JSON key: {key}")
        result[key] = value
    return result


def parse(path: Path) -> tuple[dict[str, Any], bytes]:
    require(path.is_file() and not path.is_symlink(), "proposal must be a regular file")
    raw = path.read_bytes()
    try:
        document = json.loads(raw, object_pairs_hook=unique_object)
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise DeliveryError(f"invalid proposal JSON: {exc}") from exc
    require(isinstance(document, dict), "proposal root must be an object")
    return document, raw


def safe_path(value: str) -> str:
    path = PurePosixPath(value)
    require(bool(value) and "\\" not in value and not path.is_absolute() and ".." not in path.parts and "." not in path.parts, f"unsafe relative path: {value}")
    return path.as_posix()


def validate_schema(document: dict[str, Any]) -> str:
    version = document.get("schemaVersion")
    require(version in {"1.2.0", "1.3.0"}, "only knowledge proposal v1.2/v1.3 are accepted")
    schema_name = "knowledge-change-proposal-v1.2.schema.json" if version == "1.2.0" else "knowledge-change-proposal-v1.3.schema.json"
    schema = json.loads((SCHEMAS / schema_name).read_text(encoding="utf-8"))
    try:
        jsonschema.Draft202012Validator(schema).validate(document)
    except jsonschema.ValidationError as exc:
        raise DeliveryError(f"proposal schema {version} rejected at {'/'.join(map(str, exc.path))}: {exc.message}") from exc
    return version


def git(root: Path, *args: str) -> str:
    result = subprocess.run(["git", "-C", str(root), *args], check=False, capture_output=True, text=True)
    require(result.returncode == 0, f"git verification failed: {args[0]}")
    return result.stdout.strip()


def blob_at(root: Path, revision: str, path: str) -> bytes:
    result = subprocess.run(["git", "-C", str(root), "show", f"{revision}:{path}"], check=False, capture_output=True)
    require(result.returncode == 0, f"missing candidate source: {path}")
    return result.stdout


def validate_controlled_trace(
    root: Path, revision: str, *, task_id: str, requirements: set[str], declared_digests: Any,
) -> None:
    """Bind a proposal to the immutable DAG and contract tree at the PR base."""
    freeze_path = "docs/decisions/T01-contract-freeze.json"
    dag_path = "docs/baselines/T1.0/03_开发执行/task-dag.json"
    manifest_path = "contracts/manifest.json"
    try:
        freeze = json.loads(blob_at(root, revision, freeze_path), object_pairs_hook=unique_object)
        dag = json.loads(blob_at(root, revision, dag_path), object_pairs_hook=unique_object)
        manifest = json.loads(blob_at(root, revision, manifest_path), object_pairs_hook=unique_object)
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise DeliveryError(f"invalid controlled trace JSON: {exc}") from exc

    require(freeze.get("inputDagSha256") == sha256(blob_at(root, revision, dag_path)), "controlled DAG does not match frozen inputDagSha256")
    files = manifest.get("files")
    require(isinstance(files, list) and len(files) == manifest.get("fileCount"), "controlled contract manifest file count mismatch")
    entries: dict[str, dict[str, Any]] = {}
    for item in files:
        require(isinstance(item, dict) and isinstance(item.get("path"), str), "invalid controlled contract manifest entry")
        path = safe_path(item["path"])
        require(path not in entries and path != "manifest.json", "duplicate or recursive controlled contract manifest path")
        entries[path] = item
    digest = hashlib.sha256()
    for path in sorted(entries):
        content = blob_at(root, revision, f"contracts/{path}")
        content_digest = sha256(content)
        require(entries[path].get("sha256") == content_digest, f"controlled contract content digest mismatch: {path}")
        digest.update(path.encode("utf-8"))
        digest.update(b"\0")
        digest.update(content_digest.encode("ascii"))
        digest.update(b"\n")
    current_digest = digest.hexdigest()
    require(manifest.get("treeSha256") == current_digest, "controlled contract tree digest mismatch")
    require(freeze.get("machineContractFreeze", {}).get("treeSha256") == current_digest, "contract tree does not match T01 freeze")
    require(declared_digests == [f"sha256:{current_digest}"], "proposal contractDigests must exactly match the current frozen contract tree")

    tasks = dag.get("tasks")
    require(isinstance(tasks, list), "controlled DAG tasks must be an array")
    matching_tasks = [task for task in tasks if isinstance(task, dict) and task.get("taskId") == task_id]
    require(len(matching_tasks) == 1, "proposal taskId is not a unique controlled task in the frozen DAG")
    controlled_requirements = matching_tasks[0].get("requirementIds")
    require(isinstance(controlled_requirements, list) and all(isinstance(item, str) for item in controlled_requirements), "controlled task requirements are invalid")
    require(len(controlled_requirements) == len(set(controlled_requirements)), "controlled task contains duplicate requirement IDs")
    require(requirements == set(controlled_requirements), "proposal requirements do not match the controlled task trace")


def changed_proposals(root: Path, base_sha: str, candidate_sha: str) -> list[Path]:
    """Only newly changed candidates belong to this PR; merged history stays immutable."""
    output = git(root, "diff", "--name-only", "-z", base_sha, candidate_sha, "--", "knowledge/proposals/*.json", "knowledge/proposals/**/*.json")
    paths = [root / safe_path(item) for item in output.split("\0") if item]
    require(bool(paths), "PR has no knowledge proposal")
    return paths


def reviewed_patch(root: Path, base_sha: str, head_sha: str) -> bytes:
    """Return the complete deterministic tree patch, including binary bytes and modes."""
    result = subprocess.run(
        ["git", "-C", str(root), "diff", "--no-renames", "--binary", "--full-index", base_sha, head_sha],
        check=False, capture_output=True,
    )
    require(result.returncode == 0, "git verification failed: reviewed patch")
    return result.stdout


def merged_proposals(root: Path, candidate_sha: str, merge_sha: str, merge_strategy: str, base_sha: str) -> list[Path]:
    """Bind the exact reviewed candidate and its complete patch to this delivery commit."""
    require(merge_strategy in {"merge", "squash", "rebase"}, "unsupported merge strategy")
    require(
        all(SHA.fullmatch(value) for value in (base_sha, candidate_sha, merge_sha)),
        "base/candidate/merge SHA must be full lowercase Git SHA",
    )
    require(git(root, "rev-parse", "HEAD") == merge_sha, "merge SHA must equal checked-out HEAD")
    require(git(root, "rev-parse", f"{candidate_sha}^{{commit}}") == candidate_sha, "reviewed candidate commit is not present")
    require(git(root, "merge-base", base_sha, candidate_sha) == base_sha, "base is not an ancestor of reviewed candidate")
    parents = git(root, "rev-list", "--parents", "-n", "1", merge_sha).split()[1:]
    require(bool(parents), "delivery commit must have a target parent")
    target_parent = parents[0]
    require(git(root, "merge-base", base_sha, target_parent) == base_sha, "base is not an ancestor of delivery target")
    if merge_strategy == "merge":
        require(len(parents) == 2, "merge strategy requires exactly one target and one reviewed source parent")
        require(candidate_sha == parents[1], "reviewed candidate is not the source parent of this merge")
    else:
        require(len(parents) == 1, f"{merge_strategy} delivery must have exactly one target parent")
        # Squash/rebase have no candidate ancestry. Equality of the complete Git
        # patches proves this commit delivers exactly the reviewed tree changes,
        # not merely an equally named proposal from an unrelated PR/build.
        expected = reviewed_patch(root, base_sha, candidate_sha)
        actual = reviewed_patch(root, target_parent, merge_sha)
        if merge_strategy == "rebase":
            # A rebased PR can contain multiple commits. Find the boundary of
            # the complete replayed series, bounded by the reviewed series size.
            # Compare the full binary/mode patch, never only proposal filenames.
            remaining = int(git(root, "rev-list", "--count", f"{base_sha}..{candidate_sha}"))
            while actual != expected and remaining > 1 and target_parent != base_sha:
                boundary_parents = git(root, "rev-list", "--parents", "-n", "1", target_parent).split()[1:]
                require(len(boundary_parents) == 1, "rebased delivery series must be linear")
                target_parent = boundary_parents[0]
                require(git(root, "merge-base", base_sha, target_parent) == base_sha, "rebase boundary predates reviewed base")
                actual = reviewed_patch(root, target_parent, merge_sha)
                remaining -= 1
        require(actual == expected, "delivery commit does not contain exactly the reviewed candidate patch")
    return changed_proposals(root, target_parent, merge_sha)


def directory_digest(root: Path) -> str:
    """Hash real verification bytes, including relative names, and reject empty input."""
    require(root.is_dir() and not root.is_symlink(), "verification artifact directory is missing")
    files = sorted(path for path in root.rglob("*") if path.is_file())
    require(bool(files), "verification artifact directory contains no files")
    digest = hashlib.sha256()
    for path in files:
        require(not path.is_symlink(), "verification artifact cannot contain symbolic links")
        relative = path.relative_to(root).as_posix()
        safe_path(relative)
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        digest.update(hashlib.sha256(path.read_bytes()).digest())
    return digest.hexdigest()


def validate_candidate(
    document: dict[str, Any], raw: bytes, *, root: Path, task_id: str,
    requirements: set[str], repo_url: str, branch: str, pull_request_id: str,
    base_sha: str, candidate_sha: str,
) -> None:
    version = validate_schema(document)
    require(document["taskId"] == task_id, "proposal task does not match PR task")
    require(set(document["requirementIds"]) == requirements, "proposal requirements do not match PR scope")
    validate_controlled_trace(
        root, base_sha, task_id=task_id, requirements=requirements,
        declared_digests=document.get("contractDigests"),
    )
    require(document["repo"]["url"] == repo_url, "proposal repository does not match origin")
    require(bool(branch) and document["repo"]["branch"] == branch, "proposal branch does not match actual PR source branch")
    require_actual_pull_request_id(pull_request_id)
    require(document["repo"]["pullRequestId"] == pull_request_id, "proposal pull request ID does not match actual PR")
    require(document["knowledgeStatus"] == "proposed", "candidate cannot claim approval or publication")
    require(SHA.fullmatch(base_sha) and SHA.fullmatch(candidate_sha), "base/candidate SHA must be full lowercase Git SHA")
    require(git(root, "rev-parse", f"{candidate_sha}^{{commit}}") == candidate_sha, "candidate commit is not present")
    require(git(root, "merge-base", base_sha, candidate_sha) == base_sha, "base is not an ancestor of candidate")
    if version == "1.3.0":
        require(document["repo"].get("baseSha") == base_sha, "proposal baseSha mismatch")
        require("headSha" not in document["repo"] and "finalCommitSha" not in document and "mergeSha" not in document, "candidate contains forbidden self/merge SHA")

    output = subprocess.run(["git", "-C", str(root), "diff", "--name-status", "--no-renames", "-z", base_sha, candidate_sha], check=True, capture_output=True).stdout
    fields = output.decode("utf-8").split("\0")
    actual: dict[str, str] = {}
    index = 0
    while index < len(fields) and fields[index]:
        status, path = fields[index:index + 2]
        actual[path] = {"A": "added", "M": "modified", "D": "removed"}[status[0]]
        index += 2
    # Proposal bytes are bound separately by proposalSha256. Excluding them here
    # avoids an impossible self-referential content hash, per the v1.3 protocol.
    actual = {path: change for path, change in actual.items() if not path.startswith("knowledge/proposals/")}
    declared = {safe_path(item["path"]): item for item in document["changedFiles"]}
    require(len(declared) == len(document["changedFiles"]), "duplicate changedFiles path")
    require(set(declared) == set(actual), "changedFiles must exactly match the candidate Git diff excluding proposal files")
    for path, item in declared.items():
        require(item["changeType"] == actual[path], f"changeType mismatch: {path}")
        if actual[path] != "removed":
            content = blob_at(root, candidate_sha, path)
            require(item.get("contentSha256") == sha256(content), f"changedFiles contentSha256 mismatch: {path}")
            if "blobSha" in item:
                require(item["blobSha"] == git(root, "rev-parse", f"{candidate_sha}:{path}"), f"blobSha mismatch: {path}")
    for anchor in document["sourceAnchors"]:
        path = safe_path(anchor["path"])
        content = blob_at(root, candidate_sha, path)
        if "contentSha256" in anchor:
            require(anchor["contentSha256"] == sha256(content), f"source content hash mismatch: {path}")
        if "blobSha" in anchor:
            require(anchor["blobSha"] == git(root, "rev-parse", f"{candidate_sha}:{path}"), f"source blob hash mismatch: {path}")
    require(bool(raw), "empty proposal")


def atomic_write(path: Path, payload: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        if path.name == "knowledge-delivery-manifest-v1.3.json":
            left = json.loads(path.read_bytes())
            right = json.loads(payload)
            left.pop("verifiedAt", None)
            right.pop("verifiedAt", None)
            require(left == right, f"refusing to overwrite different evidence: {path.name}")
        else:
            require(path.read_bytes() == payload, f"refusing to overwrite different evidence: {path.name}")
        return
    fd, temporary = tempfile.mkstemp(prefix=path.name + ".", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        try:
            os.link(temporary, path)
        except FileExistsError:
            existing = path.read_bytes()
            if path.name == "knowledge-delivery-manifest-v1.3.json":
                left = json.loads(existing)
                right = json.loads(payload)
                left.pop("verifiedAt", None)
                right.pop("verifiedAt", None)
                require(left == right, f"concurrent evidence conflict: {path.name}")
            else:
                require(existing == payload, f"concurrent evidence conflict: {path.name}")
    finally:
        os.unlink(temporary)


def create_manifest(
    proposal_path: Path, output_dir: Path, *, candidate_sha: str, merge_sha: str,
    merge_strategy: str, repo_url: str, branch: str, pull_request_id: str,
    build_id: str, build_digest: str, ci_run_id: str,
    artifact_uri: str, verification_root: Path, verification_artifact_uri: str,
    candidate_build_id: str, post_merge_build_id: str, attestation_path: Path,
    signer_id: str = "CIService",
) -> dict[str, Any]:
    document, raw = parse(proposal_path)
    require(validate_schema(document) == "1.3.0", "post-merge manifests require native v1.3 proposals")
    require(document["repo"]["url"] == repo_url, "manifest repository does not match validated PR identity")
    require(document["repo"]["branch"] == branch, "manifest branch does not match validated PR identity")
    require_actual_pull_request_id(pull_request_id)
    require(document["repo"]["pullRequestId"] == pull_request_id, "manifest pull request ID does not match validated PR identity")
    require(SHA.fullmatch(candidate_sha) and SHA.fullmatch(merge_sha), "candidate/merge SHA must be full lowercase Git SHA")
    require(DIGEST.fullmatch(build_digest), "buildDigest must be a SHA-256 digest")
    actual_build_digest = directory_digest(verification_root)
    require(build_digest.removeprefix("sha256:") == actual_build_digest, "buildDigest does not match downloaded verification artifact")
    require(all(document["testEvidence"]), "test evidence is required")
    require(all(item["result"] == "passed" for item in document["testEvidence"]), "verified manifest cannot contain failed/skipped evidence")
    require(all("pending" not in item["artifactUri"].casefold() for item in document["testEvidence"]), "verified manifest cannot contain placeholder evidence locators")
    test_evidence = [
        {**item, "artifactUri": verification_artifact_uri, "artifactSha256": actual_build_digest}
        for item in document["testEvidence"]
    ]
    require(document["taskId"] and document["projectId"], "task/project identity required")
    require(bool(candidate_build_id) and candidate_build_id != post_merge_build_id, "candidate and post-merge build IDs must be distinct")
    require(build_id == post_merge_build_id and ci_run_id == post_merge_build_id, "manifest build identity must be the actual post-merge run")
    require(attestation_path.is_file() and not attestation_path.is_symlink(), "Azure candidate-build attestation is missing")
    attestation_raw = attestation_path.read_bytes()
    try:
        attestation = json.loads(attestation_raw, object_pairs_hook=unique_object)
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise DeliveryError(f"invalid candidate-build attestation: {exc}") from exc
    require(attestation.get("schemaVersion") == "1.0.0", "unsupported candidate-build attestation schema")
    require(attestation.get("verifiedBy") == "Azure DevOps Builds API", "candidate build was not verified by Azure DevOps")
    expected_attestation = {
        "candidateBuildId": candidate_build_id, "candidateSha": candidate_sha,
        "pullRequestId": pull_request_id, "sourceBranch": branch,
        "reason": "pullRequest", "status": "completed", "result": "succeeded",
    }
    for key, value in expected_attestation.items():
        require(attestation.get(key) == value, f"candidate-build attestation mismatch: {key}")
    require(DIGEST.fullmatch(attestation.get("responseSha256", "")), "candidate-build attestation response digest is invalid")
    attestation_digest = sha256(attestation_raw)
    manifest_path = output_dir / "knowledge-delivery-manifest-v1.3.json"
    binding_path = output_dir / "candidate-binding.json"
    if manifest_path.exists() and binding_path.exists():
        existing = json.loads(manifest_path.read_text(encoding="utf-8"))
        binding = json.loads(binding_path.read_text(encoding="utf-8"))
        expected_identity = (document["proposalId"], sha256(raw), merge_sha, build_id, ci_run_id, repo_url, pull_request_id)
        actual_identity = (existing.get("proposalId"), existing.get("proposalSha256"), existing.get("finalCommitSha"), existing.get("buildId"), existing.get("ciRunId"), existing.get("repo", {}).get("url"), existing.get("repo", {}).get("pullRequestId"))
        expected_binding = {"candidateSha": candidate_sha, "mergeSha": merge_sha, "branch": branch, "pullRequestId": pull_request_id, "candidateBuildId": candidate_build_id, "postMergeBuildId": post_merge_build_id, "candidateBuildAttestationSha256": attestation_digest}
        require(actual_identity == expected_identity and all(binding.get(key) == value for key, value in expected_binding.items()), "existing artifact is bound to different evidence")
        return existing
    # A crash between the two atomic writes is recoverable: validate each existing
    # artifact against the same immutable inputs, then atomically add the missing one.
    resolutions = []
    for anchor in document["sourceAnchors"]:
        path = safe_path(anchor["path"])
        resolved = ROOT / path
        require(resolved.is_file(), f"merged source missing: {path}")
        resolved_hash = sha256(resolved.read_bytes())
        expected = anchor.get("contentSha256")
        require(expected is not None, f"verified source requires contentSha256: {path}")
        require(expected == resolved_hash, f"merged source hash mismatch: {path}")
        resolutions.append({
            "path": path, "locator": anchor["locator"],
            "resolvedBlobSha": resolved_hash, "verification": "matched",
            "explanation": "Merged source bytes match the reviewed candidate content SHA-256; no remapped source is accepted as verified.",
        })
    manifest = {
        "schemaVersion": "1.3.0",
        "deliveryId": f"{document['proposalId']}-{merge_sha[:12]}",
        "proposalId": document["proposalId"], "proposalSha256": sha256(raw),
        "projectId": document["projectId"], "taskId": document["taskId"],
        "repo": {"url": repo_url, "pullRequestId": pull_request_id},
        "finalCommitSha": merge_sha, "mergeStrategy": merge_strategy,
        "buildId": build_id, "buildDigest": build_digest.removeprefix("sha256:"),
        "verifiedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "testEvidence": test_evidence, "anchorResolutions": resolutions,
        "status": "verified", "artifactUri": artifact_uri, "ciRunId": ci_run_id, "signerId": signer_id,
    }
    schema = json.loads((SCHEMAS / "knowledge-delivery-manifest-v1.3.schema.json").read_text(encoding="utf-8"))
    jsonschema.Draft202012Validator(schema).validate(manifest)
    atomic_write(output_dir / "knowledge-delivery-manifest-v1.3.json", (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode())
    evidence = {"candidateSha": candidate_sha, "mergeSha": merge_sha, "mergeStrategy": merge_strategy, "repoUrl": repo_url, "branch": branch, "pullRequestId": pull_request_id, "proposalSha256": sha256(raw), "buildId": build_id, "ciRunId": ci_run_id, "candidateBuildId": candidate_build_id, "postMergeBuildId": post_merge_build_id, "candidateBuildAttestationSha256": attestation_digest}
    atomic_write(output_dir / "candidate-binding.json", (json.dumps(evidence, indent=2) + "\n").encode())
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    check = sub.add_parser("validate-proposal")
    check.add_argument("proposal", type=Path)
    check.add_argument("--legacy-only", action="store_true")
    digest = sub.add_parser("digest-directory")
    digest.add_argument("directory", type=Path)
    pr = sub.add_parser("validate-pr")
    pr.add_argument("--task-id", required=True)
    pr.add_argument("--requirements", required=True)
    pr.add_argument("--repo-url", required=True)
    pr.add_argument("--branch", required=True)
    pr.add_argument("--pull-request-id", required=True)
    pr.add_argument("--base-sha", required=True)
    pr.add_argument("--candidate-sha", required=True)
    pr.add_argument("--root", type=Path, default=ROOT)
    merged = sub.add_parser("list-merged-proposals")
    merged.add_argument("--base-sha", required=True)
    merged.add_argument("--candidate-sha", required=True)
    merged.add_argument("--merge-sha", required=True)
    merged.add_argument("--merge-strategy", choices=["merge", "squash", "rebase"], required=True)
    merged.add_argument("--root", type=Path, default=ROOT)
    merge = sub.add_parser("create-manifest")
    merge.add_argument("proposal", type=Path)
    merge.add_argument("--output-dir", type=Path, required=True)
    merge.add_argument("--candidate-sha", required=True)
    merge.add_argument("--merge-sha", required=True)
    merge.add_argument("--merge-strategy", choices=["merge", "squash", "rebase"], required=True)
    merge.add_argument("--repo-url", required=True)
    merge.add_argument("--branch", required=True)
    merge.add_argument("--pull-request-id", required=True)
    merge.add_argument("--build-id", required=True)
    merge.add_argument("--build-digest", required=True)
    merge.add_argument("--ci-run-id", required=True)
    merge.add_argument("--artifact-uri", required=True)
    merge.add_argument("--verification-root", required=True, type=Path)
    merge.add_argument("--verification-artifact-uri", required=True)
    merge.add_argument("--candidate-build-id", required=True)
    merge.add_argument("--post-merge-build-id", required=True)
    merge.add_argument("--attestation", required=True, type=Path)
    args = parser.parse_args()
    if args.command == "validate-proposal":
        document, _ = parse(args.proposal)
        version = validate_schema(document)
        require(not args.legacy_only or version == "1.2.0", "legacy compatibility input must remain v1.2")
        print(f"Knowledge proposal {version} schema validation passed")
    elif args.command == "digest-directory":
        print(directory_digest(args.directory))
    elif args.command == "validate-pr":
        proposals = changed_proposals(args.root.resolve(), args.base_sha, args.candidate_sha)
        requirements = set(args.requirements.split(","))
        for path in proposals:
            document, raw = parse(path)
            validate_candidate(document, raw, root=args.root.resolve(), task_id=args.task_id, requirements=requirements, repo_url=args.repo_url, branch=args.branch, pull_request_id=args.pull_request_id, base_sha=args.base_sha, candidate_sha=args.candidate_sha)
            print(f"Validated original proposal bytes: {path.name} sha256={sha256(raw)}")
    elif args.command == "list-merged-proposals":
        for path in merged_proposals(args.root.resolve(), args.candidate_sha, args.merge_sha, args.merge_strategy, args.base_sha):
            print(path.relative_to(args.root.resolve()).as_posix())
    else:
        manifest = create_manifest(args.proposal, args.output_dir, candidate_sha=args.candidate_sha, merge_sha=args.merge_sha, merge_strategy=args.merge_strategy, repo_url=args.repo_url, branch=args.branch, pull_request_id=args.pull_request_id, build_id=args.build_id, build_digest=args.build_digest, ci_run_id=args.ci_run_id, artifact_uri=args.artifact_uri, verification_root=args.verification_root, verification_artifact_uri=args.verification_artifact_uri, candidate_build_id=args.candidate_build_id, post_merge_build_id=args.post_merge_build_id, attestation_path=args.attestation)
        print(f"Manifest {manifest['deliveryId']} bound to merge {args.merge_sha}")


if __name__ == "__main__":
    try:
        main()
    except (DeliveryError, jsonschema.ValidationError, subprocess.CalledProcessError) as exc:
        raise SystemExit(f"Knowledge delivery gate failed: {exc}")
