#!/usr/bin/env python3
"""Attest the reviewed candidate using the Azure DevOps Builds service, not artifacts."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Callable


class AttestationError(RuntimeError):
    """The candidate build could not be authenticated by Azure DevOps."""


class AttestationPermissionError(AttestationError):
    """The build identity check is blocked by external Azure permissions."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AttestationError(message)


def _string(document: dict[str, Any], key: str) -> str:
    value = document.get(key)
    require(isinstance(value, str) and bool(value), f"Azure Build field {key} is missing")
    return value


def fetch_build(
    *, collection_uri: str, project_id: str, build_id: str, access_token: str,
    transport: Callable[..., Any] = urllib.request.urlopen,
) -> tuple[dict[str, Any], bytes, str]:
    require(collection_uri.startswith("https://"), "Azure collection URI must use HTTPS")
    require(all(value and value == value.strip() for value in (project_id, build_id, access_token)), "Azure project, build ID and access token are required")
    require(re.fullmatch(r"[0-9]+", build_id) is not None, "Azure candidate build ID must be numeric")
    base = collection_uri.rstrip("/")
    parsed = urllib.parse.urlsplit(base)
    require(parsed.hostname is not None and parsed.username is None and parsed.password is None, "invalid Azure collection URI")
    url = f"{base}/{urllib.parse.quote(project_id, safe='')}/_apis/build/builds/{build_id}?api-version=7.1"
    request = urllib.request.Request(url, headers={"Authorization": f"Bearer {access_token}", "Accept": "application/json"}, method="GET")
    try:
        with transport(request, timeout=30) as response:
            status = getattr(response, "status", 200)
            require(status == 200, f"Azure Builds API returned HTTP {status}")
            raw = response.read(1024 * 1024 + 1)
            require(len(raw) <= 1024 * 1024, "Azure Builds API response exceeds 1 MiB")
    except urllib.error.HTTPError as exc:
        if exc.code in {401, 403}:
            raise AttestationPermissionError(f"Azure Builds API permission denied (HTTP {exc.code}); grant System.AccessToken Build read permission") from exc
        if exc.code == 404:
            raise AttestationError("Azure candidate build was not found in this collection/project") from exc
        raise AttestationError(f"Azure Builds API failed with HTTP {exc.code}") from exc
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        raise AttestationError(f"Azure Builds API transport failed: {type(exc).__name__}") from exc
    try:
        document = json.loads(raw)
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise AttestationError("Azure Builds API returned malformed JSON") from exc
    require(isinstance(document, dict), "Azure Builds API response must be an object")
    return document, raw, url


