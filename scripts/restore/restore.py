#!/usr/bin/env python3
"""Restore a Business Atlas backup into an isolated, unexposed PostgreSQL/container.

No production Compose project, volume, network, or published port is touched. The
result includes baseline identities/digests and a row-by-row database-to-blob hash
reconciliation. Physical base backups support --target-time PITR with archived WAL.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import uuid

BACKUP_MODULE = Path(__file__).resolve().parents[2] / "deploy/backup/backup.py"
spec = importlib.util.spec_from_file_location("atlas_backup", BACKUP_MODULE)
if spec is None or spec.loader is None:
    raise RuntimeError("Cannot load backup verification implementation")
backup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backup)


def docker_path() -> str:
    return os.environ.get("ATLAS_DOCKER", shutil.which("docker") or "/opt/homebrew/bin/docker")


def docker(*arguments: str, input_text: str | None = None) -> str:
    environment = dict(os.environ)
    environment.setdefault("DOCKER_CONTEXT", "colima-atlas-prod")
    result = subprocess.run([docker_path(), *arguments], check=True, text=True, input=input_text, capture_output=True, env=environment)
    return result.stdout.strip()


def sql(container: str, statement: str, database: str = "atlas") -> str:
    return docker("exec", "-i", container, "psql", "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-U", "atlas", "-d", database, "-c", statement)


def wait_ready(container: str, recovering: bool = False) -> None:
    for _ in range(90):
        try:
            value = sql(container, "SELECT pg_is_in_recovery()", "postgres")
            if value == "f" or (recovering and value == "t"):
                return
        except subprocess.CalledProcessError:
            pass
        time.sleep(1)
    raise RuntimeError("The isolated PostgreSQL restore did not become ready")


def safe_blob_extract(archive: Path, destination: Path) -> None:
    with tarfile.open(archive, "r:") as blobs:
        for member in blobs.getmembers():
            target = (destination / member.name).resolve()
            if destination.resolve() not in target.parents and target != destination.resolve():
                raise ValueError("Blob archive contains a path traversal")
            if member.issym() or member.islnk() or member.isdev():
                raise ValueError("Blob archive contains an unsupported link/device")
        blobs.extractall(destination)


def database_inventory(container: str) -> dict:
    exists = sql(container, "SELECT to_regclass('gov.baseline') IS NOT NULL")
    if exists != "t":
        raise RuntimeError("Restored atlas schema is incomplete: gov.baseline is absent")
    baselines = sql(container, "SELECT COALESCE(jsonb_agg(row_to_json(b) ORDER BY project_id,baseline_no),'[]'::jsonb) FROM (SELECT project_id,baseline_id,baseline_no,parent_baseline_id,digest,sealed FROM gov.baseline) b")
    counts = sql(container, """SELECT jsonb_build_object(
      'baselines',(SELECT count(*) FROM gov.baseline),
      'baselineEntries',(SELECT count(*) FROM gov.baseline_entry),
      'revisions',(SELECT count(*) FROM core.revision),
      'sources',(SELECT count(*) FROM intake.source_blob_link),
      'jobs',(SELECT count(*) FROM ops.job),
      'auditEvents',(SELECT count(*) FROM ops.audit_event))""")
    return {"baselines": json.loads(baselines), "counts": json.loads(counts)}


def reconcile_blobs(container: str, extracted: Path, blob_volume: str) -> dict:
    rows = sql(container, "SELECT COALESCE(jsonb_agg(jsonb_build_object('project',project_id,'sha256',sha256,'storageKey',storage_key,'bytes',byte_size)),'[]'::jsonb) FROM intake.blob")
    references = json.loads(rows)
    checked = 0
    for reference in references:
        project = reference["project"].replace("-", "")
        storage_key = reference["storageKey"]
        if len(storage_key) != 64 or any(character not in "0123456789abcdef" for character in storage_key):
            raise RuntimeError("Database contains an invalid blob storage key")
        path = extracted / "blobs" / project / storage_key
        if not path.is_file() or path.stat().st_size != reference["bytes"] or backup.sha256(path) != reference["sha256"]:
            raise RuntimeError(f"Restored blob mismatch for database object {reference['sha256']}")
        # Verify the bytes in the actual isolated volume, not only the staging copy.
        volume_hash = docker("run", "--rm", "-v", f"{blob_volume}:/blobs:ro", "postgres:17", "sha256sum", f"/blobs/{project}/{storage_key}").split()[0]
        if volume_hash != reference["sha256"]:
            raise RuntimeError("Isolated blob volume checksum mismatch")
        checked += 1
    return {"databaseBlobReferences": len(references), "verifiedBlobReferences": checked}


def restore(args: argparse.Namespace) -> dict:
    archive = args.archive.resolve()
    key = backup.require_key(args.key_file)
    work = args.work_dir.resolve()
    if work in (Path("/"), Path.home().resolve()) or len(work.parts) < 3:
        raise ValueError("Choose a dedicated isolated restore work directory")
    work.mkdir(parents=True, exist_ok=True)
    work.chmod(0o700)
    restore_id = uuid.uuid4().hex[:12]
    container = f"atlas-restore-{restore_id}"
    data_volume = f"atlas-restore-data-{restore_id}"
    blob_volume = f"atlas-restore-blobs-{restore_id}"
    network = f"atlas-restore-net-{restore_id}"
    password = secrets.token_urlsafe(32)
    decrypted = work / "bundle.tar.gz"
    extracted = work / "content"
    if extracted.exists():
        raise ValueError("The restore work directory already contains extracted data")
    backup.decrypt(archive, decrypted, key)
    extracted.mkdir()
    backup.safe_extract(decrypted, extracted)
    manifest = backup.verify_decrypted(extracted)
    if args.target_time and manifest["kind"] != "base":
        raise ValueError("PITR requires a physical base backup")
    safe_blob_extract(extracted / "blobs.tar", extracted / "blobs")
    started = dt.datetime.now(dt.timezone.utc)
    try:
        docker("network", "create", "--internal", network)
        docker("volume", "create", data_volume)
        docker("volume", "create", blob_volume)
        docker("run", "--rm", "-v", f"{blob_volume}:/target", "-v", f"{extracted / 'blobs'}:/source:ro", "postgres:17", "sh", "-c", "cp -a /source/. /target/")
        if manifest["kind"] == "base":
            if not list((extracted / "database").glob("*.tar")):
                raise RuntimeError("Physical base backup contains no base tar archive")
            staging = work / "physical"
            staging.mkdir()
            with tarfile.open(extracted / "database/base.tar", "r:") as base_tar:
                for member in base_tar.getmembers():
                    target = (staging / member.name).resolve()
                    if staging.resolve() not in target.parents and target != staging.resolve():
                        raise ValueError("Physical base archive contains a path traversal")
                base_tar.extractall(staging)
            wal_archive = extracted / "database/pg_wal.tar"
            if wal_archive.exists():
                wal_target = staging / "pg_wal"
                wal_target.mkdir(exist_ok=True)
                with tarfile.open(wal_archive, "r:") as streamed_wal:
                    for member in streamed_wal.getmembers():
                        target = (wal_target / member.name).resolve()
                        if wal_target.resolve() not in target.parents and target != wal_target.resolve():
                            raise ValueError("Streamed WAL archive contains a path traversal")
                    streamed_wal.extractall(wal_target)
            recovery = staging / "postgresql.auto.conf"
            with recovery.open("a") as configuration:
                configuration.write("\nrestore_command = 'test -f /wal/%f && cp /wal/%f %p'\n")
                if args.target_time:
                    configuration.write(f"recovery_target_time = '{args.target_time}'\n")
                configuration.write("recovery_target_action = 'promote'\n")
            (staging / "recovery.signal").touch()
            docker("run", "--rm", "-v", f"{data_volume}:/target", "-v", f"{staging}:/source:ro", "postgres:17", "sh", "-c", "cp -a /source/. /target/ && chown -R 999:999 /target && chmod 700 /target")
            wal_stage = work / "wal"
            wal_stage.mkdir()
            if args.wal_dir:
                for wal in args.wal_dir.glob("*.enc"):
                    backup.decrypt(wal, wal_stage / wal.name.removesuffix(".enc"), key)
            docker("run", "-d", "--name", container, "--network", network, "-e", f"POSTGRES_PASSWORD={password}", "-e", "POSTGRES_DB=atlas", "-e", "POSTGRES_USER=atlas", "-v", f"{data_volume}:/var/lib/postgresql/data", "-v", f"{wal_stage}:/wal:ro", "postgres:17", "postgres", "-c", "archive_mode=off")
            wait_ready(container)
            # A freshly promoted PITR cluster can answer before recovery completes;
            # require recovery to be finished before reconciling business facts.
            for _ in range(90):
                if sql(container, "SELECT pg_is_in_recovery()") == "f":
                    break
                time.sleep(1)
            else:
                raise RuntimeError("PITR did not reach the requested recovery target")
        else:
            docker("run", "-d", "--name", container, "--network", network, "-e", f"POSTGRES_PASSWORD={password}", "-e", "POSTGRES_DB=atlas", "-e", "POSTGRES_USER=atlas", "-v", f"{data_volume}:/var/lib/postgresql/data", "postgres:17")
            wait_ready(container)
            docker("exec", "-i", container, "createdb", "-U", "atlas", "keycloak")
            environment = dict(os.environ)
            environment.setdefault("DOCKER_CONTEXT", "colima-atlas-prod")
            for database_name in ("atlas", "keycloak"):
                with (extracted / f"{database_name}.dump").open("rb") as dump:
                    subprocess.run([docker_path(), "exec", "-i", container, "pg_restore", "-U", "atlas", "-d", database_name, "--no-owner", "--no-privileges"], stdin=dump, check=True, env=environment)
        inventory = database_inventory(container)
        blobs = reconcile_blobs(container, extracted, blob_volume)
        report = {
            "format": "business-atlas-restore-verification-v1", "backupId": manifest["backupId"],
            "backupKind": manifest["kind"], "requestedTargetTime": args.target_time,
            "startedAt": started.isoformat(), "completedAt": dt.datetime.now(dt.timezone.utc).isoformat(),
            "durationSeconds": (dt.datetime.now(dt.timezone.utc) - started).total_seconds(),
            "isolation": {"publishedPorts": 0, "internalNetwork": network, "container": container},
            "inventory": inventory, "blobVerification": blobs, "status": "verified",
        }
        report_path = work / "restore-report.json"
        backup.atomic_json(report_path, report)
        print(json.dumps({"status": "verified", "report": str(report_path), "container": container, "dataVolume": data_volume, "blobVolume": blob_volume}, indent=2))
        return report
    except Exception:
        try:
            environment = dict(os.environ)
            environment.setdefault("DOCKER_CONTEXT", "colima-atlas-prod")
            logs = subprocess.run([docker_path(), "logs", "--tail", "80", container], text=True, capture_output=True, env=environment)
            print(logs.stdout + logs.stderr, file=sys.stderr)
        except Exception:
            pass
        docker("rm", "-f", container) if container else None
        raise
    finally:
        decrypted.unlink(missing_ok=True)
        if not args.keep:
            docker("rm", "-f", container)
            docker("volume", "rm", data_volume, blob_volume)
            docker("network", "rm", network)
            # The verification report is durable; plaintext database dumps,
            # streamed WAL and private blobs are not left behind after a drill.
            for transient in (extracted, work / "physical", work / "wal"):
                shutil.rmtree(transient, ignore_errors=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--key-file", type=Path, required=True)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--target-time", help="RFC3339/PostgreSQL recovery target timestamp for a base backup")
    parser.add_argument("--wal-dir", type=Path, help="Directory of encrypted .enc WAL archives")
    parser.add_argument("--keep", action="store_true", help="Keep the isolated verified environment for application migration/smoke checks")
    args = parser.parse_args()
    if args.target_time:
        # Reject SQL syntax/configuration injection; PostgreSQL accepts this explicit timestamp form.
        dt.datetime.fromisoformat(args.target_time.replace("Z", "+00:00"))
    restore(args)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(json.dumps({"code": "RESTORE_FAILED", "errorType": type(error).__name__, "message": str(error)}), file=sys.stderr)
        raise SystemExit(1)
