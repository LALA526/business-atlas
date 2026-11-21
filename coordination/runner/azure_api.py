"""Azure DevOps access for the deterministic coordinator. No credentials in Git."""
import base64, json, time, urllib.request, urllib.parse
from pathlib import Path

ORG = '2717243207'
PROJECT = 'Business Atlas'

def request(path, method='GET', payload=None, content_type='application/json', as_text=False):
    if not path.startswith('/') or '://' in path:
        raise ValueError('Only relative paths in the configured Azure organization are accepted')
    if method == 'GET':
        path += ('&' if '?' in path else '?') + 'atlasFresh=' + str(time.time_ns())
    token = (Path.home() / '.config/atlas/azure-pat').read_text().strip()
    body = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request('https://dev.azure.com/' + ORG + path, data=body, method=method,
        headers={'Authorization': 'Basic ' + base64.b64encode((':' + token).encode()).decode(),
                 'Content-Type': content_type, 'Cache-Control': 'no-cache'})
    with urllib.request.urlopen(req, timeout=45) as result:
        raw = result.read()
        return raw.decode("utf-8", errors="replace") if as_text else (json.loads(raw) if raw else None)

def project_path(suffix):
    return '/' + urllib.parse.quote(PROJECT, safe='') + suffix

if __name__ == '__main__':
    data = request(project_path('/_apis/git/repositories?api-version=7.1'))
    print(json.dumps([{'id': r['id'], 'name': r['name'], 'size': r.get('size'),
                       'defaultBranch': r.get('defaultBranch')} for r in data['value']], indent=2))
