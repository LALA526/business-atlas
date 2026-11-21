"""Durable escalation after bounded attempts; never accepts or completes a task."""
from pathlib import Path
import contextlib,hashlib,json,re,subprocess,time
import atlas_runner as ar

REPAIRABLE={'recovery_planned','qa_blocked','review_blocked','delivery_setup_failed','attempt_exhausted','review_rejected','qa_rejected','verification_failed','timeout','dispatch_conflict','multica_api_failed','runtime_not_ready','model_failed','model_output_invalid','review_invalid','qa_invalid','verdict_invalid','verification_unavailable','candidate_empty','model_error','git_failed','transient_provider','integration_failed'}

def enabled(state):
    ctl=json.loads((Path(state)/'controls.json').read_text())
    return bool(ctl.get('enabled') and not ctl.get('stopRequested') and not (Path(state)/'STOP').exists() and ctl.get('autonomousRecovery',{}).get('enabled'))

def checkpoint(runner,task_id):
    raw=runner.ledger.get_metadata('recovery:'+task_id)
    return json.loads(raw) if raw else {'cycle':0,'nextAt':0}

def store(runner,task_id,data):
    with runner.ledger.connection:runner.ledger.set_metadata('recovery:'+task_id,json.dumps(data))

def attempt_cost(ledger,row):
    record=json.loads(ledger.get_metadata('recovery:'+row['task_id']) or '{}')
    waits=ledger.connection.execute("SELECT COUNT(*) FROM events WHERE task_id=? AND event_type IN ('provider_wait','pause_wait') AND id>?",(row['task_id'],int(record.get('eventBase',0)))).fetchone()[0]
    return max(0,row['attempt']-int(record.get('attemptBase',0))-waits)

def recent_reviews(root,task_id):
    files=sorted((Path(root)/task_id).glob('attempt-*/review-verdict.json'),key=lambda p:int(p.parent.name.split('-')[-1]))[-5:]
    return [{'path':str(file),'verdict':json.loads(file.read_text())} for file in files]

def settle_dispatches(runner,task_id):
    """Refuse overlap: block issue retries, cancel owned runs, wait for brief cleanup."""
    if not enabled(runner.state_dir):return False
    folder=runner.state_dir/'native-dispatch';archive=runner.state_dir/'dispatch-archive';archive.mkdir(exist_ok=True)
    for file in folder.glob('*.json'):
        data=json.loads(file.read_text());match=re.search(r'"taskId"\s*:\s*"([^"]+)"',data.get('prompt',''))
        if not match or match.group(1)!=task_id:continue
        issue=data.get('issue')
        if not issue:raise ar.RunnerError('dispatch_unresolved','Unresolved issue creation retained for exact-title reconciliation')
        runner.adapter.command('issue','status',issue['id'],'blocked','--no-start','--output','json')
        for _ in range(30):
            if not enabled(runner.state_dir):return False
            runs=runner.adapter.command('issue','runs',issue['id'],'--output','json')
            live=[r for r in runs if r['status'] not in ('completed','failed','cancelled')]
            brief=Path(data['cwd'])/'AGENTS.md'
            clean=not brief.exists() or '<!-- BEGIN MULTICA-RUNTIME (auto-managed; do not edit) -->' not in brief.read_text()
            if runs and not live and clean:break
            for run in live:runner.adapter.command('issue','cancel-task',run['id'],'--issue',issue['id'],'--output','json')
            time.sleep(1)
        else:raise ar.RunnerError('dispatch_unresolved','Old native actor or managed brief remains active')
        file.rename(archive/(file.stem+'-'+str(time.time_ns())+'.json'))
    return True

