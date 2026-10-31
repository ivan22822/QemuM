from copy import deepcopy
import hashlib,json,sys,unittest
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'src/backend'))
from vm_stand_diagnostics import summarize
from vm_stand_lifecycle import report
from vm_switch import Manager,SwitchError
from test_stand_switches import StandSwitchTests as SwitchFixture


class StandDiagnosticsTests(unittest.TestCase):
    setUp=SwitchFixture.setUp
    tearDown=SwitchFixture.tearDown
    finish=SwitchFixture.finish
    plan=SwitchFixture.plan
    def test_read_only_overview_nat_mac_and_guest_status(self):
        self.host.data['a']['network_adapters'].append(dict(id='internet',type='user',model='e1000e',mac='52:54:00:01:00:01'))
        before=deepcopy(self.host.data);value=self.runtime.overview(self.row['id'])['network_diagnostics'][0]
        self.assertEqual(value['state'],'stopped');self.assertEqual(value['guest_connectivity'],'not-tested')
        self.assertEqual(value['members'][0]['nat'],['internet']);self.assertEqual(value['members'][0]['mac'],'52:54:00:00:00:01')
        self.assertEqual(self.manager.starts,0);self.assertEqual(self.host.data,before)
    def test_conflicts_and_missing_nic_are_explicit(self):
        self.host.data['b']['network_adapters'][0]['mac']=self.host.data['a']['network_adapters'][0]['mac']
        self.host.data['c']['network_adapters']=[]
        value=self.runtime.overview(self.row['id'])['network_diagnostics'][0]
        self.assertEqual([m['problem'] for m in value['members']],['mac-conflict','mac-conflict','missing-nic'])
    def test_events_bounded_and_extra_fields_never_forwarded(self):
        native={self.row['switches'][0]['id']:dict(state='helper-ready',status=dict(received=5,sent=-1,events=[dict(time='now',category='malformed-frame',port_index=0,payload='PRIVATE')]*40))}
        value=summarize(self.row,self.host.data,{},native)[0]
        self.assertEqual(len(value['events']),32);self.assertEqual(value['counters']['received'],5);self.assertEqual(value['counters']['sent'],0)
        self.assertNotIn('PRIVATE',json.dumps(value));self.assertNotIn('payload',json.dumps(value))
    def test_network_query_does_not_hold_job_lock(self):
        import threading
        self.finish(self.plan());original=self.manager.status
        def checked(record):
            acquired=threading.Event()
            def lock():
                with self.runtime.guard:acquired.set()
            worker=threading.Thread(target=lock);worker.start()
            self.assertTrue(acquired.wait(1),'network query held global job lock');worker.join(2)
            return original(record)
        with patch.object(self.manager,'status',side_effect=checked):
            value=self.runtime.overview(self.row['id'])
        self.assertEqual(value['network_diagnostics'][0]['state'],'connected')
        self.finish(self.plan(action='stop',force=True))
    def test_report_excludes_network_names_paths_addresses_and_runtime(self):
        self.finish(self.plan());journal=self.runtime._journal(self.row['id'])
        journal['current']['phase']='a 192.0.2.12 2001:db8::42 52:54:00:11:22:33 '+str(self.root/'private-file')
        self.runtime._journal(self.row['id'],journal);text=report(self.runtime,self.row['id']);parsed=json.loads(text)
        for token in ('192.0.2.12','2001:db8::42','52:54:00:11:22:33',str(self.root),'Private LAN','fixture-helper'):
            self.assertNotIn(token,text)
        self.assertEqual(parsed['definition']['switches'][0]['name'],'Сеть 1');self.assertNotIn('pid',parsed['definition']['switches'][0])
        self.finish(self.plan(action='stop',force=True))
    def test_manifest_malformed_or_wrong_protocol_fails_before_probe(self):
        folder=self.root/'helper';folder.mkdir();exe=folder/'QemuM.Switch.exe';exe.write_bytes(b'fixture');manifest=folder/'switch-manifest.json'
        for content in ('null','[]','broken',json.dumps(dict(protocol=2,sha256=hashlib.sha256(b'fixture').hexdigest()))):
            manifest.write_text(content)
            with patch('vm_switch.subprocess.run') as probe,self.assertRaises(SwitchError) as raised:Manager(self.root,exe).verify()
            self.assertEqual(raised.exception.category,'protocol');probe.assert_not_called()
    def test_failed_helper_category_survives_empty_record_cleanup(self):
        with patch.object(self.manager,'start',side_effect=SwitchError('fixture port busy','port-unavailable')):
            self.finish(self.plan(),'failed')
        value=self.runtime.overview(self.row['id'])['network_diagnostics'][0]
        self.assertEqual(value['category'],'port-unavailable');self.assertIn('fixture port busy',value['error']);self.assertEqual(self.host.starts,[])
    def test_permission_failure_clears_intent_with_category(self):
        manager=Manager(self.root);records=[];error=OSError('fixture denied');error.winerror=5
        with patch.object(manager,'verify',return_value='f'*64),patch('vm_switch.subprocess.Popen',side_effect=error),self.assertRaises(SwitchError) as raised:
            manager.start('a'*32,[20000,20001],records.append)
        self.assertEqual(raised.exception.category,'permission');self.assertIsNone(records[-1])


del SwitchFixture
