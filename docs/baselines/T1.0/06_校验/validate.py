#!/usr/bin/env python3
"""Static packaging check only; run with Python, PyYAML and jsonschema. Not a product test."""
from pathlib import Path
import csv, json, re, zipfile, hashlib
import yaml, jsonschema
root=Path(__file__).resolve().parents[1]; mc=root/'02_机器合同'; sd=mc/'schemas'; ed=root/'03_开发执行'; base=root/'05_原始基线/P1.0/BusinessAtlas_Product_Design_Baseline'
load=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
readcsv=lambda p:list(csv.DictReader(p.open(encoding='utf-8-sig',newline='')))
def findrefs(n):
 if isinstance(n,dict):
  if '$ref' in n: yield n['$ref']
  for v in n.values(): yield from findrefs(v)
 elif isinstance(n,list):
  for v in n:yield from findrefs(v)
def verifyrefs(doc,p):
 for ref in findrefs(doc):
  if ref.startswith('#/'):
   x=doc
   for k in ref[2:].split('/'):x=x[k.replace('~1','/').replace('~0','~')]
  elif ref.startswith('./'):assert (p.parent/ref.split('#')[0]).is_file(),ref
  elif ref!='#': raise AssertionError(f'Unexpected external ref: {ref}')
schemas=list(sd.glob('*.schema.json'))
for p in schemas:
 s=load(p); jsonschema.validators.validator_for(s).check_schema(s);verifyrefs(s,p)
examples=list((mc/'examples').glob('*.json'))
mapx={'revision-v1':'domain-v1','rule-v1':'rule-v1','task-context-v1':'task-context-v1','impact-v1':'impact-v1','agent-result-v1':'agent-result-v1'}
for p in examples:
 k=p.name.removesuffix('.example.json'); n=mapx.get(k,k if k.endswith('v1.3') else k+'-v1.2');s=load(sd/(n+'.schema.json')); c=jsonschema.validators.validator_for(s);c(s,format_checker=c.FORMAT_CHECKER).validate(load(p))
api=yaml.safe_load((mc/'openapi.yaml').read_text());verifyrefs(api,mc/'openapi.yaml');ops={}
for path,methods in api['paths'].items():
 for method,op in methods.items():
  oid=op['operationId']; assert oid not in ops;ops[oid]=op
  params=op['parameters']; pp={x['name'] for x in params if x['in']=='path' and x['required']};assert set(re.findall(r'{(\w+)}',path))==pp,(oid,pp)
  h={x['name'] for x in params if x['in']=='header' and x['required']};assert not op['x-mutates-state'] or 'Idempotency-Key' in h,oid; assert not op['x-cas'] or 'If-Match' in h,oid
 assert methods
cat=readcsv(mc/'api-catalog.csv');assert set(ops)=={r['operationId'] for r in cat};assert len(cat)==len(ops)
assert {r['cognitionService'] for r in cat if r['cognitionService']}=={f'CS-{i:02}' for i in range(1,16)}
reg=load(mc/'registry.json'); domain=load(sd/'domain-v1.schema.json')['$defs']; assert set(api['components']['schemas'])==set(domain)
for t in reg['types']:assert t['strictBodySchema'] in domain,t
mcp=load(mc/'mcp-tools.json')['tools'];assert len({t['name'] for t in mcp})==len(mcp)
for t in mcp:
 o=ops[t['operationId']];assert t['requiredScope']==o['x-required-scope'];assert t['operationId'] not in ['reviewChangeSet','publishChangeSet'];assert t['readOnly']==(not o['x-mutates-state']);
 for k in ['inputSchema','outputSchema']:
  s=t[k];jsonschema.Draft202012Validator.check_schema(s);verifyrefs(s,mc/'mcp-tools.json')
d=load(ed/'task-dag.json');jsonschema.Draft202012Validator(load(sd/'task-dag-v1.schema.json')).validate(d); tasks={t['taskId']:t for t in d['tasks']};assert len(tasks)==len(d['tasks']); done=set()
while len(done)<len(tasks):
 ready={k for k,v in tasks.items() if k not in done and set(v['dependsOn'])<=done}; assert ready,'cycle or absent task';done|=ready
for t in tasks.values():
 assert all((root/p).is_file() for p in t['contractRefs']);assert set(t['operationIds'])<=set(ops);assert t['status']=='planned' and t['actualEvidence'] is None
rows=readcsv(ed/'requirements-trace.csv');ids={r['requirement_id'] for r in rows};assert len(ids)==len(rows)==154
legacy=readcsv(next(base.rglob('*116*.csv')));oldids={r['原需求ID'] for r in legacy};assert oldids=={r['requirement_id'] for r in rows if r['source']=='V1.2'};assert ids-oldids=={f'PD-{i:02}' for i in range(1,39)}
for r in rows:
 tset=set(r['implementation_tasks'].split(';')); assert tset and tset<=set(tasks);assert r['acceptance'] and r['planned_tests'];assert all(r['requirement_id'] in tasks[t]['requirementIds'] for t in tset);assert not any(r[k] for k in ['actual_pr','actual_commit','actual_test','actual_knowledge','actual_deployment'])
for t in tasks.values():assert set(t['requirementIds'])=={r['requirement_id'] for r in rows if t['taskId'] in r['implementation_tasks'].split(';')}
imported=readcsv(ed/'azure-board-import.csv');assert {i['ExternalTaskKey'] for i in imported}==set(tasks)
ownership=readcsv(ed/'operation-ownership.csv'); assert len(ownership)==len(ops) and {r['operationId'] for r in ownership}==set(ops)
for item in ownership:
 t=tasks[item['implementationTask']]; assert item['ownerModule'] in t['modules']; assert item['ownerModule']==ops[item['operationId']]['x-owner']; assert set(filter(None,item['contractConsumers'].split(';')))<=set(tasks)
sql=(mc/'database.sql').read_text();tables=set(re.findall(r'CREATE TABLE ([\w.]+) \(',sql));fks=set(re.findall(r'REFERENCES ([\w.]+)\(',sql));assert not fks-tables,fks-tables
projecttables={k for k in tables if k not in ['identity.principal']}; rls=set(re.findall(r'ALTER TABLE ([\w.]+) ENABLE ROW LEVEL SECURITY;',sql));assert projecttables<=rls,projecttables-rls
# exact original zip files and expanded P1 content
for name in ['BusinessAtlas_Product_Design_P1_0.zip','BusinessAtlas_V1_2_Engineering_Full_Delivery.zip']:
 z=root/'05_原始基线'/name
 with zipfile.ZipFile(z) as f:assert f.testzip() is None
 original=root.parent/name
 if original.is_file():assert hashlib.sha256(z.read_bytes()).digest()==hashlib.sha256(original.read_bytes()).digest()
with zipfile.ZipFile(root/'05_原始基线/BusinessAtlas_Product_Design_P1_0.zip') as z:
 for n in z.namelist():
  if n.endswith('/'):continue
  assert (root/'05_原始基线/P1.0'/n).read_bytes()==z.read(n),n
print(json.dumps({'schemas':len(schemas),'examples':len(examples),'openapi_operations':len(ops),'unique_operation_owners':len(ownership),'paths':len(api['paths']),'mcp_tools':len(mcp),'types':len(reg['types']),'predicates':len(reg['predicates']),'sql_tables':len(tables),'sql_rls_tables':len(rls),'tasks':len(tasks),'trace_rows':len(rows),'legacy_rows':len(oldids),'new_rows':len(ids-oldids),'status':'static_checks_passed'},ensure_ascii=False,indent=2))
