#!/usr/bin/env python3
"""Package a real container rehearsal; never label it a product release."""
from pathlib import Path
import argparse, hashlib, json, os, re, subprocess

p = argparse.ArgumentParser()
p.add_argument('--out', required=True)
p.add_argument('--release-id', required=True)
p.add_argument('--image', required=True)
p.add_argument('--health-release-id')
args = p.parse_args()
if not re.fullmatch(r'[A-Za-z0-9_-]{1,60}', args.release_id):
    raise ValueError('Invalid release id')
out = Path(args.out).resolve()
out.mkdir(parents=True, exist_ok=True)
(out / 'web').mkdir(exist_ok=True)
subprocess.run(['docker', 'pull', args.image], check=True)
image_id = subprocess.check_output(['docker', 'image', 'inspect', args.image, '--format', '{{.Id}}'], text=True).strip()
subprocess.run(['docker', 'save', '-o', str(out / 'image.tar'), args.image], check=True)
commit = os.environ.get('BUILD_SOURCEVERSION') or subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
if not re.fullmatch(r'[a-f0-9]{40}', commit):
    raise ValueError('Actual source commit is required')
(out / 'web/health.json').write_text(json.dumps({'kind': 'preflight', 'releaseId': args.health_release_id or args.release_id,
    'sourceCommit': commit, 'message': 'Preparation rehearsal only; Business Atlas product is not implemented'}))
manifest = {'schemaVersion': 1, 'kind': 'preflight', 'releaseId': args.release_id,
            'sourceCommit': commit, 'imageId': image_id,
            'files': {name: hashlib.sha256((out / name).read_bytes()).hexdigest() for name in ['image.tar', 'web/health.json']}}
(out / 'manifest.json').write_text(json.dumps(manifest, indent=2))
print('Prepared container rehearsal', args.release_id, 'source', commit)
