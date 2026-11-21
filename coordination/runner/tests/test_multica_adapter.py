import dataclasses,hashlib,json,sys,tempfile,unittest,subprocess
from types import SimpleNamespace
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from atlas_runner import ProcessSupervisor,IntegrationResult,RunnerError
from multica_adapter import MulticaAdapter,MulticaIntegration

class NativeMulticaTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup)
        self.home=Path(self.tmp.name).resolve()
        self.cwd=self.home/'Workspace/projects/business-atlas';self.cwd.mkdir(parents=True)
        subprocess.run(['git','init','-q',str(self.cwd)],check=True)
        self.log=self.home/'logs'
        self.binding={'profile':'test','workspaceId':'new','daemonId':'mac16','runtimeId':'runtime-mac16','tasks':{},'roles':{'atlas-leader':{'agentId':'leader','projectId':'role-project','resourceId':'resource'}}}
        self.calls=[];self.runtime='runtime-mac16';self.agent_runtime='runtime-mac16';self.actual_dir=str(self.cwd)
    def command(self,*args):
        self.calls.append(args)
        if args[:2]==('runtime','list'):return [{'id':'runtime-mac16','daemon_id':'mac16','provider':'opencode','status':'online'}]
        if args[:2]==('agent','list'):return [{'id':'leader','runtime_id':self.agent_runtime}]
        if args[:2]==('issue','create'):return {'id':'native-issue','identifier':'BAM-45'}
        if args[:2]==('issue','runs'):return [{'id':'run','agent_id':'leader','runtime_id':self.runtime,'status':'completed','result':{'output':'PLAN_ONLY','work_dir':self.actual_dir}}]
        return {}
    def adapter(self):return MulticaAdapter(self.binding,ProcessSupervisor(),command=self.command,sleep=lambda _:None)
    def test_native_dispatch_binds_exact_worktree_and_preserves_receipt(self):
        with patch('pathlib.Path.home',return_value=self.home):
            r=self.adapter().run('atlas-leader','T01 response only',self.cwd,self.log,10)
        self.assertTrue(r.ok)
        self.assertEqual(r.text,'PLAN_ONLY')
        self.assertTrue(Path(r.log_paths[0]).exists())
        update=next(c for c in self.calls if c[:3]==('project','resource','update'))
        self.assertIn(str(self.cwd),update)
    def test_misbound_agent_prevents_dispatch(self):
        self.agent_runtime='windows'
        with patch('pathlib.Path.home',return_value=self.home):
            r=self.adapter().run('atlas-leader','T01',self.cwd,self.log,10)
        self.assertFalse(r.ok)
        self.assertFalse(any(c[:2]==('issue','create') for c in self.calls))
    def test_wrong_runtime_cannot_pass(self):
        self.runtime='windows'
        with patch('pathlib.Path.home',return_value=self.home):
            r=self.adapter().run('atlas-leader','T01',self.cwd,self.log,10)
        self.assertEqual(r.category,'wrong_runtime')
    def test_wrong_worktree_cannot_pass(self):
        self.actual_dir='/tmp/unapproved'
        with patch('pathlib.Path.home',return_value=self.home):
            r=self.adapter().run('atlas-leader','T01',self.cwd,self.log,10)
        self.assertEqual(r.category,'wrong_worktree')
    def test_existing_native_dispatch_resumes_without_duplicate_issue(self):
        state=self.home/'Workspace/projects/business-atlas-preflight/runner-state-multica/native-dispatch'
        state.mkdir(parents=True)
        marker=state/(hashlib.sha256(b'atlas-leader\0T01').hexdigest()+'.json')
        marker.write_text(json.dumps({'cwd':str(self.cwd),'prompt':'T01','issue':{'id':'native-issue','identifier':'BAM-45'}}))
        with patch('pathlib.Path.home',return_value=self.home):
            self.assertTrue(self.adapter().run('atlas-leader','T01',self.cwd,self.log/'new-attempt-2',10).ok)
        self.assertFalse(any(c[:2]==('issue','create') for c in self.calls))
    def test_missing_worktree_receipt_cannot_pass(self):
        self.actual_dir=None
        with patch('pathlib.Path.home',return_value=self.home):
            r=self.adapter().run('atlas-leader','T01',self.cwd,self.log,10)
        self.assertEqual(r.category,'wrong_worktree')
    def test_lost_creation_response_is_reconciled_without_second_post(self):
        state=self.home/'Workspace/projects/business-atlas-preflight/runner-state-multica/native-dispatch'
        state.mkdir(parents=True)
        marker=state/(hashlib.sha256(b'atlas-leader\0T01').hexdigest()+'.json')
        marker.write_text(json.dumps({'cwd':str(self.cwd),'prompt':'T01','title':'saved-intent','issue':None}))
        original=self.command
        def cmd(*args):
            if args[:2]==('issue','list'):return {'issues':[{'id':'native-issue','identifier':'BAM-45','title':'saved-intent'}],'has_more':False}
            return original(*args)
        with patch('pathlib.Path.home',return_value=self.home):
            a=MulticaAdapter(self.binding,ProcessSupervisor(),command=cmd,sleep=lambda _:None)
            self.assertTrue(a.run('atlas-leader','T01',self.cwd,self.log,10).ok)
        self.assertFalse(any(c[:2]==('issue','create') for c in self.calls))
    def test_requirement_never_done_without_pr_merge_and_deployment(self):
        self.binding['tasks']['T01']={'id':'requirement','description':'baseline'}
        class Backend:
            result=IntegrationResult('pending','sha')
            def integrate(self,*a):return self.result
        b=Backend();gate=MulticaIntegration(b,self.adapter())
        gate.integrate({'taskId':'T01'},'sha',self.log)
        self.assertFalse(any(c[:2]==('issue','update') for c in self.calls))
        b.result=IntegrationResult('merged','sha',merge_sha='merged',deployment_id='',pr_id='5')
        with self.assertRaises(RunnerError):gate.integrate({'taskId':'T01'},'sha',self.log)
        b.result=IntegrationResult('merged','sha',merge_sha='merged',deployment_id='14',pr_id='5')
        gate.integrate({'taskId':'T01'},'sha',self.log)
        call=next(c for c in self.calls if c[:2]==('issue','update'))
        self.assertIn('in_review',call);self.assertNotIn('done',call)
        self.adapter().sync_status({'tasks':[{'taskId':'T01','state':'done','phase':'done','candidateSha':'sha','integration':dataclasses.asdict(b.result)}]})
        self.assertIn('done',self.calls[-1]);self.assertIn('--no-start',self.calls[-1])
    def test_done_sync_rejects_missing_ledger_receipt(self):
        self.binding['tasks']['T01']={'id':'requirement','description':'baseline'}
        with self.assertRaises(RunnerError):
            self.adapter().sync_status({'tasks':[{'taskId':'T01','state':'done','phase':'done','candidateSha':'sha'}]})
        self.assertFalse(any('done' in c for c in self.calls))
    def test_native_brief_cleanup_must_finish_before_acceptance(self):
        agents=self.cwd/'AGENTS.md'
        agents.write_text('user content\n<!-- BEGIN MULTICA-RUNTIME (auto-managed; do not edit) -->\nbrief')
        with patch('pathlib.Path.home',return_value=self.home):
            a=self.adapter()
            result=a.run('atlas-leader','T01',self.cwd,self.log/'attempt-1',10)
            self.assertEqual(result.category,'runtime_cleanup_pending')
            self.assertTrue(result.retryable)
            agents.write_text('user content\n')
            self.assertTrue(a.run('atlas-leader','T01',self.cwd,self.log/'attempt-2',10).ok)
        self.assertEqual(len([c for c in self.calls if c[:2]==('issue','create')]),1)
    def test_host_cli_avoids_agent_context_marker_and_task_token(self):
        import os
        with patch('pathlib.Path.home',return_value=self.home),patch.dict(os.environ,{'MULTICA_TOKEN':'test-task-token','MULTICA_TASK_ID':'test-task'}),patch('multica_adapter.subprocess.run',return_value=SimpleNamespace(returncode=0,stdout='{}',stderr='')) as run:
            a=MulticaAdapter(self.binding,ProcessSupervisor())
            a._command('runtime','list','--output','json')
            options=run.call_args.kwargs
            self.assertNotEqual(options['cwd'],self.cwd)
            self.assertTrue(str(options['cwd']).endswith('business-atlas-preflight/multica-control'))
            self.assertNotIn('MULTICA_TOKEN',options['env'])
            self.assertNotIn('MULTICA_TASK_ID',options['env'])
    def test_missing_worktree_is_rejected_before_dispatch(self):
        missing=self.home/'Workspace/projects/business-atlas-preflight/missing'
        with patch('pathlib.Path.home',return_value=self.home):
            with self.assertRaises(RunnerError):
                self.adapter().run('atlas-leader','T01',missing,self.log,10)
        self.assertFalse(any(c[:2]==('issue','create') for c in self.calls))

    def test_board_session_error_cannot_abort_authoritative_development(self):
        self.binding['tasks']['T01']={'id':'requirement','description':'baseline'}
        def command(*args):raise RunnerError('multica_api_failed','session temporarily unavailable')
        adapter=MulticaAdapter(self.binding,ProcessSupervisor(),command=command)
        with patch('pathlib.Path.home',return_value=self.home):
            adapter.sync_status({'tasks':[{'taskId':'T01','state':'running','phase':'developer'}]})
        pending=self.home/'Workspace/projects/business-atlas-preflight/runner-state-multica/status-sync-pending.json'
        self.assertTrue(pending.is_file())

    def test_successful_execution_survives_board_done_transport_failure(self):
        original=self.command
        def command(*args):
            if args[:2]==('issue','status') and 'done' in args:raise RunnerError('multica_api_failed','board temporarily unavailable')
            return original(*args)
        with patch('pathlib.Path.home',return_value=self.home):
            result=MulticaAdapter(self.binding,ProcessSupervisor(),command=command,sleep=lambda _:None).run('atlas-leader','T01',self.cwd,self.log,10)
        self.assertTrue(result.ok)
        receipt=json.loads(Path(result.log_paths[0]).read_text())
        self.assertIn('run',receipt)
        self.assertNotIn('category',receipt)

    def test_cli_timeout_is_normalized_as_transport_failure(self):
        with patch('pathlib.Path.home',return_value=self.home),patch('multica_adapter.subprocess.run',side_effect=subprocess.TimeoutExpired(['multica'],60)):
            with self.assertRaises(RunnerError) as raised:self.adapter()._command('issue','list')
        self.assertEqual(raised.exception.category,'multica_api_failed')

    def test_execution_status_queue_is_replayed_and_cleared(self):
        original=self.command;unavailable=[True]
        def command(*args):
            if args[:2]==('issue','status') and 'done' in args and unavailable[0]:raise RunnerError('multica_api_failed','temporary')
            return original(*args)
        adapter=MulticaAdapter(self.binding,ProcessSupervisor(),command=command,sleep=lambda _:None)
        with patch('pathlib.Path.home',return_value=self.home):
            self.assertTrue(adapter.run('atlas-leader','T01',self.cwd,self.log,10).ok)
            queue=self.home/'Workspace/projects/business-atlas-preflight/runner-state-multica/status-sync-executions'
            self.assertTrue(list(queue.glob('*.json')))
            unavailable[0]=False
            adapter.sync_status({'tasks':[]})
            self.assertFalse(list(queue.glob('*.json')))

if __name__=='__main__':unittest.main()
