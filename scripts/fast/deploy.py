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
for attempt in range(60):
 try:
  with urllib.request.urlopen('http://127.0.0.1:15809/health/ready',timeout=3) as r:assert r.status==200
  with urllib.request.urlopen('http://127.0.0.1:15809/',timeout=3) as r:assert r.status==200
  (release.parent.parent/'current-release.json').write_text(json.dumps({'buildId':sys.argv[2],'directory':str(release),'url':'http://192.168.10.10:15809'}))
  print('Product deployed: http://192.168.10.10:15809');break
 except Exception:
  if attempt==59:raise
  time.sleep(2)
