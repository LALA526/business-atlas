#!/usr/bin/env python3
"""Create and verify encrypted, consistent Business Atlas database/blob backups.

The normal path uses the Compose ``backup`` service. Writers are stopped while the
atlas dump/base-backup and the immutable blob snapshot are captured, then restarted.
The encryption key stays outside BACKUP_ROOT and is never sent to a container.
"""

from __future__ import annotations

import argparse
import datetime as dt
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import uuid

FORMAT = "business-atlas-backup-v1"
def utcnow() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def atomic_json(path: Path, value: object) -> None:
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    temporary.chmod(0o600)
    temporary.replace(path)


def run(arguments: list[str], **kwargs) -> subprocess.CompletedProcess:
    return subprocess.run(arguments, check=True, **kwargs)


def openssl() -> str:
    executable = shutil.which("openssl")
    if not executable:
        raise RuntimeError("openssl is required for authenticated-at-rest backup handling")
    return executable


def require_key(path: Path) -> Path:
    path = path.resolve()
    if not path.is_file() or path.stat().st_size < 32:
        raise ValueError("The backup encryption key file is missing or too short")
    if os.name == "posix" and path.stat().st_mode & 0o077:
        raise ValueError("The backup encryption key file must not be accessible by group/other users")
    return path


def encrypt(source: Path, target: Path, key: Path) -> None:
    require_key(key)
    with source.open("rb") as incoming, target.open("xb") as outgoing:
        run([openssl(), "enc", "-aes-256-cbc", "-pbkdf2", "-iter", "600000", "-pass", f"file:{key}"], stdin=incoming, stdout=outgoing)
    target.chmod(0o600)


def decrypt(source: Path, target: Path, key: Path) -> None:
    require_key(key)
    with source.open("rb") as incoming, target.open("xb") as outgoing:
        run([openssl(), "enc", "-d", "-aes-256-cbc", "-pbkdf2", "-iter", "600000", "-pass", f"file:{key}"], stdin=incoming, stdout=outgoing)
    target.chmod(0o600)


def safe_extract(archive: Path, destination: Path) -> None:
    root = destination.resolve()
    with tarfile.open(archive, "r:gz") as bundle:
        for member in bundle.getmembers():
            if member.issym() or member.islnk() or member.isdev():
                raise ValueError(f"Unsafe backup member: {member.name}")
            target = (destination / member.name).resolve()
            if target != root and root not in target.parents:
                raise ValueError(f"Backup path escapes extraction root: {member.name}")
        bundle.extractall(destination)


class Compose:
    def __init__(self, release: Path):
        self.release = release.resolve()
        self.docker = os.environ.get("ATLAS_DOCKER", shutil.which("docker") or "/opt/homebrew/bin/docker")
        self.project = os.environ.get("ATLAS_COMPOSE_PROJECT", "atlas-product")
        self.environment = dict(os.environ)
        self.environment.setdefault("DOCKER_CONTEXT", "colima-atlas-prod")

    def __call__(self, *arguments: str, capture: bool = False) -> subprocess.CompletedProcess:
        return run([self.docker, "compose", "-p", self.project, "-f", str(self.release / "compose.yml"), *arguments], cwd=self.release, env=self.environment, text=True, capture_output=capture)

    def sql(self, statement: str) -> str:
        result = self("exec", "-T", "postgres", "psql", "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-U", "atlas", "-d", "atlas", "-c", statement, capture=True)
        return result.stdout.strip()


def file_record(path: Path, relative: str) -> dict:
    return {"path": relative, "bytes": path.stat().st_size, "sha256": sha256(path)}


def verify_decrypted(directory: Path) -> dict:
    manifest_path = directory / "manifest.json"
    if not manifest_path.is_file():
        raise ValueError("Backup manifest is missing")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("format") != FORMAT or manifest.get("kind") not in ("logical", "base"):
        raise ValueError("Unsupported backup format")
    for record in manifest["files"]:
        path = (directory / record["path"]).resolve()
        if directory.resolve() not in path.parents or not path.is_file():
            raise ValueError(f"Backup file is missing: {record['path']}")
        if path.stat().st_size != record["bytes"] or sha256(path) != record["sha256"]:
            raise ValueError(f"Backup checksum mismatch: {record['path']}")
    blob_archive = directory / "blobs.tar"
    with tarfile.open(blob_archive, "r:") as blobs:
        for member in blobs.getmembers():
            if not member.isfile():
                continue
            parts = Path(member.name).parts
            name = parts[-1]
            if name.startswith(".upload-"):
                raise ValueError("An incomplete blob upload was captured")
            if len(name) == 64 and all(character in "0123456789abcdef" for character in name):
                stream = blobs.extractfile(member)
                if stream is None or hashlib.sha256(stream.read()).hexdigest() != name:
                    raise ValueError(f"Blob content hash mismatch: {member.name}")
    return manifest


