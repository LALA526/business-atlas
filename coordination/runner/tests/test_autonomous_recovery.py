import json,time
from pathlib import Path
from unittest.mock import patch
from test_atlas_runner import RunnerFixture
import atlas_runner as ar
import autonomous_recovery as recovery

PLAN={'decision':'repair','rootCause':'reproduced whole-series boundary defect','strategy':'minimal_reproduction','steps':['repair complete Git range'],'acceptanceChecks':['multi-commit positive and extra-change negative test']}
class RecoveryTests(RunnerFixture):
 def setup_runner(self,category='review_rejected'):
  r=self.make_runner();self.addCleanup(r.close)
  (self.state/'controls.json').write_text(json.dumps({'enabled':True,'stopRequested':False,'autonomousRecovery':{'enabled':True}}))
  with r.ledger.connection:r.ledger.connection.execute("update tasks set phase='blocked',attempt=3,failure_category=?,failure_detail='real finding' where task_id='T01'",(category,))
  return r
 def test_escalation_opens_new_bounded_epoch_without_accepting_code(self):
  r=self.setup_runner();before=r.ledger.get('T01')['candidate_sha']
  with patch.object(recovery,'settle_dispatches',return_value=True):self.assertEqual(recovery.recover(r,r.ledger.get('T01'),lambda *a:PLAN,lambda:200),'repair_planned')
  row=r.ledger.get('T01');self.assertEqual(row['phase'],'leader');self.assertEqual(row['candidate_sha'],before);self.assertEqual(row['failure_category'],'recovery_planned')
  self.assertEqual(recovery.attempt_cost(r.ledger,row),0)
  with r.ledger.connection:r.ledger.connection.execute("update tasks set attempt=6 where task_id='T01'")
  self.assertIsNone(r.ledger.claim('x',201,60,1,self.base_sha,self.state/'evidence',lambda _:None,max_attempts=3))
  self.assertEqual(r.ledger.get('T01')['phase'],'blocked')
 def test_explicit_stop_during_diagnosis_keeps_checkpoint_blocked(self):
  r=self.setup_runner()
  def stop(*a):(self.state/'STOP').touch();return PLAN
  with patch.object(recovery,'settle_dispatches',return_value=True):self.assertEqual(recovery.recover(r,r.ledger.get('T01'),stop,lambda:200),'paused')
  self.assertEqual(r.ledger.get('T01')['phase'],'blocked')
 def test_business_or_permission_blocker_is_not_silently_cleared(self):
  r=self.setup_runner('blocked_credential')
  self.assertEqual(recovery.recover(r,r.ledger.get('T01'),lambda *a:self.fail('must not diagnose')),'protected')
 def test_diagnosis_failure_has_persistent_backoff(self):
  r=self.setup_runner()
  def fail(*a):raise ValueError('invalid model output')
  with patch.object(recovery,'settle_dispatches',return_value=True):
   with self.assertRaises(ValueError):recovery.recover(r,r.ledger.get('T01'),fail,lambda:200)
   self.assertEqual(recovery.recover(r,r.ledger.get('T01'),lambda *a:self.fail(),lambda:201),'backoff')
 def test_external_diagnosis_preserves_blocker_and_missing_input(self):
  r=self.setup_runner();plan={**PLAN,'decision':'external','externalNeed':'actual owner must provide production identity'}
  with patch.object(recovery,'settle_dispatches',return_value=True):self.assertEqual(recovery.recover(r,r.ledger.get('T01'),lambda *a:plan,lambda:200),'external')
  self.assertEqual(r.ledger.get('T01')['phase'],'blocked');self.assertEqual(recovery.checkpoint(r,'T01')['decision'],'external')
 def test_live_coordinator_lock_prevents_supervisor_mutations(self):
  r=self.setup_runner()
  with ar.ProcessLock(self.state/'runner.lock'):
   result=recovery.supervise(r,start=lambda:self.fail(),diagnosis=lambda *a:self.fail())
  self.assertEqual(result['status'],'coordinator_running')
 def test_successful_recovery_restarts_coordinator_under_enabled_controls(self):
  r=self.setup_runner();starts=[]
  with patch.object(r,'preflight',return_value={'ready':True}),patch.object(recovery,'settle_dispatches',return_value=True):
   result=recovery.supervise(r,start=lambda:starts.append(1),diagnosis=lambda *a:PLAN,clock=lambda:200)
  self.assertTrue(result['restarted']);self.assertEqual(starts,[1]);self.assertNotEqual(r.ledger.get('T01')['phase'],'done')
 def test_stop_prevents_timer_restart(self):
  r=self.setup_runner();(self.state/'STOP').touch()
  self.assertEqual(recovery.supervise(r,start=lambda:self.fail())['status'],'paused')
 def test_dispatch_reconciliation_archives_only_terminal_owned_task(self):
  r=self.setup_runner('dispatch_conflict');folder=self.state/'native-dispatch';folder.mkdir()
  owned=folder/'owned.json';owned.write_text(json.dumps({'prompt':'{"taskId":"T01"}','cwd':str(self.repo),'issue':{'id':'owned'}}))
  other=folder/'other.json';other.write_text(json.dumps({'prompt':'{"taskId":"T02"}','cwd':str(self.repo),'issue':{'id':'other'}}))
  calls=[]
  def command(*args):
   calls.append(args)
   return [{'id':'run','status':'completed'}] if args[1]=='runs' else {}
  r.adapter.command=command
  self.assertTrue(recovery.settle_dispatches(r,'T01'));self.assertFalse(owned.exists());self.assertTrue(other.exists());self.assertTrue(all('other' not in a for a in calls))
 def test_unresolved_creation_is_retained_and_blocks_duplicate_writer(self):
  r=self.setup_runner();folder=self.state/'native-dispatch';folder.mkdir();f=folder/'intent.json';f.write_text(json.dumps({'prompt':'{"taskId":"T01"}','issue':None}))
  with self.assertRaises(ar.RunnerError):recovery.settle_dispatches(r,'T01')
  self.assertTrue(f.exists())

 def test_cas_rejects_transition_between_fresh_read_and_update(self):
  r=self.setup_runner();original=r.ledger.get;row=original('T01');calls=[]
  def race(tid):
   fresh=original(tid)
   if not calls:
    calls.append(1)
    with r.ledger.connection:r.ledger.connection.execute("update tasks set fence=fence+1 where task_id='T01'")
   return fresh
  with patch.object(recovery,'settle_dispatches',return_value=True),patch.object(r.ledger,'get',side_effect=race):
   with self.assertRaises(ar.RunnerError):recovery.recover(r,row,lambda *a:PLAN,lambda:200)
  self.assertEqual(original('T01')['phase'],'blocked');self.assertNotIn('attemptBase',recovery.checkpoint(r,'T01'))
 def test_diagnosis_refuses_existing_tree_at_wrong_candidate(self):
  r=self.setup_runner();row=r.ledger.get('T01');folder=self.state/'diagnosis';folder.mkdir();tree=folder/'diagnosis-source'
  ar.git(self.repo,'worktree','add','--detach',str(tree),self.base_sha)
  (tree/'wrong.txt').write_text('unreviewed');ar.git(tree,'add','.');ar.git(tree,'commit','-m','wrong candidate')
  with patch.object(r.adapter,'run',side_effect=AssertionError('model must not inspect wrong source')):
   with self.assertRaises(ar.RunnerError):recovery.diagnose(r,row,{'cycle':0},folder)
 def test_successful_checkpoint_exhaustion_gets_diagnosed_not_stranded(self):
  r=self.setup_runner(None)
  with r.ledger.connection:r.ledger.connection.execute("update tasks set phase='review' where task_id='T01'")
  self.assertIsNone(r.ledger.claim('x',200,60,1,self.base_sha,self.state/'evidence',lambda _:None,max_attempts=3))
  row=r.ledger.get('T01');self.assertEqual(row['failure_category'],'attempt_exhausted')
  with patch.object(recovery,'settle_dispatches',return_value=True):self.assertEqual(recovery.recover(r,row,lambda *a:PLAN,lambda:201),'repair_planned')
 def test_qa_blocked_is_triaged_and_external_input_is_preserved(self):
  r=self.setup_runner('qa_blocked');plan={**PLAN,'decision':'external','externalNeed':'actual business identity required'}
  with patch.object(recovery,'settle_dispatches',return_value=True):self.assertEqual(recovery.recover(r,r.ledger.get('T01'),lambda *a:plan,lambda:200),'external')
  self.assertEqual(r.ledger.get('T01')['phase'],'blocked')
 def test_technical_qa_blocker_gets_claimable_repair_epoch(self):
  r=self.setup_runner('qa_blocked')
  with patch.object(recovery,'settle_dispatches',return_value=True):self.assertEqual(recovery.recover(r,r.ledger.get('T01'),lambda *a:PLAN,lambda:200),'repair_planned')
  self.assertIsNotNone(r.ledger.claim('worker',201,60,1,self.base_sha,self.state/'evidence',lambda _:None,max_attempts=3))
 def test_same_second_historical_wait_is_not_subtracted_twice(self):
  r=self.setup_runner()
  r.ledger._event('T01','provider_wait',0,'old wait',200)
  with patch.object(recovery,'settle_dispatches',return_value=True):recovery.recover(r,r.ledger.get('T01'),lambda *a:PLAN,lambda:200)
  r.ledger.connection.execute("update tasks set attempt=4 where task_id='T01'")
  self.assertEqual(recovery.attempt_cost(r.ledger,r.ledger.get('T01')),1)
  r.ledger._event('T01','provider_wait',1,'new wait at same wall clock',200)
  self.assertEqual(recovery.attempt_cost(r.ledger,r.ledger.get('T01')),0)
 def test_recent_review_history_sorts_attempts_numerically(self):
  root=self.state/'reviews'
  for attempt in (2,3,4,5,6,9,10,11):
   folder=root/'T01'/('attempt-'+str(attempt));folder.mkdir(parents=True)
   (folder/'review-verdict.json').write_text(json.dumps({'attempt':attempt}))
  self.assertEqual([5,6,9,10,11],[d['verdict']['attempt'] for d in recovery.recent_reviews(root,'T01')])
 def test_epoch_metadata_failure_rolls_back_task_transition(self):
  r=self.setup_runner();row=r.ledger.get('T01');original=r.ledger.set_metadata
  def broken(key,value):
   if 'attemptBase' in value:raise OSError('simulated metadata write failure')
   return original(key,value)
  with patch.object(recovery,'settle_dispatches',return_value=True),patch.object(r.ledger,'set_metadata',side_effect=broken):
   with self.assertRaises(OSError):recovery.recover(r,row,lambda *a:PLAN,lambda:200)
  after=r.ledger.get('T01');self.assertEqual(after['phase'],'blocked');self.assertEqual(after['fence'],row['fence']);self.assertNotIn('attemptBase',recovery.checkpoint(r,'T01'))

 def test_stop_at_recovery_entry_never_calls_diagnosis(self):
  r=self.setup_runner();(self.state/'STOP').touch()
  self.assertEqual(recovery.recover(r,r.ledger.get('T01'),lambda *a:self.fail()),'paused')
 def test_stop_after_settlement_never_calls_diagnosis(self):
  r=self.setup_runner()
  def settle(*a):(self.state/'STOP').touch();return True
  with patch.object(recovery,'settle_dispatches',side_effect=settle):self.assertEqual(recovery.recover(r,r.ledger.get('T01'),lambda *a:self.fail()),'paused')
 def test_empty_run_list_cannot_prove_old_actor_terminal(self):
  r=self.setup_runner();folder=self.state/'native-dispatch';folder.mkdir();f=folder/'intent.json';f.write_text(json.dumps({'prompt':'{"taskId":"T01"}','issue':{'id':'owned'},'cwd':str(self.repo)}))
  r.adapter.command=lambda *a:[]
  with patch.object(recovery.time,'sleep',return_value=None):
   with self.assertRaises(ar.RunnerError):recovery.settle_dispatches(r,'T01')
  self.assertTrue(f.exists())

 def test_another_exhausted_recovery_epoch_can_be_diagnosed(self):
  r=self.setup_runner()
  with patch.object(recovery,'settle_dispatches',return_value=True):
   self.assertEqual(recovery.recover(r,r.ledger.get('T01'),lambda *a:PLAN,lambda:200),'repair_planned')
   r.ledger.connection.execute("update tasks set phase='developer',attempt=6,failure_category='recovery_planned' where task_id='T01'")
   self.assertIsNone(r.ledger.claim('next',201,60,1,self.base_sha,self.state/'evidence',lambda _:None,max_attempts=3))
   self.assertEqual(recovery.recover(r,r.ledger.get('T01'),lambda *a:PLAN,lambda:202),'repair_planned')
  self.assertEqual(recovery.checkpoint(r,'T01')['cycle'],2)

 def test_stop_during_failed_preflight_cannot_restart_services(self):
  r=self.setup_runner()
  def preflight():(self.state/'STOP').touch();return {'ready':False}
  with patch.object(r,'preflight',side_effect=preflight),patch.object(recovery.subprocess,'run') as run:
   self.assertEqual(recovery.supervise(r)['status'],'paused')
  run.assert_not_called()
