#!/usr/bin/env python3
"""Focused T40 checks; set ATLAS_T40_DOCKER_TEST=1 for the real Docker drill."""

import argparse
import datetime as dt
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("atlas_backup", ROOT / "deploy/backup/backup.py")
backup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backup)


class BackupFormatTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="atlas-t40-unit-")
        self.root = Path(self.temporary.name)
        self.key = self.root / "key"
        self.key.write_bytes(b"automated-test-key-not-a-production-secret-12345")
        self.key.chmod(0o600)

    def tearDown(self):
        self.temporary.cleanup()

    def archive(self):
        content = self.root / "content"
        content.mkdir()
        blob = b"automated Business Atlas backup test bytes"
        digest = backup.hashlib.sha256(blob).hexdigest()
        (content / "atlas.dump").write_bytes(b"test-dump")
        with tarfile.open(content / "blobs.tar", "w") as blobs:
            import io
            info = tarfile.TarInfo("project/" + digest)
            info.size = len(blob)
            blobs.addfile(info, io.BytesIO(blob))
        dump = content / "atlas.dump"
        backup.atomic_json(content / "manifest.json", {"format": backup.FORMAT, "backupId": "automated-test", "kind": "logical", "files": [backup.file_record(dump, "atlas.dump"), backup.file_record(content / "blobs.tar", "blobs.tar")]})
        plain = self.root / "bundle.tar.gz"
        with tarfile.open(plain, "w:gz") as bundle:
            for path in content.iterdir():
                bundle.add(path, arcname=path.name)
        encrypted = self.root / "backup.tar.enc"
        backup.encrypt(plain, encrypted, self.key)
        return encrypted

    def test_encrypted_round_trip_and_blob_hash(self):
        archive = self.archive()
        self.assertEqual("automated-test", backup.verify_archive(archive, self.key)["backupId"])

    def test_ciphertext_tamper_is_rejected(self):
        archive = self.archive()
        with archive.open("r+b") as stream:
            stream.seek(20)
            stream.write(bytes([stream.read(1)[0] ^ 1]))
        with self.assertRaises(Exception):
            backup.verify_archive(archive, self.key)


class AlertDeliveryTests(unittest.TestCase):
    def test_signed_webhook_and_locator(self):
        import http.server
        import threading
        monitor_spec = importlib.util.spec_from_file_location("atlas_monitor", ROOT / "deploy/ops/monitor.py")
        monitor = importlib.util.module_from_spec(monitor_spec)
        monitor_spec.loader.exec_module(monitor)
        received = []
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                body = self.rfile.read(int(self.headers["Content-Length"]))
                received.append((body, self.headers["X-Atlas-Signature-256"]))
                self.send_response(202)
                self.end_headers()
            def log_message(self, *args):
                pass
        server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.handle_request)
        thread.start()
        with tempfile.TemporaryDirectory(prefix="atlas-t40-alert-") as temporary:
            state = Path(temporary)
            secret = state / "webhook.key"
            secret.write_bytes(b"automated-alert-test-key")
            args = argparse.Namespace(state_dir=state, dry_run=False)
            config = {"webhookUrl": f"http://127.0.0.1:{server.server_port}/alerts", "webhookSecretFile": str(secret), "runbookBase": "https://example.invalid/runbook"}
            monitor.deliver([{"code": "ATLAS_SYNTHETIC_ALERT_TEST", "severity": "info", "locator": "automated-test-actor"}], config, args)
        thread.join(timeout=5)
        server.server_close()
        self.assertEqual(1, len(received))
        body, signature = received[0]
        import hmac
        expected = "sha256=" + hmac.new(b"automated-alert-test-key", body, backup.hashlib.sha256).hexdigest()
        self.assertEqual(expected, signature)
        self.assertEqual("automated-test-actor", json.loads(body)["event"]["locator"])


