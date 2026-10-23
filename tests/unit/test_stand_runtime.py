import sys
from pathlib import Path
from contextlib import nullcontext
from copy import deepcopy
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'src/backend'))
from vm_stands import Store,create
from vm_stand_plan import upgrade
from vm_stand_runtime import Runtime,Conflict,Unknown,fingerprint

class FakeHost:
    def __init__(self):
        self.data={name:dict(ram_mb=128,cores=1,extra_args=[],disk_path='',iso_path='',network_adapters=[dict(id='lan',model='e1000e',type='user',mac='52:54:00:00:00:'+last)]) for name,last in [('a','01'),('b','02'),('c','03'),('d','04')]}
        self.active={};self.starts=[];self.stops=[];self.fail=None;self.callback=None;self.listeners=True
    def config(self,n):return deepcopy(self.data[n])
    def config_hash(self,n):return fingerprint(self.data[n])
    def identity(self,n,data):return deepcopy(self.active.get(n))
    def lock(self,n):return nullcontext()
    def alive(self,i):return i in self.active.values()
    def listener(self,port,i):return self.listeners and self.alive(i)
    def connected(self,port,listener,connector):return self.listeners and self.alive(listener) and self.alive(connector)
    def ports(self):return {2222,4444}
    def preflight(self,n,data,running):pass
    def assert_stopped(self,n):
        if n in self.active:raise Unknown('still running')
    def capacity(self):return dict(host_cores=4,running_ram_mb=len(self.active)*128,running_cores=len(self.active))
    def start(self,n,data,expected):
        if n==self.fail:raise ValueError('injected start failure')
        self.starts.append((n,deepcopy(data)));identity=dict(pid=len(self.starts)+100,created=len(self.starts)*1000)
        self.active[n]=identity
        if self.callback:self.callback(n)
        return dict(pid=identity['pid'],identity=identity)
    def stop(self,n,force):self.stops.append((n,force));self.active.pop(n,None)

class StandRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.host=FakeHost();self.runtime=Runtime(self.temp.name,self.host)
        self.row=upgrade(create('Lab',['a','b']));self.row['links']=[dict(id='1'*32,listen=dict(vm='a',nic='lan'),connect=dict(vm='b',nic='lan'))]
        self.row=Store(self.temp.name).save(self.row,0)
    def tearDown(self):self.runtime.close();self.temp.cleanup()
    def run_plan(self,p):
        self.runtime.execute(p['id'],p['id']);self.runtime.jobs[p['id']]['thread'].join(5)
        result=self.runtime.status(p['id']);self.assertNotIn(result['state'],('queued','running'));return result
    def plan(self,**kwargs):return self.runtime.make_plan(self.row['id'],**kwargs)
    def run_ok(self,p):
        result=self.run_plan(p);self.assertEqual(result['state'],'completed',result);return result
    def test_network_start_idempotency_and_original_data(self):
        before=deepcopy(self.host.data);p=self.plan();result=self.run_plan(p)
        self.assertEqual(result['state'],'completed',result)
        self.assertEqual([n for n,d in self.host.starts],['a','b'])
        self.assertEqual(self.host.starts[0][1]['network_adapters'][0]['mode'],'listen')
        self.runtime.execute(p['id'],p['id']);self.assertEqual(len(self.host.starts),2)
        self.assertEqual(self.host.data,before)
        self.assertEqual(self.runtime.overview(self.row['id'])['state'],'running')
    def test_stale_plan_and_confirmation(self):
        p=self.plan()
        with self.assertRaises(ValueError):self.runtime.execute(p['id'],'wrong')
        self.host.data['a']['ram_mb']=129
        result=self.run_plan(p);self.assertEqual(result['category'],'conflict');self.assertEqual(self.host.starts,[])
    def test_failure_middle_then_explicit_resume_only_missing(self):
        self.host.fail='b';first=self.run_plan(self.plan());self.assertEqual(first['state'],'failed')
        self.assertIn('a',self.host.active);self.assertEqual(self.host.stops,[])
        self.host.fail=None;second=self.run_ok(self.plan());self.assertEqual(second['state'],'completed',second)
        self.assertEqual([n for n,d in self.host.starts],['a','b'])
    def test_cancel_preserves_new_guest_and_reconciles_identity(self):
        p=self.plan();self.host.callback=lambda n:self.runtime.jobs[p['id']]['cancel'].set() if n=='a' else None
        result=self.run_plan(p);self.assertEqual(result['state'],'cancelled');self.assertIn('a',self.host.active)
        self.host.callback=None;result=self.run_ok(self.plan());self.assertEqual(result['state'],'completed',result)
        self.assertEqual([n for n,d in self.host.starts],['a','b'])
    def test_stop_reverse_and_force_is_explicit(self):
        self.assertEqual(self.run_plan(self.plan())['state'],'completed')
        stopped=self.run_plan(self.plan(action='stop'));self.assertEqual(stopped['state'],'completed',stopped)
        self.assertEqual(self.host.stops,[('b',False),('a',False)])
        self.run_ok(self.plan());self.run_plan(self.plan(action='stop',force=True))
        self.assertEqual(self.host.stops[-2:],[('b',True),('a',True)])
    def test_preexisting_unlinked_guest_not_stopped_without_opt_in(self):
        self.row['links']=[];self.row=Store(self.temp.name).save(self.row,self.row['revision'])
        self.host.active['a']=dict(pid=42,created=99)
        self.run_ok(self.plan());self.run_plan(self.plan(action='stop'))
        self.assertEqual(self.host.stops,[('b',False)]);self.assertIn('a',self.host.active)
        self.run_plan(self.plan(action='stop',include_existing=True));self.assertNotIn('a',self.host.active)
    def test_preexisting_foreign_network_is_rejected(self):
        self.host.active['a']=dict(pid=42,created=99)
        with self.assertRaises(Conflict):self.plan()
    def test_pid_reuse_and_changed_state_prevent_execution(self):
        self.run_ok(self.plan());p=self.plan(action='stop')
        self.host.active['b']['created']+=1
        result=self.run_plan(p);self.assertEqual(result['category'],'conflict');self.assertEqual(self.host.stops,[])
    def test_timeout_leaves_listener_alive_and_no_connect_start(self):
        self.row['startup']['b']['timeout']=1;self.row=Store(self.temp.name).save(self.row,self.row['revision']);self.host.listeners=False
        result=self.run_plan(self.plan());self.assertEqual(result['category'],'timeout',result)
        self.assertEqual([n for n,d in self.host.starts],['a']);self.assertEqual(self.host.stops,[])
    def test_qmp_ready_does_not_hide_failed_socket_connection_or_skip_retry_check(self):
        self.row['startup']['b']['timeout']=1;self.row=Store(self.temp.name).save(self.row,self.row['revision'])
        self.host.connected=lambda *args:False
        first=self.run_plan(self.plan());self.assertEqual(first['category'],'timeout');self.assertEqual(len(self.host.starts),2)
        retry=self.run_plan(self.plan());self.assertEqual(retry['category'],'timeout');self.assertEqual(len(self.host.starts),2)
        self.host.connected=lambda *args:True;self.run_ok(self.plan());self.assertEqual(len(self.host.starts),2)
    def test_windows_tcp_table_requires_both_established_ends(self):
        import os,socket
        from vm_runtime import loopback_connection
        with socket.socket() as listener:
            listener.bind(('127.0.0.1',0));port=listener.getsockname()[1]
            self.assertFalse(loopback_connection(port,os.getpid(),os.getpid()));listener.listen(1)
            with socket.create_connection(('127.0.0.1',port)) as connector:
                accepted,_=listener.accept()
                with accepted:
                    self.assertTrue(loopback_connection(port,os.getpid(),os.getpid()))
                    self.assertFalse(loopback_connection(port,os.getpid()+100000,os.getpid()))
    def test_two_stands_allocate_distinct_ports(self):
        first=self.plan();other=deepcopy(self.row);other.update(id='2'*32,revision=0,members=['c','d'],order=['c','d'],startup={n:dict(delay=0,timeout=5,ready='qmp') for n in ['c','d']})
        other['links'][0]['listen']['vm']='c';other['links'][0]['connect']['vm']='d';Store(self.temp.name).save(other,0)
        second=self.runtime.make_plan(other['id']);self.assertNotEqual(first['ports'],second['ports'])
        self.assertEqual(self.run_plan(first)['state'],'completed');self.assertEqual(self.run_plan(second)['state'],'completed')
    def test_revision_change_and_expired_plan(self):
        p=self.plan();self.row['description']='changed';self.row=Store(self.temp.name).save(self.row,self.row['revision'])
        self.assertEqual(self.run_plan(p)['category'],'conflict')
        p=self.plan();self.runtime.plans[p['id']]['expires']=0
        with self.assertRaises(Conflict):self.runtime.execute(p['id'],p['id'])
    def test_pending_unidentified_start_blocks_repetition(self):
        self.runtime._journal(self.row['id'],dict(owned={},ports={},pending={'name':'a'}))
        with self.assertRaises(Unknown):self.plan()
    def test_discard_releases_plan_and_never_starts(self):
        p=self.plan();self.runtime.discard(p['id'])
        with self.assertRaises(Conflict):self.runtime.execute(p['id'],p['id'])
        self.assertEqual(self.host.starts,[])
    def test_shutdown_timeout_never_escalates(self):
        self.row['startup']['b']['timeout']=1;self.row=Store(self.temp.name).save(self.row,self.row['revision'])
        self.run_ok(self.plan());self.host.stop=lambda n,force:self.host.stops.append((n,force))
        result=self.run_plan(self.plan(action='stop'))
        self.assertEqual(result['category'],'timeout');self.assertEqual(self.host.stops,[('b',False)])
        self.assertEqual(set(self.host.active),{'a','b'})
    def test_delay_is_cancellable_before_process_creation(self):
        self.row['startup']['a']['delay']=5;self.row=Store(self.temp.name).save(self.row,self.row['revision'])
        p=self.plan();self.runtime.execute(p['id'],p['id']);self.runtime.cancel(p['id'])
        self.runtime.jobs[p['id']]['thread'].join(3)
        self.assertEqual(self.runtime.status(p['id'])['state'],'cancelled');self.assertEqual(self.host.starts,[])
    def test_tcp_readiness_is_optional_and_limited_to_loopback(self):
        import socket
        with socket.socket() as server:
            server.bind(('127.0.0.1',0));server.listen(1)
            self.row['startup']['b'].update(ready='tcp',tcp_port=server.getsockname()[1]);self.row=Store(self.temp.name).save(self.row,self.row['revision'])
            result=self.run_ok(self.plan());self.assertEqual(result['state'],'completed',result)
            self.assertEqual(result['steps']['b'],'tcp-ready')
    def test_running_link_cannot_adopt_changed_configuration(self):
        self.run_ok(self.plan());self.host.data['a']['network_adapters'][0]['model']='virtio-net-pci'
        with self.assertRaises(Conflict):self.plan()
    def test_resource_warning_is_an_estimate_not_a_hidden_refusal(self):
        self.host.capacity=lambda:dict(host_cores=1,available_ram_mb=64,running_ram_mb=128,running_cores=1)
        p=self.plan();self.assertIn('превышает',p['warning']);self.assertEqual(p['requested_ram_mb'],256)
    def test_only_participant_port_collisions_block_the_plan(self):
        from vm_stand_runtime import Host
        import vm
        configs={'a':dict(qmp_port=23000,network_adapters=[dict(type='user',hostfwd=['tcp:127.0.0.1:23001-:22'])]),
                 'b':dict(qmp_port=23001,network_adapters=[])}
        with patch.object(vm,'entries',return_value=list(configs)),patch.object(vm,'load',side_effect=lambda n:configs[n]):
            host=Host(None);self.assertIn(23001,host.ports())
            with self.assertRaises(Conflict):host.validate_ports(['a','b'])
    def test_actual_vm_mutex_blocks_stand_before_any_start(self):
        from vm_runtime import vm_lock
        self.host.lock=lambda name:vm_lock(Path(self.temp.name),name)
        p=self.plan()
        # Windows mutex ownership is thread-specific: this simulates a snapshot
        # operation holding the same VM lock while the stand worker attempts it.
        with vm_lock(Path(self.temp.name),'a'):
            result=self.run_plan(p)
        self.assertEqual(result['category'],'conflict');self.assertEqual(self.host.starts,[])
    def test_host_preflight_retains_snapshot_pending_guard(self):
        from vm_stand_runtime import Host
        import vm_snapshot_store
        with patch('vm_backend.normalized'),patch('vm.checked_qmp_port'),patch.object(vm_snapshot_store,'assert_can_start',side_effect=ValueError('snapshot recovery pending')) as guard:
            with self.assertRaisesRegex(ValueError,'snapshot recovery pending'):Host(None).preflight('a',self.host.data['a'],False)
        guard.assert_called_once();self.assertEqual(guard.call_args.args[1],'a');self.assertEqual(self.host.starts,[])
