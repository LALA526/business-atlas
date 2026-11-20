#!/usr/bin/env python3.13
"""Conservative, stdlib-only Business Atlas development runner.

This module coordinates existing OpenCode CLIs; it does not implement product
features.  The source DAG is immutable input.  All mutable state and evidence
live outside the Git worktree by default.
"""

from __future__ import annotations

import argparse
import base64
import contextlib
import dataclasses
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from typing import Any, Callable, Iterable, Protocol


DEFAULT_MODEL = "kimi-code-plan-cn/k3.1-pro"
DEFAULT_STATE = Path("/Users/fox/Workspace/projects/business-atlas-preflight/runner-state")
DEFAULT_BASELINE = Path("docs/baselines/T1.0")
DEFAULT_DAG = DEFAULT_BASELINE / "03_开发执行/task-dag.json"
DEFAULT_CONTROLS = Path("coordination/runner/controls.json")
DEFAULT_POLICY = Path("coordination/runner/policy.json")
STAGE_ORDER = ("leader", "developer", "review", "qa", "integration")
NEXT_PHASE = {
    "leader": "developer",
    "developer": "review",
    "review": "qa",
    "qa": "integration",
}


class RunnerError(RuntimeError):
    def __init__(self, category: str, message: str):
        super().__init__(message)
        self.category = category


class StopRequested(RunnerError):
    def __init__(self) -> None:
        super().__init__("stopped", "stop requested; accepted stages were checkpointed")


@dataclasses.dataclass(frozen=True)
class RoleResult:
    ok: bool
    text: str = ""
    category: str = ""
    retryable: bool = False
    log_paths: tuple[str, ...] = ()


@dataclasses.dataclass(frozen=True)
class IntegrationResult:
    status: str  # merged | pending | failed
    candidate_sha: str
    merge_sha: str = ""
    deployment_id: str = ""
    pr_id: str = ""
    detail: str = ""


class RoleAdapter(Protocol):
    def run(
        self,
        role: str,
        prompt: str,
        cwd: Path,
        log_dir: Path,
        timeout_seconds: int,
    ) -> RoleResult: ...


class IntegrationBackend(Protocol):
    def preflight(self) -> dict[str, Any]: ...

    def integrate(
        self, task: dict[str, Any], candidate_sha: str, evidence_dir: Path
    ) -> IntegrationResult: ...


def read_json(path: Path) -> Any:
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + f".{os.getpid()}.tmp")
    data = json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    with temporary.open("w", encoding="utf-8") as handle:
        handle.write(data)
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temporary, path)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def tree_digest(root: Path) -> str:
    digest = hashlib.sha256()
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.as_posix()):
        relative = path.relative_to(root).as_posix().encode("utf-8")
        digest.update(len(relative).to_bytes(8, "big"))
        digest.update(relative)
        digest.update(path.stat().st_size.to_bytes(8, "big"))
        with path.open("rb") as handle:
            for block in iter(lambda: handle.read(1024 * 1024), b""):
                digest.update(block)
    return digest.hexdigest()


def load_dag(path: Path) -> tuple[dict[str, Any], list[dict[str, Any]], str]:
    raw = path.read_bytes()
    document = json.loads(raw)
    if document.get("schemaVersion") != "1.0.0" or document.get("status") != "planned":
        raise RunnerError("dag_invalid", "DAG must be schema 1.0.0 with immutable planned status")
    tasks = document.get("tasks")
    if not isinstance(tasks, list) or not tasks:
        raise RunnerError("dag_invalid", "DAG contains no tasks")
    by_id: dict[str, dict[str, Any]] = {}
    for order, task in enumerate(tasks):
        task_id = task.get("taskId")
        if not isinstance(task_id, str) or not re.fullmatch(r"T[0-9]{2}", task_id):
            raise RunnerError("dag_invalid", "every task needs a stable Txx taskId")
        if task_id in by_id:
            raise RunnerError("dag_invalid", f"duplicate task {task_id}")
        if not isinstance(task.get("dependsOn"), list) or task.get("status") != "planned":
            raise RunnerError("dag_invalid", f"invalid dependencies/status for {task_id}")
        copied = dict(task)
        copied["_order"] = order
        by_id[task_id] = copied
    for task in by_id.values():
        for dependency in task["dependsOn"]:
            if dependency not in by_id:
                raise RunnerError("dag_invalid", f"unknown dependency {dependency}")
    visiting: set[str] = set()
    visited: set[str] = set()

    def visit(task_id: str) -> None:
        if task_id in visiting:
            raise RunnerError("dag_cycle", f"dependency cycle includes {task_id}")
        if task_id in visited:
            return
        visiting.add(task_id)
        for dependency in by_id[task_id]["dependsOn"]:
            visit(dependency)
        visiting.remove(task_id)
        visited.add(task_id)

    for task_id in by_id:
        visit(task_id)
    return document, [by_id[task["taskId"]] for task in tasks], sha256_bytes(raw)


def contract_digest(task: dict[str, Any], baseline: Path) -> str:
    digest = hashlib.sha256()
    public_task = {key: value for key, value in task.items() if not key.startswith("_")}
    digest.update(json.dumps(public_task, ensure_ascii=False, sort_keys=True).encode("utf-8"))
    for reference in sorted(task.get("contractRefs", [])):
        path = baseline / reference
        if not path.is_file():
            raise RunnerError("contract_missing", f"missing contract reference {reference}")
        digest.update(reference.encode("utf-8"))
        digest.update(b"\0")
        digest.update(path.read_bytes())
    return digest.hexdigest()