@unittest.skipUnless(os.environ.get("ATLAS_T40_DOCKER_TEST") == "1", "real Docker drill is explicitly selected")
class DockerRecoveryDrill(unittest.TestCase):
    def test_compose_backup_and_isolated_restore(self):
        if not shutil.which("docker"):
            self.fail("Docker is required for the selected real recovery drill")
        root = Path(tempfile.mkdtemp(prefix=".t40-docker-", dir=ROOT))
        project = "atlas-t40-" + uuid.uuid4().hex[:8]
        environment = dict(os.environ, ATLAS_COMPOSE_PROJECT=project, ATLAS_DB_PASSWORD="automated-drill-password", ATLAS_BACKUP_ROOT=str(root / "backups"), DOCKER_CONTEXT=os.environ.get("DOCKER_CONTEXT", "colima-atlas"))
        release = root / "release"
        release.mkdir()
        shutil.copy2(ROOT / "scripts/fast/compose.yml", release / "compose.yml")
        shutil.copy2(ROOT / "scripts/fast/pg_hba.conf", release / "pg_hba.conf")
        (release / ".env").write_text(f"ATLAS_DB_PASSWORD=automated-drill-password\nATLAS_BACKUP_ROOT={root / 'backups'}\n")
        (root / "backups/wal").mkdir(parents=True)
        key = root / "key"
        key.write_bytes(b"automated-drill-encryption-key-0123456789abcdef")
        key.chmod(0o600)
        try:
            subprocess.run(["docker", "compose", "-p", project, "-f", str(release / "compose.yml"), "up", "-d", "postgres"], cwd=release, env=environment, check=True, capture_output=True)
            for _ in range(60):
                ready = subprocess.run(["docker", "compose", "-p", project, "exec", "-T", "postgres", "pg_isready", "-U", "atlas", "-d", "atlas"], cwd=release, env=environment, capture_output=True)
                if ready.returncode == 0:
                    break
                import time
                time.sleep(1)
            else:
                self.fail("Disposable PostgreSQL did not become ready")
            subprocess.run(["docker", "compose", "-p", project, "exec", "-T", "postgres", "createdb", "-U", "atlas", "keycloak"], cwd=release, env=environment, check=True, capture_output=True)
            schema = (ROOT / "docs/baselines/T1.0/02_机器合同/database.sql").read_text() + "\nCREATE TABLE public.__EFMigrationsHistory(version text PRIMARY KEY); INSERT INTO public.__EFMigrationsHistory VALUES ('automated-drill');\n"
            subprocess.run(["docker", "compose", "-p", project, "exec", "-T", "postgres", "psql", "-v", "ON_ERROR_STOP=1", "-U", "atlas", "-d", "atlas"], cwd=release, env=environment, input=schema, text=True, check=True, capture_output=True)
            args = argparse.Namespace(release_dir=release, backup_root=root / "backups", key_file=key, kind="logical", build_id="automated-t40-drill", keep_writers=False)
            os.environ.update(environment)
            try:
                metadata = backup.create(args)
            except Exception:
                for candidate in (root / "backups").glob("*.tar.enc"):
                    decrypted = root / "debug.tar.gz"
                    backup.decrypt(candidate, decrypted, key)
                    with tarfile.open(decrypted, "r:gz") as bundle:
                        print("ARCHIVE MEMBERS", bundle.getnames())
                raise
            archive = root / "backups" / metadata["archive"]
            restore = ROOT / "scripts/restore/restore.py"
            report_dir = root / "restored"
            subprocess.run(["python3", str(restore), str(archive), "--key-file", str(key), "--work-dir", str(report_dir)], env=environment, check=True, capture_output=True)
            report = json.loads((report_dir / "restore-report.json").read_text())
            self.assertEqual("verified", report["status"])
            self.assertEqual(0, report["isolation"]["publishedPorts"])
            self.assertIn("baselines", report["inventory"])
            base_args = argparse.Namespace(release_dir=release, backup_root=root / "backups", key_file=key, kind="base", build_id="automated-t40-drill", keep_writers=False)
            base_metadata = backup.create(base_args)
            base_archive = root / "backups" / base_metadata["archive"]
            pitr_dir = root / "restored-pitr"
            target = dt.datetime.now(dt.timezone.utc).isoformat()
            import time
            drill_compose = backup.Compose(release)
            subprocess.run(["docker", "compose", "-p", project, "exec", "-T", "postgres", "psql", "-v", "ON_ERROR_STOP=1", "-U", "atlas", "-d", "atlas", "-c", "CREATE TABLE ops.pitr_drill_marker(id int PRIMARY KEY); INSERT INTO ops.pitr_drill_marker VALUES (1);"], cwd=release, env=environment, check=True, capture_output=True)
            drill_compose.sql("SELECT pg_switch_wal()")
            for _ in range(30):
                archived_at = drill_compose.sql("SELECT last_archived_time FROM pg_stat_archiver")
                if archived_at and dt.datetime.fromisoformat(archived_at.replace(" ", "T").replace("+00", "+00:00")) > dt.datetime.fromisoformat(target):
                    break
                time.sleep(1)
            else:
                self.fail("A WAL segment newer than the recovery target was not archived")
            backup.wal_sync(argparse.Namespace(backup_root=root / "backups", key_file=key, wal_root=root / "backups/wal"))
            pitr_result = subprocess.run(["python3", str(restore), str(base_archive), "--key-file", str(key), "--work-dir", str(pitr_dir), "--target-time", target, "--wal-dir", str(root / "backups/wal")], env=environment, capture_output=True, text=True)
            if pitr_result.returncode:
                print("RESTORE WAL", sorted(path.name for path in (pitr_dir / "wal").iterdir()))
                print(pitr_result.stdout, pitr_result.stderr)
            pitr_result.check_returncode()
            pitr_report = json.loads((pitr_dir / "restore-report.json").read_text())
            self.assertEqual("verified", pitr_report["status"])
            self.assertEqual(target, pitr_report["requestedTargetTime"])
        finally:
            subprocess.run(["docker", "compose", "-p", project, "-f", str(release / "compose.yml"), "down", "-v", "--remove-orphans"], cwd=release, env=environment, capture_output=True)
            shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
