#!/opt/homebrew/bin/python3.13
"""Persistent host controller. Installing it does not start product development."""
from pathlib import Path
import argparse, contextlib, hashlib, json, os, signal, subprocess, sys, time

REPO = Path.home() / 'Workspace/projects/business-atlas'
PREP = REPO.parent / 'business-atlas-preflight'
STATE = PREP / 'runner-state'
CONTROLS = STATE / 'controls.json'
sys.path.insert(0, str(REPO / 'coordination/runner'))
import atlas_runner as ar

def save(path, data):
    ar.write_json(path, data)
    path.chmod(0o600)

def prepare():
    STATE.mkdir(parents=True, exist_ok=True)
    STATE.chmod(0o700)
    pipeline = json.loads((PREP / 'evidence/azure-pipeline.json').read_text())
    proof = PREP / 'evidence/kimi-k31-smoke.jsonl'
    assert 'ATLAS_KIMI_K31_OK' in ar.parse_opencode_jsonl(proof.read_text())
    if not CONTROLS.exists():
        save(CONTROLS, {'enabled': False, 'stopRequested': False, 'maxConcurrentTasks': 1,
            'maxRetries': 2, 'maxTaskAttempts': 3, 'timeoutSeconds': 2700, 'leaseSeconds': 3600,
            'model': {'id': 'kimi-code-plan-cn/k3.1-pro', 'verified': True,
                'evidenceFile': str(proof), 'evidenceSha256': hashlib.sha256(proof.read_bytes()).hexdigest()},
            'integration': {'backend': 'azure_gates', 'organization': '2717243207', 'project': 'Business Atlas',
                'projectId': 'dd3e7018-2931-4702-8812-ef2a69a00103', 'repositoryId': pipeline['repositoryId'],
                'pipelineId': pipeline['pipelineId'], 'buildPoolId': pipeline['buildPoolId'],
                'targetBranch': 'main', 'deployStage': 'DeployMac8', 'timeoutSeconds': 1800, 'pollSeconds': 15}})
    runner = ar.build_real_runner(REPO, STATE, CONTROLS)
    try:
        result = runner.ledger.initialize(REPO / ar.DEFAULT_DAG, REPO / ar.DEFAULT_BASELINE, int(time.time()))
    finally:
        runner.close()
    print(json.dumps({'prepared': True, 'taskCount': result['tasks'], 'enabled': json.loads(CONTROLS.read_text())['enabled']}))

def snapshot():
    runner = ar.build_real_runner(REPO, STATE, CONTROLS)
    try:
        manifest = runner.preflight()
        tasks = runner.status()['tasks']
        counts = {}
        for t in tasks:
            key = t['state']
            counts[key] = counts.get(key, 0) + 1
        result = {'ready': manifest['ready'], 'enabled': runner.controls['enabled'],
                  'counts': counts, 'preflight': manifest, 'productAcceptance': False}
        save(STATE / 'status.json', result)
        return result
    finally:
        runner.close()

def serve():
    control = json.loads(CONTROLS.read_text())
    if not control.get('enabled'):
        print('Continuous product development is disabled')
        return 0
    runner = ar.build_real_runner(REPO, STATE, CONTROLS)
    for signum in (signal.SIGTERM, signal.SIGINT):
        signal.signal(signum, runner.request_stop)
    try:
        with ar.ProcessLock(STATE / 'runner.lock'):
            result = runner.loop()
        save(STATE / 'last-run-result.json', result)
        print(json.dumps(result), flush=True)
        return 0 if result['status'] in ('idle', 'stopped', 'disabled') else 2
    finally:
        runner.close()

def main():
    p = argparse.ArgumentParser()
    p.add_argument('command', choices=['prepare', 'status', 'preflight', 'start', 'stop', 'serve'])
    args = p.parse_args()
    if args.command == 'prepare':
        prepare()
        return 0
    if not CONTROLS.is_file():
        raise RuntimeError('Run atlasctl prepare first')
    if args.command in ('status', 'preflight'):
        value = snapshot()
        print(json.dumps(value, ensure_ascii=False, indent=2))
        return 0 if value['ready'] else 2
    if args.command == 'start':
        value = snapshot()
        if not value['ready']:
            raise RuntimeError('Actual startup gates are not ready')
        controls = json.loads(CONTROLS.read_text())
        controls.update(enabled=True, stopRequested=False)
        save(CONTROLS, controls)
        with contextlib.suppress(FileNotFoundError):
            (STATE / 'STOP').unlink()
        subprocess.run(['sudo', '-n', 'launchctl', 'kickstart', 'system/local.businessatlas.runner'], check=True)
        print('Started supervised autonomous development; actual progress is in the persistent ledger')
        return 0
    if args.command == 'stop':
        controls = json.loads(CONTROLS.read_text())
        controls.update(enabled=False, stopRequested=True)
        save(CONTROLS, controls)
        (STATE / 'STOP').touch()
        subprocess.run(['sudo', '-n', 'launchctl', 'kill', 'SIGTERM', 'system/local.businessatlas.runner'], check=False, capture_output=True)
        print('Stop requested; coordinator checkpoints and terminates only its own child groups')
        return 0
    return serve()

if __name__ == '__main__':
    sys.exit(main())
