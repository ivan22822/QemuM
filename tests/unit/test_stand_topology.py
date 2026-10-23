import sys
from pathlib import Path
import tempfile
import unittest
from copy import deepcopy
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'src/backend'))
from vm_stands import create,validate,Store
from vm_stand_plan import upgrade,effective,plan

class StandTopologyTests(unittest.TestCase):
    def fixture(self):
        row=upgrade(create('Lab',['a','b']))
        row['links']=[dict(id='1'*32,listen=dict(vm='a',nic='lan'),connect=dict(vm='b',nic='lan'))]
        configs={name:dict(ram_mb=256,cores=1,extra_args=[],network_adapters=[dict(id='lan',type='user',model='e1000e',mac='52:54:00:00:00:'+last),dict(id='nat',type='user',model='e1000e',mac='52:54:00:00:01:'+last)]) for name,last in [('a','01'),('b','02')]}
        return row,configs
    def test_roundtrip_schema_two_and_copy_isolation(self):
        row,configs=self.fixture();before=deepcopy(configs)
        result=plan(row,configs,{'1'*32:23456})
        self.assertEqual(configs,before)
        self.assertEqual(result['configs']['a']['network_adapters'][0]['mode'],'listen')
        self.assertEqual(result['configs']['b']['network_adapters'][0]['mode'],'connect')
        self.assertEqual(result['configs']['a']['network_adapters'][1],configs['a']['network_adapters'][1])
        self.assertIn('127.0.0.1:23456',str(result['networks']))
        with tempfile.TemporaryDirectory() as folder:
            store=Store(folder);saved=store.save(row,0);self.assertEqual(store.read(row['id']),saved)
    def test_order_loops_endpoints_and_timeouts(self):
        row,_=self.fixture()
        for change in ('reverse','duplicate','self','timeout'):
            bad=deepcopy(row)
            if change=='reverse':bad['order'].reverse()
            if change=='duplicate':bad['links'].append(deepcopy(bad['links'][0]))
            if change=='self':bad['links'][0]['connect']['vm']='a'
            if change=='timeout':bad['startup']['a']['timeout']=0
            with self.assertRaises(ValueError):validate(bad)
    def test_missing_nic_mac_and_extra_args(self):
        row,configs=self.fixture()
        for change in ('missing','mac','extra'):
            bad=deepcopy(configs)
            if change=='missing':bad['a']['network_adapters'][0]['id']='other'
            if change=='mac':bad['b']['network_adapters'][0]['mac']=bad['a']['network_adapters'][0]['mac']
            if change=='extra':bad['a']['extra_args']=['-netdev','user,id=old']
            with self.assertRaises(ValueError):effective(row,bad,{'1'*32:23456})
    def test_upgrade_preserves_original(self):
        row=create('Old',['b','a']);before=deepcopy(row);up=upgrade(row)
        self.assertEqual(row,before);self.assertEqual(up['order'],['b','a']);validate(up)
    def test_clipboard_and_usb_devices_do_not_conflict_with_network_overlay(self):
        row,configs=self.fixture();extras=['-device','usb-tablet','-device','virtio-serial-pci','-display','gtk,clipboard=on']
        configs['a']['extra_args']=extras
        result=effective(row,configs,{'1'*32:23456});self.assertEqual(result['a']['extra_args'],extras)
        configs['a']['extra_args']=['-device','virtio-net-pci,netdev=old']
        with self.assertRaises(ValueError):effective(row,configs,{'1'*32:23456})
