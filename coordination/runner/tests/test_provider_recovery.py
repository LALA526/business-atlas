import copy,json,sys,tempfile,time,unittest
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import atlas_runner as ar
from provider_recovery import ProviderHealth,ProviderRouter,wake,normalize_legacy_quota_waits
from test_atlas_runner import RunnerFixture,FakeAdapter

class Pool(FakeAdapter):
    def command(self,*a):return {}
    def preflight(self):return {'ready':True}
    def sync_status(self,*a):pass

def config(standby=False):
    return {'enabled':True,'retrySeconds':1800,'providers':{'kimi':{'model':'kimi-code-plan-cn/k3.1-pro','verified':True,'bindingPath':'unused'},'routin':{'model':'routinai/gpt-6.2-sol','verified':standby,'bindingPath':'unused'}}}

class HealthAndRoutingTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup);self.root=Path(self.tmp.name)
    def router(self,primary,standby,ready=True):
        return ProviderRouter({},ar.ProcessSupervisor(),config(ready),self.root,clock=lambda:100,adapters={'kimi':primary,'routin':standby})
    def test_quota_switches_same_stage_worktree_and_prompt_to_verified_standby(self):
        primary=Pool(lambda *a:ar.RoleResult(False,category='blocked_quota'));standby=Pool()
        r=self.router(primary,standby)
        result=r.run('atlas-leader','accepted context',self.root,self.root/'logs',30)
        self.assertTrue(result.ok)
        self.assertEqual(primary.calls[0]['cwd'],standby.calls[0]['cwd'])
        self.assertEqual(primary.calls[0]['prompt'],standby.calls[0]['prompt'])
        self.assertEqual(r.health.available(),['routin'])
    def test_unverified_standby_is_never_called(self):
        standby=Pool();r=self.router(Pool(lambda *a:ar.RoleResult(False,category='blocked_quota')),standby,False)
        self.assertEqual(r.run('atlas-leader','plan',self.root,self.root/'logs',30).category,'provider_wait')
        self.assertEqual(standby.calls,[])
    def test_nonquota_failure_does_not_switch_or_mask_problem(self):
        for category in ('verification_failed','source_mutated','blocked_credential'):
            standby=Pool();r=self.router(Pool(lambda *a:ar.RoleResult(False,category=category)),standby)
            self.assertEqual(r.run('atlas-leader','plan',self.root,self.root/'logs',30).category,category)
            self.assertEqual(standby.calls,[])
    def test_wait_state_survives_restart_instead_of_reenabling_from_defaults(self):
        h=ProviderHealth(self.root/'provider-health.json',config(True)['providers'],lambda:100)
        h.quota('kimi');h.quota('routin')
        restored=ProviderHealth(self.root/'provider-health.json',config(True)['providers'],lambda:101)
        self.assertEqual(restored.available(),[])
        self.assertEqual(restored.read()['providers']['routin']['nextProbeAt'],1900)
    def test_both_pools_exhausted_return_wait_without_claiming_success(self):
        r=self.router(Pool(lambda *a:ar.RoleResult(False,category='blocked_quota')),Pool(lambda *a:ar.RoleResult(False,category='blocked_quota')))
        self.assertEqual(r.run('atlas-leader','plan',self.root,self.root/'logs',30).category,'provider_wait')
        self.assertEqual(r.health.available(),[])
    def test_model_change_requires_new_verification(self):
        c=config(True);h=ProviderHealth(self.root/'health.json',c['providers']);self.assertIn('routin',h.available())
        c['providers']['routin']['model']='routinai/different-model'
        replaced=ProviderHealth(self.root/'health.json',c['providers'])
        self.assertNotIn('routin',replaced.available())
    def test_pending_native_retry_must_be_retired_even_after_restart(self):
        class NativeQuotaPool(Pool):
            settled=False
            def settle_quota_run(self,result):return self.settled
        primary=NativeQuotaPool(lambda *a:ar.RoleResult(False,category='blocked_quota',log_paths=('receipt',)))
        standby=Pool();r=self.router(primary,standby)
        self.assertEqual(r.run('atlas-leader','plan',self.root,self.root/'logs',30).category,'provider_wait')
        self.assertEqual(standby.calls,[])
        restored=self.router(primary,standby)
        self.assertEqual(restored.run('atlas-leader','plan',self.root,self.root/'logs2',30).category,'provider_wait')
        self.assertEqual(standby.calls,[])
        primary.settled=True
        self.assertTrue(restored.run('atlas-leader','plan',self.root,self.root/'logs3',30).ok)
        self.assertEqual(restored.health.read()['retirements'],[])
    def test_native_queued_retry_is_cancelled_before_handoff(self):
        from multica_adapter import MulticaAdapter
        receipt=self.root/'receipt.json';receipt.write_text(json.dumps({'issueId':'own-issue','cwd':str(self.root),'run':{'status':'failed'}}))
        calls=[];runs=[{'id':'retry-run','status':'queued'}]
        def command(*args):
            calls.append(args)
            if args[:2]==('issue','runs'):return runs
            if args[:2]==('issue','cancel-task'):runs[0]['status']='cancelled'
            return {}
        adapter=MulticaAdapter({},ar.ProcessSupervisor(),command=command,sleep=lambda _:None)
        self.assertTrue(adapter.settle_quota_run(ar.RoleResult(False,category='blocked_quota',log_paths=(str(receipt),))))
        self.assertTrue(any(c[:2]==('issue','cancel-task') for c in calls))
        self.assertIn('blocked',calls[0]);self.assertIn('--no-start',calls[0])
    def test_handoff_refuses_missing_native_failure_proof(self):
        from multica_adapter import MulticaAdapter
        a=MulticaAdapter({},ar.ProcessSupervisor(),command=lambda *a:{})
        self.assertFalse(a.settle_quota_run(ar.RoleResult(False,category='blocked_quota')))
    def test_every_unsettled_quota_snapshot_contains_durable_retirement(self):
        class NativeQuotaPool(Pool):
            def settle_quota_run(self,result):return False
        real_write=ar.write_json;snapshots=[]
        def observe(path,value):
            if Path(path).name=='provider-health.json':snapshots.append(copy.deepcopy(value))
            return real_write(path,value)
        with patch('provider_recovery.write_json',side_effect=observe):
            r=self.router(NativeQuotaPool(lambda *a:ar.RoleResult(False,category='blocked_quota',log_paths=('receipt',))),Pool())
            r.run('atlas-leader','plan',self.root,self.root/'logs',30)
        waiting=[s for s in snapshots if s['providers']['kimi']['status']=='quota_wait']
        self.assertTrue(waiting)
        self.assertTrue(all(s['retirements']==[{'provider':'kimi','logs':['receipt']}] for s in waiting))
    def test_probe_env_override_isolated_from_native_task_and_still_strips_pat(self):
        out=self.root/'out';err=self.root/'err'
        command=[sys.executable,'-c','import os,json;print(json.dumps({k:os.environ.get(k) for k in ("MULTICA_TOKEN","OPENCODE_CONFIG_CONTENT","AZURE_DEVOPS_PAT","PWD")}))']
        with patch.dict('os.environ',{'MULTICA_TOKEN':'outer-task-token'}):
            self.assertEqual(ar.ProcessSupervisor().execute(command,self.root,10,out,err,environment={'PATH':'/usr/bin:/bin','OPENCODE_CONFIG_CONTENT':'isolated-probe','AZURE_DEVOPS_PAT':'must-be-stripped'}),0)
        data=json.loads(out.read_text())
        self.assertIsNone(data['MULTICA_TOKEN']);self.assertIsNone(data['AZURE_DEVOPS_PAT'])
        self.assertEqual(data['OPENCODE_CONFIG_CONTENT'],'isolated-probe');self.assertEqual(data['PWD'],str(self.root.resolve()))

