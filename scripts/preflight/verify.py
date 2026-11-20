#!/usr/bin/env python3
"""Clean-checkout validation of preparation inputs; not product acceptance."""
from pathlib import Path
import hashlib, json, subprocess, sys, tarfile, tempfile

root = Path(__file__).resolve().parents[2]
baseline = root / 'docs/baselines/T1.0'
subprocess.run([sys.executable, str(baseline / '06_校验/validate.py')], check=True)
config = json.loads((root / 'opencode.json').read_text())
assert len(config['agent']) == 4
assert all(a['model'].startswith('kimi-code-plan-cn/') for a in config['agent'].values())
manifest = json.loads((root / 'docs/ui-reference/source-manifest.json').read_text())
for name, digest in manifest['files'].items():
    assert hashlib.sha256((root / 'docs/ui-reference' / name).read_bytes()).hexdigest() == digest
for name in ['.env', 'auth.json', 'azure-pat']:
    tracked = subprocess.check_output(['git', '-C', str(root), 'ls-files'], text=True).splitlines()
    assert not any(Path(p).name == name for p in tracked), 'Credential file must not be committed'
with tempfile.TemporaryDirectory(prefix='atlas-ui-') as tmp:
    with tarfile.open(root / 'docs/ui-reference/working-v9.4.tar.gz') as archive:
        archive.extractall(tmp, filter='data')
    subprocess.run(['node', 'build.js'], cwd=tmp, check=True)
    subprocess.run(['node', 'test.js'], cwd=tmp, check=True)
runner_tests = root / 'coordination/runner/tests'
if not runner_tests.is_dir():
    raise RuntimeError('Autonomous runner verification is not yet implemented')
subprocess.run([sys.executable, '-m', 'unittest', 'discover', '-s', str(runner_tests), '-v'], cwd=root, check=True)
print('Preparation inputs verified. Product T01–T44 acceptance remains separate.')
