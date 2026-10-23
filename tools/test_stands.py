"""Explicit --qemu integration: private paused TCG guests, never user VM files."""
import argparse
from contextlib import ExitStack
from copy import deepcopy
import hashlib
import json
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src/backend'))
import vm
from vm_backend import Service
from vm_stands import Store,create
from vm_stand_plan import upgrade

def run():
    exe=shutil.which('qemu-system-x86_64.exe') or shutil.which('qemu-system-x86_64')
    image=shutil.which('qemu-img.exe') or shutil.which('qemu-img')
    if not exe or not image:raise ValueError('QEMU tools must be available in PATH.')
    with tempfile.TemporaryDirectory(dir=ROOT/'build',prefix='stand-integration-') as folder:
        root=Path(folder);(root/'machines').mkdir();ports=[]
        with ExitStack() as leases:
            for index in range(4):
                listener=leases.enter_context(socket.socket());listener.bind(('127.0.0.1',0));ports.append(listener.getsockname()[1])
        names=['stand-probe-a','stand-probe-b','stand-probe-c','stand-probe-d']
        for index,name in enumerate(names):
            disk=root/(name+'.qcow2')
            subprocess.run([image,'create','-f','qcow2',str(disk),'8M'],check=True,capture_output=True)
            data=dict(label=name,disk_path=str(disk),disk_format='qcow2',iso_path='',ram_mb=128,cores=1,
                      launch_profile='tcg',qmp_port=ports[index],extra_args=['-display','none'],vga='none',network_adapters=[
                          dict(id='lan',type='user',model='e1000e',mac='52:54:00:00:00:0'+str(index+1)),
                          dict(id='nat',type='user',model='e1000e',mac='52:54:00:00:01:0'+str(index+1))])
            (root/'machines'/(name+'.json')).write_text(json.dumps(data),encoding='utf-8')
        before={p.name:p.read_bytes() for p in (root/'machines').iterdir()}
        old_root=vm.ROOT;service=Service(root);services=[service]
        original_start=service.start
        service.start=lambda args,**kwargs:original_start(dict(args,paused=True),**kwargs)
        row=upgrade(create('Disposable TCG network',names[:2]))
        row['links']=[dict(id='1'*32,listen=dict(vm=names[0],nic='lan'),connect=dict(vm=names[1],nic='lan'))]
        row=Store(root).save(row,0)
        second=upgrade(create('Second isolated stand',names[2:]))
        second['links']=[dict(id='1'*32,listen=dict(vm=names[2],nic='lan'),connect=dict(vm=names[3],nic='lan'))]
        second=Store(root).save(second,0)
        def finish(plan):
            service.stand_execute(dict(plan_id=plan['id'],confirm=plan['id']))
            job=service.stand_runtime.jobs[plan['id']];job['thread'].join(55)
            status=service.stand_status(dict(operation_id=plan['id']))
            if status['state']!='completed':raise AssertionError(status)
            return status
        try:
            planned=service.stand_plan(dict(id=row['id']));finish(planned)
            second_plan=service.stand_plan(dict(id=second['id']));assert planned['ports']!=second_plan['ports'];finish(second_plan)
            for name in names:
                state=service.query(name,vm.load(name));assert state['status'] in ('paused','prelaunch') and state['running'] is False,state
                network=service.query(name,vm.load(name),'human-monitor-command',{'command-line':'info network'})
                assert 'socket' in network and 'user' in network,network
            assert {p.name:p.read_bytes() for p in (root/'machines').iterdir()}==before
            duplicate=service.stand_execute(dict(plan_id=planned['id'],confirm=planned['id']));assert duplicate['state']=='completed'
            # Close/reopen the backend while its own paused guests remain alive.
            service.close();service=Service(root);services.append(service);original_start=service.start
            service.start=lambda args,**kwargs:original_start(dict(args,paused=True),**kwargs)
            assert service.stand_status(dict(operation_id=planned['id']))['state']=='completed'
            recovery=service.stand_reconcile(dict(id=row['id'],revision=row['revision'],confirm=row['id']))
            assert set(recovery['members'].values())=={'owned-running'} and not recovery['automatic_actions'],recovery
            finish(service.stand_plan(dict(id=row['id'],action='stop',force=True)))
            finish(service.stand_plan(dict(id=second['id'],action='stop',force=True)))
            assert all(record['process'].poll() is not None for owner in services for record in owner.sessions.values())
            single=Store(root).save(upgrade(create('Single guest stand',names[:1])),0)
            finish(service.stand_plan(dict(id=single['id'])));finish(service.stand_plan(dict(id=single['id'],action='stop',force=True)))
            three=upgrade(create('Three guest chain',names[:3]));three['links']=[
                dict(id='2'*32,listen=dict(vm=names[0],nic='lan'),connect=dict(vm=names[1],nic='lan')),
                dict(id='3'*32,listen=dict(vm=names[1],nic='nat'),connect=dict(vm=names[2],nic='lan'))]
            three=Store(root).save(three,0);finish(service.stand_plan(dict(id=three['id'])));finish(service.stand_plan(dict(id=three['id'],action='stop',force=True)))
            # Returning to standalone uses the original user NICs, no socket overlay.
            service.start(dict(name=names[0]))
            deadline=time.monotonic()+10
            while True:
                try:standalone=service.query(names[0],vm.load(names[0]),'human-monitor-command',{'command-line':'info network'});break
                except (ValueError,OSError):
                    if time.monotonic()>deadline:raise
                    time.sleep(.1)
            assert 'socket' not in standalone and 'user' in standalone,standalone
            service.control(dict(name=names[0],action='quit',confirm=names[0]))
            assert {p.name:p.read_bytes() for p in (root/'machines').iterdir()}==before
            print(json.dumps(dict(paused_guests=4,stand_sizes=[1,2,3],concurrent_stands=2,distinct_ports=True,listen_before_connect=True,backend_restart='reconciled without launching guests',config_bytes_unchanged=True,standalone_network='restored',duplicate_rpc='idempotent',force_stop='confirmed',guest_ip_connectivity='not tested')))
        finally:
            service.stand_runtime.close()
            for job in service.stand_runtime.jobs.values():job['thread'].join(15)
            for owner in services:
                for record in owner.sessions.values():
                    process=record['process']
                    if process.poll() is None:process.kill()
                    process.wait(timeout=10)
            service.close();vm.ROOT=old_root

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--qemu',action='store_true',required=True);parser.parse_args();run()
