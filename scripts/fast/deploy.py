import json,os,shutil,subprocess,sys,time,urllib.request
from pathlib import Path
source=Path(sys.argv[1]);release=Path.home()/'atlas-deploy/product/releases'/sys.argv[2]
release.parent.mkdir(parents=True,exist_ok=True)
if release.exists():shutil.rmtree(release)
shutil.copytree(source,release)
env={**os.environ,'DOCKER_CONTEXT':'colima-atlas-prod','PATH':'/opt/homebrew/bin:/usr/bin:/bin'}
def docker(*args):subprocess.run(['/opt/homebrew/bin/docker',*args],cwd=release,env=env,check=True)
docker('load','-i',str(release/'runtime-images.tar'))
docker('compose','-p','atlas-product','up','-d','--remove-orphans')
def report_and_restore(error_type,error,trace):
 try:
  docker('compose','-p','atlas-product','logs','--tail','40','api','worker','web')
  marker=release.parent.parent/'current-release.json'
  if marker.exists():
   previous=Path(json.loads(marker.read_text())['directory']).resolve()
   if previous!=release.resolve() and previous.is_relative_to(release.parent.resolve()) and (previous/'compose.yml').is_file():
    subprocess.run(['/opt/homebrew/bin/docker','compose','-p','atlas-product','-f',str(previous/'compose.yml'),'up','-d'],cwd=previous,env=env,check=True)
    print('Deployment failed; restored previous working release',previous.name,flush=True)
 except Exception as recovery_error:print('Deployment recovery error:',type(recovery_error).__name__,str(recovery_error),flush=True)
 sys.__excepthook__(error_type,error,trace)
sys.excepthook=report_and_restore
for attempt in range(60):
 try:
  with urllib.request.urlopen('http://127.0.0.1:15809/health/ready',timeout=3) as r:assert r.status==200
  with urllib.request.urlopen('http://127.0.0.1:15809/',timeout=3) as r:assert r.status==200
  (release.parent.parent/'current-release.json').write_text(json.dumps({'buildId':sys.argv[2],'directory':str(release),'url':'http://192.168.10.10:15809'}))
  print('Product deployed: http://192.168.10.10:15809');break
 except Exception:
  if attempt==59:raise
  time.sleep(2)
