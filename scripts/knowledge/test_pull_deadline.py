import importlib.util,sys,tempfile,unittest
from pathlib import Path
spec=importlib.util.spec_from_file_location('pull_arm64',Path(__file__).resolve().parents[1]/'ci/pull_arm64.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
class PullDeadlineTests(unittest.TestCase):
 def test_hung_client_is_terminated_for_retry(self):
  with tempfile.TemporaryDirectory() as tmp:
   command=Path(tmp)/'docker';command.write_text('#!'+sys.executable+'\nimport time\ntime.sleep(30)\n');command.chmod(0o755)
   self.assertEqual(module.pull('image',timeout_seconds=.1,executable=str(command)),124)
 def test_successful_client_return_is_preserved(self):
  with tempfile.TemporaryDirectory() as tmp:
   command=Path(tmp)/'docker';command.write_text('#!'+sys.executable+'\nraise SystemExit(0)\n');command.chmod(0o755)
   self.assertEqual(module.pull('image',timeout_seconds=5,executable=str(command)),0)
