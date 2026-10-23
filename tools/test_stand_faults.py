"""Opt-in QEMU fault/crash checks on private paused TCG guests only."""
import argparse
from copy import deepcopy
import ctypes
from ctypes import wintypes
import json
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src/backend'))
import vm
from vm_backend import Service
from vm_runtime import ProcessWatcher
from vm_stands import Store,create
from vm_stand_plan import upgrade
from vm_stand_runtime import reserve,Unknown


def worker(root,key):
    root=Path(root).resolve()
    if not root.is_relative_to(ROOT/'build') or not root.name.startswith('stand-faults-'):
        raise ValueError('Private test fixture required')
    service=Service(root);original=service.stand_runtime.host.start
    start=service.start;service.start=lambda args,**kwargs:start(dict(args,paused=True),**kwargs)
    plan=service.stand_plan(dict(id=key))
    def interrupted(name,data,expected):
        result=original(name,data,expected)
        handshake=root/'handshake.tmp';handshake.write_text(json.dumps(dict(operation=plan['id'],identity=result['identity'])))
        handshake.replace(root/'handshake.json')
        threading.Event().wait(120)
        raise RuntimeError('Crash controller did not terminate its helper')
    service.stand_runtime.host.start=interrupted
    service.stand_execute(dict(plan_id=plan['id'],confirm=plan['id']))
    service.stand_runtime.jobs[plan['id']]['thread'].join(130)
    service.close()


def terminate_owned(identity):
    """Terminate only the test-created handle, after creation-time verification."""
    try:probe=ProcessWatcher(identity['pid'])
    except OSError as error:
        if getattr(error,'winerror',None)==87:return
        raise
    try:
        kernel=probe.kernel
        handle=kernel.OpenProcess(0x00101001,False,identity['pid'])
        if not handle:
            if probe.poll() is not None:return
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            created,exited,system,user=(wintypes.FILETIME() for _ in range(4))
            if not kernel.GetProcessTimes(handle,ctypes.byref(created),ctypes.byref(exited),ctypes.byref(system),ctypes.byref(user)):
                raise ctypes.WinError(ctypes.get_last_error())
            if ((created.dwHighDateTime<<32)|created.dwLowDateTime)!=identity['created']:
                raise AssertionError('Refusing to terminate a reused PID')
            kernel.TerminateProcess.argtypes=[wintypes.HANDLE,wintypes.UINT]
            if probe.poll() is None and not kernel.TerminateProcess(handle,0):raise ctypes.WinError(ctypes.get_last_error())
            assert kernel.WaitForSingleObject(handle,10000)==0
        finally:kernel.CloseHandle(handle)
    finally:probe.close()


