import json,re,sqlite3,sys,threading,time,unittest
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import atlas_runner as ar
from parallel_executor import ParallelAtlasRunner,binding_for_slot,conflicts,eligible_rows,run_parallel,prepare_stopped_dispatches
from test_atlas_runner import RunnerFixture,FakeAdapter,FakeIntegration,PASS_QA,PASS_REVIEW

class ParallelTests(RunnerFixture):
    def independent(self,shared=False):
        self.dag['tasks'][1]['dependsOn']=[]
        for task,name in zip(self.dag['tasks'],('alpha.txt','beta.txt')):
            task['repoPaths']=[name]
            task['resourceLocks']=['shared-contract'] if shared else []
        self.dag_path.write_text(json.dumps(self.dag))
        policy={'version':1,'productVerify':'scripts/ci/verify','taskVerify':{tid:[sys.executable,'-c',"from pathlib import Path;assert Path('"+name+"').read_text()=='implemented'"] for tid,name in [('T01','alpha.txt'),('T02','beta.txt')]}}
        self.policy_path.write_text(json.dumps(policy))
        ar.git(self.repo,'add','-A');ar.git(self.repo,'commit','-m','independent fixture');self.base_sha=ar.git_head(self.repo)
    def parallel_runner(self,adapter=None,integration=None,**updates):
        r=ParallelAtlasRunner(self.repo,self.baseline,self.state,self.controls(enabled=True,parallelValidation=True,**updates),adapter or FakeAdapter(),integration or FakeIntegration(),sleep=lambda _:None)
        r.ledger.initialize(self.dag_path,self.baseline,100);self.addCleanup(r.close);return r
    def test_dependencies_are_not_bypassed_by_second_slot(self):
        r=self.make_runner(maxConcurrentTasks=2)
        self.assertEqual(r.ledger.claim('one',100,100,2,self.base_sha,self.state/'evidence',lambda _:None,worker_slot=1)['task_id'],'T01')
        self.assertIsNone(r.ledger.claim('two',100,100,2,self.base_sha,self.state/'evidence',lambda _:None,worker_slot=2))
    def test_independent_claims_are_capped_at_two(self):
        self.independent();r=self.make_runner(maxConcurrentTasks=2)
        a=r.ledger.claim('one',100,100,2,self.base_sha,self.state/'evidence',lambda _:None,worker_slot=1)
        b=r.ledger.claim('two',100,100,2,self.base_sha,self.state/'evidence',lambda _:None,worker_slot=2)
        self.assertNotEqual(a['task_id'],b['task_id'])
        self.assertIsNone(r.ledger.claim('three',100,100,2,self.base_sha,self.state/'evidence',lambda _:None))
    def test_shared_resource_excludes_second_task(self):
        self.independent(shared=True);r=self.make_runner(maxConcurrentTasks=2)
        r.ledger.claim('one',100,100,2,self.base_sha,self.state/'evidence',lambda _:None,worker_slot=1)
        self.assertIsNone(r.ledger.claim('two',100,100,2,self.base_sha,self.state/'evidence',lambda _:None,worker_slot=2))
    def test_overlapping_directories_conflict(self):
        self.assertTrue(conflicts({'repoPaths':['apps/web']},{'repoPaths':['apps/web/src/features']}))
        self.assertFalse(conflicts({'repoPaths':['src/Modules/SchemaCatalog']},{'repoPaths':['src/Modules/PlatformOps/Jobs']}))
    def test_slot_affinity_survives_quota_checkpoint(self):
        r=self.make_runner(maxConcurrentTasks=2)
        row=r.ledger.claim('two',100,100,2,self.base_sha,self.state/'evidence',lambda _:None,worker_slot=2)
        r.ledger.defer_provider('T01','two',row['fence'],'quota',101)
        self.assertIsNone(r.ledger.claim('one',102,100,2,self.base_sha,self.state/'evidence',lambda _:None,worker_slot=1))
        self.assertEqual(r.ledger.claim('two-new',102,100,2,self.base_sha,self.state/'evidence',lambda _:None,worker_slot=2)['task_id'],'T01')
    def test_second_slot_requires_distinct_native_project_binding(self):
        with self.assertRaises(ar.RunnerError):binding_for_slot({'roles':{}},2)
        a={'roles':{'role':{'projectId':'one'}},'slots':{'2':{'roles':{'role':{'projectId':'two'}}}}}
        self.assertEqual(binding_for_slot(a,2)['roles']['role']['projectId'],'two')
        self.assertEqual(a['roles']['role']['projectId'],'one')
    def test_three_validation_lanes_overlap_on_same_candidate(self):
        barrier=threading.Barrier(3);seen={};normal=FakeAdapter()
        def handler(role,prompt,cwd,count):
            if role in ('atlas-reviewer','atlas-qa'):
                seen[role]=ar.git_head(cwd);barrier.wait(5)
                return ar.RoleResult(True,text=PASS_REVIEW if role=='atlas-reviewer' else PASS_QA)
            return normal.run(role,prompt,cwd,self.state,30)
        r=self.parallel_runner(adapter=FakeAdapter(handler));original=r._run_verification
        def verification(row,cwd):
            if not r._validation_cache:
                seen['verification']=ar.git_head(cwd);barrier.wait(5)
            return original(row,cwd)
        r._run_verification=verification
        with patch.object(r.supervisor,'execute',wraps=r.supervisor.execute) as execute:
            result=r.run_once()
            self.assertEqual(execute.call_count,1)
        self.assertEqual(result['status'],'done')
        self.assertEqual(len(seen),3);self.assertEqual(len(set(seen.values())),1)
        receipt=json.loads((Path(r.ledger.get('T01')['evidence_dir'])/'parallel-validation.json').read_text())
        self.assertTrue(receipt['allJoined'] and receipt['verificationOk'] and receipt['qaOk'])
    def test_rejected_review_cannot_be_overridden_by_parallel_qa(self):
        normal=FakeAdapter()
        def handler(role,prompt,cwd,count):
            if role=='atlas-reviewer':return ar.RoleResult(True,text=json.dumps({'verdict':'needs_changes','findings':['fix source'],'evidence':['actual file']}))
            return normal.run(role,prompt,cwd,self.state,30)
        integration=FakeIntegration();r=self.parallel_runner(adapter=FakeAdapter(handler),integration=integration)
        self.assertEqual(r.run_once()['status'],'needs_changes');self.assertEqual(integration.calls,[])
        self.assertIsNone(r.ledger.latest_accepted('T01','qa'))
    def test_failed_deterministic_verification_cannot_be_overridden(self):
        d=json.loads(self.policy_path.read_text());d['taskVerify']['T01']=[sys.executable,'-c','raise SystemExit(3)'];self.policy_path.write_text(json.dumps(d))
        ar.git(self.repo,'add','-A');ar.git(self.repo,'commit','-m','failure gate fixture');self.base_sha=ar.git_head(self.repo)
        integration=FakeIntegration();r=self.parallel_runner(integration=integration)
        self.assertEqual(r.run_once()['category'],'verification_failed');self.assertEqual(integration.calls,[])
    def test_two_real_worker_threads_build_independent_candidates_and_serialize_integration(self):
        self.independent();parent=self.parallel_runner(maxConcurrentTasks=2);barrier=threading.Barrier(2)
        counts=[];gate=threading.Lock();merging=[0,0]
        def handler(role,prompt,cwd,count):
            if role=='atlas-builder':
                tid=re.search(r'"taskId"\s*:\s*"([^"]+)"',prompt).group(1)
                (cwd/('alpha.txt' if tid=='T01' else 'beta.txt')).write_text('implemented')
                with sqlite3.connect(self.state/'ledger.sqlite3') as db:counts.append(db.execute("SELECT COUNT(*) FROM tasks WHERE lease_owner IS NOT NULL").fetchone()[0])
                barrier.wait(10);return ar.RoleResult(True,text='implemented')
            return ar.RoleResult(True,text='plan' if role=='atlas-leader' else PASS_REVIEW)
        class Integration(FakeIntegration):
            def integrate(self,*args):
                with gate:merging[0]+=1;merging[1]=max(merging)
                time.sleep(.03)
                try:return super().integrate(*args)
                finally:
                    with gate:merging[0]-=1
        def factory(slot):
            return ParallelAtlasRunner(self.repo,self.baseline,self.state,self.controls(enabled=True,maxConcurrentTasks=2,workerSlot=slot,parallelValidation=True),FakeAdapter(handler),Integration(),sleep=lambda _:None)
        with patch.object(parent,'preflight',return_value={'ready':True}):result=run_parallel(parent,self.state/'controls.json',factory=factory)
        self.assertEqual(result['status'],'idle');self.assertEqual(counts,[2,2]);self.assertEqual(merging[1],1)
        self.assertTrue(all(row['phase']=='done' for row in parent.ledger.rows()))
    def test_explicit_resume_archives_cancelled_journal_without_changing_candidate(self):
        r=self.make_runner();row=r.ledger.claim('old',100,100,1,self.base_sha,self.state/'evidence',lambda _:None)
        r.ledger.fail('T01','old',row['fence'],'stopped','user pause',101)
        folder=self.state/'native-dispatch';folder.mkdir();journal=folder/'stopped.json'
        journal.write_text(json.dumps({'prompt':'{"taskId":"T01"}','issue':{'id':'owned-issue'}}))
        class Adapter:
            def command(self,*args):return [{'id':'old-run','status':'cancelled'}]
        r.adapter=Adapter();prepare_stopped_dispatches(r)
        self.assertFalse(journal.exists());self.assertEqual(len(list((self.state/'dispatch-archive').glob('*.json'))),1)
        self.assertEqual(r.ledger.get('T01')['phase'],'leader');self.assertIsNone(r.ledger.get('T01')['candidate_sha'])
        prepare_stopped_dispatches(r)
        self.assertEqual(r.ledger.connection.execute("SELECT COUNT(*) FROM events WHERE event_type='pause_wait'").fetchone()[0],1)

if __name__=='__main__':unittest.main()
