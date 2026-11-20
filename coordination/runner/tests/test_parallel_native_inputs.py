import concurrent.futures,json,subprocess,sys,tempfile,threading,unittest
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from atlas_runner import ProcessSupervisor
from multica_adapter import MulticaAdapter

class ScopedNativeInputTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup);self.home=Path(self.tmp.name).resolve()
    def native(self,label,barrier=None,tamper=False):
        cwd=self.home/'Workspace/projects/business-atlas-preflight'/label;cwd.mkdir(parents=True)
        subprocess.run(['git','init','-q',str(cwd)],check=True)
        calls=[]
        binding={'profile':'test','workspaceId':'new-space','runtimeId':'mac16','daemonId':'mac16-daemon','tasks':{},'roles':{'atlas-leader':{'agentId':'leader','projectId':'project-'+label,'resourceId':'resource-'+label}}}
        def command(*args):
            calls.append(args)
            if args[:2]==('runtime','list'):return [{'id':'mac16','daemon_id':'mac16-daemon','provider':'opencode','status':'online'}]
            if args[:2]==('agent','list'):return [{'id':'leader','runtime_id':'mac16'}]
            if args[:2]==('issue','create'):
                if barrier:barrier.wait(10)
                return {'id':'issue-'+label,'identifier':label}
            if args[:2]==('issue','runs'):
                text=(cwd/'.atlas-runtime/task-input.txt').read_text()
                if tamper:(cwd/'.atlas-runtime/task-input.txt').write_text('wrong task')
                return [{'id':'run-'+label,'agent_id':'leader','runtime_id':'mac16','status':'completed','result':{'output':text,'work_dir':str(cwd)}}]
            return {}
        return MulticaAdapter(binding,ProcessSupervisor(),command=command,sleep=lambda _:None),cwd,calls
    def test_two_concurrent_roles_read_their_own_worktree_inputs(self):
        barrier=threading.Barrier(2);a,ca,_=self.native('a',barrier);b,cb,_=self.native('b',barrier)
        with patch('pathlib.Path.home',return_value=self.home),concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            fa=pool.submit(a.run,'atlas-leader','PREP-A own input',ca,self.home/'logs-a',30)
            fb=pool.submit(b.run,'atlas-leader','PREP-B other input',cb,self.home/'logs-b',30)
            ra,rb=fa.result(),fb.result()
        self.assertTrue(ra.ok and rb.ok);self.assertEqual(ra.text,'PREP-A own input');self.assertEqual(rb.text,'PREP-B other input')
        for cwd in (ca,cb):
            self.assertEqual(subprocess.check_output(['git','status','--porcelain'],cwd=cwd,text=True),'')
    def test_input_mutation_cannot_be_accepted(self):
        a,cwd,calls=self.native('tamper',tamper=True)
        with patch('pathlib.Path.home',return_value=self.home):result=a.run('atlas-leader','PREP-A original input',cwd,self.home/'logs',30)
        self.assertFalse(result.ok);self.assertEqual(result.category,'input_mutated')
        self.assertFalse(any(c[:2]==('issue','status') and 'done' in c for c in calls))

if __name__=='__main__':unittest.main()
