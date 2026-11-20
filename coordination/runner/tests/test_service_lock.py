import sys, tempfile, unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from atlas_runner import ProcessLock, RunnerError

class ServiceLockTests(unittest.TestCase):
    def test_context_excludes_second_writer_and_releases_on_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'runner.lock'
            with self.assertRaisesRegex(ValueError, 'intentional'):
                with ProcessLock(path):
                    with self.assertRaises(RunnerError):
                        ProcessLock(path)
                    raise ValueError('intentional')
            with ProcessLock(path):
                with self.assertRaises(RunnerError):
                    ProcessLock(path)