def run():
    exe=shutil.which('qemu-system-x86_64.exe');image=shutil.which('qemu-img.exe')
    if not exe or not image:raise ValueError('QEMU tools required')
    old=vm.ROOT;services=[];child=None;orphan=None;orphan_stopped=False;blocked=[]
    with tempfile.TemporaryDirectory(dir=ROOT/'build',prefix='stand-faults-') as folder:
        root=Path(folder);(root/'machines').mkdir()
        for index,name in enumerate(('probe-a','probe-b')):
            disk=root/(name+'.qcow2');subprocess.run([image,'create','-f','qcow2',str(disk),'8M'],check=True,capture_output=True)
            with reserve() as port:qmp=port.getsockname()[1]
            data=dict(label=name,disk_path=str(disk),disk_format='qcow2',iso_path='',ram_mb=128,cores=1,launch_profile='tcg',qmp_port=qmp,
                      extra_args=['-display','none'],vga='none',network_adapters=[dict(id='lan',type='user',model='e1000e',mac='52:54:00:00:03:0'+str(index+1))])
            (root/'machines'/(name+'.json')).write_text(json.dumps(data))
        row=upgrade(create('Private fault fixture',['probe-a','probe-b']))
        row['links']=[dict(id='1'*32,listen=dict(vm='probe-a',nic='lan'),connect=dict(vm='probe-b',nic='lan'))]
        for step in row['startup'].values():step['timeout']=5
        row=Store(root).save(row,0);before={p.name:p.read_bytes() for p in (root/'machines').iterdir()}
        try:
            service=Service(root);services.append(service);start=service.start
            service.start=lambda args,**kwargs:start(dict(args,paused=True),**kwargs)
            plan=service.stand_plan(dict(id=row['id']));original=service.stand_runtime.host.start
            def occupied(name,data,expected):
                blocker=reserve(data['network_adapters'][0]['port']);blocker.listen(1);blocked.append(blocker)
                return original(name,data,expected)
            service.stand_runtime.host.start=occupied
            service.stand_execute(dict(plan_id=plan['id'],confirm=plan['id']))
            service.stand_runtime.jobs[plan['id']]['thread'].join(60)
            result=service.stand_status(dict(operation_id=plan['id']))
            assert result['state']=='failed' and 'probe-b' not in service.sessions,result
            print('Occupied port: later member did not start',flush=True)
            for handle in blocked:handle.close()
            blocked.clear()
            for record in service.sessions.values():
                if record['process'].poll() is None:record['process'].kill()
                record['process'].wait(10)
            service.close()
            # A direct connect to a bound but non-listening endpoint must fail.
            data=vm.load('probe-a');data=deepcopy(data)
            with reserve() as port:
                data['network_adapters'][0].update(type='socket',mode='connect',address='127.0.0.1',port=port.getsockname()[1])
                command=[exe]+vm.build_args('probe-a',data,accelerator='tcg')+['-S']
                direct=subprocess.Popen(command,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
                try:
                    try:
                        _,error=direct.communicate(timeout=5)
                        assert direct.returncode!=0
                    except subprocess.TimeoutExpired:
                        # QEMU can remain QMP-ready with a disconnected NIC.
                        # TCP-table verification must reject that condition.
                        from vm_runtime import loopback_connection
                        assert not loopback_connection(port.getsockname()[1],__import__('os').getpid(),direct.pid)
                        direct.kill();_,error=direct.communicate(timeout=10)
                finally:
                    if direct.poll() is None:direct.kill();direct.wait(10)
            print('Unavailable connect endpoint: no established connection, even if QMP is ready',flush=True)
            # A new stand avoids reusing an earlier deliberately failed journal.
            crash=Store(root).save(upgrade(create('Crash fixture',['probe-a','probe-b'])),0)
            child=subprocess.Popen([sys.executable,__file__,'--worker',str(root),crash['id']],stdout=subprocess.DEVNULL,stderr=subprocess.PIPE,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
            handshake=root/'handshake.json';deadline=time.monotonic()+60
            while not handshake.exists() and child.poll() is None and time.monotonic()<deadline:time.sleep(.1)
            assert handshake.exists(),'helper did not create its private guest'
            value=json.loads(handshake.read_text());orphan=value['identity'];child.kill();child.wait(10)
            recovered=Service(root);services.append(recovered)
            assert recovered.stand_status(dict(operation_id=value['operation']))['state']=='interrupted'
            try:recovered.stand_plan(dict(id=crash['id']))
            except Unknown:pass
            else:raise AssertionError('Unconfirmed process creation must block a duplicate start')
            review=recovered.stand_reconcile(dict(id=crash['id'],revision=crash['revision'],confirm=crash['id']))
            assert review['start_blocked'] and not review['automatic_actions']
            terminate_owned(orphan);orphan=None;orphan_stopped=True
            review=recovered.stand_reconcile(dict(id=crash['id'],revision=crash['revision'],confirm=crash['id']))
            assert not review['start_blocked'] and not recovered.sessions
            assert {p.name:p.read_bytes() for p in (root/'machines').iterdir()}==before
            print(json.dumps(dict(occupied_port='blocks later members',connection_refused='bounded refusal/non-readiness',backend_process_killed=True,
                  orphan_creation='unknown intent blocks duplicate',reconciliation='explicit; no autostart',config_bytes_unchanged=True)))
        finally:
            for handle in blocked:handle.close()
            if child and child.poll() is None:child.kill();child.wait(10)
            if child and orphan is None and not orphan_stopped and (root/'handshake.json').exists():orphan=json.loads((root/'handshake.json').read_text())['identity']
            if orphan:terminate_owned(orphan)
            for service in services:
                service.close()
                for job in service.stand_runtime.jobs.values():job['thread'].join(30)
                for record in service.sessions.values():
                    process=record['process']
                    if process.poll() is None:process.kill()
                    process.wait(10)
            vm.ROOT=old


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--qemu',action='store_true');parser.add_argument('--worker',nargs=2);args=parser.parse_args()
    if args.worker:worker(*args.worker)
    elif args.qemu:run()
    else:parser.error('--qemu is required')