def git(repo: Path, *args: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        ["git", *args],
        cwd=repo,
        stdin=subprocess.DEVNULL,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if check and result.returncode != 0:
        raise RunnerError("git_failed", f"git {args[0]} failed; see coordinator context")
    return result


def git_head(repo: Path) -> str:
    result = git(repo, "rev-parse", "HEAD", check=False)
    if result.returncode != 0:
        raise RunnerError("git_base_missing", "a committed Git base is required")
    return result.stdout.strip()


def assert_candidate_immutable(worktree: Path, candidate_sha: str) -> None:
    if git_head(worktree) != candidate_sha:
        raise RunnerError("source_mutated", "review/QA changed the candidate HEAD")
    status = git(worktree, "status", "--porcelain", "--untracked-files=all").stdout
    if status:
        raise RunnerError("source_mutated", "review/QA modified tracked source")
    diff = git(worktree, "diff", "--exit-code", candidate_sha, "--", ".", check=False)
    cached = git(worktree, "diff", "--cached", "--exit-code", candidate_sha, "--", ".", check=False)
    if diff.returncode != 0 or cached.returncode != 0:
        raise RunnerError("source_mutated", "candidate content differs from its committed SHA")


class ProcessSupervisor:
    """Owns child process groups so timeout/SIGTERM never targets the runner."""

    def __init__(self) -> None:
        self.stop_event = threading.Event()
        self._children: set[subprocess.Popen[Any]] = set()
        self._lock = threading.Lock()

    def cancel_children(self) -> None:
        self.stop_event.set()
        with self._lock:
            children = list(self._children)
        for child in children:
            if child.poll() is None:
                with contextlib.suppress(ProcessLookupError, PermissionError):
                    os.killpg(child.pid, signal.SIGTERM)

    def execute(
        self,
        command: list[str],
        cwd: Path,
        timeout_seconds: int,
        stdout_path: Path,
        stderr_path: Path,
        environment: dict[str, str] | None = None,
    ) -> int:
        if self.stop_event.is_set():
            raise StopRequested()
        stdout_path.parent.mkdir(parents=True, exist_ok=True)
        with stdout_path.open("wb") as stdout, stderr_path.open("wb") as stderr:
            child = subprocess.Popen(
                command,
                cwd=cwd,
                stdin=subprocess.DEVNULL,
                stdout=stdout,
                stderr=stderr,
                start_new_session=True,
                env={**{k: v for k, v in (os.environ if environment is None else environment).items() if k not in {"AZURE_DEVOPS_PAT", "PERSONAL_ACCESS_TOKEN", "ADO_MCP_AUTH_TOKEN", "AZURE_DEVOPS_EXT_PAT"}}, "PWD": str(cwd.resolve())},
            )
            with self._lock:
                self._children.add(child)
            try:
                try:
                    return child.wait(timeout=timeout_seconds)
                except subprocess.TimeoutExpired:
                    with contextlib.suppress(ProcessLookupError, PermissionError):
                        os.killpg(child.pid, signal.SIGTERM)
                    try:
                        child.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        with contextlib.suppress(ProcessLookupError, PermissionError):
                            os.killpg(child.pid, signal.SIGKILL)
                        child.wait()
                    raise RunnerError("timeout", f"child process exceeded {timeout_seconds} seconds")
            finally:
                with self._lock:
                    self._children.discard(child)


def parse_opencode_jsonl(output: str) -> str:
    """Parse JSONL text events; any error/malformed/empty output is failure."""
    texts: list[str] = []
    meaningful_lines = 0
    for line_number, line in enumerate(output.splitlines(), 1):
        if not line.strip():
            continue
        meaningful_lines += 1
        try:
            event = json.loads(line)
        except json.JSONDecodeError as error:
            raise RunnerError("output_malformed", f"malformed OpenCode JSONL at line {line_number}") from error
        if not isinstance(event, dict) or not isinstance(event.get("type"), str):
            raise RunnerError("output_malformed", f"invalid OpenCode event at line {line_number}")
        part = event.get("part")
        if event["type"] == "error" or (isinstance(part, dict) and part.get("type") == "error"):
            raise RunnerError("model_error", "OpenCode emitted an error event")
        if event["type"] == "text":
            text = part.get("text") if isinstance(part, dict) else None
            if not isinstance(text, str):
                raise RunnerError("output_malformed", f"text event has no text at line {line_number}")
            texts.append(text)
    if meaningful_lines == 0 or not "".join(texts).strip():
        raise RunnerError("output_empty", "OpenCode produced no text events")
    return "".join(texts)


def classify_role_failure(output: str, default: str = "model_failed") -> tuple[str, bool]:
    if re.search(r"(?:^|\D)(401|403)(?:\D|$)|unauthorized|forbidden|invalid.api.key", output, re.I):
        return "blocked_credential", False
    if re.search(r"(?:^|\D)402(?:\D|$)|payment.required|quota|insufficient.balance", output, re.I):
        return "blocked_quota", False
    if re.search(r"(?:^|\D)429(?:\D|$)|rate.limit|(?:^|\D)5[0-9]{2}(?:\D|$)|temporarily.unavailable|unexpected.server.error", output, re.I):
        return "transient_provider", True
    return default, False


class OpenCodeAdapter:
    def __init__(
        self,
        model: str = DEFAULT_MODEL,
        supervisor: ProcessSupervisor | None = None,
    ) -> None:
        if not model or model.startswith("-"):
            raise RunnerError("model_invalid", "an explicit model is required")
        self.model = model
        self.supervisor = supervisor or ProcessSupervisor()

    def command(self, role: str, prompt: str) -> list[str]:
        if not role or role.startswith("-"):
            raise RunnerError("role_invalid", "invalid OpenCode role")
        return ["opencode", "run", prompt, "--auto", "--format", "json", "--agent", role, "-m", self.model]

    def run(
        self,
        role: str,
        prompt: str,
        cwd: Path,
        log_dir: Path,
        timeout_seconds: int,
    ) -> RoleResult:
        serial = f"{time.time_ns()}-{uuid.uuid4().hex[:8]}"
        stdout_path = log_dir / f"{role}-{serial}.stdout.jsonl"
        stderr_path = log_dir / f"{role}-{serial}.stderr.log"
        try:
            returncode = self.supervisor.execute(
                self.command(role, prompt), cwd, timeout_seconds, stdout_path, stderr_path
            )
        except RunnerError as error:
            return RoleResult(False, category=error.category, log_paths=(str(stdout_path), str(stderr_path)))
        output = stdout_path.read_text(encoding="utf-8", errors="replace")
        errors = stderr_path.read_text(encoding="utf-8", errors="replace")
        if returncode != 0:
            category, retryable = classify_role_failure(output + "\n" + errors)
            return RoleResult(False, category=category, retryable=retryable, log_paths=(str(stdout_path), str(stderr_path)))
        try:
            text = parse_opencode_jsonl(output)
        except RunnerError as error:
            category, retryable = classify_role_failure(output + "\n" + errors, error.category)
            if category == error.category:
                retryable = False
            return RoleResult(False, category=category, retryable=retryable, log_paths=(str(stdout_path), str(stderr_path)))
        return RoleResult(True, text=text, log_paths=(str(stdout_path), str(stderr_path)))


class PendingIntegrationBackend:
    def preflight(self) -> dict[str, Any]:
        return {"ready": False, "reason": "Azure integration is not configured"}

    def integrate(
        self, task: dict[str, Any], candidate_sha: str, evidence_dir: Path
    ) -> IntegrationResult:
        return IntegrationResult("pending", candidate_sha, detail="integration backend unavailable")


class AzureIntegrationBackend(PendingIntegrationBackend):
    """Retired manual-merge backend. Kept as an explicitly disabled sentinel."""
    def __init__(self, config, **kwargs):
        self.config = config

    def preflight(self):
        return {"ready": False, "missing": ["unsupported_legacy_backend", "requiredContexts"], "credentialPresent": False}


class Ledger:
    def __init__(self, state_dir: Path) -> None:
        state_dir.mkdir(parents=True, exist_ok=True)
        self.state_dir = state_dir
        self.db_path = state_dir / "ledger.sqlite3"
        self.connection = sqlite3.connect(self.db_path, timeout=5, isolation_level=None)
        self.connection.row_factory = sqlite3.Row
        self.connection.execute("PRAGMA journal_mode=WAL")
        self.connection.execute("PRAGMA foreign_keys=ON")
        self.connection.execute("PRAGMA busy_timeout=5000")
        self._create_schema()

    def close(self) -> None:
        self.connection.close()

    def _create_schema(self) -> None:
        self.connection.executescript(
            """
            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY, value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tasks (
                task_id TEXT PRIMARY KEY,
                task_order INTEGER NOT NULL UNIQUE,
                task_json TEXT NOT NULL,
                phase TEXT NOT NULL,
                attempt INTEGER NOT NULL DEFAULT 0,
                lease_owner TEXT,
                lease_expires_at INTEGER,
                fence INTEGER NOT NULL DEFAULT 0,
                base_sha TEXT,
                candidate_sha TEXT,
                contract_digest TEXT,
                evidence_dir TEXT,
                failure_category TEXT,
                failure_detail TEXT,
                integration_json TEXT,
                updated_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS stages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id TEXT NOT NULL,
                stage TEXT NOT NULL,
                attempt INTEGER NOT NULL,
                candidate_sha TEXT,
                artifact_path TEXT NOT NULL,
                accepted INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                FOREIGN KEY(task_id) REFERENCES tasks(task_id)
            );
            CREATE INDEX IF NOT EXISTS stages_task_stage ON stages(task_id, stage, id);
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id TEXT,
                event_type TEXT NOT NULL,
                fence INTEGER,
                detail TEXT NOT NULL,
                created_at INTEGER NOT NULL
            );
            """
        )

    def initialize(self, dag_path: Path, baseline: Path, now: int) -> dict[str, Any]:
        _, tasks, dag_digest = load_dag(dag_path)
        with self.connection:
            existing = self.get_metadata("dagDigest")
            if existing and existing != dag_digest:
                raise RunnerError("dag_changed", "initialized ledger belongs to a different source DAG")
            if existing:
                count = self.connection.execute("SELECT COUNT(*) FROM tasks").fetchone()[0]
                return {"tasks": count, "dagDigest": dag_digest, "unchanged": True}
            for task in tasks:
                self.connection.execute(
                    """INSERT INTO tasks
                    (task_id, task_order, task_json, phase, contract_digest, updated_at)
                    VALUES (?, ?, ?, 'leader', ?, ?)""",
                    (
                        task["taskId"],
                        task["_order"],
                        json.dumps({k: v for k, v in task.items() if not k.startswith("_")}, ensure_ascii=False, sort_keys=True),
                        contract_digest(task, baseline),
                        now,
                    ),
                )
            self.set_metadata("dagDigest", dag_digest)
            self.set_metadata("dagPath", str(dag_path.resolve()))
            self.set_metadata("baselinePath", str(baseline.resolve()))
            self.set_metadata("baselineReadmeSha256", sha256_file(baseline / "README.md"))
            self.set_metadata("baselineTreeSha256", tree_digest(baseline))
            self._event(None, "initialized", None, f"{len(tasks)} tasks; {dag_digest}", now)
        return {"tasks": len(tasks), "dagDigest": dag_digest, "unchanged": False}

    def set_metadata(self, key: str, value: str) -> None:
        self.connection.execute(
            "INSERT INTO metadata(key,value) VALUES (?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            (key, value),
        )

    def get_metadata(self, key: str) -> str | None:
        row = self.connection.execute("SELECT value FROM metadata WHERE key=?", (key,)).fetchone()
        return row[0] if row else None

    def _event(self, task_id: str | None, event_type: str, fence: int | None, detail: str, now: int) -> None:
        self.connection.execute(
            "INSERT INTO events(task_id,event_type,fence,detail,created_at) VALUES (?,?,?,?,?)",
            (task_id, event_type, fence, detail[:500], now),
        )

    def get(self, task_id: str) -> sqlite3.Row:
        row = self.connection.execute("SELECT * FROM tasks WHERE task_id=?", (task_id,)).fetchone()
        if not row:
            raise RunnerError("task_unknown", f"unknown task {task_id}")
        return row

    def rows(self) -> list[sqlite3.Row]:
        return list(self.connection.execute("SELECT * FROM tasks ORDER BY task_order"))

    @staticmethod
    def task(row: sqlite3.Row) -> dict[str, Any]:
        return json.loads(row["task_json"])

    def _dependencies_done(self, row: sqlite3.Row) -> bool:
        for dependency in self.task(row)["dependsOn"]:
            if self.get(dependency)["phase"] != "done":
                return False
        return True

    def claim(
        self,
        owner: str,
        now: int,
        lease_seconds: int,
        max_tasks: int,
        base_sha: str,
        evidence_root: Path,
        reconciler: Callable[[sqlite3.Row], None],
        max_attempts: int | None = None,
        worker_slot: int | None = None,
    ) -> sqlite3.Row | None:
        self.connection.execute("BEGIN IMMEDIATE")
        try:
            from parallel_executor import eligible_rows
            candidates = eligible_rows(self, now, max_tasks, worker_slot)
            selected: sqlite3.Row | None = None
            for row in candidates:
                category = row["failure_category"] or ""
                provider_waits = self.connection.execute("SELECT COUNT(*) FROM events WHERE task_id=? AND event_type IN ('provider_wait','pause_wait')", (row["task_id"],)).fetchone()[0]
                exhausted = max_attempts is not None and row["attempt"] - provider_waits >= max_attempts and row["phase"] not in ("integration", "pending_integration")
                if row["phase"] == "blocked":
                    continue
                if exhausted or category.startswith("blocked_") or category.endswith("_blocked"):
                    self.connection.execute("UPDATE tasks SET phase='blocked',lease_owner=NULL,lease_expires_at=NULL,fence=fence+1 WHERE task_id=?", (row["task_id"],))
                    continue
                if row["lease_owner"] is not None:
                    reconciler(row)
                selected = row
                break
            if selected is None:
                self.connection.execute("COMMIT")
                return None
            if worker_slot is not None:
                self.set_metadata("workerSlot:" + selected["task_id"], str(worker_slot))
            fence = selected["fence"] + 1
            attempt = selected["attempt"] + 1
            evidence = evidence_root / selected["task_id"] / f"attempt-{attempt}"
            evidence.mkdir(parents=True, exist_ok=True)
            self.connection.execute(
                """UPDATE tasks SET lease_owner=?, lease_expires_at=?, fence=?, attempt=?,
                base_sha=COALESCE(base_sha,?), evidence_dir=?, updated_at=? WHERE task_id=?""",
                (owner, now + lease_seconds, fence, attempt, base_sha, str(evidence), now, selected["task_id"]),
            )
            self._event(selected["task_id"], "claimed", fence, f"attempt {attempt}", now)
            self.connection.execute("COMMIT")
            return self.get(selected["task_id"])
        except Exception:
            self.connection.execute("ROLLBACK")
            raise

    def _owned(self, task_id: str, owner: str, fence: int, now: int, require_unexpired: bool = True) -> sqlite3.Row:
        row = self.get(task_id)
        condition = row["lease_owner"] == owner and row["fence"] == fence
        if require_unexpired:
            condition = condition and row["lease_expires_at"] is not None and row["lease_expires_at"] > now
        if not condition:
            raise RunnerError("lease_fenced", "state transition rejected by lease/fence ownership")
        return row

    def heartbeat(self, task_id: str, owner: str, fence: int, now: int, lease_seconds: int) -> None:
        with self.connection:
            self._owned(task_id, owner, fence, now)
            self.connection.execute(
                "UPDATE tasks SET lease_expires_at=?,updated_at=? WHERE task_id=?",
                (now + lease_seconds, now, task_id),
            )

    def accept_stage(
        self,
        task_id: str,
        stage: str,
        next_phase: str,
        artifact_path: Path,
        owner: str,
        fence: int,
        now: int,
        candidate_sha: str | None = None,
    ) -> None:
        with self.connection:
            row = self._owned(task_id, owner, fence, now)
            if row["phase"] != stage:
                raise RunnerError("stage_conflict", f"cannot accept {stage} from {row['phase']}")
            self.connection.execute(
                "INSERT INTO stages(task_id,stage,attempt,candidate_sha,artifact_path,accepted,created_at) VALUES (?,?,?,?,?,1,?)",
                (task_id, stage, row["attempt"], candidate_sha, str(artifact_path), now),
            )
            candidate = candidate_sha if candidate_sha is not None else row["candidate_sha"]
            self.connection.execute(
                "UPDATE tasks SET phase=?,candidate_sha=?,failure_category=NULL,failure_detail=NULL,updated_at=? WHERE task_id=?",
                (next_phase, candidate, now, task_id),
            )
            self._event(task_id, stage + "_accepted", fence, str(artifact_path), now)

    def fail(
        self,
        task_id: str,
        owner: str,
        fence: int,
        category: str,
        detail: str,
        now: int,
        phase: str | None = None,
    ) -> None:
        with self.connection:
            row = self._owned(task_id, owner, fence, now)
            self.connection.execute(
                """UPDATE tasks SET phase=?,lease_owner=NULL,lease_expires_at=NULL,
                failure_category=?,failure_detail=?,updated_at=? WHERE task_id=?""",
                (phase or row["phase"], category, detail[:500], now, task_id),
            )
            self._event(task_id, "failed", fence, category, now)

    def defer_provider(self, task_id, owner, fence, detail, now):
        with self.connection:
            self._owned(task_id, owner, fence, now)
            self.connection.execute("UPDATE tasks SET lease_owner=NULL,lease_expires_at=NULL,failure_category='provider_wait',failure_detail=?,updated_at=? WHERE task_id=?", (detail[:500], now, task_id))
            self._event(task_id, "provider_wait", fence, detail, now)

    def latest_accepted(self, task_id: str, stage: str, candidate_sha: str | None = None) -> sqlite3.Row | None:
        if candidate_sha is None:
            return self.connection.execute(
                "SELECT * FROM stages WHERE task_id=? AND stage=? AND accepted=1 ORDER BY id DESC LIMIT 1",
                (task_id, stage),
            ).fetchone()
        return self.connection.execute(
            """SELECT * FROM stages WHERE task_id=? AND stage=? AND accepted=1
            AND candidate_sha=? ORDER BY id DESC LIMIT 1""",
            (task_id, stage, candidate_sha),
        ).fetchone()

    def finish_integration(
        self,
        task_id: str,
        owner: str,
        fence: int,
        result: IntegrationResult,
        artifact_path: Path,
        now: int,
    ) -> str:
        with self.connection:
            row = self._owned(task_id, owner, fence, now)
            if row["phase"] not in ("integration", "pending_integration"):
                raise RunnerError("stage_conflict", "integration is not the current phase")
            if row["candidate_sha"] != result.candidate_sha:
                raise RunnerError("integration_sha_mismatch", "integration result is for another candidate")
            for stage in ("leader", "developer", "review", "qa"):
                accepted = self.latest_accepted(task_id, stage)
                if not accepted or (stage in ("developer", "review", "qa") and accepted["candidate_sha"] != row["candidate_sha"]):
                    raise RunnerError("completion_gate", f"missing current {stage} acceptance")
            phase = "pending_integration"
            if result.status == "merged":
                if not result.merge_sha or not result.deployment_id or not result.pr_id:
                    raise RunnerError("completion_gate", "merge SHA, PR and deployment confirmation are required")
                phase = "done"
            elif result.status == "failed":
                phase = "developer"
            elif result.status != "pending":
                raise RunnerError("integration_invalid", "unknown integration status")
            payload = dataclasses.asdict(result)
            write_json(artifact_path, payload)
            self.connection.execute(
                "UPDATE tasks SET phase=?,integration_json=?,lease_owner=NULL,lease_expires_at=NULL,failure_category=?,failure_detail=?,updated_at=? WHERE task_id=?",
                (
                    phase,
                    json.dumps(payload, sort_keys=True),
                    "integration_failed" if result.status == "failed" else None,
                    result.detail[:500],
                    now,
                    task_id,
                ),
            )
            self._event(task_id, "integration_" + result.status, fence, result.merge_sha or result.detail, now)
            return phase


class VerificationPolicy:
    def __init__(self, document: dict[str, Any]) -> None:
        if document.get("version") != 1 or not isinstance(document.get("taskVerify"), dict):
            raise RunnerError("policy_invalid", "verification policy must be version 1")
        self.document = document

    @classmethod
    def from_committed(cls, repo: Path, revision: str, policy_path: Path) -> "VerificationPolicy":
        relative = policy_path.resolve().relative_to(repo.resolve()).as_posix()
        result = git(repo, "show", f"{revision}:{relative}", check=False)
        if result.returncode != 0:
            raise RunnerError("policy_missing", "verification policy must exist in the committed base")
        try:
            return cls(json.loads(result.stdout))
        except json.JSONDecodeError as error:
            raise RunnerError("policy_invalid", "verification policy is not valid JSON") from error

    def command_for(self, task_id: str, worktree: Path) -> list[str]:
        product = self.document.get("productVerify", "scripts/ci/verify")
        if (worktree / product).is_file():
            return [product]
        command = self.document["taskVerify"].get(task_id)
        if not isinstance(command, list) or not command or not all(isinstance(part, str) and part for part in command):
            raise RunnerError("verification_missing", f"no approved verification command for {task_id}")
        return command


class ProcessLock:
    def __init__(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        self.handle = path.open("a+")
        try:
            fcntl.flock(self.handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            self.handle.close()
            raise RunnerError("writer_locked", "another runner process owns the persistent writer lock") from error

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        self.release()
        return False

    def release(self) -> None:
        fcntl.flock(self.handle.fileno(), fcntl.LOCK_UN)
        self.handle.close()


class AtlasRunner:
    def __init__(
        self,
        repo: Path,
        baseline: Path,
        state_dir: Path,
        controls: dict[str, Any],
        adapter: RoleAdapter,
        integration: IntegrationBackend,
        clock: Callable[[], int] = lambda: int(time.time()),
        sleep: Callable[[float], None] = time.sleep,
        supervisor: ProcessSupervisor | None = None,
    ) -> None:
        self.repo = repo.resolve()
        self.baseline = baseline.resolve()
        self.state_dir = state_dir.resolve()
        self.controls = controls
        self.adapter = adapter
        self.integration = integration
        self.clock = clock
        self.sleep = sleep
        self.supervisor = supervisor or ProcessSupervisor()
        self.ledger = Ledger(self.state_dir)
        self.owner = f"runner-{os.getpid()}-{uuid.uuid4().hex[:12]}"
        self.max_retries = int(controls.get("maxRetries", 2))
        self.timeout_seconds = int(controls.get("timeoutSeconds", 2700))
        self.lease_seconds = int(controls.get("leaseSeconds", 3600))
        self.max_tasks = int(controls.get("maxConcurrentTasks", 1))
        self.policy_path = (self.repo / controls.get("policyPath", str(DEFAULT_POLICY))).resolve()
        self.worktree_root = Path(controls.get("worktreeRoot", str(self.state_dir / "worktrees"))).resolve()
        self.evidence_root = Path(controls.get("evidenceRoot", str(self.state_dir / "evidence"))).resolve()
        if any(
            path == self.repo or self.repo in path.parents
            for path in (self.state_dir, self.worktree_root, self.evidence_root)
        ):
            raise RunnerError("state_location", "ledger, worktrees and evidence must be outside the Git repository")
        if self.max_retries < 0 or self.max_retries > 2:
            raise RunnerError("retry_invalid", "maxRetries must be between 0 and 2")
        if self.max_tasks not in (1, 2):
            raise RunnerError("concurrency_invalid", "Mac16 supports one or two leased task workers")
        if self.lease_seconds <= self.timeout_seconds:
            raise RunnerError("lease_invalid", "leaseSeconds must exceed the model/verification timeout")
        self.stop_event = threading.Event()

    def close(self) -> None:
        self.ledger.close()

    def request_stop(self, _signum: int | None = None, _frame: Any = None) -> None:
        self.stop_event.set()
        self.supervisor.cancel_children()
        if isinstance(self.adapter, OpenCodeAdapter):
            self.adapter.supervisor.cancel_children()

    def _check_stop(self) -> None:
        controls_path = self.repo / self.controls.get("controlsPath", str(DEFAULT_CONTROLS))
        stop_file = self.state_dir / "STOP"
        if self.stop_event.is_set() or stop_file.exists():
            raise StopRequested()
        if controls_path.is_file():
            try:
                if read_json(controls_path).get("stopRequested") is True:
                    raise StopRequested()
            except (OSError, json.JSONDecodeError) as error:
                raise RunnerError("controls_invalid", "cannot read stop controls") from error

    def reconcile(self, row: sqlite3.Row) -> None:
        phase = row["phase"]
        if row["base_sha"]:
            git(self.repo, "cat-file", "-e", row["base_sha"] + "^{commit}")
        candidate = row["candidate_sha"]
        if candidate:
            git(self.repo, "cat-file", "-e", candidate + "^{commit}")
        if phase in ("review", "qa", "integration", "pending_integration"):
            if not candidate:
                raise RunnerError("recovery_blocked", "expired lease has no candidate SHA to reconcile")
            if phase in ("integration", "pending_integration"):
                accepted = self.ledger.latest_accepted(row["task_id"], "qa", candidate)
                if not accepted:
                    raise RunnerError("recovery_blocked", "candidate has no accepted QA stage")
            worktree = self.worktree_root / row["task_id"] / "development"
            if worktree.exists():
                assert_candidate_immutable(worktree, candidate)
        if candidate and phase == "developer":
            worktree = self.worktree_root / row["task_id"] / "development"
            if worktree.exists():
                assert_candidate_immutable(worktree, candidate)

    def invoke_role(
        self, role: str, prompt: str, cwd: Path, log_dir: Path, timeout_seconds: int | None = None
    ) -> RoleResult:
        last = RoleResult(False, category="model_not_called")
        for retry in range(self.max_retries + 1):
            self._check_stop()
            if getattr(self, "active_task", None):
                active = self.ledger.get(self.active_task)
                self.ledger.heartbeat(self.active_task, self.owner, active["fence"], self.clock(), self.lease_seconds)
            last = self.adapter.run(role, prompt, cwd, log_dir, timeout_seconds or self.timeout_seconds)
            if last.ok or not last.retryable or retry >= self.max_retries:
                break
            self.sleep(float(self.controls.get("retryBackoffSeconds", 2)) * (2**retry))
        return last

    @staticmethod
    def parse_verdict(text: str) -> dict[str, Any]:
        try:
            value = json.loads(text)
        except json.JSONDecodeError as error:
            raise RunnerError("verdict_invalid", "reviewer/QA must return one strict JSON object") from error
        if not isinstance(value, dict) or set(value) != {"verdict", "findings", "evidence"}:
            raise RunnerError("verdict_invalid", "verdict JSON has incorrect fields")
        if value["verdict"] not in ("pass", "needs_changes", "blocked"):
            raise RunnerError("verdict_invalid", "unknown verdict")
        if not isinstance(value["findings"], list) or not isinstance(value["evidence"], list):
            raise RunnerError("verdict_invalid", "findings and evidence must be arrays")
        if not all(isinstance(item, str) for item in value["findings"] + value["evidence"]):
            raise RunnerError("verdict_invalid", "findings and evidence must contain strings")
        if value["verdict"] == "pass" and not value["evidence"]:
            raise RunnerError("verdict_invalid", "pass requires independent evidence locators")
        return value

    def _prompt_context(self, row: sqlite3.Row) -> str:
        task = self.ledger.task(row)
        return (
            "You are executing exactly one Business Atlas task. Read AGENTS.md and the T1.0 "
            "contract references before acting. Do not modify shared contracts/migrations owned by "
            "other tasks, do not claim independent approval, and report real evidence.\n"
            + json.dumps(task, ensure_ascii=False, indent=2)
            + f"\nBase SHA: {row['base_sha']}\nContract digest: {row['contract_digest']}\n"
        )

    def _development_worktree(self, row: sqlite3.Row) -> Path:
        task = self.ledger.task(row)
        path = self.worktree_root / row["task_id"] / "development"
        branch = task["plannedBranch"]
        if path.exists():
            current = git(path, "branch", "--show-current").stdout.strip()
            if current != branch:
                raise RunnerError("worktree_conflict", "development worktree is on an unexpected branch")
            return path
        path.parent.mkdir(parents=True, exist_ok=True)
        branch_exists = git(self.repo, "show-ref", "--verify", "--quiet", "refs/heads/" + branch, check=False).returncode == 0
        if branch_exists:
            git(self.repo, "worktree", "add", str(path), branch)
        else:
            git(self.repo, "worktree", "add", "-b", branch, str(path), row["base_sha"])
        return path

    def _detached_worktree(self, row: sqlite3.Row, stage: str) -> Path:
        path = self.worktree_root / row["task_id"] / f"{stage}-a{row['attempt']}"
        if path.exists():
            assert_candidate_immutable(path, row["candidate_sha"])
            return path
        path.parent.mkdir(parents=True, exist_ok=True)
        git(self.repo, "worktree", "add", "--detach", str(path), row["candidate_sha"])
        return path

    def _persist_output(self, row: sqlite3.Row, name: str, text: str) -> Path:
        path = Path(row["evidence_dir"]) / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def _run_leader(self, row: sqlite3.Row) -> None:
        prompt = (
            self._prompt_context(row)
            + "\nProduce and persist a concrete implementation plan in your response. Include affected "
            "callers/data flow, acceptance/negative/concurrency/recovery tests, ownership boundaries, "
            "verification evidence and knowledge candidates. Do not implement product code."
        )
        worktree = self._development_worktree(row)
        result = self.invoke_role("atlas-leader", prompt, worktree, Path(row["evidence_dir"]))
        assert_candidate_immutable(worktree, row["base_sha"])
        if not result.ok:
            raise RunnerError(result.category, "leader model call failed")
        path = self._persist_output(row, "leader-plan.md", result.text)
        self.ledger.accept_stage(row["task_id"], "leader", "developer", path, self.owner, row["fence"], self.clock())

    def _run_developer(self, row: sqlite3.Row) -> None:
        worktree = self._development_worktree(row)
        feedback = ""
        if row["failure_category"] in ("review_rejected", "qa_rejected", "integration_failed"):
            feedback = (
                "\nAddress the persisted independent review/QA findings; do not weaken acceptance checks. "
                f"Previous findings: {row['failure_detail'] or 'see evidence'}"
            )
        plan = self.ledger.latest_accepted(row["task_id"], "leader")
        plan_text = Path(plan["artifact_path"]).read_text() if plan else ""
        prompt = (
            self._prompt_context(row)
            + "\nAccepted leader implementation plan:\n" + plan_text
            + feedback
            + "\nImplement this task on the existing task branch/worktree. Analyze actual callers and data "
            "flow. Add meaningful tests and knowledge candidates. Do not self-approve or claim CI/PR/deployment. "
            "The coordinator will commit the resulting worktree as the candidate."
        )
        result = self.invoke_role("atlas-builder", prompt, worktree, Path(row["evidence_dir"]))
        if not result.ok:
            raise RunnerError(result.category, "developer model call failed")
        self._persist_output(row, "developer-result.md", result.text)
        if git(worktree, "status", "--porcelain").stdout.strip():
            git(worktree, "add", "-A")
            git(worktree, "commit", "-m", f"{row['task_id']} candidate attempt {row['attempt']}")
        candidate = git_head(worktree)
        git(self.repo, "merge-base", "--is-ancestor", row["base_sha"], candidate)
        if candidate == row["base_sha"]:
            raise RunnerError("candidate_empty", "candidate SHA equals base SHA")
        path = self._persist_output(row, "developer-result.md", result.text)
        self.ledger.accept_stage(row["task_id"], "developer", "review", path, self.owner, row["fence"], self.clock(), candidate)

    def _run_review(self, row: sqlite3.Row) -> str:
        worktree = self._detached_worktree(row, "review")
        diff = git(self.repo, "diff", "--no-ext-diff", "--unified=80", row["base_sha"], row["candidate_sha"]).stdout
        if len(diff.encode("utf-8")) > 1_000_000:
            diff = diff[:1_000_000] + "\n[diff truncated; inspect the detached candidate worktree]"
        prompt = (
            self._prompt_context(row)
            + "\nIndependently inspect the committed candidate diff and relevant callers. Do not edit any "
            "file. Do not trust the developer summary. Return ONLY strict JSON with exactly "
            '{"verdict":"pass|needs_changes|blocked","findings":[],"evidence":[]}; pass needs actual evidence locators.\n'
            + diff
        )
        result = self.invoke_role("atlas-reviewer", prompt, worktree, Path(row["evidence_dir"]))
        if not result.ok:
            raise RunnerError(result.category, "reviewer model call failed")
        assert_candidate_immutable(worktree, row["candidate_sha"])
        verdict = self.parse_verdict(result.text)
        path = self._persist_output(row, "review-verdict.json", json.dumps(verdict, ensure_ascii=False, indent=2))
        if verdict["verdict"] == "needs_changes":
            self.ledger.fail(
                row["task_id"], self.owner, row["fence"], "review_rejected",
                "; ".join(verdict["findings"]), self.clock(), phase="developer",
            )
            return "needs_changes"
        if verdict["verdict"] == "blocked":
            self.ledger.fail(row["task_id"], self.owner, row["fence"], "review_blocked", "independent review blocked", self.clock())
            return "blocked"
        self.ledger.accept_stage(row["task_id"], "review", "qa", path, self.owner, row["fence"], self.clock(), row["candidate_sha"])
        return "pass"

    def _run_verification(self, row: sqlite3.Row, worktree: Path) -> Path:
        policy = VerificationPolicy.from_committed(self.repo, row["base_sha"], self.policy_path)
        command = policy.command_for(row["task_id"], worktree)
        if any("\0" in part for part in command):
            raise RunnerError("policy_invalid", "NUL is not allowed in verification commands")
        log_dir = Path(row["evidence_dir"])
        try:
            returncode = self.supervisor.execute(
                command,
                worktree,
                self.timeout_seconds,
                log_dir / "verification.stdout.log",
                log_dir / "verification.stderr.log",
            )
        except RunnerError:
            raise
        except OSError as error:
            raise RunnerError("verification_unavailable", "approved verification command could not start") from error
        result = {"command": command, "exitCode": returncode, "candidateSha": row["candidate_sha"]}
        path = log_dir / "verification-result.json"
        write_json(path, result)
        if returncode != 0:
            raise RunnerError("verification_failed", f"approved verification exited {returncode}")
        return path

    def _run_qa(self, row: sqlite3.Row) -> str:
        worktree = self._detached_worktree(row, "qa")
        # Resolve the gate before asking a model to assert success.
        policy = VerificationPolicy.from_committed(self.repo, row["base_sha"], self.policy_path)
        policy.command_for(row["task_id"], worktree)
        prompt = (
            self._prompt_context(row)
            + "\nIndependently inspect this exact committed candidate and its acceptance criteria. Do not "
            "modify tracked source. Coordinator-owned deterministic verification runs separately; never supply "
            "commands for execution. Return ONLY strict JSON with exactly verdict/findings/evidence fields. "
            "A pass requires independent evidence and does not replace CI or deployment."
        )
        result = self.invoke_role("atlas-qa", prompt, worktree, Path(row["evidence_dir"]))
        if not result.ok:
            raise RunnerError(result.category, "QA model call failed")
        assert_candidate_immutable(worktree, row["candidate_sha"])
        verdict = self.parse_verdict(result.text)
        verdict_path = self._persist_output(row, "qa-verdict.json", json.dumps(verdict, ensure_ascii=False, indent=2))
        if verdict["verdict"] != "pass":
            category = "qa_blocked" if verdict["verdict"] == "blocked" else "qa_rejected"
            self.ledger.fail(row["task_id"], self.owner, row["fence"], category, "; ".join(verdict["findings"]), self.clock(), phase="developer")
            return verdict["verdict"]
        verification_tree = self._detached_worktree(row, "verification")
        verification_path = self._run_verification(row, verification_tree)
        assert_candidate_immutable(verification_tree, row["candidate_sha"])
        assert_candidate_immutable(worktree, row["candidate_sha"])
        combined = self._persist_output(
            row,
            "qa-acceptance.json",
            json.dumps({"verdict": str(verdict_path), "verification": str(verification_path)}, indent=2),
        )
        self.ledger.accept_stage(row["task_id"], "qa", "integration", combined, self.owner, row["fence"], self.clock(), row["candidate_sha"])
        return "pass"

    def _run_integration(self, row: sqlite3.Row) -> str:
        result = self.integration.integrate(self.ledger.task(row), row["candidate_sha"], Path(row["evidence_dir"]))
        if not isinstance(result, IntegrationResult):
            raise RunnerError("integration_invalid", "integration backend returned an invalid result")
        path = Path(row["evidence_dir"]) / "integration-result.json"
        return self.ledger.finish_integration(
            row["task_id"], self.owner, row["fence"], result, path, self.clock()
        )

    def current_base(self):
        base = git_head(self.repo)
        if self.controls.get("integration", {}).get("backend") == "azure_gates":
            git(self.repo, "fetch", "origin", "main")
            base = git(self.repo, "rev-parse", "origin/main").stdout.strip()
        return base

    def run_once(self) -> dict[str, Any]:
        base = self.current_base()
        try:
            row = self.ledger.claim(
                self.owner,
                self.clock(),
                self.lease_seconds,
                self.max_tasks,
                base,
                self.evidence_root,
                self.reconcile,
                max_attempts=int(self.controls.get("maxTaskAttempts", 3)),
                worker_slot=self.controls.get("workerSlot"),
            )
        except RunnerError as error:
            return {"status": "recovery_blocked", "category": error.category}
        if row is None:
            return {"status": "idle", "detail": "no dependency-ready task or lease capacity"}
        task_id = row["task_id"]
        self.active_task = task_id
        try:
            while True:
                self._check_stop()
                row = self.ledger.get(task_id)
                self.ledger.heartbeat(task_id, self.owner, row["fence"], self.clock(), self.lease_seconds)
                row = self.ledger.get(task_id)
                if row["phase"] == "leader":
                    self._run_leader(row)
                elif row["phase"] == "developer":
                    self._run_developer(row)
                elif row["phase"] == "review":
                    review = self._run_review(row)
                    if review != "pass":
                        return {"status": review, "taskId": task_id, "attempt": row["attempt"]}
                elif row["phase"] == "qa":
                    qa = self._run_qa(row)
                    if qa != "pass":
                        return {"status": qa, "taskId": task_id, "attempt": row["attempt"]}
                elif row["phase"] in ("integration", "pending_integration"):
                    phase = self._run_integration(row)
                    return {"status": phase, "taskId": task_id, "attempt": row["attempt"]}
                elif row["phase"] == "done":
                    return {"status": "done", "taskId": task_id}
                else:
                    raise RunnerError("phase_invalid", f"unknown phase {row['phase']}")
        except StopRequested:
            row = self.ledger.get(task_id)
            self.ledger.fail(task_id, self.owner, row["fence"], "stopped", "checkpoint after stop request", self.clock())
            return {"status": "stopped", "taskId": task_id, "attempt": row["attempt"]}
        except RunnerError as error:
            row = self.ledger.get(task_id)
            if error.category == "provider_wait":
                self.ledger.defer_provider(task_id, self.owner, row["fence"], "All verified model providers are waiting for quota recovery", self.clock())
            else:
                self.ledger.fail(task_id, self.owner, row["fence"], error.category, str(error), self.clock())
            return {"status": "failed", "taskId": task_id, "attempt": row["attempt"], "category": error.category}

    def status(self) -> dict[str, Any]:
        tasks = []
        for row in self.ledger.rows():
            state = row["phase"]
            if row["attempt"] == 0:
                state = "planned"
            elif row["lease_owner"]:
                state = "running"
            elif row["failure_category"] == "provider_wait":
                state = "waiting_provider"
            tasks.append(
                {
                    "taskId": row["task_id"],
                    "phase": row["phase"],
                    "state": state,
                    "attempt": row["attempt"],
                    "leaseExpiresAt": row["lease_expires_at"],
                    "baseSha": row["base_sha"],
                    "candidateSha": row["candidate_sha"],
                    "integration": json.loads(row["integration_json"]) if row["integration_json"] else None,
                    "contractDigest": row["contract_digest"],
                    "evidenceDir": row["evidence_dir"],
                    "failureCategory": row["failure_category"],
                    "fence": row["fence"],
                }
            )
        return {"tasks": tasks, "dagDigest": self.ledger.get_metadata("dagDigest")}

    def preflight(self) -> dict[str, Any]:
        checks: dict[str, Any] = {}
        head = git(self.repo, "rev-parse", "HEAD", check=False)
        checks["git"] = {"ready": head.returncode == 0, "committedBase": bool(head.stdout.strip())}
        identity = git(self.repo, "config", "user.name", check=False).stdout.strip() and git(
            self.repo, "config", "user.email", check=False
        ).stdout.strip()
        checks["git"]["identityConfigured"] = bool(identity)
        checks["git"]["ready"] = checks["git"]["ready"] and bool(identity)
        clean = not git(self.repo, "status", "--porcelain").stdout.strip()
        checks["git"]["clean"] = clean
        checks["git"]["ready"] = checks["git"]["ready"] and clean
        if self.controls.get("integration", {}).get("backend") == "azure_gates":
            remote = git(self.repo, "rev-parse", "origin/main", check=False)
            synced = remote.returncode == 0 and remote.stdout.strip() == head.stdout.strip()
            checks["git"]["syncedWithOriginMain"] = synced
            checks["git"]["ready"] = checks["git"]["ready"] and synced
        dag_path = self.repo / DEFAULT_DAG
        baseline_ready = self.baseline.is_dir() and dag_path.is_file() and (self.baseline / "README.md").is_file()
        checks["baseline"] = {"ready": baseline_ready}
        if baseline_ready:
            try:
                _, tasks, digest = load_dag(dag_path)
                checks["baseline"].update({"taskCount": len(tasks), "dagDigest": digest})
                checks["baseline"]["ready"] = len(tasks) == 44
                initialized_digest = self.ledger.get_metadata("dagDigest")
                checks["baseline"]["matchesLedger"] = initialized_digest in (None, digest)
                checks["baseline"]["ready"] = checks["baseline"]["ready"] and checks["baseline"]["matchesLedger"]
                readme_digest = sha256_file(self.baseline / "README.md")
                stored_readme = self.ledger.get_metadata("baselineReadmeSha256")
                checks["baseline"]["readmeUnchanged"] = stored_readme in (None, readme_digest)
                stored_tree = self.ledger.get_metadata("baselineTreeSha256")
                checks["baseline"]["treeUnchanged"] = stored_tree in (None, tree_digest(self.baseline))
                checks["baseline"]["ready"] = (
                    checks["baseline"]["ready"]
                    and checks["baseline"]["readmeUnchanged"]
                    and checks["baseline"]["treeUnchanged"]
                )
            except (OSError, RunnerError, json.JSONDecodeError):
                checks["baseline"]["ready"] = False
        opencode = subprocess.run(
            ["opencode", "--version"], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL, check=False,
        ) if shutil_which("opencode") else None
        model_verified = self.controls.get("model", {}).get("verified") is True
        exact_model = self.controls.get("model", {}).get("id") == DEFAULT_MODEL
        checks["model"] = {
            "ready": bool(opencode and opencode.returncode == 0 and model_verified and exact_model),
            "cliPresent": opencode is not None and opencode.returncode == 0,
            "explicitlyVerified": model_verified,
            "expectedModelSelected": exact_model,
        }
        pat_name = self.controls.get("integration", {}).get("patEnvironment", "AZURE_DEVOPS_PAT")
        file_credential = (Path.home() / ".config/atlas/azure-pat").is_file()
        present = file_credential if self.controls.get("integration", {}).get("backend") == "azure_gates" else bool(os.environ.get(pat_name))
        checks["credentials"] = {"ready": present, "azurePatPresent": present, "valuesIncluded": False}
        policy_committed = False
        if head.returncode == 0:
            relative = self.policy_path.resolve().relative_to(self.repo).as_posix()
            policy_committed = git(self.repo, "cat-file", "-e", f"{head.stdout.strip()}:{relative}", check=False).returncode == 0
        checks["verificationPolicy"] = {"ready": policy_committed, "committed": policy_committed}
        integration = self.integration.preflight()
        checks["ci"] = {"ready": bool(integration.get("ready")), "requiredCheckCount": integration.get("requiredCheckCount", 0)}
        checks["deployment"] = {"ready": bool(integration.get("ready"))}
        checks["integration"] = integration
        checks["controls"] = {"continuousEnabled": self.controls.get("enabled") is True}
        ready = all(check.get("ready", True) for check in checks.values())
        manifest = {"ready": ready, "checks": checks, "generatedAt": self.clock()}
        write_json(self.state_dir / "preflight-manifest.json", manifest)
        return manifest

    def loop(self) -> dict[str, Any]:
        if self.controls.get("enabled") is not True:
            return {"status": "disabled", "detail": "continuous controls.enabled is false"}
        manifest = self.preflight()
        if not manifest["ready"]:
            return {"status": "preflight_failed", "manifest": str(self.state_dir / "preflight-manifest.json")}
        if hasattr(self.adapter, "sync_status"):
            self.adapter.sync_status(self.status())
        iterations = 0
        limit = int(self.controls.get("maxLoopIterations", 0))
        while not self.stop_event.is_set():
            self._check_stop()
            result = self.run_once()
            if hasattr(self.adapter, "sync_status"):
                self.adapter.sync_status(self.status())
            iterations += 1
            if result["status"] == "idle":
                return {"status": "idle", "iterations": iterations}
            if result.get("category") == "provider_wait":
                return {"status": "provider_wait", "last": result, "iterations": iterations}
            if result.get("category") in ("blocked_credential", "blocked_quota"):
                return {"status": "provider_blocked", "last": result, "iterations": iterations}
            if limit and iterations >= limit:
                return {"status": "iteration_limit", "last": result, "iterations": iterations}
            if result["status"] == "stopped":
                return {"status": "stopped", "iterations": iterations}
            if result["status"] == "recovery_blocked":
                return {"status": "recovery_blocked", "iterations": iterations}
            self.sleep(float(self.controls.get("loopPollSeconds", 2)))
        return {"status": "stopped", "iterations": iterations}


def shutil_which(command: str) -> str | None:
    # Deliberately tiny PATH lookup avoids another abstraction and never runs a shell.
    for directory in os.environ.get("PATH", "").split(os.pathsep):
        candidate = Path(directory) / command
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return str(candidate)
    return None


DEFAULT_CONTROLS_DOCUMENT: dict[str, Any] = {
    "enabled": False,
    "stopRequested": False,
    "maxConcurrentTasks": 1,
    "maxRetries": 2,
    "timeoutSeconds": 2700,
    "leaseSeconds": 3600,
    "retryBackoffSeconds": 2,
    "loopPollSeconds": 2,
    "maxLoopIterations": 0,
    "model": {"id": DEFAULT_MODEL, "verified": False},
    "policyPath": str(DEFAULT_POLICY),
    "integration": {
        "backend": "azure",
        "organization": "",
        "project": "",
        "repositoryId": "",
        "targetBranch": "main",
        "requiredContexts": [],
        "patEnvironment": "AZURE_DEVOPS_PAT",
        "ciDefinitionUrl": "",
        "deploymentStatusUrl": "",
        "timeoutSeconds": 1800,
        "pollSeconds": 15,
    },
}


def load_controls(repo: Path, path: Path, state_dir: Path = DEFAULT_STATE) -> dict[str, Any]:
    controls = json.loads(json.dumps(DEFAULT_CONTROLS_DOCUMENT))
    if path.exists():
        supplied = read_json(path)
        if not isinstance(supplied, dict):
            raise RunnerError("controls_invalid", "controls must be a JSON object")
        for key, value in supplied.items():
            if isinstance(value, dict) and isinstance(controls.get(key), dict):
                controls[key].update(value)
            else:
                controls[key] = value
        controls["controlsPath"] = str(path.resolve())
    controls.setdefault("worktreeRoot", str(state_dir / "worktrees"))
    controls.setdefault("evidenceRoot", str(state_dir / "evidence"))
    return controls


def build_real_runner(repo: Path, state_dir: Path, controls_path: Path, overrides=None) -> AtlasRunner:
    controls = load_controls(repo, controls_path, state_dir)
    controls.update(overrides or {})
    supervisor = ProcessSupervisor()
    model = controls["model"]["id"]
    if controls.get("roleBackend") == "multica":
        from multica_adapter import MulticaAdapter
        binding = read_json(Path(controls["multicaBindingPath"]))
        from parallel_executor import binding_for_slot
        binding = binding_for_slot(binding, controls.get("workerSlot", 1))
        if controls.get("providerRecovery", {}).get("enabled"):
            from provider_recovery import ProviderRouter
            adapter = ProviderRouter(binding, supervisor, controls["providerRecovery"], state_dir, slot=controls.get("workerSlot", 1))
        else:
            adapter = MulticaAdapter(binding, supervisor)
    else:
        adapter = OpenCodeAdapter(model, supervisor)
    integration_config = controls["integration"]
    if integration_config.get("backend") == "azure_gates":
        from azure_gate_backend import AzureGateBackend
        from azure_api import request
        integration: IntegrationBackend = AzureGateBackend(integration_config, repo, request)
    elif integration_config.get("backend") == "azure":
        raise RunnerError("integration_invalid", "Legacy manual-merge backend is disabled; use azure_gates")
    elif integration_config.get("backend") == "pending":
        integration = PendingIntegrationBackend()
    else:
        raise RunnerError("integration_invalid", "integration.backend must be azure or pending")
    if controls.get("roleBackend") == "multica":
        from multica_adapter import MulticaIntegration
        integration = MulticaIntegration(integration, adapter)
    runner_class = AtlasRunner
    if controls.get("parallelValidation") or controls.get("maxConcurrentTasks", 1) > 1:
        from parallel_executor import ParallelAtlasRunner
        runner_class = ParallelAtlasRunner
    return runner_class(
        repo,
        repo / DEFAULT_BASELINE,
        state_dir,
        controls,
        adapter,
        integration,
        supervisor=supervisor,
    )


def emit(value: Any) -> None:
    print(json.dumps(value, ensure_ascii=False, sort_keys=True))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Business Atlas pre-development runner")
    parser.add_argument("command", choices=("init", "status", "preflight", "run-once", "loop"))
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--state", type=Path, default=DEFAULT_STATE)
    parser.add_argument("--controls", type=Path, default=None)
    args = parser.parse_args(argv)
    repo = args.repo.resolve()
    controls_path = args.controls or repo / DEFAULT_CONTROLS
    lock: ProcessLock | None = None
    runner: AtlasRunner | None = None
    try:
        if args.command in ("init", "run-once", "loop"):
            lock = ProcessLock(args.state / "runner.lock")
        runner = build_real_runner(repo, args.state, controls_path)
        if args.command == "init":
            result = runner.ledger.initialize(repo / DEFAULT_DAG, repo / DEFAULT_BASELINE, int(time.time()))
        elif args.command == "status":
            result = runner.status()
        elif args.command == "preflight":
            result = runner.preflight()
        elif args.command == "run-once":
            manifest = runner.preflight()
            result = runner.run_once() if manifest["ready"] else {
                "status": "preflight_failed", "manifest": str(args.state / "preflight-manifest.json")
            }
        else:
            previous_term = signal.getsignal(signal.SIGTERM)
            previous_int = signal.getsignal(signal.SIGINT)
            signal.signal(signal.SIGTERM, runner.request_stop)
            signal.signal(signal.SIGINT, runner.request_stop)
            try:
                result = runner.loop()
            finally:
                signal.signal(signal.SIGTERM, previous_term)
                signal.signal(signal.SIGINT, previous_int)
        emit(result)
        if result.get("status") in ("preflight_failed", "recovery_blocked") or result.get("ready") is False:
            return 2
        if result.get("status") in ("failed", "needs_changes", "blocked", "provider_blocked"):
            return 1
        return 0
    except (RunnerError, OSError, sqlite3.Error, json.JSONDecodeError) as error:
        category = error.category if isinstance(error, RunnerError) else "runner_error"
        emit({"status": "error", "category": category, "detail": str(error)[:300]})
        return 2
    finally:
        if runner:
            runner.close()
        if lock:
            lock.release()


if __name__ == "__main__":
    raise SystemExit(main())
