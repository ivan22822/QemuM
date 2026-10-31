from copy import deepcopy
from contextlib import nullcontext
from pathlib import Path
import tempfile
import unittest
import sys
from unittest.mock import patch,Mock
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'src/backend'))
from vm_stands import create,validate,Store
from vm_stand_plan import upgrade,plan,effective
from vm_network import import_legacy,network_args,probe_multicast
from vm_stand_runtime import Runtime,Conflict
from test_stand_runtime import FakeHost


class MulticastTests(unittest.TestCase):
    def test_probe_rejects_backend_failure_without_any_guest_disks(self):
        row,host=self.fixture();nic=effective(row,host.data,{})['a']['network_adapters'][0]
        child=Mock();child.communicate.return_value=(b'',b'cannot bind');child.returncode=1;child.poll.return_value=1
        with patch('vm_network.subprocess.Popen',return_value=child) as start:
            with self.assertRaisesRegex(ValueError,'MCAST'):probe_multicast('qemu-fixture',[nic])
            args=start.call_args.args[0];self.assertIn('-S',args);self.assertIn('tcg',args);self.assertNotIn('-drive',args)
            child.kill.assert_not_called()
    def test_probe_timeout_reaps_only_owned_child(self):
        import subprocess
        row,host=self.fixture();nic=effective(row,host.data,{})['a']['network_adapters'][0]
        child=Mock();child.communicate.side_effect=subprocess.TimeoutExpired('probe',8);child.poll.return_value=None
        with patch('vm_network.subprocess.Popen',return_value=child):
            with self.assertRaisesRegex(ValueError,'8'):probe_multicast('qemu-fixture',[nic])
        child.kill.assert_called_once();child.wait.assert_called_once()
    def fixture(self):
        row=upgrade(create('Multicast',['a','b','c']));row.update(schema_version=3,multicast=[dict(id='a'*32,address='239.192.50.1',port=23456,localaddr='',members=[dict(vm=n,nic='lan') for n in row['members']])])
        host=FakeHost()
        for n,data in host.data.items():data['network_adapters'].append(dict(id='nat',type='user',model='e1000e',mac='52:54:00:01:00:'+str(ord(n)-96).zfill(2)))
        return row,host
    def test_shared_bus_preserves_nat_and_source(self):
        row,host=self.fixture();before=deepcopy(host.data);row['order'].reverse()
        result=plan(row,{n:host.config(n) for n in row['members']},{})
        self.assertEqual(host.data,before)
        for n in row['members']:
            self.assertIn('mcast=239.192.50.1:23456',str(result['networks'][n]))
            self.assertEqual(result['configs'][n]['network_adapters'][1],before[n]['network_adapters'][1])
        self.assertEqual(result['order'],['c','b','a'])
    def test_bad_groups_fail_closed(self):
        row,host=self.fixture()
        for field,value in [('address','127.0.0.1'),('address','224.0.0.1'),('port',0),('port',True),('localaddr','bad'),('members',[]),('members',[dict(vm='missing',nic='lan'),dict(vm='b',nic='lan')])]:
            bad=deepcopy(row);bad['multicast'][0][field]=value
            with self.subTest(field=field,value=value),self.assertRaises(ValueError):validate(bad)
    def test_tcp_conflict_and_duplicate_group(self):
        row,host=self.fixture();row['links']=[dict(id='b'*32,listen=dict(vm='a',nic='lan'),connect=dict(vm='b',nic='lan'))]
        with self.assertRaises(ValueError):validate(row)
        row['links']=[];row['multicast'].append(deepcopy(row['multicast'][0]))
        with self.assertRaises(ValueError):validate(row)
    def test_duplicate_mac_and_missing_nic(self):
        for case in ('mac','nic','extra'):
            row,host=self.fixture()
            if case=='mac':host.data['b']['network_adapters'][0]['mac']=host.data['a']['network_adapters'][0]['mac']
            if case=='nic':row['multicast'][0]['members'][0]['nic']='absent'
            if case=='extra':host.data['a']['extra_args']=['-netdev','user,id=old']
            with self.subTest(case=case),self.assertRaises(ValueError):effective(row,host.data,{})
    def test_schema_roundtrip_and_older_schema_rejects_hidden_groups(self):
        row,_=self.fixture()
        with tempfile.TemporaryDirectory() as tmp:
            store=Store(tmp);saved=store.save(row,0);self.assertEqual(store.read(row['id']),saved)
        row['schema_version']=2
        with self.assertRaises(ValueError):validate(row)
    def test_manual_legacy_import_roundtrip(self):
        data=dict(network_adapters=[],extra_args=['-netdev','socket,id=lan,mcast=239.192.50.1:23456,localaddr=127.0.0.1','-device','e1000e,netdev=lan,mac=52:54:00:00:00:01'])
        result=import_legacy(data);self.assertEqual(result['adapters'][0]['mode'],'mcast')
        self.assertIn('localaddr=127.0.0.1',str(network_args(dict(network_adapters=result['adapters'],extra_args=[]))))
    def test_runtime_never_waits_for_tcp_listener_and_rechecks_ownership(self):
        row,host=self.fixture();host.listeners=False
        with tempfile.TemporaryDirectory() as tmp:
            store=Store(tmp);row=store.save(row,0);runtime=Runtime(tmp,host)
            try:
                preview=runtime.make_plan(row['id']);self.assertEqual(preview['ports'],{})
                runtime.execute(preview['id'],preview['id']);runtime.jobs[preview['id']]['thread'].join(5)
                self.assertEqual(runtime.status(preview['id'])['state'],'completed')
                runtime.make_plan(row['id'])
                host.active['a']=dict(pid=9999,created=9999)
                with self.assertRaises(Conflict):runtime.make_plan(row['id'])
            finally:runtime.close()