def diagnose(runner,row,record,folder):
    tree=folder/'diagnosis-source'
    if not tree.exists():ar.git(runner.repo,'worktree','add','--detach',str(tree),row['candidate_sha'] or row['base_sha'])
    expected=row['candidate_sha'] or row['base_sha'];ar.assert_candidate_immutable(tree,expected)
    history=recent_reviews(runner.evidence_root,row['task_id'])
    for name in ('qa-verdict.json','verification-result.json'):
        file=Path(row['evidence_dir'])/name
        if file.is_file():history.append({'path':str(file),'verdict':json.loads(file.read_text())})
    old=record.get('plan',{});strategies=['minimal_reproduction','split_into_focused_repairs','replan_callers_and_dataflow']
    strategy=strategies[record['cycle']%len(strategies)]
    prompt=json.dumps({'taskId':'PREP-RECOVERY-'+row['task_id'],'productTask':runner.ledger.task(row),'candidateSha':row['candidate_sha'],'failureCategory':row['failure_category'],'failureDetail':row['failure_detail'],'recentReviews':history,'previousPlan':old,'requiredStrategy':strategy},ensure_ascii=False)
    prompt+='\nYou are the independent recovery planner. Inspect this exact source read-only. Diagnose why prior attempts failed; propose a materially different, executable repair with reproduction and regression checks. Never lower acceptance, fabricate facts, approve code, mutate source/state or dispatch agents. Distinguish code/environment issues from genuinely missing external permission/business facts. Return ONLY JSON: {"decision":"repair|external","rootCause":"...","strategy":"...","steps":["..."],"acceptanceChecks":["..."],"externalNeed":"..."}. A repair needs concrete steps and checks; do not merely ask to retry.'
    result=runner.adapter.run('atlas-leader',prompt,tree,folder,300)
    ar.assert_candidate_immutable(tree,expected)
    if not result.ok:raise ar.RunnerError(result.category,'Recovery diagnosis model unavailable')
    plan=json.loads(result.text)
    if plan.get('decision') not in ('repair','external') or not isinstance(plan.get('rootCause'),str) or not plan['rootCause'].strip():raise ValueError('Invalid diagnosis')
    if plan['decision']=='repair':
        if plan.get('externalNeed'):raise ValueError('Repair cannot bypass an identified external input')
        for name in ('strategy','steps','acceptanceChecks'):
            if not plan.get(name):raise ValueError('Diagnosis lacks '+name)
        if not all(isinstance(v,list) and all(isinstance(x,str) and x.strip() for x in v) for v in (plan['steps'],plan['acceptanceChecks'])):raise ValueError('Invalid repair steps/checks')
    elif not plan.get('externalNeed'):raise ValueError('External blocker must identify missing input')
    ar.write_json(folder/'diagnosis.json',plan)
    return plan

