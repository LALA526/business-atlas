#!/usr/bin/env python3
"""Bootstrap gates are explicit; product gates must exist from T02 onward."""
from pathlib import Path
import hashlib, json, subprocess, sys

root = Path(__file__).resolve().parents[2]
task = sys.argv[1]
if task != 'T01':
    raise RuntimeError(task + ' requires a real executable scripts/ci/verify; preparation tests cannot replace product verification')
subprocess.run([sys.executable, str(root / 'scripts/preflight/verify.py')], check=True)
freeze = root / 'docs/decisions/T01-contract-freeze.json'
if not freeze.is_file():
    raise RuntimeError('T01 must create docs/decisions/T01-contract-freeze.json; see coordination/runner/OPERATING_CONTRACT.txt')
decision = json.loads(freeze.read_text())
dag = root / 'docs/baselines/T1.0/03_开发执行/task-dag.json'
assert decision.get('inputDagSha256') == hashlib.sha256(dag.read_bytes()).hexdigest(), 'Freeze must reference actual input bytes'
assert decision.get('technicalBaseline') == 'T1.0'
assert decision.get('scope', {}).get('textFirst') is True
assert decision.get('scope', {}).get('dedicatedDrawing') is False
assert decision.get('approval', {}).get('kind') == 'delegated-ai', 'Do not fabricate a human signature'
assert isinstance(decision.get('unknowns'), list), 'Preserve unavailable business/production inputs explicitly'
assert (root / 'docs/ui-reference/source-manifest.json').is_file()
print('T01 freeze input and explicit AI delegation record verified; not a product acceptance result')
