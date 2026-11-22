#!/usr/bin/env python3
"""Deploy an immutable Business Atlas release on the ARM64 runtime host."""

import datetime
import ipaddress
import http.cookiejar
import json
import os
import secrets
import shutil
import ssl
import subprocess
import sys
import time
import urllib.request
from pathlib import Path
from urllib.parse import urlsplit

SECRET_NAMES = (
    "ATLAS_DB_PASSWORD",
    "ATLAS_RUNTIME_PASSWORD",
    "ATLAS_WORKER_PASSWORD",
    "KEYCLOAK_DB_PASSWORD",
    "KEYCLOAK_ADMIN_PASSWORD",
    "ATLAS_OIDC_CLIENT_SECRET",
    "ATLAS_USER_PASSWORD",
    "ATLAS_CURSOR_KEY",
)
SERVICES = ("postgres", "identity", "api", "worker", "web")


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def atomic_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.chmod(0o600)
    temporary.replace(path)


def append_event(path, event):
    with path.open("a") as stream:
        stream.write(json.dumps(event) + "\n")
    path.chmod(0o600)


def base_url():
    value = os.environ.get("ATLAS_BASE_URL", "https://192.168.10.10:15809").rstrip("/")
    parsed = urlsplit(value)
    if parsed.scheme != "https" or not parsed.hostname or parsed.path or parsed.query or parsed.fragment:
        raise ValueError("ATLAS_BASE_URL must be an HTTPS origin without a path")
    if (parsed.port or 443) != 15809:
        raise ValueError("The supplied Compose/Caddy runtime requires port 15809")
    return value


def ensure_tls(product, host, directory):
    """Create a private local CA and a SAN certificate for LAN and host access."""
    source = product / "tls"
    source.mkdir(parents=True, exist_ok=True)
    source.chmod(0o700)
    ca_key = source / "atlas-local-ca.key"
    ca_cert = source / "atlas-local-ca.crt"
    openssl = shutil.which("openssl") or "/usr/bin/openssl"
    if not ca_key.exists() or not ca_cert.exists():
        subprocess.run([
            openssl, "req", "-x509", "-newkey", "rsa:3072", "-sha256", "-days", "3650",
            "-nodes", "-subj", "/CN=Business Atlas Local Deployment CA",
            "-keyout", str(ca_key), "-out", str(ca_cert),
        ], check=True, stdout=subprocess.DEVNULL)
        ca_key.chmod(0o600)
    try:
        ipaddress.ip_address(host)
        host_san = "IP.1 = " + host
    except ValueError:
        host_san = "DNS.1 = " + host
    server_key = source / "server.key"
    server_cert = source / "server.crt"
    if not server_key.exists() or not server_cert.exists():
        config = source / "server-openssl.cnf"
        config.write_text(
            "[req]\ndistinguished_name=dn\nreq_extensions=req_ext\nprompt=no\n"
            "[dn]\nCN=Business Atlas\n[req_ext]\nsubjectAltName=@alt_names\n"
            "[alt_names]\n" + host_san + "\nIP.2 = 127.0.0.1\nDNS.2 = localhost\n"
        )
        csr = source / "server.csr"
        ext = source / "server-ext.cnf"
        ext.write_text("extendedKeyUsage=serverAuth\nsubjectAltName=@alt_names\n[alt_names]\n" + host_san + "\nIP.2 = 127.0.0.1\nDNS.2 = localhost\n")
        subprocess.run([openssl, "req", "-new", "-newkey", "rsa:2048", "-nodes", "-subj", "/CN=Business Atlas", "-keyout", str(server_key), "-out", str(csr), "-config", str(config)], check=True, stdout=subprocess.DEVNULL)
        subprocess.run([openssl, "x509", "-req", "-in", str(csr), "-CA", str(ca_cert), "-CAkey", str(ca_key), "-CAcreateserial", "-out", str(server_cert), "-days", "825", "-sha256", "-extfile", str(ext)], check=True, stdout=subprocess.DEVNULL)
        server_key.chmod(0o600)
        csr.unlink(missing_ok=True)
        config.unlink(missing_ok=True)
        ext.unlink(missing_ok=True)
    directory.mkdir(exist_ok=True)
    for name in ("atlas-local-ca.crt", "server.crt", "server.key"):
        shutil.copy2(source / name, directory / name)
    (directory / "server.key").chmod(0o600)
    return ca_cert


