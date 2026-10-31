from copy import deepcopy
from pathlib import Path
import json,sys,tempfile,time,unittest,uuid
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'src/backend'))
from vm_stands import Store,create,validate
from vm_stand_plan import upgrade,effective,validation_ports
from vm_stand_runtime import Runtime,Conflict
from vm_stand_lifecycle import duplicate,reconcile
from test_stand_runtime import FakeHost

class FakeSwitch:
    def __init__(self,host):self.host=host;self.running={};self.owners={};self.starts=0
    def verify(self):return 'f'*64
    def check(self,record):return bool(self.running.get(record['run_id']))
    def status(self,record):
        if not self.check(record):raise ValueError('helper unavailable')
        return dict(protocol=1,ports=[dict(port=p,connected=(record['run_id'],i) in self.owners) for i,p in enumerate(record['ports'])])
    def start(self,network,ports,persist):
        self.starts+=1;run=uuid.uuid4().hex;record=dict(protocol=1,network_id=network,run_id=run,pipe='qemum-switch-'+run,pid=1000+self.starts,created=1,ports=ports,executable='fixture-helper',sha256='f'*64)
        self.running[run]=True;persist(record);return record
    def authorize(self,record,index,identity):self.status(record);self.owners[record['run_id'],index]=identity
    def stop(self,record):
        if any(identity in self.host.active.values() for (run,_),identity in self.owners.items() if run==record['run_id']):raise ValueError('guests alive')
        self.running[record['run_id']]=False

class StandSwitchTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.root=Path(self.tmp.name).resolve();self.host=FakeHost();self.manager=FakeSwitch(self.host);self.host.switch_manager=lambda:self.manager
        self.row=upgrade(create('Switch fixture',['a','b','c']));self.row.update(schema_version=4,multicast=[],switches=[dict(id='a'*32,name='Private LAN',members=[dict(vm=n,nic='lan') for n in self.row['members']])])
        self.row=Store(self.root).save(self.row,0);self.runtime=Runtime(self.root,self.host)
    def tearDown(self):self.runtime.close();self.tmp.cleanup()
    def finish(self,plan,state='completed'):
        self.runtime.execute(plan['id'],plan['id']);self.runtime.jobs[plan['id']]['thread'].join(5)
        status=self.runtime.status(plan['id']);self.assertEqual(status['state'],state,status);return status
    def plan(self,**kw):return self.runtime.make_plan(self.row['id'],**kw)
    def test_schema_roundtrip_and_old_definitions(self):
        self.assertEqual(Store(self.root).read(self.row['id']),self.row)
        for version in (1,2,3):
            row=create('Legacy',['a','b'])
            if version>=2:row=upgrade(row)
            if version==3:row.update(schema_version=3,multicast=[])
            before=deepcopy(row);validate(row);self.assertEqual(row,before)
        bad=deepcopy(self.row);bad['schema_version']=99
        with self.assertRaises(ValueError):validate(bad)
    def test_topology_conflicts_names_members_and_ids(self):
        for case in ('id','empty','missing','duplicate','name','tcp'):
            bad=deepcopy(self.row);group=bad['switches'][0]
            if case=='id':group['id']='bad'
            if case=='empty':group['members']=[]
            if case=='missing':group['members'][0]['vm']='missing'
            if case=='duplicate':group['members'][1]=group['members'][0]
            if case=='name':group['name']='bad\nname'
            if case=='tcp':bad['links']=[dict(id='b'*32,listen=dict(vm='a',nic='lan'),connect=dict(vm='b',nic='lan'))]
            with self.subTest(case=case),self.assertRaises(ValueError):validate(bad)
    def test_overlay_preserves_source_nat_and_device_options(self):
        for n,data in self.host.data.items():
            data['network_adapters'][0]['device_options']='bootindex=1';data['network_adapters'].append(dict(id='nat',type='user',model='e1000e',mac='52:54:00:01:00:'+str(ord(n)).zfill(2)))
        before=deepcopy(self.host.data);result=effective(self.row,self.host.data,validation_ports(self.row))
        self.assertEqual(before,self.host.data)
        for name in self.row['members']:
            self.assertEqual(result[name]['network_adapters'][0]['mode'],'connect');self.assertEqual(result[name]['network_adapters'][0]['device_options'],'bootindex=1');self.assertEqual(result[name]['network_adapters'][1],before[name]['network_adapters'][1])
    def test_missing_nic_duplicate_mac_and_extra_args_rejected(self):
        for case in ('nic','mac','other-nic','extra'):
            data=deepcopy(self.host.data)
            if case=='nic':data['a']['network_adapters']=[]
            if case=='mac':data['b']['network_adapters'][0]['mac']=data['a']['network_adapters'][0]['mac']
            if case=='other-nic':data['a']['network_adapters'].append(dict(data['a']['network_adapters'][0],id='nat'))
            if case=='extra':data['a']['extra_args']=['-netdev','user,id=old']
            with self.subTest(case=case),self.assertRaises(ValueError):effective(self.row,data,validation_ports(self.row))
    def test_preview_cancel_and_expiry_release_ports(self):
        p=self.plan();self.assertEqual(self.manager.starts,0);self.assertEqual(len(p['ports']),3)
        handles=list(self.runtime.plans[p['id']]['leases'].values());self.runtime.discard(p['id']);self.assertTrue(all(h.fileno()==-1 for h in handles))
        p=self.plan();handles=list(self.runtime.plans[p['id']]['leases'].values());self.runtime.plans[p['id']]['expires']=time.monotonic()-1;self.runtime._cleanup();self.assertTrue(all(h.fileno()==-1 for h in handles))
    def test_start_stop_and_duplicate_rpc(self):
        original=deepcopy(self.host.data);p=self.plan();self.finish(p);self.assertEqual(self.manager.starts,1)
        self.runtime.execute(p['id'],p['id']);self.assertEqual(len(self.host.starts),3)
        self.finish(self.plan(action='stop',force=True));self.assertFalse(any(self.manager.running.values()));self.assertEqual(original,self.host.data)
    def test_changed_config_prevents_helper_and_guest_start(self):
        p=self.plan();self.host.data['a']['ram_mb']+=1;self.finish(p,'failed');self.assertEqual(self.manager.starts,0);self.assertFalse(self.host.starts)
    def test_partial_failure_preserves_network_and_resume(self):
        self.host.fail='b';self.finish(self.plan(),'failed');self.assertTrue(any(self.manager.running.values()));self.assertIn('a',self.host.active)
        self.host.fail=None;self.finish(self.plan());self.assertEqual(self.manager.starts,1);self.finish(self.plan(action='stop',force=True))
    def test_failure_before_first_guest_cleans_empty_network(self):
        self.host.fail='a';self.finish(self.plan(),'failed');self.assertFalse(any(self.manager.running.values()));self.assertFalse(self.runtime._journal(self.row['id'])['switches'])
    def test_close_reopen_reconcile_and_foreign_owner(self):
        self.finish(self.plan());self.runtime.close();self.runtime=Runtime(self.root,self.host)
        status=reconcile(self.runtime,self.row['id'],self.row['revision'],self.row['id']);self.assertFalse(status['start_blocked'])
        self.host.active['a']=dict(pid=9999,created=1)
        with self.assertRaises(Conflict):self.plan()
    def test_dead_helper_not_restarted_with_live_guests(self):
        self.finish(self.plan());self.manager.running={run:False for run in self.manager.running}
        with self.assertRaises(ValueError):self.plan()
        self.assertEqual(self.manager.starts,1);self.finish(self.plan(action='stop',force=True))
    def test_copy_assigns_network_ids_and_revision_conflicts(self):
        copied=duplicate(self.runtime,self.row['id'],self.row['revision'],'Copied');self.assertNotEqual(copied['switches'][0]['id'],self.row['switches'][0]['id']);self.assertFalse(self.runtime._journal(copied['id']).get('switches'))
        with self.assertRaises(ValueError):Store(self.root).save(self.row,0)
    def test_uncertain_helper_journal_blocks_without_writes(self):
        path=self.root/'stand-runtime'/(self.row['id']+'.json');path.parent.mkdir(exist_ok=True);path.write_text(json.dumps(dict(switches={'bad':{}})),encoding='utf-8');before=path.read_bytes()
        with self.assertRaises(ValueError):self.plan()
        self.assertEqual(path.read_bytes(),before)
    def test_parallel_independent_operations_and_shared_vm_conflict(self):
        left=deepcopy(self.row);left['members']=left['order']=['a','b'];left['startup']={n:left['startup'][n] for n in left['members']};left['switches'][0]['members']=left['switches'][0]['members'][:2];left['startup']['a']['delay']=1;left=Store(self.root).save(left,left['revision'])
        right=upgrade(create('Right',['c','d']));right.update(schema_version=4,multicast=[],switches=[dict(id='b'*32,name='Other LAN',members=[dict(vm=n,nic='lan') for n in right['members']])]);right=Store(self.root).save(right,0)
        competing=deepcopy(left);competing.update(id=uuid.uuid4().hex,revision=0);competing=Store(self.root).save(competing,0)
        first=self.runtime.make_plan(left['id']);second=self.runtime.make_plan(right['id']);third=self.runtime.make_plan(competing['id'])
        self.runtime.execute(first['id'],first['id'])
        with self.assertRaises(Conflict):self.runtime.execute(third['id'],third['id'])
        self.runtime.discard(third['id']);self.finish(second)
        self.runtime.jobs[first['id']]['thread'].join(5);self.assertEqual(self.runtime.status(first['id'])['state'],'completed');self.assertEqual(self.manager.starts,2)
        self.finish(self.runtime.make_plan(left['id'],action='stop',force=True));self.finish(self.runtime.make_plan(right['id'],action='stop',force=True))
    def test_cancel_after_first_guest_keeps_live_network(self):
        import threading
        def cancel(name):
            if name=='a':self.runtime.jobs[plan['id']]['cancel'].set()
        self.host.callback=cancel;plan=self.plan();self.finish(plan,'cancelled')
        self.assertIn('a',self.host.active);self.assertNotIn('b',self.host.active);self.assertTrue(any(self.manager.running.values()))
        self.host.callback=None;self.finish(self.plan(action='stop',force=True));self.assertFalse(any(self.manager.running.values()))
    def test_two_networks_edit_copy_delete_without_vm_changes(self):
        row=deepcopy(self.row);row['members']=row['order']=['a','b','c','d'];row['startup']['d']=deepcopy(row['startup']['c'])
        row['switches'][0]['members']=[dict(vm=n,nic='lan') for n in ('a','b')]
        row['switches'].append(dict(id='b'*32,name='Second LAN',members=[dict(vm=n,nic='lan') for n in ('c','d')]))
        row=Store(self.root).save(row,row['revision']);before=deepcopy(self.host.data)
        self.finish(self.runtime.make_plan(row['id']));self.assertEqual(self.manager.starts,2)
        self.finish(self.runtime.make_plan(row['id'],action='stop',force=True))
        copy=duplicate(self.runtime,row['id'],row['revision'],'Independent copy')
        self.assertFalse(set(g['id'] for g in copy['switches']) & set(g['id'] for g in row['switches']))
        row['switches'][0]['name']='Renamed';row['switches'].pop();row=Store(self.root).save(row,row['revision'])
        self.assertEqual(Store(self.root).read(row['id'])['switches'][0]['name'],'Renamed');self.assertEqual(before,self.host.data)
    def test_connected_vm_is_rechecked_on_repeat_launch(self):
        from unittest.mock import patch
        self.finish(self.plan());status=self.manager.status
        def disconnected(record):
            result=status(record)
            for port in result['ports']:port['connected']=False
            return result
        with patch.object(self.manager,'status',side_effect=disconnected),patch.object(self.runtime,'_wait',side_effect=TimeoutError('disconnected')):
            self.finish(self.plan(),'failed')
        self.assertEqual(self.manager.starts,1);self.finish(self.plan(action='stop',force=True))
    def test_failed_create_process_clears_intent_and_pending_check_fails_closed(self):
        from unittest.mock import patch
        from vm_switch import Manager,SwitchError
        manager=Manager(self.root);records=[]
        with patch.object(manager,'verify',return_value='f'*64),patch('vm_switch.subprocess.Popen',side_effect=OSError('fixture denied')):
            with self.assertRaises(SwitchError):manager.start('a'*32,[20000,20001],records.append)
        self.assertTrue(records[0]['pending']);self.assertIsNone(records[-1])
        with self.assertRaises(SwitchError):manager.check(records[0])