def recover(runner,row,diagnosis=diagnose,clock=time.time):
    if not enabled(runner.state_dir):return 'paused'
    tid=row['task_id'];record=checkpoint(runner,tid);now=int(clock())
    if record.get('nextAt',0)>now:return 'backoff'
    if row['phase']!='blocked' or row['lease_owner'] is not None or row['failure_category'] not in REPAIRABLE:return 'protected'
    if record.get('decision')=='external':return 'external'
    folder=runner.state_dir/'recovery'/tid/('cycle-'+str(record['cycle']+1));folder.mkdir(parents=True,exist_ok=True)
    ar.write_json(folder/'before.json',dict(row))
    # Persist cooldown before calling any model: failures/restarts cannot spin.
    record['nextAt']=now+min(3600,300*(2**min(record['cycle'],4)));store(runner,tid,record)
    if not settle_dispatches(runner,tid):return 'paused'
    if not enabled(runner.state_dir):return 'paused'
    plan=diagnosis(runner,row,record,folder)
    if not enabled(runner.state_dir):return 'paused'
    fresh=runner.ledger.get(tid)
    if fresh['phase']!='blocked' or fresh['fence']!=row['fence'] or fresh['candidate_sha']!=row['candidate_sha'] or fresh['lease_owner'] is not None:raise ar.RunnerError('lease_fenced','Recovery checkpoint changed during diagnosis')
    record.update(cycle=record['cycle']+1,plan=plan,decision=plan['decision'],planPath=str(folder/'diagnosis.json'))
    if plan['decision']=='external':store(runner,tid,record);return 'external'
    # Accepted plans are repair input only. All review/QA/CI remain necessary.
    phase='developer' if runner.ledger.latest_accepted(tid,'leader') else 'leader'
    detail=(row['failure_detail'] or '')+'\nRecovery plan: '+record['planPath']
    record.update(attemptBase=row['attempt'],epochAt=now,nextAt=0)
    # Ledger uses autocommit; explicitly begin so epoch+CAS+event are atomic.
    connection=runner.ledger.connection
    connection.execute('BEGIN IMMEDIATE')
    try:
        changed=connection.execute("UPDATE tasks SET phase=?,failure_category='recovery_planned',failure_detail=?,fence=fence+1,updated_at=? WHERE task_id=? AND phase='blocked' AND fence=? AND candidate_sha IS ? AND lease_owner IS NULL",(phase,detail,now,tid,row['fence'],row['candidate_sha']))
        if changed.rowcount!=1:raise ar.RunnerError('lease_fenced','Recovery CAS lost to another transition')
        record['eventBase']=connection.execute('SELECT COALESCE(MAX(id),0) FROM events WHERE task_id=?',(tid,)).fetchone()[0]
        runner.ledger.set_metadata('recovery:'+tid,json.dumps(record))
        runner.ledger._event(tid,'recovery_planned',row['fence']+1,record['planPath'],now)
        connection.execute('COMMIT')
    except Exception:
        connection.execute('ROLLBACK')
        raise
    return 'repair_planned'

def supervise(runner,start=None,diagnosis=diagnose,clock=time.time):
    result={'at':int(clock()),'tasks':{},'restarted':False}
    if not enabled(runner.state_dir):return {**result,'status':'paused'}
    try:
        with ar.ProcessLock(runner.state_dir/'runner.lock'):
            rows=runner.ledger.rows()
            if rows and all(r['phase']=='done' for r in rows):return {**result,'status':'all_tasks_delivered'}
            # Full preflight includes pinned runtime, available verified model,
            # baseline, protected Azure policy and both real build/deploy agents.
            manifest=runner.preflight()
            if not enabled(runner.state_dir):
                result['status']='paused'
                ar.write_json(runner.state_dir/'supervisor-status.json',result)
                return result
            if not manifest['ready']:
                result['status']='environment_wait'
                # kickstart without -k never interrupts a running service/job.
                result['serviceChecks']={}
                for label in ('local.businessatlas.multica-mac16','local.businessatlas.azure-build-agent','local.businessatlas.build-colima'):
                    if not enabled(runner.state_dir):break
                    check=subprocess.run(['sudo','-n','launchctl','kickstart','system/'+label],capture_output=True,timeout=15)
                    result['serviceChecks'][label]=check.returncode

            else:
                from parallel_executor import prepare_stopped_dispatches
                prepare_stopped_dispatches(runner)
                for row in rows:
                    if not enabled(runner.state_dir):break
                    if row['phase']!='blocked':continue
                    try:result['tasks'][row['task_id']]=recover(runner,row,diagnosis,clock)
                    except Exception as error:result['tasks'][row['task_id']]='diagnosis_wait:'+getattr(error,'category',type(error).__name__)
                fresh=runner.ledger.rows()
                eligible=any(r['phase'] not in ('blocked','done') and runner.ledger._dependencies_done(r) for r in fresh)
                if eligible and enabled(runner.state_dir):
                    (start or (lambda:subprocess.run(['sudo','-n','launchctl','kickstart','system/local.businessatlas.runner'],check=True)))()
                    result['restarted']=True
                result['status']='monitoring'
    except ar.RunnerError as error:
        if error.category!='writer_locked':raise
        result['status']='coordinator_running'
    ar.write_json(runner.state_dir/'supervisor-status.json',result)
    return result
