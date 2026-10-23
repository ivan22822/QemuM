import hashlib
import json
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from copy import deepcopy

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'src/backend'))
from test_stand_runtime import FakeHost
from vm_stands import Store,create
from vm_stand_plan import upgrade
from vm_stand_runtime import Runtime,Conflict,Unknown
from vm_stand_lifecycle import reconcile,duplicate,remove,report,save_report


class StandLifecycleTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name).resolve();self.host=FakeHost();self.runtime=Runtime(self.root,self.host)
        self.row=Store(self.root).save(upgrade(create('Лаборатория',['a','b'])),0)
    def tearDown(self):
        self.runtime.close()
        for job in self.runtime.jobs.values():job['thread'].join(5)
        self.temp.cleanup()
    def start(self):
        plan=self.runtime.make_plan(self.row['id']);self.runtime.execute(plan['id'],plan['id']);self.runtime.jobs[plan['id']]['thread'].join(5)
        status=self.runtime.status(plan['id']);self.assertEqual(status['state'],'completed',status);return plan
    def test_terminal_history_reopens_without_starting_anything(self):
        plan=self.start();before=deepcopy(self.host.starts);runtime=Runtime(self.root,self.host)
        try:
            self.assertEqual(runtime.status(plan['id'])['state'],'completed')
            self.assertEqual(runtime.overview(self.row['id'])['operation_id'],plan['id'])
            self.assertEqual(self.host.starts,before)
        finally:runtime.close()
    def test_interrupted_summary_is_read_only_until_explicit_reconciliation(self):
        plan=self.start();journal=self.runtime._journal(self.row['id']);journal['current']['state']='running';self.runtime._journal(self.row['id'],journal)
        path=self.root/'stand-runtime'/(self.row['id']+'.json');before=path.read_bytes();runtime=Runtime(self.root,self.host)
        try:
            self.assertEqual(runtime.status(plan['id'])['state'],'interrupted');self.assertEqual(path.read_bytes(),before)
            result=reconcile(runtime,self.row['id'],self.row['revision'],self.row['id'])
            self.assertFalse(result['automatic_actions']);self.assertEqual(set(result['members'].values()),{'owned-running'})
            self.assertEqual(len(self.host.starts),2)
        finally:runtime.close()
    def test_unidentified_intent_requires_proof_of_stopped_process(self):
        self.runtime._journal(self.row['id'],dict(owned={},ports={},pending=dict(name='a')))
        self.host.active['a']=dict(pid=999,created=999)
        result=reconcile(self.runtime,self.row['id'],self.row['revision'],self.row['id']);self.assertTrue(result['start_blocked'])
        self.assertEqual(self.runtime._journal(self.row['id'])['owned'],{})
        self.host.active.clear();result=reconcile(self.runtime,self.row['id'],self.row['revision'],self.row['id']);self.assertFalse(result['start_blocked'])
    def test_pid_reuse_cannot_become_owned_after_restart(self):
        self.start();self.host.active['a']['created']+=1
        result=reconcile(self.runtime,self.row['id'],self.row['revision'],self.row['id'])
        self.assertEqual(result['members']['a'],'unowned-running');self.assertTrue(result['issues'])
    def test_history_bounded_and_active_markers_released(self):
        first=self.start()
        for index in range(22):self.start()
        journal=self.runtime._journal(self.row['id']);self.assertEqual(len(journal['history']),20)
        self.assertEqual(list((self.root/'stand-runtime'/'active').glob('*.lock')),[])
    def test_duplicate_uses_new_uuid_and_same_vm_references(self):
        result=duplicate(self.runtime,self.row['id'],self.row['revision'],'Копия')
        self.assertNotEqual(result['id'],self.row['id']);self.assertEqual(result['members'],self.row['members'])
        self.assertEqual(self.host.starts,[])
        with self.assertRaises(Conflict):duplicate(self.runtime,self.row['id'],0,'Stale')
    def test_remove_keeps_guests_and_runtime_and_other_stands(self):
        self.start();copied=duplicate(self.runtime,self.row['id'],self.row['revision'],'Second');before=deepcopy(self.host.active)
        result=remove(self.runtime,self.row['id'],self.row['revision'],self.row['id'])
        self.assertFalse(result['vm_files_changed']);self.assertEqual(self.host.active,before)
        self.assertTrue((self.root/'stand-runtime'/(self.row['id']+'.json')).exists())
        self.assertEqual(Store(self.root).read(copied['id'])['members'],self.row['members'])
    def test_remove_while_starting_is_rejected(self):
        gate=threading.Event();self.host.callback=lambda n:gate.wait(3)
        p=self.runtime.make_plan(self.row['id']);self.runtime.execute(p['id'],p['id'])
        try:
            with self.assertRaises(Conflict):remove(self.runtime,self.row['id'],self.row['revision'],self.row['id'])
        finally:gate.set();self.runtime.jobs[p['id']]['thread'].join(5)
    def test_missing_vm_reference_is_visible_and_not_deleted(self):
        self.host.data.pop('a')
        result=reconcile(self.runtime,self.row['id'],self.row['revision'],self.row['id']);self.assertEqual(result['members']['a'],'unknown')
        self.assertEqual(Store(self.root).read(self.row['id'])['members'],['a','b'])
    def test_report_allowlist_anonymization_and_exact_preview_bytes(self):
        self.row['description']='password=SHOULD_NOT_LEAK';self.row=Store(self.root).save(self.row,self.row['revision']);self.start()
        # Deliberately short fake marker: this tests the report allowlist,
        # without storing a credential-shaped literal in the source tree.
        journal=self.runtime._journal(self.row['id']);journal['current']['events'].append(dict(time=0,phase='Failure '+str(self.root/'private.qcow2'),vm='a',password='FAKE_KEY'));journal['current']['error']='SECRET_ERROR';self.runtime._journal(self.row['id'],journal)
        text=report(self.runtime,self.row['id'])
        for secret in ('SHOULD_NOT_LEAK','FAKE_KEY','SECRET_ERROR',str(self.root)):self.assertNotIn(secret,text)
        parsed=json.loads(text);self.assertEqual(parsed['definition']['members'],['vm-1','vm-2'])
        token='f'*32;digest=hashlib.sha256(text.encode()).hexdigest();self.runtime.exports[token]=dict(text=text,expires=time.monotonic()+10)
        target=self.root/'report.json';save_report(self.runtime,token,str(target),digest);self.assertEqual(target.read_bytes(),text.encode())
        self.assertNotIn(token,self.runtime.exports)
    def test_report_never_overwrites_an_existing_file(self):
        text=report(self.runtime,self.row['id']);token='f'*32;digest=hashlib.sha256(text.encode()).hexdigest();self.runtime.exports[token]=dict(text=text,expires=time.monotonic()+10)
        target=self.root/'existing.json';target.write_bytes(b'KEEP')
        with self.assertRaises(FileExistsError):save_report(self.runtime,token,str(target),digest)
        self.assertEqual(target.read_bytes(),b'KEEP')
    def test_damaged_unrelated_journal_does_not_hide_a_valid_operation(self):
        p=self.start();(self.root/'stand-runtime'/('0'*32+'.json')).write_text('[]')
        runtime=Runtime(self.root,self.host)
        try:self.assertEqual(runtime.status(p['id'])['state'],'completed')
        finally:runtime.close()
    def test_malformed_nested_journal_is_rejected_without_mutating_guests(self):
        path=self.root/'stand-runtime'/(self.row['id']+'.json');path.parent.mkdir()
        for value in (dict(owned={'a':None}),dict(owned={},current={'id':'x'}),dict(owned={},history=[None]),dict(owned={},pending=[])):
            path.write_text(json.dumps(value));before=path.read_bytes()
            with self.assertRaises(Unknown):self.runtime.make_plan(self.row['id'])
            self.assertEqual(path.read_bytes(),before);self.assertEqual(self.host.starts,[]);self.assertEqual(self.host.stops,[])
