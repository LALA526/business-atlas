#!/usr/bin/env python3
"""Evaluate Business Atlas health/recovery signals and deliver locatable alerts."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import hmac
import importlib.util
import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import sys
import time
import urllib.request
import uuid

spec = importlib.util.spec_from_file_location("atlas_backup", Path(__file__).resolve().parents[1] / "backup/backup.py")
if spec is None or spec.loader is None:
    raise RuntimeError("Cannot load Compose operations")
backup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backup)


def now() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc)


def atomic(path: Path, value: object) -> None:
    backup.atomic_json(path, value)


def load(path: Path, default):
    return json.loads(path.read_text()) if path.exists() else default


def check(compose: backup.Compose, config: dict, args: argparse.Namespace) -> list[dict]:
    alerts = []
    started = time.monotonic()
    try:
        lsn = compose.sql("SELECT pg_current_wal_lsn()")
        metrics_raw = compose.sql("""SELECT jsonb_build_object(
          'oldestQueuedSeconds',COALESCE((SELECT EXTRACT(EPOCH FROM now()-min(available_at)) FROM ops.job WHERE state='queued'),0),
          'expiredLeases',COALESCE((SELECT count(*) FROM ops.job WHERE state='running' AND lease_until<now()),0),
          'failedJobs',COALESCE((SELECT count(*) FROM ops.job WHERE state='failed' AND version>0),0),
          'outboxRetries',COALESCE((SELECT count(*) FROM ops.outbox WHERE attempt>1),0),
          'connections',(SELECT count(*) FROM pg_stat_activity))""")
        metrics = json.loads(metrics_raw)
        metrics["databaseLsn"] = lsn
    except Exception as error:
        alerts.append({"code": "ATLAS_DB_UNAVAILABLE", "severity": "critical", "locator": "compose:postgres", "errorType": type(error).__name__})
        metrics = {}
    latency_ms = (time.monotonic() - started) * 1000
    thresholds = config["thresholds"]
    if metrics:
        for metric, threshold, code in (
            ("oldestQueuedSeconds", thresholds["oldestQueuedSeconds"], "ATLAS_QUEUE_STALE"),
            ("expiredLeases", thresholds["expiredLeases"], "ATLAS_LEASE_EXPIRED"),
            ("failedJobs", thresholds["failedJobs"], "ATLAS_JOB_FAILED"),
            ("outboxRetries", thresholds["outboxRetries"], "ATLAS_OUTBOX_RETRY"),
            ("connections", thresholds["databaseConnections"], "ATLAS_DB_CONNECTIONS"),
        ):
            if metrics.get(metric, 0) > threshold:
                alerts.append({"code": code, "severity": "critical" if code in ("ATLAS_LEASE_EXPIRED", "ATLAS_JOB_FAILED") else "warning", "locator": f"postgres:{metric}", "value": metrics.get(metric), "threshold": threshold})
    if latency_ms > thresholds["databaseQueryMilliseconds"]:
        alerts.append({"code": "ATLAS_DB_SLOW", "severity": "warning", "locator": "postgres:monitor-query", "value": round(latency_ms, 2), "threshold": thresholds["databaseQueryMilliseconds"]})
    if args.backup_root:
        metadata = sorted(args.backup_root.glob("*.json"))
        latest = None
        for path in reversed(metadata):
            value = load(path, {})
            if value.get("format") == backup.FORMAT and value.get("archiveSha256"):
                latest = value
                break
        if latest is None:
            alerts.append({"code": "ATLAS_BACKUP_MISSING", "severity": "critical", "locator": str(args.backup_root)})
        else:
            completed = dt.datetime.fromisoformat(latest["completedAt"])
            age_hours = (now() - completed).total_seconds() / 3600
            if age_hours > thresholds["backupAgeHours"]:
                alerts.append({"code": "ATLAS_BACKUP_STALE", "severity": "critical", "locator": latest.get("archive"), "value": round(age_hours, 2), "threshold": thresholds["backupAgeHours"]})
        wal_status = args.backup_root / "wal/status.json"
        if wal_status.exists():
            modified = dt.datetime.fromtimestamp(wal_status.stat().st_mtime, dt.timezone.utc)
            lag_minutes = (now() - modified).total_seconds() / 60
            if lag_minutes > thresholds["walArchiveLagMinutes"]:
                alerts.append({"code": "ATLAS_WAL_STALE", "severity": "critical", "locator": str(wal_status), "value": round(lag_minutes, 2), "threshold": thresholds["walArchiveLagMinutes"]})
        else:
            alerts.append({"code": "ATLAS_WAL_NOT_ARCHIVED", "severity": "critical", "locator": str(args.backup_root / "wal")})
        usage = shutil.disk_usage(args.backup_root)
        free_percent = usage.free * 100 / usage.total
        if free_percent < thresholds["minimumDiskFreePercent"]:
            alerts.append({"code": "ATLAS_DISK_LOW", "severity": "critical", "locator": str(args.backup_root), "value": round(free_percent, 2), "threshold": thresholds["minimumDiskFreePercent"]})
    if args.health_url:
        try:
            context = ssl.create_default_context(cafile=str(args.ca_file)) if args.ca_file else ssl.create_default_context()
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context))
            with opener.open(args.health_url, timeout=5) as response:
                if response.status != 200:
                    raise RuntimeError(f"HTTP {response.status}")
        except Exception as error:
            alerts.append({"code": "ATLAS_READINESS_FAILED", "severity": "critical", "locator": args.health_url, "errorType": type(error).__name__})
    return alerts


def deliver(alerts: list[dict], config: dict, args: argparse.Namespace) -> None:
    state_path = args.state_dir / "alert-state.json"
    previous = load(state_path, {})
    current = {alert["code"]: alert for alert in alerts}
    events = []
    for code, alert in current.items():
        alert.update({"eventId": str(uuid.uuid4()), "observedAt": now().isoformat(), "state": "firing", "runbook": config["runbookBase"] + "#" + code.lower().replace("_", "-")})
        if code not in previous:
            events.append(alert)
    for code in previous.keys() - current.keys():
        events.append({"code": code, "eventId": str(uuid.uuid4()), "observedAt": now().isoformat(), "state": "resolved", "severity": "info", "locator": previous[code].get("locator")})
    for event in events:
        # Notification content contains opaque locators only, never project content or secrets.
        body = json.dumps({"source": "business-atlas", "event": event}, separators=(",", ":")).encode()
        request = urllib.request.Request(config["webhookUrl"], data=body, method="POST", headers={"Content-Type": "application/json", "User-Agent": "BusinessAtlas-Ops/1"})
        secret_file = config.get("webhookSecretFile")
        if secret_file:
            secret = Path(secret_file).read_bytes()
            request.add_header("X-Atlas-Signature-256", "sha256=" + hmac.new(secret, body, hashlib.sha256).hexdigest())
        if not args.dry_run:
            with urllib.request.urlopen(request, timeout=10) as response:
                if response.status >= 300:
                    raise RuntimeError(f"Alert webhook returned HTTP {response.status}")
        with (args.state_dir / "alerts.jsonl").open("ab") as stream:
            stream.write(body + b"\n")
    atomic(state_path, current)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release-dir", type=Path, required=True)
    parser.add_argument("--config", type=Path, default=Path(__file__).with_name("alerts.json"))
    parser.add_argument("--state-dir", type=Path, required=True)
    parser.add_argument("--backup-root", type=Path)
    parser.add_argument("--health-url")
    parser.add_argument("--ca-file", type=Path)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--test-alert", action="store_true", help="Deliver an explicitly synthetic alert-path test")
    args = parser.parse_args()
    args.state_dir.mkdir(parents=True, exist_ok=True)
    args.state_dir.chmod(0o700)
    config = load(args.config, {})
    if args.test_alert:
        deliver([{"code": "ATLAS_SYNTHETIC_ALERT_TEST", "severity": "info", "locator": "automated-test-actor"}], config, args)
        print(json.dumps({"status": "synthetic-alert-delivered" if not args.dry_run else "synthetic-alert-dry-run"}))
        return
    compose = backup.Compose(args.release_dir)
    alerts = check(compose, config, args)
    deliver(alerts, config, args)
    print(json.dumps({"status": "critical" if any(item["severity"] == "critical" for item in alerts) else "ok", "alerts": alerts}, indent=2))
    if alerts:
        raise SystemExit(1)


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception as error:
        print(json.dumps({"code": "MONITOR_FAILED", "errorType": type(error).__name__, "message": str(error)}), file=sys.stderr)
        raise SystemExit(2)
