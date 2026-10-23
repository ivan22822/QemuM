import sys
from pathlib import Path
from copy import deepcopy
import tempfile
import unittest
import threading

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'src/backend'))
from test_stand_runtime import FakeHost
from vm_stands import Store,create
from vm_stand_plan import upgrade
from vm_stand_runtime import Runtime,Conflict


class StandMatrixTests(unittest.TestCase):
    def fixture(self,size):
        host=FakeHost();base=host.data['a'];host.data={}
        names=['node'+str(index) for index in range(size)]
        for index,name in enumerate(names):
            data=deepcopy(base);data['network_adapters']=[dict(id=role,type='user',model='e1000e',mac='52:54:00:'+format(index,'02x')+':00:'+last) for role,last in [('in','01'),('out','02')]];host.data[name]=data
        row=upgrade(create('Matrix '+str(size),names))
        row['links']=[dict(id=format(index+1,'032x'),listen=dict(vm=names[index],nic='out'),connect=dict(vm=names[index+1],nic='in')) for index in range(size-1)]
        return host,row
    def finish(self,runtime,plan):
        runtime.execute(plan['id'],plan['id']);runtime.jobs[plan['id']]['thread'].join(30)
        result=runtime.status(plan['id']);self.assertNotIn(result['state'],('running','queued'));return result
    def test_supported_sizes_one_two_three_and_sixty_four_with_modeled_processes(self):
        for size in (1,2,3,64):
            with self.subTest(size=size),tempfile.TemporaryDirectory() as folder:
                host,row=self.fixture(size);row=Store(folder).save(row,0);runtime=Runtime(folder,host)
                try:
                    result=self.finish(runtime,runtime.make_plan(row['id']));self.assertEqual(result['state'],'completed',result)
                    self.assertEqual([name for name,data in host.starts],row['order'])
                    ports=runtime._journal(row['id'])['ports'];self.assertEqual(len(set(ports.values())),size-1)
                    result=self.finish(runtime,runtime.make_plan(row['id'],action='stop'));self.assertEqual(result['state'],'completed',result)
                    self.assertEqual([name for name,force in host.stops],list(reversed(row['order'])))
                finally:runtime.close()
    def test_first_middle_last_failure_keeps_confirmed_guests(self):
        for failed in (0,1,2):
            with self.subTest(failed=failed),tempfile.TemporaryDirectory() as folder:
                host,row=self.fixture(3);row=Store(folder).save(row,0);runtime=Runtime(folder,host);host.fail=row['order'][failed]
                try:
                    result=self.finish(runtime,runtime.make_plan(row['id']));self.assertEqual(result['state'],'failed');self.assertEqual(len(host.starts),failed);self.assertEqual(host.stops,[])
                    host.fail=None;result=self.finish(runtime,runtime.make_plan(row['id']));self.assertEqual(result['state'],'completed',result)
                    self.assertEqual(len(host.starts),3)
                finally:runtime.close()
    def test_cancel_after_each_creation_and_resume_without_duplicate(self):
        for cancelled in (0,1,2):
            with self.subTest(cancelled=cancelled),tempfile.TemporaryDirectory() as folder:
                host,row=self.fixture(3);row=Store(folder).save(row,0);runtime=Runtime(folder,host)
                try:
                    plan=runtime.make_plan(row['id']);host.callback=lambda n:runtime.jobs[plan['id']]['cancel'].set() if n==row['order'][cancelled] else None
                    result=self.finish(runtime,plan);self.assertEqual(result['state'],'cancelled',result);self.assertEqual(host.stops,[])
                    host.callback=None;result=self.finish(runtime,runtime.make_plan(row['id']));self.assertEqual(result['state'],'completed',result);self.assertEqual(len(host.starts),3)
                finally:runtime.close()