def verify_archive(archive: Path, key: Path, workspace: Path | None = None) -> dict:
    archive = archive.resolve()
    sidecar = archive.with_suffix(archive.suffix + ".sha256")
    if sidecar.is_file() and sidecar.read_text().split()[0] != sha256(archive):
        raise ValueError("Encrypted archive checksum mismatch")
    owned = workspace is None
    temporary = Path(tempfile.mkdtemp(prefix="atlas-verify-")) if owned else workspace
    try:
        encrypted = temporary / "bundle.tar.gz"
        decrypt(archive, encrypted, key)
        extracted = temporary / "content"
        extracted.mkdir()
        safe_extract(encrypted, extracted)
        return verify_decrypted(extracted)
    finally:
        if owned:
            shutil.rmtree(temporary, ignore_errors=True)


def create(args: argparse.Namespace) -> dict:
    release = args.release_dir.resolve()
    root = args.backup_root.resolve()
    key = require_key(args.key_file)
    root.mkdir(parents=True, exist_ok=True)
    if os.name == "posix":
        root.chmod(0o700)
    compose = Compose(release)
    backup_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    staging = root / f".{backup_id}.partial"
    staging.mkdir(mode=0o700)
    lock_path = root / ".backup.lock"
    with lock_path.open("w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        running = set(compose("ps", "--status", "running", "--services", capture=True).stdout.splitlines())
        writers = [name for name in ("api", "worker", "identity") if name in running]
        started = utcnow()
        try:
            if writers and not args.keep_writers:
                compose("stop", *writers)
            elif writers and args.keep_writers:
                raise ValueError("A consistent snapshot requires stopping API and Worker writers")
            lsn = compose.sql("SELECT pg_current_wal_lsn()")
            compose("run", "--rm", "--no-deps", "--volume", f"{staging}:/out", "backup", "sh", "-eu", "-c",
                "pg_dumpall --host=postgres --username=atlas --globals-only --file=/out/globals.sql && " +
                ("pg_basebackup --host=postgres --username=atlas --pgdata=/out/database --format=tar --wal-method=stream --checkpoint=fast --no-password" if args.kind == "base" else
                 "pg_dump --host=postgres --username=atlas --dbname=atlas --format=custom --file=/out/atlas.dump --no-password && pg_dump --host=postgres --username=atlas --dbname=keycloak --format=custom --file=/out/keycloak.dump --no-password") +
                " && tar -C /blobs -cf /out/blobs.tar .")
            if args.kind == "logical":
                # Do not reference a fresh installation's missing history table in
                # a CASE branch: PostgreSQL resolves relations while planning the
                # query, before either branch can run.
                has_history = compose.sql("SELECT to_regclass('public.__EFMigrationsHistory') IS NOT NULL") == "t"
                migration = compose.sql("SELECT COALESCE(string_agg(version, ',' ORDER BY version), '') FROM public.__EFMigrationsHistory") if has_history else ""
            else:
                migration = "physical-base-backup"
            expected = staging / ("database/base.tar" if args.kind == "base" else "atlas.dump")
            required_outputs = [expected, staging / "blobs.tar", staging / "globals.sql"]
            if args.kind == "logical":
                required_outputs.append(staging / "keycloak.dump")
            if any(not path.is_file() for path in required_outputs):
                raise RuntimeError("Backup service output is incomplete; check BACKUP_ROOT host mount visibility")
            files = [file_record(path, path.relative_to(staging).as_posix()) for path in sorted(staging.rglob("*")) if path.is_file()]
            manifest = {
                "format": FORMAT, "backupId": backup_id, "kind": args.kind,
                "startedAt": started, "completedAt": utcnow(), "databases": ["atlas", "keycloak"],
                "databaseLsn": lsn, "migrationVersions": migration,
                "applicationBuildId": args.build_id, "writersStopped": writers,
                "consistency": "API, Worker and Identity stopped across database and blob capture",
                "files": files,
            }
            atomic_json(staging / "manifest.json", manifest)
            plain = root / f".{backup_id}.tar.gz"
            with tarfile.open(plain, "x:gz") as bundle:
                for path in sorted(staging.iterdir()):
                    bundle.add(path, arcname=path.name, recursive=True)
            archive = root / f"{backup_id}.{args.kind}.tar.enc"
            encrypt(plain, archive, key)
            plain.unlink()
            archive.with_suffix(archive.suffix + ".sha256").write_text(f"{sha256(archive)}  {archive.name}\n")
            verified = verify_archive(archive, key)
            metadata = {key: value for key, value in verified.items() if key != "files"}
            metadata.update({"archive": archive.name, "archiveSha256": sha256(archive), "verifiedAt": utcnow()})
            atomic_json(root / f"{backup_id}.json", metadata)
            return metadata
        finally:
            if writers and not args.keep_writers:
                compose("start", *writers)
            shutil.rmtree(staging, ignore_errors=True)


def wal_sync(args: argparse.Namespace) -> dict:
    source = args.wal_root.resolve()
    destination = args.backup_root.resolve() / "wal"
    destination.mkdir(parents=True, exist_ok=True)
    key = require_key(args.key_file)
    copied = 0
    for path in sorted(source.iterdir()):
        if not path.is_file() or path.name.endswith((".enc", ".sha256", ".partial")) or path.name == "status.json":
            continue
        target = destination / (path.name + ".enc")
        temporary = destination / (path.name + ".partial")
        if target.exists() and target.with_suffix(target.suffix + ".sha256").exists():
            continue
        encrypt(path, temporary, key)
        temporary.replace(target)
        target.with_suffix(target.suffix + ".sha256").write_text(f"{sha256(target)}  {target.name}\n")
        copied += 1
    result = {"at": utcnow(), "encryptedWalFiles": copied, "latestSourceMtime": max((path.stat().st_mtime for path in source.iterdir() if path.is_file()), default=0)}
    atomic_json(destination / "status.json", result)
    return result


def retain(args: argparse.Namespace) -> list[str]:
    root = args.backup_root.resolve()
    now = dt.datetime.now(dt.timezone.utc)
    archives = sorted(root.glob("*.tar.enc"), key=lambda path: path.stat().st_mtime, reverse=True)
    kept: set[Path] = set()
    for index, path in enumerate(archives):
        age = now - dt.datetime.fromtimestamp(path.stat().st_mtime, dt.timezone.utc)
        timestamp = dt.datetime.fromtimestamp(path.stat().st_mtime, dt.timezone.utc)
        if index < args.daily or age.days < 7 or (age.days <= 28 and timestamp.weekday() == 6) or (age.days <= 92 and timestamp.day == 1):
            kept.add(path)
    removed = []
    for path in archives:
        if path in kept:
            continue
        backup_id = path.name.split(".", 1)[0]
        path.unlink()
        path.with_suffix(path.suffix + ".sha256").unlink(missing_ok=True)
        (root / f"{backup_id}.json").unlink(missing_ok=True)
        removed.append(path.name)
    return removed


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--backup-root", type=Path, required=True)
    common.add_argument("--key-file", type=Path, required=True)
    create_parser = subparsers.add_parser("create", parents=[common])
    create_parser.add_argument("--release-dir", type=Path, required=True)
    create_parser.add_argument("--kind", choices=("logical", "base"), default="logical")
    create_parser.add_argument("--build-id")
    create_parser.add_argument("--keep-writers", action="store_true", help=argparse.SUPPRESS)
    create_parser.set_defaults(handler=create)
    verify_parser = subparsers.add_parser("verify", parents=[common])
    verify_parser.add_argument("archive", type=Path)
    verify_parser.set_defaults(handler=lambda args: verify_archive(args.archive, args.key_file))
    wal_parser = subparsers.add_parser("wal-sync", parents=[common])
    wal_parser.add_argument("--wal-root", type=Path, required=True)
    wal_parser.set_defaults(handler=wal_sync)
    retain_parser = subparsers.add_parser("retain", parents=[common])
    retain_parser.add_argument("--daily", type=int, default=7)
    retain_parser.set_defaults(handler=retain)
    args = parser.parse_args()
    result = args.handler(args)
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(json.dumps({"code": "BACKUP_FAILED", "errorType": type(error).__name__, "message": str(error)}), file=sys.stderr)
        raise SystemExit(1)