def https_get(opener, url):
    with opener.open(url, timeout=4) as response:
        if response.status != 200:
            raise RuntimeError(f"Health endpoint returned HTTP {response.status}: {url}")


def check_health(ca_cert, authority, local_url):
    context = ssl.create_default_context(cafile=str(ca_cert))
    opener = urllib.request.build_opener(
        urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context),
        urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar())
    )
    https_get(opener, local_url + "/health/ready")
    https_get(opener, local_url + "/")
    https_get(opener, local_url + "/auth/realms/atlas/.well-known/openid-configuration")
    # Exercise the real BFF authorization-code + PKCE challenge through discovery.
    https_get(opener, authority + "/auth/login?returnUrl=/")


def main():
    if len(sys.argv) != 3:
        raise SystemExit("Usage: deploy.py <release-artifact-directory> <build-id>")
    source = Path(sys.argv[1]).resolve()
    build_id = sys.argv[2]
    if not build_id or "/" in build_id or build_id in (".", ".."):
        raise ValueError("A safe, non-empty build ID is required")
    product = Path.home() / "atlas-deploy/product"
    release = product / "releases" / build_id
    release.parent.mkdir(parents=True, exist_ok=True)
    product.chmod(0o700)
    history = product / "deployment-history.jsonl"
    marker = product / "current-release.json"
    previous = json.loads(marker.read_text()) if marker.exists() else None
    append_event(history, {"at": utc_now(), "event": "deployment_started", "buildId": build_id, "previousBuildId": previous.get("buildId") if previous else None})
    if release.exists():
        shutil.rmtree(release)
    shutil.copytree(source, release)
    release.chmod(0o700)

    secret_file = product / "secrets.json"
    local_secrets = json.loads(secret_file.read_text()) if secret_file.exists() else {}
    for name in SECRET_NAMES:
        if not local_secrets.get(name):
            local_secrets[name] = secrets.token_urlsafe(36)
    atomic_json(secret_file, local_secrets)

    public_url = base_url()
    host = urlsplit(public_url).hostname
    local_url = "https://127.0.0.1:15809"
    ca_cert = ensure_tls(product, host, release / "tls")
    (release / "db-init").mkdir(exist_ok=True)
    shutil.copy2(release / "init-keycloak-db.sql", release / "db-init/init-keycloak-db.sql")
    (release / "keycloak").mkdir(exist_ok=True)
    realm = (release / "keycloak-realm.json").read_text()
    replacements = {**local_secrets, "ATLAS_BASE_URL": public_url}
    for name, value in replacements.items():
        realm = realm.replace("__" + name + "__", value)
    (release / "keycloak/atlas-realm.json").write_text(realm)
    # Caddy listens on container port 8443; clients use the mapped public origin.
    env_values = {**local_secrets, "ATLAS_AUTHORITY": public_url + "/auth"}
    (release / ".env").write_text("".join(f"{name}={value}\n" for name, value in env_values.items()))
    (release / ".env").chmod(0o600)

    login_file = product / "initial-login.json"
    login = {
        "applicationUrl": public_url,
        "username": "atlas-admin",
        "password": local_secrets["ATLAS_USER_PASSWORD"],
        "keycloakAdminUrl": public_url + "/auth/admin",
        "keycloakUsername": "atlas-keycloak-admin",
        "keycloakPassword": local_secrets["KEYCLOAK_ADMIN_PASSWORD"],
        "caCertificate": str(ca_cert),
        "tlsTrust": "Install the local CA only on authorized Business Atlas clients.",
    }

    docker_path = "/opt/homebrew/bin/docker"
    env = {**os.environ, "DOCKER_CONTEXT": "colima-atlas-prod", "PATH": "/opt/homebrew/bin:/usr/bin:/bin"}

    def docker(*args, capture=False, cwd=release):
        return subprocess.run([docker_path, *args], cwd=cwd, env=env, check=True, text=True, capture_output=capture)

    def compose(*args, capture=False, cwd=release):
        return docker("compose", "-p", "atlas-product", *args, capture=capture, cwd=cwd)

    def sql(statement, database="atlas"):
        subprocess.run([
            docker_path, "compose", "-p", "atlas-product", "exec", "-T", "postgres",
            "psql", "-v", "ON_ERROR_STOP=1", "-U", "atlas", "-d", database,
        ], cwd=release, env=env, input=statement, text=True, check=True)

    def record_failure(error):
        append_event(history, {"at": utc_now(), "event": "deployment_failed", "buildId": build_id, "errorType": type(error).__name__})
        if release.exists():
            subprocess.run([docker_path, "compose", "-p", "atlas-product", "logs", "--tail", "35", "api", "identity", "postgres"], cwd=release, env=env, check=False)
        if previous:
            old = Path(previous["directory"]).resolve()
            if old != release.resolve() and old.is_relative_to(release.parent.resolve()) and (old / "compose.yml").is_file():
                try:
                    compose("up", "-d", cwd=old)
                    previous_url = previous.get("applicationUrl", previous.get("url", public_url))
                    previous_local = local_url if previous_url.startswith("https:") else "http://127.0.0.1:15809"
                    check_health(ca_cert, previous_url, previous_local)
                    append_event(history, {"at": utc_now(), "event": "rollback_succeeded", "buildId": build_id, "restoredBuildId": previous["buildId"]})
                    print("Restored previous working release", previous["buildId"], flush=True)
                except Exception as recovery_error:
                    append_event(history, {"at": utc_now(), "event": "rollback_failed", "buildId": build_id, "restoredBuildId": previous["buildId"], "errorType": type(recovery_error).__name__})
                    print("Automatic rollback failed; see deployment history", flush=True)

    def verify_architecture_and_resources():
        report = {"buildId": build_id, "measuredAt": utc_now(), "hostMachine": os.uname().machine, "logicalCpuCount": os.cpu_count(), "services": {}}
        memory = subprocess.run(["/usr/sbin/sysctl", "-n", "hw.memsize"], text=True, capture_output=True, check=False)
        if memory.returncode == 0:
            report["hostMemoryBytes"] = int(memory.stdout.strip())
        containers = []
        for service in SERVICES:
            container = compose("ps", "-q", service, capture=True).stdout.strip()
            if not container:
                raise RuntimeError(f"No running container for {service}")
            containers.append(container)
            image = docker("inspect", "-f", "{{.Image}}", container, capture=True).stdout.strip()
            architecture = docker("image", "inspect", "-f", "{{.Architecture}}", image, capture=True).stdout.strip()
            if architecture != "arm64":
                raise RuntimeError(f"{service} image is {architecture}, expected arm64")
            report["services"][service] = {"containerId": container, "architecture": architecture}
        stats = docker("stats", "--no-stream", "--format", "{{json .}}", *containers, capture=True)
        usage = {row["ID"]: row for line in stats.stdout.splitlines() if (row := json.loads(line))}
        for service in SERVICES:
            container = report["services"][service]["containerId"]
            measured = next(
                (row for identifier, row in usage.items() if container.startswith(identifier) or identifier.startswith(container)), None
            )
            if not measured:
                raise RuntimeError(f"Docker did not return resource measurements for {service}")
            report["services"][service]["resources"] = measured
        report_path = product / "reports" / f"{build_id}-resources.json"
        report_path.parent.mkdir(exist_ok=True)
        atomic_json(report_path, report)
        return report_path

    try:
        if (release / "runtime-images.tar").exists():
            docker("load", "-i", str(release / "runtime-images.tar"))
        else:
            compose("pull", "postgres", "identity", "api", "worker", "web")
        compose("up", "-d", "postgres")
        for attempt in range(60):
            ready = subprocess.run([docker_path, "compose", "-p", "atlas-product", "exec", "-T", "postgres", "pg_isready", "-U", "atlas", "-d", "atlas"], cwd=release, env=env, capture_output=True)
            if ready.returncode == 0:
                break
            time.sleep(2)
        else:
            raise RuntimeError("PostgreSQL did not become ready")
        sql("""DO $$ BEGIN
 IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='atlas_runtime') THEN CREATE ROLE atlas_runtime LOGIN NOSUPERUSER NOBYPASSRLS; END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='atlas_background') THEN CREATE ROLE atlas_background LOGIN NOSUPERUSER NOBYPASSRLS; END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='keycloak') THEN CREATE ROLE keycloak LOGIN NOSUPERUSER NOBYPASSRLS; END IF;
 END $$;
""" + "ALTER ROLE atlas_runtime PASSWORD '" + local_secrets["ATLAS_RUNTIME_PASSWORD"] + "';" +
            "ALTER ROLE atlas_background PASSWORD '" + local_secrets["ATLAS_WORKER_PASSWORD"] + "';" +
            "ALTER ROLE keycloak PASSWORD '" + local_secrets["KEYCLOAK_DB_PASSWORD"] + "';" +
            "ALTER DATABASE keycloak OWNER TO keycloak;")
        # Existing installations created Keycloak relations as atlas. REASSIGN
        # OWNED is deliberately not used here: it also targets shared objects
        # such as extensions, whose ownership PostgreSQL cannot transfer.
        sql("""DO $$
DECLARE relation RECORD;
BEGIN
 FOR relation IN
  SELECT c.oid, n.nspname, c.relname
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE pg_get_userbyid(c.relowner) = 'atlas'
    AND c.relkind IN ('r', 'p', 'S', 'v', 'm', 'f')
    AND n.nspname NOT LIKE 'pg_%'
    AND n.nspname <> 'information_schema'
    AND NOT EXISTS (
      SELECT 1 FROM pg_depend d
      WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e'
    )
 LOOP
  EXECUTE format('ALTER TABLE %I.%I OWNER TO keycloak', relation.nspname, relation.relname);
 END LOOP;
END $$;""", database="keycloak")
        compose("up", "-d", "identity")
        compose("run", "--rm", "--no-deps", "migrate")
        sql("GRANT atlas_app TO atlas_runtime; GRANT atlas_worker TO atlas_background;")
        compose("up", "-d", "api", "worker", "web")
        for attempt in range(90):
            try:
                # Keycloak does not re-import a realm into an existing database.
                # Reconcile its callback URLs before exercising the BFF login;
                # otherwise a healthy API is failed by stale identity settings.
                subprocess.run([sys.executable, str(release / "configure-identity.py"), str(release)], check=True)
                check_health(ca_cert, public_url, local_url)
                break
            except Exception:
                if attempt == 89:
                    raise
                time.sleep(2)
        resource_report = verify_architecture_and_resources()
        atomic_json(login_file, login)
        atomic_json(marker, {
            "buildId": build_id,
            "directory": str(release),
            "applicationUrl": public_url,
            "deployedAt": utc_now(),
            "loginFile": str(login_file),
            "caCertificate": str(ca_cert),
            "resourceReport": str(resource_report),
            "previousRelease": ({key: previous[key] for key in ("buildId", "directory", "applicationUrl", "deployedAt", "resourceReport") if key in previous} if previous else None),
        })
        append_event(history, {"at": utc_now(), "event": "deployment_succeeded", "buildId": build_id, "resourceReport": str(resource_report)})
        print("Product deployed:", public_url)
        print("ARM64 and resource report:", resource_report)
    except Exception as error:
        record_failure(error)
        raise


if __name__ == "__main__":
    main()