class CheckpointAndWakeTests(RunnerFixture):
    def make_wait(self,phase='atlas-reviewer'):
        normal=FakeAdapter()
        def handler(role,prompt,cwd,count):
            if role==phase:return ar.RoleResult(False,category='provider_wait')
            return normal.run(role,prompt,cwd,self.state,30)
        r=self.make_runner(adapter=FakeAdapter(handler),maxTaskAttempts=3)
        result=r.run_once();self.assertEqual(result['category'],'provider_wait')
        self.private={'enabled':True,'stopRequested':False,'providerRecovery':config(False)}
        (self.state/'controls.json').write_text(json.dumps(self.private))
        self.health=ProviderHealth(self.state/'provider-health.json',self.private['providerRecovery']['providers'],lambda:100)
        self.health.quota('kimi');self.health.quota('routin',60)
        return r
    def test_quota_wait_does_not_exhaust_product_repair_budget(self):
        r=self.make_runner(maxTaskAttempts=3)
        for i in range(7):
            row=r.ledger.claim('owner',100+i,100,1,self.base_sha,self.state/'evidence',lambda _:None,max_attempts=3)
            self.assertIsNotNone(row)
            r.ledger.defer_provider('T01','owner',row['fence'],'quota',101+i)
        r.adapter=FakeAdapter()
        self.assertEqual(r.run_once()['status'],'done')
        self.assertEqual(r.ledger.get('T02')['attempt'],0)
    def test_resume_keeps_candidate_and_accepted_plan_without_reimplementing(self):
        r=self.make_wait();candidate=r.ledger.get('T01')['candidate_sha']
        self.assertEqual(r.status()['tasks'][0]['state'],'waiting_provider')
        r.adapter=FakeAdapter();self.assertEqual(r.run_once()['status'],'done')
        self.assertEqual(r.ledger.get('T01')['candidate_sha'],candidate)
        self.assertEqual([c['role'] for c in r.adapter.calls],['atlas-reviewer','atlas-qa'])
    def test_partial_builder_changes_survive_wait_and_resume(self):
        normal=FakeAdapter()
        def first(role,prompt,cwd,count):
            if role=='atlas-builder':
                (cwd/'partial.txt').write_text('preserve me')
                return ar.RoleResult(False,category='provider_wait')
            return normal.run(role,prompt,cwd,self.state,30)
        r=self.make_runner(adapter=FakeAdapter(first))
        self.assertEqual(r.run_once()['category'],'provider_wait')
        def resumed(role,prompt,cwd,count):
            if role=='atlas-builder':self.assertEqual((cwd/'partial.txt').read_text(),'preserve me')
            return normal.run(role,prompt,cwd,self.state,30)
        r.adapter=FakeAdapter(resumed)
        self.assertEqual(r.run_once()['status'],'done')
    def test_due_probe_and_verified_backup_wake_checkpoint_only(self):
        r=self.make_wait();starts=[]
        result=wake(r,self.state,self.repo,probe=lambda *a:{'verified':True,'category':'available'},clock=lambda:2000,start=lambda:starts.append(True))
        self.assertTrue(result['resumed']);self.assertEqual(starts,[True])
        self.assertEqual(r.ledger.get('T01')['phase'],'review')
        self.assertEqual(r.ledger.get('T02')['attempt'],0)
    def test_future_quota_reset_does_not_probe_or_restart(self):
        r=self.make_wait();starts=[]
        result=wake(r,self.state,self.repo,probe=lambda *a:self.fail('probe too early'),clock=lambda:110,start=lambda:starts.append(True))
        self.assertFalse(result['resumed']);self.assertEqual(starts,[])
    def test_explicit_user_stop_is_never_overridden(self):
        r=self.make_wait();self.private['enabled']=False
        (self.state/'controls.json').write_text(json.dumps(self.private))
        result=wake(r,self.state,self.repo,probe=lambda *a:self.fail('must remain stopped'),clock=lambda:2000,start=lambda:self.fail('must remain stopped'))
        self.assertEqual(result['status'],'paused')
    def test_stop_file_is_never_overridden(self):
        r=self.make_wait();(self.state/'STOP').touch()
        self.assertEqual(wake(r,self.state,self.repo,probe=lambda *a:self.fail('must remain stopped'))['status'],'paused')
    def test_existing_coordinator_lock_prevents_second_writer_start(self):
        r=self.make_wait()
        with ar.ProcessLock(self.state/'runner.lock'):
            result=wake(r,self.state,self.repo,probe=lambda *a:{'verified':True,'category':'available'},clock=lambda:2000,start=lambda:self.fail('duplicate writer'))
        self.assertFalse(result['resumed'])
    def test_source_failure_is_not_auto_unblocked(self):
        r=self.make_wait()
        with r.ledger.connection:r.ledger.connection.execute("UPDATE tasks SET phase='blocked',failure_category='source_mutated' WHERE task_id='T01'")
        result=wake(r,self.state,self.repo,probe=lambda *a:{'verified':True,'category':'available'},clock=lambda:2000,start=lambda:self.fail('do not resume source blocker'))
        self.assertFalse(result['resumed']);self.assertEqual(r.ledger.get('T01')['phase'],'blocked')
    def test_legacy_quota_upgrade_preserves_last_accepted_candidate(self):
        r=self.make_wait();before=r.ledger.get('T01')['candidate_sha']
        with r.ledger.connection:r.ledger.connection.execute("UPDATE tasks SET phase='blocked',failure_category='blocked_quota' WHERE task_id='T01'")
        normalize_legacy_quota_waits(r,self.health)
        row=r.ledger.get('T01');self.assertEqual(row['phase'],'review');self.assertEqual(row['candidate_sha'],before)
        self.assertIsNotNone(r.ledger.latest_accepted('T01','developer',before))
        self.assertEqual(self.health.read()['retirements'],[{'provider':'kimi','logs':[]}])
    def test_legacy_native_quota_receipt_is_retired_before_model_handoff(self):
        r=self.make_wait();row=r.ledger.get('T01');file=Path(row['evidence_dir'])/'failed.multica.json'
        file.write_text(json.dumps({'issueId':'old-quota','run':{'status':'failed','error':'402 plan quota exceeded'}}))
        with r.ledger.connection:r.ledger.connection.execute("UPDATE tasks SET failure_category='blocked_quota' WHERE task_id='T01'")
        normalize_legacy_quota_waits(r,self.health)
        self.assertEqual(self.health.read()['retirements'],[{'provider':'kimi','logs':[str(file)]}])

if __name__=='__main__':unittest.main()
