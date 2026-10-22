"""Paused disposable TCG RAM, CPU and two-disk state; no user guest/media."""
from pathlib import Path
import json
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import argparse
from contextlib import contextmanager
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src/backend'))
import vm
from vm_snapshot_ram import RamSnapshots
from vm_snapshot_service import SnapshotService
from vm_runtime import watch_qmp_process


@contextmanager
def versioned_backend(base,pid,command,variant):
    """Separate packaged Python copies, changed source bytes, same saved schema."""
    folder=base/('application-'+variant)
    shutil.copytree(ROOT/'src/backend',folder,ignore=shutil.ignore_patterns('__pycache__'))
    source=folder/'vm_snapshot_ram.py'
    with source.open('a',encoding='utf-8') as stream:stream.write('\n# Isolated application update variant '+variant+'\n')
    code='''import sys,json,shutil
sys.path.insert(0,sys.argv[1])
import vm_backend,vm
import vm_snapshot_service
from vm_snapshot_service import SnapshotService
from vm_runtime import watch_qmp_process
service=vm_backend.Service(sys.argv[2])
def factory(root,name):
    load=lambda:vm.load(name)
    return SnapshotService(root,name,lambda cmd,args=None:service.query(name,load(),cmd,args,timeout=15),load,shutil.which('qemu-img'),
        processes=lambda:[dict(ProcessId=int(sys.argv[3]),CommandLine=sys.argv[4])],owner=lambda:watch_qmp_process(vm.qmp_port(name,load())))
vm_snapshot_service.manager_service=factory
try:
    for line in sys.stdin:
        request=json.loads(line)
        try:print(json.dumps(dict(ok=True,result=getattr(service,request['command'])(request['args']))),flush=True)
        except Exception as error:print(json.dumps(dict(ok=False,error=str(error))),flush=True)
finally:service.close()
'''
    process=subprocess.Popen([sys.executable,'-c',code,str(folder),str(base),str(pid),subprocess.list2cmdline(command)],
        stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
    def call(command,args):
        process.stdin.write(json.dumps(dict(command=command,args=args))+'\n');process.stdin.flush()
        line=process.stdout.readline()
        if not line:raise AssertionError(process.stderr.read())
        value=json.loads(line)
        if not value['ok']:raise AssertionError(value['error'])
        return value['result']
    try:yield call
    finally:
        process.stdin.close()
        try:process.wait(timeout=5)
        except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=5)
        process.stdout.close();process.stderr.close()


