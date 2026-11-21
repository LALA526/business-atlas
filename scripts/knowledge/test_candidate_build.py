from __future__ import annotations

import copy
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
import urllib.error

import candidate_build


class Response:
    status = 200

    def __init__(self, payload: bytes):
        self.payload = payload

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return None

    def read(self, limit: int = -1) -> bytes:
        return self.payload if limit < 0 else self.payload[:limit]


class CandidateBuildTests(unittest.TestCase):
    def setUp(self) -> None:
        self.candidate = "9" * 40
        self.document = {
            "id": 42,
            "project": {"id": "project-guid"},
            "definition": {"id": 7},
            "sourceVersion": self.candidate,
            "sourceBranch": "refs/pull/123/merge",
            "reason": "pullRequest",
            "status": "completed",
            "result": "succeeded",
            "triggerInfo": {"pr.id": "123", "pr.sourceBranch": "refs/heads/task/T02"},
        }
        self.identity = dict(
            collection_uri="https://dev.azure.com/example", project_id="project-guid",
            definition_id="7", build_id="42", candidate_sha=self.candidate,
            pull_request_id="123", source_branch="task/T02",
        )

    def attest(self, document: dict | None = None) -> dict:
        raw = json.dumps(document or self.document).encode()
        return candidate_build.attest_build(document or self.document, raw, "https://dev.azure.com/example/project-guid/_apis/build/builds/42?api-version=7.1", **self.identity)

    def test_valid_service_response_produces_nonsecret_attestation(self) -> None:
        result = self.attest()
        self.assertEqual("42", result["candidateBuildId"])
        self.assertEqual(self.candidate, result["candidateSha"])
        self.assertEqual("Azure DevOps Builds API", result["verifiedBy"])
        self.assertNotIn("accessToken", result)
        self.assertEqual(64, len(result["responseSha256"]))

    def test_every_server_identity_and_outcome_mismatch_is_rejected(self) -> None:
        mutations = {
            "sourceVersion": "8" * 40,
            "reason": "individualCI",
            "status": "inProgress",
            "result": "failed",
            "sourceBranch": "refs/pull/456/merge",
        }
        for field, value in mutations.items():
            with self.subTest(field=field):
                document = copy.deepcopy(self.document)
                document[field] = value
                with self.assertRaises(candidate_build.AttestationError):
                    self.attest(document)
        for container, field, value in [
            ("project", "id", "other-project"), ("definition", "id", 99),
            ("triggerInfo", "pr.id", "456"), ("triggerInfo", "pr.sourceBranch", "refs/heads/task/other"),
        ]:
            with self.subTest(container=container, field=field):
                document = copy.deepcopy(self.document)
                document[container][field] = value
                with self.assertRaises(candidate_build.AttestationError):
                    self.attest(document)
        document = copy.deepcopy(self.document)
        document.pop("triggerInfo")
        with self.assertRaisesRegex(candidate_build.AttestationError, "trigger identity"):
            self.attest(document)

    def test_transport_authenticates_and_never_exposes_token(self) -> None:
        captured = []
        def transport(request, timeout):
            captured.append((request, timeout))
            return Response(json.dumps(self.document).encode())
        with tempfile.TemporaryDirectory() as tmp:
            environment = {
                "SYSTEM_COLLECTIONURI": "https://dev.azure.com/example",
                "SYSTEM_TEAMPROJECTID": "project-guid", "SYSTEM_DEFINITIONID": "7",
                "ATLAS_CANDIDATE_BUILD_ID": "42", "ATLAS_CANDIDATE_SHA": self.candidate,
                "ATLAS_PULL_REQUEST_ID": "123", "ATLAS_SOURCE_BRANCH": "task/T02",
                "SYSTEM_ACCESSTOKEN": "super-secret-token",
            }
            output = Path(tmp) / "candidate-build-attestation.json"
            candidate_build.verify_from_environment(environment, output=output, transport=transport)
            request, timeout = captured[0]
            self.assertEqual("Bearer super-secret-token", request.get_header("Authorization"))
            self.assertEqual(30, timeout)
            self.assertNotIn("super-secret-token", output.read_text())
            first = output.read_bytes()
            candidate_build.verify_from_environment(environment, output=output, transport=transport)
            self.assertEqual(first, output.read_bytes())
            output.write_text("tampered", encoding="utf-8")
            with self.assertRaisesRegex(candidate_build.AttestationError, "different candidate-build attestation"):
                candidate_build.verify_from_environment(environment, output=output, transport=transport)

    def test_http_permission_not_found_network_and_malformed_json_fail_closed(self) -> None:
        cases = [
            (urllib.error.HTTPError("https://example.invalid", 401, "no", {}, io.BytesIO()), candidate_build.AttestationPermissionError),
            (urllib.error.HTTPError("https://example.invalid", 403, "no", {}, io.BytesIO()), candidate_build.AttestationPermissionError),
            (urllib.error.HTTPError("https://example.invalid", 404, "no", {}, io.BytesIO()), candidate_build.AttestationError),
            (urllib.error.URLError("offline"), candidate_build.AttestationError),
        ]
        for error, expected in cases:
            with self.subTest(error=type(error).__name__, code=getattr(error, "code", None)):
                def transport(request, timeout):
                    raise error
                with self.assertRaises(expected) as caught:
                    candidate_build.fetch_build(collection_uri="https://dev.azure.com/example", project_id="project-guid", build_id="42", access_token="token", transport=transport)
                self.assertNotIn("token", str(caught.exception))
        with self.assertRaisesRegex(candidate_build.AttestationError, "malformed JSON"):
            candidate_build.fetch_build(collection_uri="https://dev.azure.com/example", project_id="project-guid", build_id="42", access_token="token", transport=lambda request, timeout: Response(b"{"))

    def test_old_self_reported_build_gate_ab_and_current_service_gate(self) -> None:
        # A/B reproduction of the former defect: artifact metadata alone could
        # describe a successful unrelated build. The service response rejects it.
        artifact = {"candidateSha": self.candidate, "buildId": "42", "pullRequestId": "123"}
        old_gate = artifact["candidateSha"] == self.candidate and artifact["buildId"] == "42"
        self.assertTrue(old_gate)
        unrelated = copy.deepcopy(self.document)
        unrelated["sourceVersion"] = "8" * 40
        with self.assertRaisesRegex(candidate_build.AttestationError, "sourceVersion"):
            self.attest(unrelated)

    def test_missing_environment_is_rejected_before_network_access(self) -> None:
        called = False
        def transport(request, timeout):
            nonlocal called
            called = True
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(candidate_build.AttestationError):
                candidate_build.verify_from_environment({}, output=Path(tmp) / "out.json", transport=transport)
        self.assertFalse(called)


if __name__ == "__main__":
    unittest.main()
