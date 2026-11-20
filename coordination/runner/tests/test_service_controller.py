import importlib.util,json,sys,tempfile,unittest
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
spec=importlib.util.spec_from_file_location('atlasctl_tested',Path(__file__).resolve().parents[1]/'atlasctl.py')
ctl=importlib.util.module_from_spec(spec);spec.loader.exec_module(ctl)

class ServiceControllerTests(unittest.TestCase):
    def test_actual_serve_entry_acquires_lock_calls_loop_and_checkpoints(self):
        with tempfile.TemporaryDirectory() as tmp:
            state=Path(tmp); controls=state/'controls.json';controls.write_text('{"enabled":true,"roleBackend":"multica","multicaBindingPath":"private-binding"}')
            class Runner:
                called=False
                closed=False
                def request_stop(self,*a):pass
                def loop(self):
                    self.called=True
                    with self_test.assertRaises(ctl.ar.RunnerError):ctl.ar.ProcessLock(state/'runner.lock')
                    return {'status':'idle','iterations':1}
                def close(self):self.closed=True
            self_test=self; runner=Runner()
            with patch.object(ctl,'STATE',state),patch.object(ctl,'CONTROLS',controls),patch.object(ctl.ar,'build_real_runner',return_value=runner),patch.object(ctl.signal,'signal'):
                self.assertEqual(ctl.serve(),0)
            self.assertTrue(runner.called and runner.closed)
            self.assertEqual(json.loads((state/'last-run-result.json').read_text())['status'],'idle')
    def test_serve_rejects_legacy_direct_controls(self):
        with tempfile.TemporaryDirectory() as tmp:
            controls=Path(tmp)/'controls.json';controls.write_text('{"enabled":true}')
            with patch.object(ctl,'CONTROLS',controls):
                with self.assertRaisesRegex(RuntimeError,'Native Multica binding'):
                    ctl.serve()
    def test_prepare_rejects_existing_legacy_controls_before_selecting_adapter(self):
        with tempfile.TemporaryDirectory() as tmp:
            prep=Path(tmp);evidence=prep/'evidence';evidence.mkdir()
            (evidence/'azure-pipeline.json').write_text('{}')
            (evidence/'kimi-k31-smoke.jsonl').write_text('proof')
            state=prep/'state';state.mkdir();controls=state/'controls.json';controls.write_text('{"enabled":false}')
            with patch.object(ctl,'PREP',prep),patch.object(ctl,'STATE',state),patch.object(ctl,'CONTROLS',controls),patch.object(ctl.ar,'parse_opencode_jsonl',return_value='ATLAS_KIMI_K31_OK'),patch.object(ctl.ar,'build_real_runner') as build:
                with self.assertRaisesRegex(RuntimeError,'Native Multica binding'):
                    ctl.prepare()
                build.assert_not_called()

if __name__=='__main__':unittest.main()
