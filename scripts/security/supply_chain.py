#!/usr/bin/env python3
"""Run Business Atlas secret, dependency, license, and SBOM checks.

The report contains locations and package metadata, never matched secret values.
Network dependency audits can be skipped explicitly for offline development; the
result is then reported as incomplete rather than passed.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
ALLOWED_LICENSES = {
    "0bsd", "apache-2.0", "bsd-2-clause", "bsd-3-clause", "cc0-1.0",
    "isc", "mit", "python-2.0", "unlicense", "cc-by-4.0",
}
SECRET_PATTERNS = (
    ("private-key", re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")),
    ("aws-access-key", re.compile(r"(?<![A-Z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Z0-9])")),
    ("github-token", re.compile(r"(?<![A-Za-z0-9_])(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,})")),
    ("generic-secret", re.compile(
        r"(?i)\b(?:password|passwd|api[_-]?key|access[_-]?token|client[_-]?secret|private[_-]?key)\b"
        r"\s*[\"']?\s*[:=]\s*[\"']([^\"']{12,})[\"']"
    )),
    ("environment-secret", re.compile(
        r"(?i)^\s*(?:export\s+)?(?:password|passwd|api[_-]?key|access[_-]?token|client[_-]?secret|private[_-]?key)"
        r"\s*=\s*([A-Za-z0-9+/=_-]{12,})\s*(?:#.*)?$"
    )),
)
PLACEHOLDERS = re.compile(
    r"(?i)(?:example|placeholder|replace|changeme|not-a-production|automated-|test-|dummy|"
    r"your[_-]|\$\{|\{\{|<|xxx+|localhost|invalid)"
)
SKIP_SUFFIXES = {".zip", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".pdf", ".dll", ".pdb"}


def tracked_files(root: Path) -> list[str]:
    result = subprocess.run(["git", "ls-files", "-z"], cwd=root, check=True, capture_output=True)
    return [item.decode("utf-8") for item in result.stdout.split(b"\0") if item]


def scan_secrets(root: Path) -> list[dict]:
    findings: list[dict] = []
    for relative in tracked_files(root):
        path = root / relative
        if path.suffix.lower() in SKIP_SUFFIXES or path.stat().st_size > 2_000_000:
            continue
        try:
            lines = path.read_text(encoding="utf-8").splitlines()
        except UnicodeDecodeError:
            continue
        for number, line in enumerate(lines, 1):
            for kind, pattern in SECRET_PATTERNS:
                match = pattern.search(line)
                if not match:
                    continue
                if kind in {"generic-secret", "environment-secret"}:
                    value = match.group(1)
                    if PLACEHOLDERS.search(value) or value.startswith(("\"", "'")):
                        continue
                findings.append({"kind": kind, "path": relative, "line": number})
    return findings


def run_json(arguments: list[str], cwd: Path) -> tuple[int, dict, str]:
    result = subprocess.run(arguments, cwd=cwd, text=True, capture_output=True)
    try:
        return result.returncode, json.loads(result.stdout), result.stderr[-2000:]
    except json.JSONDecodeError:
        return result.returncode, {"commandError": "The dependency tool did not return JSON"}, result.stderr[-2000:]


def dependency_audits(root: Path, skip_network: bool) -> tuple[list[dict], list[dict]]:
    findings: list[dict] = []
    evidence: list[dict] = []
    if skip_network:
        return findings, [{"check": "dependency-audit", "status": "not_run", "reason": "offline"}]
    dotnet_code, dotnet, dotnet_error = run_json(
        ["dotnet", "list", "BusinessAtlas.slnx", "package", "--vulnerable", "--include-transitive", "--format", "json"], root
    )
    dotnet_findings: list[dict] = []
    for project in dotnet.get("projects", []):
        for framework in project.get("frameworks", []):
            for group in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(group, []):
                    for advisory in package.get("vulnerabilities", []):
                        severity = str(advisory.get("severity", "unknown")).lower()
                        finding = {
                            "ecosystem": "NuGet", "package": package.get("id"),
                            "version": package.get("resolvedVersion"), "severity": severity,
                            "advisory": advisory.get("advisoryurl"),
                        }
                        dotnet_findings.append(finding)
                        if severity in {"high", "critical"}:
                            findings.append(finding)
    evidence.append({"check": "nuget-audit", "status": "passed" if dotnet_code == 0 else "failed",
                     "vulnerabilities": dotnet_findings, "diagnostic": dotnet_error if dotnet_code else None})
    if dotnet_code != 0 and not dotnet_findings:
        findings.append({"ecosystem": "NuGet", "kind": "audit-failed", "diagnostic": dotnet_error})

    web = root / "apps/web"
    npm_code, npm, npm_error = run_json(["npm", "audit", "--package-lock-only", "--json"], web)
    vulnerabilities = npm.get("vulnerabilities", {})
    npm_findings = []
    for name, advisory in vulnerabilities.items():
        finding = {"ecosystem": "npm", "package": name, "severity": advisory.get("severity"),
                   "via": [item.get("url") for item in advisory.get("via", []) if isinstance(item, dict)]}
        npm_findings.append(finding)
        if advisory.get("severity") in {"high", "critical"}:
            findings.append(finding)
    evidence.append({"check": "npm-audit", "status": "passed" if npm_code == 0 else "failed",
                     "vulnerabilities": npm_findings, "diagnostic": npm_error if npm_code and not npm_findings else None})
    if npm_code != 0 and not npm_findings:
        findings.append({"ecosystem": "npm", "kind": "audit-failed", "diagnostic": npm_error})
    return findings, evidence


def license_allowed(value: object) -> bool:
    normalized = re.sub(r"[()\s]+", "", str(value).lower())
    if not normalized:
        return False
    if " AND " in str(value).upper():
        return all(license_allowed(part) for part in re.split(r"\s+AND\s+", str(value), flags=re.IGNORECASE))
    if " OR " in str(value).upper():
        return any(license_allowed(part) for part in re.split(r"\s+OR\s+", str(value), flags=re.IGNORECASE))
    return normalized in ALLOWED_LICENSES


def npm_components(root: Path) -> tuple[list[dict], list[dict]]:
    lock = json.loads((root / "apps/web/package-lock.json").read_text(encoding="utf-8"))
    components, license_findings = [], []
    for path, package in lock.get("packages", {}).items():
        if not path or "node_modules/" not in path:
            continue
        name = package.get("name") or path.rsplit("node_modules/", 1)[-1]
        version = package.get("version")
        license_value = package.get("license", "UNKNOWN")
        components.append(component("npm", name, version, license_value))
        if not license_allowed(license_value):
            license_findings.append({"ecosystem": "npm", "package": name, "version": version, "license": license_value})
    return components, license_findings


def nuget_license(root: Path, name: str, version: str) -> str:
    package = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages")) / name.lower() / version
    nuspec = package / f"{name.lower()}.nuspec"
    if not nuspec.is_file():
        candidates = list(package.glob("*.nuspec"))
        if not candidates:
            return "UNKNOWN"
        nuspec = candidates[0]
    tree = ET.parse(nuspec)
    namespace = {"n": "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"}
    expression = tree.find(".//n:license[@type='expression']", namespace)
    if expression is not None and expression.text:
        return expression.text.strip()
    for node in tree.iter():
        if node.tag.endswith("license") and node.attrib.get("type") == "expression" and node.text:
            return node.text.strip()
        if node.tag.endswith("licenseUrl") and node.text:
            url = node.text.lower()
            if "apache" in url:
                return "Apache-2.0"
            if "mit" in url:
                return "MIT"
    return "UNKNOWN"


def nuget_components(root: Path) -> tuple[list[dict], list[dict]]:
    components, license_findings = [], []
    seen: set[tuple[str, str]] = set()
    for lock in sorted(root.glob("**/packages.lock.json")):
        document = json.loads(lock.read_text(encoding="utf-8"))
        for framework in document.get("targets", {}).values():
            for identifier, package in framework.items():
                if package.get("type") != "Direct" and package.get("type") != "Transitive":
                    continue
                name, _, version = identifier.rpartition("/")
                if not name or (name, version) in seen:
                    continue
                seen.add((name, version))
                license_value = nuget_license(root, name, version)
                components.append(component("nuget", name, version, license_value))
                if not license_allowed(license_value):
                    license_findings.append({"ecosystem": "NuGet", "package": name, "version": version, "license": license_value})
    return components, license_findings


def component(ecosystem: str, name: str, version: str | None, license_value: str) -> dict:
    return {"type": "library", "name": name, "version": version or "unknown",
            "purl": f"pkg:{ecosystem}/{name}@{version or 'unknown'}", "properties": [{"name": "license", "value": str(license_value)}]}


def write_sbom(root: Path, output: Path, components: list[dict]) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    serial = "urn:uuid:" + str(uuid.uuid5(uuid.NAMESPACE_URL, str(dt.datetime.now(dt.timezone.utc)) + str(root)))
    document = {"bomFormat": "CycloneDX", "specVersion": "1.5", "serialNumber": serial, "version": 1,
                "metadata": {"timestamp": dt.datetime.now(dt.timezone.utc).isoformat(),
                             "component": {"type": "application", "name": "BusinessAtlas"}},
                "components": sorted(components, key=lambda item: (item["purl"]))}
    output.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/security/t42-supply-chain.json")
    parser.add_argument("--sbom", type=Path, default=ROOT / "artifacts/security/sbom.cdx.json")
    parser.add_argument("--skip-network", action="store_true")
    args = parser.parse_args()
    root = args.root.resolve()
    secret_findings = scan_secrets(root)
    dependency_findings, audit_evidence = dependency_audits(root, args.skip_network)
    npm, npm_licenses = npm_components(root)
    nuget, nuget_licenses = nuget_components(root)
    license_findings = npm_licenses + nuget_licenses
    write_sbom(root, args.sbom.resolve(), npm + nuget)
    incomplete = any(item.get("status") == "not_run" for item in audit_evidence)
    status = "failed" if secret_findings or dependency_findings or license_findings else "incomplete" if incomplete else "passed"
    report = {"format": "business-atlas-t42-security-v1", "generatedAt": dt.datetime.now(dt.timezone.utc).isoformat(),
              "status": status, "secrets": secret_findings, "dependencies": dependency_findings,
              "licenses": license_findings, "audits": audit_evidence,
              "sbom": str(args.sbom.resolve()), "sbomSha256": hashlib.sha256(args.sbom.resolve().read_bytes()).hexdigest()}
    args.output.resolve().parent.mkdir(parents=True, exist_ok=True)
    args.output.resolve().write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"status": status, "report": str(args.output.resolve()),
                      "secretFindings": len(secret_findings), "dependencyFindings": len(dependency_findings),
                      "licenseFindings": len(license_findings), "audits": [item["status"] for item in audit_evidence]}, indent=2))
    return 0 if status == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