def port():
    with socket.socket() as listener:listener.bind(('127.0.0.1',0));return listener.getsockname()[1]


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cores',type=int,choices=(1,2),default=1)
    parser.add_argument('--ram',type=int,choices=(64,128),default=64)
    parser.add_argument('--vga',choices=('std','none'),default='std')
    parser.add_argument('--cpu',choices=('max','qemu64','qemu32'),default='max')
    parser.add_argument('--incompatible-qemu',type=Path)
    options=parser.parse_args()
    qemu,image=shutil.which('qemu-system-x86_64'),shutil.which('qemu-img')
    if not qemu or not image:raise RuntimeError('QEMU required')
    hidden=getattr(subprocess,'CREATE_NO_WINDOW',0)
    with tempfile.TemporaryDirectory(prefix='ram-real-',dir=ROOT/'build') as temporary:
        base=Path(temporary);root=base/'machines';root.mkdir();qmp,qtest=port(),port();name='ram-test'
        paths=[base/('disk-'+str(index)+'.qcow2') for index in range(2)]
        for path in paths:subprocess.run([image,'create','-f','qcow2',str(path),'8M'],check=True,stdout=subprocess.DEVNULL,creationflags=hidden)
        data=dict(disk_path=str(paths[0]),iso_path='',ram_mb=options.ram,cores=options.cores,vga=options.vga,qmp_port=qmp,launch_profile='tcg',extra_args=['-drive','file='+str(paths[1])+',format=qcow2,if=virtio','-display','none'])
        if options.cpu!='max':data['launch_profile_settings']=dict(accelerator='tcg',cpu=options.cpu,options='')
        (root/(name+'.json')).write_text(json.dumps(data));vm.ROOT=root
        command=[qemu,*vm.build_args(name,data,accelerator='tcg'),'-S','-qtest','tcp:127.0.0.1:'+str(qtest)+',server=on,wait=off']
        process=subprocess.Popen(command,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=hidden)
        request=lambda cmd,args=None:vm.qmp_command(name,qmp,cmd,args,timeout=15)
        try:
            deadline=time.monotonic()+15
            while True:
                try:request('query-status');break
                except (OSError,ValueError):
                    if process.poll() is not None or time.monotonic()>deadline:raise
                    time.sleep(.05)
            service=SnapshotService(root,name,request,lambda:data,image,owner=lambda:watch_qmp_process(qmp),
                processes=lambda:[dict(ProcessId=process.pid,CommandLine=subprocess.list2cmdline(command))])
            ram=RamSnapshots(service)
            with socket.create_connection(('127.0.0.1',qtest),timeout=5) as test:
                reader=test.makefile('rb')
                def write(value):
                    test.sendall(('write 0x100000 0x4 '+value+'\n').encode());assert reader.readline().startswith(b'OK')
                def read():
                    path=base/'memory.bin'
                    request('pmemsave',dict(val=0x100000,size=4,filename=str(path)))
                    value=path.read_bytes();path.unlink();return value
                write('0x11223344');before=read();registers=request('human-monitor-command',{'command-line':'info registers'})
                plan=ram.plan(dict(label='Full TCG state'));result=ram.create(plan)
                assert ram.create(plan)==result
                write('0xAABBCCDD');assert read()!=before
                ram.restore(result['group_id']);assert read()==before
                assert request('human-monitor-command',{'command-line':'info registers'})==registers
                with versioned_backend(base,process.pid,command,'v1') as previous_app:
                    packaged_plan=previous_app('snapshot_plan',dict(name=name,options=dict(kind='ram',label='Before application update')))
                    packaged=previous_app('snapshot_execute',dict(name=name,operation_id=packaged_plan['operation_id']))
                write('0x55667788')
                with versioned_backend(base,process.pid,command,'v2') as updated_app:
                    updated_app('snapshot_ram_restore',dict(name=name,group_id=packaged['group_id'],start_after=False))
                assert read()==before
                group=next(row for row in service.read_catalog(True,data,'paused')['rows'] if row['id']==result['group_id'])
                assert group['kind']=='ram' and group['state']=='ready' and group['ram_size']>0
                original_version=group['fingerprint']['qemu'];group['fingerprint']['qemu']={'changed':'version'}
                catalog=service.store.catalog();target=next(row for row in catalog['groups'] if row['id']==group['id']);target['fingerprint']=group['fingerprint']
                service.store.atomic(service.store.path,catalog)
                try:ram.restore(group['id'])
                except ValueError:pass
                else:raise AssertionError('Different QEMU version accepted for RAM')
                catalog=service.store.catalog();target=next(row for row in catalog['groups'] if row['id']==group['id']);target['fingerprint']['qemu']=original_version
                service.store.atomic(service.store.path,catalog)
                reader.close()
            request('quit');process.wait(timeout=10)
            if options.incompatible_qemu:
                original_executable=command[0];command[0]=str(options.incompatible_qemu.resolve())
                process=subprocess.Popen(command,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=hidden)
                deadline=time.monotonic()+15
                while True:
                    try:request('query-status');break
                    except (OSError,ValueError):
                        if process.poll() is not None or time.monotonic()>deadline:raise
                        time.sleep(.05)
                incompatible=SnapshotService(root,name,request,lambda:data,image,owner=lambda:watch_qmp_process(qmp),
                    processes=lambda:[dict(ProcessId=process.pid,CommandLine=subprocess.list2cmdline(command))])
                try:RamSnapshots(incompatible).restore(result['group_id'])
                except ValueError:pass
                else:raise AssertionError('Different actual QEMU build accepted for RAM')
                assert request('query-status')['status']=='prelaunch'
                request('quit');process.wait(timeout=10);command[0]=original_executable
            process=subprocess.Popen(command,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=hidden)
            deadline=time.monotonic()+15
            while True:
                try:request('query-status');break
                except (OSError,ValueError):
                    if process.poll() is not None or time.monotonic()>deadline:raise
                    time.sleep(.05)
            # Reconstruct the service, as after an application restart/update.
            fresh=SnapshotService(root,name,request,lambda:data,image,owner=lambda:watch_qmp_process(qmp),
                processes=lambda:[dict(ProcessId=process.pid,CommandLine=subprocess.list2cmdline(command))])
            RamSnapshots(fresh).restore(result['group_id'])
            memory=base/'restarted-memory.bin';request('pmemsave',dict(val=0x100000,size=4,filename=str(memory)))
            assert memory.read_bytes()==before
        finally:
            if process.poll() is None:
                try:request('quit')
                except (OSError,ValueError):pass
            try:process.wait(timeout=10)
            except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=10)
        from vm_snapshot_transaction import OfflineTransaction
        fresh.processes=lambda:[]
        transaction=OfflineTransaction(fresh)
        transaction.execute(transaction.plan('offline-delete',result['group_id']))
        assert not any(row['id']==result['group_id'] for row in fresh.store.catalog()['groups'])
        assert all(row['state']=='ready' for row in fresh.read_catalog(False,data)['rows'])
    print('Actual paused TCG full-state save/load: RAM bytes, CPU registers, two disks and version mismatch guard passed.')


if __name__=='__main__':main()
