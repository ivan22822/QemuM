import json
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
from contextlib import ExitStack

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'src/backend'))
import vm,vm_backend,vm_profiles
from vm_backend import Service
from vm_stands import Store,create
from vm_stand_plan import upgrade


class StandPlanChecksTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=Path(self.temp.name).resolve();self.old=vm.ROOT
        (self.root/'machines').mkdir();self.disk=self.root/'private.qcow2';self.disk.write_bytes(b'Private fixture; never boot')
        self.exe=self.root/'qemu.exe';self.exe.write_bytes(b'Fixture; never execute')
        for index in range(20):
            data=dict(label='Fixture',disk_path=str(self.root/('disk'+str(index)+'.qcow2')),disk_format='qcow2',iso_path='',ram_mb=128,cores=1,launch_profile='tcg',qmp_port=24000+index,extra_args=[],network_adapters=[])
            Path(data['disk_path']).write_bytes(b'Private fixture; never boot')
            (self.root/'machines'/('vm'+str(index)+'.json')).write_text(json.dumps(data))
        self.row=Store(self.root).save(upgrade(create('Plan',['vm0','vm1'])),0)
        self.service=Service(self.root);self.host=self.service.stand_runtime.host
        self.patches=ExitStack()
        self.inventory=self.patches.enter_context(patch.object(vm_backend,'qemu_processes',return_value=[]))
        self.query=self.patches.enter_context(patch.object(self.service,'query',side_effect=ConnectionRefusedError()))
        self.choose=self.patches.enter_context(patch.object(vm_profiles,'choose',return_value=('tcg',{'state':'ok'})))
        self.patches.enter_context(patch.object(vm,'qemu_executable',return_value=str(self.exe)))
        self.patches.enter_context(patch.object(vm,'qmp_bind_error',return_value=None))
        self.patches.enter_context(patch.object(vm,'build_args',return_value=[]))
    def tearDown(self):
        self.service.close();self.patches.close();vm.ROOT=self.old;self.temp.cleanup()
    def plan(self):return self.service.stand_plan(dict(id=self.row['id']))
    def test_one_inventory_and_probe_without_polling_unrelated_library(self):
        result=self.plan()
        self.assertEqual(self.inventory.call_count,1);self.assertEqual(self.query.call_count,2);self.assertEqual(self.choose.call_count,1)
        self.assertEqual(result['resources']['running_ram_mb'],0);self.assertTrue(result['resources']['estimated']);self.assertEqual(self.service.sessions,{})
    def test_next_preview_and_execution_checks_do_not_reuse_old_inventory(self):
        self.plan();self.inventory.return_value=[dict(ProcessId=7,CommandLine='qemu-system-x86_64 -name vm0')]
        with self.assertRaises(ValueError):self.host.identity('vm0',vm.load('vm0'))
        self.assertEqual(self.inventory.call_count,2)
        self.inventory.return_value=[];self.plan();self.assertEqual(self.inventory.call_count,3);self.assertEqual(self.choose.call_count,2)
    def test_different_cpu_settings_need_separate_probes(self):
        path=vm.config_path('vm1');data=json.loads(path.read_text());data['launch_profile_settings']=dict(accelerator='tcg',options='',cpu='qemu64');path.write_text(json.dumps(data))
        self.plan();self.assertEqual(self.choose.call_count,2)
    def test_parallel_execution_thread_never_sees_preview_snapshot(self):
        errors=[]
        def verify():
            try:self.host.identity('vm0',vm.load('vm0'))
            except Exception as error:errors.append(error)
        with self.host.planning():
            self.host.identity('vm0',vm.load('vm0'))
            thread=threading.Thread(target=verify);thread.start();thread.join(3);self.assertFalse(thread.is_alive())
        self.assertFalse(errors);self.assertEqual(self.inventory.call_count,2)
    def test_unknown_inventory_cannot_produce_a_successful_plan(self):
        self.inventory.side_effect=ValueError('Cannot inspect all QEMU processes')
        with self.assertRaises(ValueError):self.plan()
        self.assertEqual(self.service.sessions,{})
    def test_resource_estimate_counts_uncertain_processes_conservatively(self):
        self.inventory.return_value=[dict(ProcessId=7,CommandLine='qemu-system-x86_64 -name vm9')]
        result=self.plan();self.assertEqual(result['resources']['running_ram_mb'],128);self.assertEqual(self.query.call_count,2)
