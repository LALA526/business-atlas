"""Exercise actual Git trees, including target-only content in a synthetic PR merge."""
import os,subprocess,tempfile,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
class CandidateCheckoutTests(unittest.TestCase):
 def test_synthetic_merge_is_replaced_by_exact_candidate_and_guard_rejects_wrong_head(self):
  with tempfile.TemporaryDirectory() as tmp:
   cwd=Path(tmp)
   def git(*args):return subprocess.check_output(['git',*args],cwd=cwd,text=True).strip()
   git('init','-b','main');git('config','user.name','Test');git('config','user.email','test@example.invalid')
   (cwd/'base').write_text('base');git('add','.');git('commit','-m','base')
   git('checkout','-b','source');(cwd/'source-only').write_text('reviewed');git('add','.');git('commit','-m','source');candidate=git('rev-parse','HEAD')
   git('checkout','main');(cwd/'target-only').write_text('unreviewed target');git('add','.');git('commit','-m','target')
   git('merge','--no-ff','source','-m','synthetic PR merge');self.assertNotEqual(candidate,git('rev-parse','HEAD'))
   env={**os.environ,'SYSTEM_PULLREQUEST_SOURCECOMMITID':candidate}
   def guard():return subprocess.run(['bash','-c','source "$1"; atlas_verification_candidate','bash',str(ROOT/'scripts/ci/verify_context.sh')],cwd=cwd,env=env,text=True,capture_output=True)
   self.assertNotEqual(0,guard().returncode)
   subprocess.run(['bash',str(ROOT/'scripts/ci/checkout_candidate.sh')],cwd=cwd,env=env,check=True,capture_output=True)
   self.assertEqual(candidate,git('rev-parse','HEAD'));self.assertFalse((cwd/'target-only').exists());self.assertEqual('reviewed',(cwd/'source-only').read_text())
   result=guard();self.assertEqual(0,result.returncode,result.stderr);self.assertEqual(candidate,result.stdout.strip())
   (cwd/'source-only').write_text('dirty')
   result=subprocess.run(['bash',str(ROOT/'scripts/ci/checkout_candidate.sh')],cwd=cwd,env=env,capture_output=True)
   self.assertNotEqual(0,result.returncode);self.assertEqual('dirty',(cwd/'source-only').read_text())
 def test_pipeline_selects_candidate_before_verification(self):
  pipeline=(ROOT/'azure-pipelines.yml').read_text()
  self.assertLess(pipeline.index('bash scripts/ci/checkout_candidate.sh'),pipeline.index('python scripts/preflight/verify.py'))