def attest_build(
    document: dict[str, Any], raw: bytes, url: str, *, collection_uri: str,
    project_id: str, definition_id: str, build_id: str, candidate_sha: str,
    pull_request_id: str, source_branch: str,
) -> dict[str, Any]:
    require(document.get("id") == int(build_id), "Azure response build ID mismatch")
    project = document.get("project")
    definition = document.get("definition")
    require(isinstance(project, dict) and project.get("id") == project_id, "Azure candidate build project mismatch")
    require(isinstance(definition, dict) and str(definition.get("id")) == definition_id, "Azure candidate build definition mismatch")
    parameters=document.get("parameters")
    if parameters:
        try:parameters=json.loads(parameters) if isinstance(parameters,str) else parameters
        except ValueError as error:raise AttestationError("Malformed Azure PR parameters") from error
        require(isinstance(parameters,dict), "Azure PR parameters must be an object")
        require(parameters.get("system.pullRequest.sourceCommitId")==candidate_sha,"Azure PR sourceCommitId does not equal reviewed candidate")
        require(str(parameters.get("system.pullRequest.pullRequestId"))==pull_request_id,"Azure PR parameters identity mismatch")
        expected_branch=source_branch if source_branch.startswith("refs/") else "refs/heads/"+source_branch
        require(parameters.get("system.pullRequest.sourceBranch")==expected_branch,"Azure PR parameters branch mismatch")
    else:
        require(document.get("sourceVersion") == candidate_sha, "Azure candidate build sourceVersion does not equal reviewed candidate")
    require(document.get("reason") == "pullRequest", "Azure candidate build was not triggered by a pull request")
    require(document.get("status") == "completed", "Azure candidate build is not completed")
    require(document.get("result") == "succeeded", "Azure candidate build did not succeed")

    expected_source = source_branch if source_branch.startswith("refs/") else f"refs/heads/{source_branch}"
    trigger = document.get("triggerInfo")
    require(isinstance(trigger, dict), "Azure candidate build has no pull request trigger identity")
    actual_pr = str(trigger.get("pr.id",trigger.get("pr.number", "")))
    require(actual_pr == pull_request_id, "Azure candidate build pull request identity mismatch")
    actual_source=parameters.get("system.pullRequest.sourceBranch") if parameters else trigger.get("pr.sourceBranch")
    require(actual_source == expected_source, "Azure candidate build source branch mismatch")
    expected_merge = f"refs/pull/{pull_request_id}/merge"
    require(document.get("sourceBranch") == expected_merge, "Azure candidate build sourceBranch is not the expected PR merge ref")

    # The persisted attestation contains only selected non-secret service facts.
    return {
        "schemaVersion": "1.0.0",
        "collectionUri": collection_uri.rstrip("/"),
        "projectId": project_id,
        "definitionId": definition_id,
        "candidateBuildId": build_id,
        "candidateSha": candidate_sha,
        "pullRequestId": pull_request_id,
        "sourceBranch": source_branch.removeprefix("refs/heads/"),
        "buildApiUrl": url,
        "responseSha256": hashlib.sha256(raw).hexdigest(),
        "reason": "pullRequest",
        "status": "completed",
        "result": "succeeded",
        "verifiedBy": "Azure DevOps Builds API",
    }


def verify_from_environment(environment: dict[str, str], *, output: Path, transport: Callable[..., Any] = urllib.request.urlopen) -> dict[str, Any]:
    names = ("SYSTEM_COLLECTIONURI", "SYSTEM_TEAMPROJECTID", "SYSTEM_DEFINITIONID", "ATLAS_CANDIDATE_BUILD_ID", "ATLAS_CANDIDATE_SHA", "ATLAS_PULL_REQUEST_ID", "ATLAS_SOURCE_BRANCH", "SYSTEM_ACCESSTOKEN")
    values = {name: environment.get(name, "") for name in names}
    document, raw, url = fetch_build(
        collection_uri=values["SYSTEM_COLLECTIONURI"], project_id=values["SYSTEM_TEAMPROJECTID"],
        build_id=values["ATLAS_CANDIDATE_BUILD_ID"], access_token=values["SYSTEM_ACCESSTOKEN"], transport=transport,
    )
    attestation = attest_build(
        document, raw, url, collection_uri=values["SYSTEM_COLLECTIONURI"], project_id=values["SYSTEM_TEAMPROJECTID"],
        definition_id=values["SYSTEM_DEFINITIONID"], build_id=values["ATLAS_CANDIDATE_BUILD_ID"],
        candidate_sha=values["ATLAS_CANDIDATE_SHA"], pull_request_id=values["ATLAS_PULL_REQUEST_ID"],
        source_branch=values["ATLAS_SOURCE_BRANCH"],
    )
    output.parent.mkdir(parents=True, exist_ok=True)
    payload = (json.dumps(attestation, indent=2) + "\n").encode("utf-8")
    if output.exists():
        require(output.read_bytes() == payload, "refusing to overwrite a different candidate-build attestation")
        return attestation
    descriptor, temporary_name = tempfile.mkstemp(prefix=output.name + ".", dir=output.parent)
    try:
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        try:
            os.link(temporary_name, output)
        except FileExistsError:
            require(output.read_bytes() == payload, "concurrent candidate-build attestation conflict")
    finally:
        os.unlink(temporary_name)
    return attestation


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    attestation = verify_from_environment(os.environ, output=args.output)
    print(f"Azure candidate build {attestation['candidateBuildId']} verified for {attestation['candidateSha']}")


if __name__ == "__main__":
    try:
        main()
    except AttestationPermissionError as exc:
        raise SystemExit(f"External Azure permission blocker: {exc}")
    except AttestationError as exc:
        raise SystemExit(f"Candidate build attestation failed: {exc}")
