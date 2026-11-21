import json,os,secrets,shutil,subprocess,sys,time,urllib.request
from pathlib import Path
source=Path(sys.argv[1]);product=Path.home()/'atlas-deploy/product';release=product/'releases'/sys.argv[2]
release.parent.mkdir(parents=True,exist_ok=True)
product.chmod(0o700)
if release.exists():shutil.rmtree(release)
shutil.copytree(source,release)
secret_file=product/'secrets.json'
if secret_file.exists():
 local_secrets=json.loads(secret_file.read_text())
else:
 local_secrets={name:secrets.token_urlsafe(36) for name in ('ATLAS_DB_PASSWORD','ATLAS_OIDC_CLIENT_SECRET','KEYCLOAK_ADMIN_PASSWORD','ATLAS_USER_PASSWORD')}
 secret_file.write_text(json.dumps(local_secrets,indent=2))
 os.chmod(secret_file,0o600)
for name in ('ATLAS_RUNTIME_PASSWORD','ATLAS_WORKER_PASSWORD'):
 local_secrets.setdefault(name,secrets.token_urlsafe(36))
secret_file.write_text(json.dumps(local_secrets,indent=2));secret_file.chmod(0o600)
(release/'db-init').mkdir(exist_ok=True)
shutil.copy2(release/'init-keycloak-db.sql',release/'db-init/init-keycloak-db.sql')
(release/'keycloak').mkdir(exist_ok=True)
realm=(release/'keycloak-realm.json').read_text()
for name,value in local_secrets.items():realm=realm.replace('__'+name+'__',value)
(release/'keycloak/atlas-realm.json').write_text(realm)
(release/'.env').write_text(''.join(f'{name}={value}\n' for name,value in local_secrets.items()))
os.chmod(release/'.env',0o600)
login_file=product/'initial-login.json'
login_file.write_text(json.dumps({'applicationUrl':'http://192.168.10.10:15809','username':'atlas-admin','password':local_secrets['ATLAS_USER_PASSWORD'],'keycloakAdminUrl':'http://192.168.10.10:15809/auth/admin','keycloakUsername':'atlas-keycloak-admin','keycloakPassword':local_secrets['KEYCLOAK_ADMIN_PASSWORD']},indent=2))
os.chmod(login_file,0o600)
env={**os.environ,'DOCKER_CONTEXT':'colima-atlas-prod','PATH':'/opt/homebrew/bin:/usr/bin:/bin'}
def docker(*args):subprocess.run(['/opt/homebrew/bin/docker',*args],cwd=release,env=env,check=True)
def report_and_restore(error_type,error,trace):
 try:
  subprocess.run(['/opt/homebrew/bin/docker','compose','-p','atlas-product','logs','--tail','35','api','identity','postgres'],cwd=release,env=env,check=False)
  marker=product/'current-release.json'
  if marker.exists():
   previous=Path(json.loads(marker.read_text())['directory']).resolve()
   if previous!=release.resolve() and previous.is_relative_to(release.parent.resolve()) and (previous/'compose.yml').is_file():
    subprocess.run(['/opt/homebrew/bin/docker','compose','-p','atlas-product','-f',str(previous/'compose.yml'),'up','-d'],cwd=previous,env=env,check=True)
    print('Restored previous working release',previous.name,flush=True)
 except Exception as recovery_error:print('Recovery error:',type(recovery_error).__name__,flush=True)
 sys.__excepthook__(error_type,error,trace)
sys.excepthook=report_and_restore
if (release/'runtime-images.tar').exists():docker('load','-i',str(release/'runtime-images.tar'))
docker('compose','-p','atlas-product','up','-d','postgres','identity')
for attempt in range(60):
 ready=subprocess.run(['/opt/homebrew/bin/docker','compose','-p','atlas-product','exec','-T','postgres','pg_isready','-U','atlas','-d','atlas'],cwd=release,env=env,capture_output=True)
 if ready.returncode==0:break
 time.sleep(2)
else:raise RuntimeError('PostgreSQL did not become ready')
def sql(statement):
 subprocess.run(['/opt/homebrew/bin/docker','compose','-p','atlas-product','exec','-T','postgres','psql','-v','ON_ERROR_STOP=1','-U','atlas','-d','atlas'],cwd=release,env=env,input=statement,text=True,check=True)
sql("""DO $$ BEGIN
 IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='atlas_runtime') THEN CREATE ROLE atlas_runtime LOGIN NOSUPERUSER NOBYPASSRLS; END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='atlas_background') THEN CREATE ROLE atlas_background LOGIN NOSUPERUSER NOBYPASSRLS; END IF;
 END $$;"""+
 "ALTER ROLE atlas_runtime PASSWORD '"+local_secrets['ATLAS_RUNTIME_PASSWORD']+"';"+
 "ALTER ROLE atlas_background PASSWORD '"+local_secrets['ATLAS_WORKER_PASSWORD']+"';")
docker('compose','-p','atlas-product','run','--rm','--no-deps','migrate')
sql('GRANT atlas_app TO atlas_runtime; GRANT atlas_worker TO atlas_background;')
docker('compose','-p','atlas-product','up','-d','api','worker','web')
for attempt in range(90):
 try:
  with urllib.request.urlopen('http://127.0.0.1:15809/health/ready',timeout=3) as r:assert r.status==200
  with urllib.request.urlopen('http://127.0.0.1:15809/',timeout=3) as r:assert r.status==200
  with urllib.request.urlopen('http://127.0.0.1:15809/auth/realms/atlas/.well-known/openid-configuration',timeout=3) as r:assert r.status==200
  (product/'current-release.json').write_text(json.dumps({'buildId':sys.argv[2],'directory':str(release),'url':'http://192.168.10.10:15809','loginFile':str(login_file)}))
  print('Product deployed: http://192.168.10.10:15809');break
 except Exception:
  if attempt==89:raise
  time.sleep(2)
