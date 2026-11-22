from pathlib import Path
import json
import ssl
import sys
import urllib.parse
import urllib.request

release = Path(sys.argv[1])
product = release.parent.parent
secrets = json.loads((product / "secrets.json").read_text())
realm = json.loads((release / "keycloak-realm.json").read_text())
# Use loopback while retaining the certificate trust and public issuer.
base = "https://127.0.0.1:15809/auth"
context = ssl.create_default_context(cafile=str(product / "tls/atlas-local-ca.crt"))
http = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context))
body = urllib.parse.urlencode({
    "grant_type": "password",
    "client_id": "admin-cli",
    "username": "atlas-keycloak-admin",
    "password": secrets["KEYCLOAK_ADMIN_PASSWORD"],
}).encode()
with http.open(urllib.request.Request(base + "/realms/master/protocol/openid-connect/token", data=body), timeout=15) as response:
    token = json.load(response)["access_token"]


def call(path, method="GET", body=None):
    request = urllib.request.Request(
        base + "/admin/realms/" + realm["realm"] + path,
        data=json.dumps(body).encode() if body is not None else None,
        method=method,
        headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"},
    )
    with http.open(request, timeout=15) as response:
        raw = response.read()
        return json.loads(raw) if raw else None


call("", "PUT", {"realm": realm["realm"], "enabled": True, "sslRequired": "external"})
existing = {item["name"]: item for item in call("/client-scopes")}
for scope in realm["clientScopes"]:
    if scope["name"] not in existing:
        call("/client-scopes", "POST", scope)
existing = {item["name"]: item for item in call("/client-scopes")}
clients = call("/clients?clientId=atlas-web")
if not clients:
    raise RuntimeError("The atlas-web OIDC client was not imported")
client = clients[0]
desired_client = realm["clients"][0]
client.update({
    "enabled": True,
    "standardFlowEnabled": True,
    "directAccessGrantsEnabled": False,
    "redirectUris": desired_client["redirectUris"],
    "webOrigins": desired_client["webOrigins"],
    "secret": secrets["ATLAS_OIDC_CLIENT_SECRET"],
})
call("/clients/" + client["id"], "PUT", client)
for name in desired_client["defaultClientScopes"]:
    call("/clients/" + client["id"] + "/default-client-scopes/" + existing[name]["id"], "PUT")
print("Existing identity service configuration synchronized")
