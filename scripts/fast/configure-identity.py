from pathlib import Path
import json,sys,urllib.request,urllib.parse
release=Path(sys.argv[1]);product=release.parent.parent
secrets=json.loads((product/'secrets.json').read_text());realm=json.loads((release/'keycloak-realm.json').read_text())
base='http://127.0.0.1:15809/auth';http=urllib.request.build_opener(urllib.request.ProxyHandler({}))
body=urllib.parse.urlencode({'grant_type':'password','client_id':'admin-cli','username':'atlas-keycloak-admin','password':secrets['KEYCLOAK_ADMIN_PASSWORD']}).encode()
with http.open(urllib.request.Request(base+'/realms/master/protocol/openid-connect/token',data=body),timeout=15) as response:token=json.load(response)['access_token']
def call(path,method='GET',body=None):
 req=urllib.request.Request(base+'/admin/realms/'+realm['realm']+path,data=json.dumps(body).encode() if body is not None else None,method=method,headers={'Authorization':'Bearer '+token,'Content-Type':'application/json'})
 with http.open(req,timeout=15) as response:
  raw=response.read();return json.loads(raw) if raw else None
existing={x['name']:x for x in call('/client-scopes')}
for scope in realm['clientScopes']:
 if scope['name'] not in existing:call('/client-scopes','POST',scope)
existing={x['name']:x for x in call('/client-scopes')}
client=call('/clients?clientId=atlas-web')[0]
for name in realm['clients'][0]['defaultClientScopes']:
 call('/clients/'+client['id']+'/default-client-scopes/'+existing[name]['id'],'PUT')
print('Existing identity service configuration synchronized')
