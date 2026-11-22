#!/usr/bin/env python3
"""Restore the previous application release and record the actual result."""

import datetime
import json
import os
import ssl
import subprocess
import sys
import urllib.request
import time
from pathlib import Path

product = Path.home() / "atlas-deploy/product"
marker = product / "current-release.json"
history = product / "deployment-history.jsonl"
current = json.loads(marker.read_text())
target = current.get("previousRelease")
if not target:
    raise SystemExit("No previous release is recorded")
release = Path(target["directory"]).resolve()
releases = (product / "releases").resolve()
if not release.is_relative_to(releases) or not (release / "compose.yml").is_file():
    raise SystemExit("The previous release is missing or outside the release directory")
env = {**os.environ, "DOCKER_CONTEXT": "colima-atlas-prod", "PATH": "/opt/homebrew/bin:/usr/bin:/bin"}
event = {"at": datetime.datetime.now(datetime.timezone.utc).isoformat(), "event": "manual_rollback_started", "fromBuildId": current["buildId"], "restoredBuildId": target["buildId"]}
try:
    subprocess.run(["/opt/homebrew/bin/docker", "compose", "-p", "atlas-product", "up", "-d"], cwd=release, env=env, check=True)
    context = ssl.create_default_context(cafile=str(product / "tls/atlas-local-ca.crt"))
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context))
    for attempt in range(45):
        try:
            scheme = "https" if target.get("applicationUrl", target.get("url", "")).startswith("https:") else "http"
            with opener.open(scheme + "://127.0.0.1:15809/health/ready", timeout=10) as response:
                if response.status != 200:
                    raise RuntimeError("The restored API is not ready")
            break
        except Exception:
            if attempt == 44:
                raise
            time.sleep(2)
    temporary = marker.with_suffix(".tmp")
    temporary.write_text(json.dumps(target, indent=2) + "\n")
    temporary.chmod(0o600)
    temporary.replace(marker)
    event["event"] = "manual_rollback_succeeded"
    print("Restored release", target["buildId"])
except Exception as error:
    event["event"] = "manual_rollback_failed"
    event["errorType"] = type(error).__name__
    raise
finally:
    with history.open("a") as stream:
        stream.write(json.dumps(event) + "\n")
    history.chmod(0o600)
