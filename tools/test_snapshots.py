"""Real snapshots only on private, paused TCG test media. No user VM access."""
from pathlib import Path
import json
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import uuid

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src/backend'))
import vm
from vm_snapshot_service import SnapshotService, identity
from vm_snapshot_store import Store, assert_can_start
from vm_runtime import watch_qmp_process
from vm_snapshots import journalled_apply, image_command
from vm_snapshot_lease import ImageLease
from vm_snapshot_offline import OfflinePreparation


def main():
    qemu,image=shutil.which('qemu-system-x86_64'),shutil.which('qemu-img')
    if not qemu or not image:raise RuntimeError('QEMU tools required')
    hidden=getattr(subprocess,'CREATE_NO_WINDOW',0)
    with tempfile.TemporaryDirectory(prefix='snapshots-real-',dir=ROOT/'build') as temporary:
        root=Path(temporary)/'machines';root.mkdir();name='snapshot-'+uuid.uuid4().hex[:10]
        paths=[Path(temporary)/('disk-'+str(i)+'.qcow2') for i in range(2)]
        for path in paths:subprocess.run([image,'create','-f','qcow2',str(path),'32M'],check=True,stdout=subprocess.DEVNULL,creationflags=hidden)
        before=paths[0].read_bytes()
        clone=Path(temporary)/'private-clone.qcow2'
        with ImageLease(paths[0]) as lease:
            lease.copy_to(clone)
            denied=subprocess.run([image,'snapshot','-c','must-not-create',str(paths[0])],stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=hidden,timeout=15)
            assert denied.returncode!=0
            subprocess.run([image,'snapshot','-c','private-copy-only',str(clone)],check=True,stdout=subprocess.DEVNULL,creationflags=hidden,timeout=15)
        assert paths[0].read_bytes()==before
        with socket.socket() as listener:listener.bind(('127.0.0.1',0));port=listener.getsockname()[1]
        data=dict(disk_path=str(paths[0]),iso_path='',ram_mb=128,cores=1,qmp_port=port,launch_profile='tcg',extra_args=['-drive','file='+str(paths[1])+',format=qcow2,if=virtio','-display','none','-S'])
        (root/(name+'.json')).write_text(json.dumps(data))
        process=subprocess.Popen([qemu,*vm.build_args(name,data,accelerator='tcg')],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=hidden)
        request=lambda command,args=None:vm.qmp_command(name,port,command,args,timeout=15)
        try:
            deadline=time.monotonic()+15
            while True:
                try:request('query-status');break
                except (OSError,ValueError):
                    if process.poll() is not None or time.monotonic()>deadline:raise
                    time.sleep(.1)
            service=SnapshotService(root,name,request,lambda:data,image,owner=lambda:watch_qmp_process(port),
                processes=lambda:[dict(ProcessId=process.pid,CommandLine='qemu-system-x86_64 -name '+name)])
            plan=service.plan_create(dict(label='Before update'))
            result=service.create(plan)
            assert service.create(plan)==result
            catalog=service.read_catalog(True,data,request('query-status')['status'])
            assert catalog['rows'][0]['state']=='ready' and len(catalog['rows'][0]['disks'])==2
            selected_id=catalog['rows'][0]['id'];tag=catalog['rows'][0]['name']
            service.metadata(selected_id,service.store.catalog()['revision'],'edit',dict(label='Renamed without changing QEMU tag',description='Metadata only'))
            service.metadata(selected_id,service.store.catalog()['revision'],'protect',dict(protected=True))
            catalog=service.read_catalog(True,data,request('query-status')['status'])
            assert catalog['rows'][0]['name']==tag and catalog['rows'][0]['protected']
            assert request('query-status')['status']=='prelaunch'
            request('cont')
            running=service.plan_create(dict(label='Running guest'))
            service.create(running)
            assert request('query-status')['status']=='running'
            request('stop')
            paused=service.plan_create(dict(label='Paused guest'))
            service.create(paused)
            assert request('query-status')['status']=='paused'
            from vm_snapshot_schedule import Schedule
            now=[service.clock()];service.clock=lambda:now[0]
            scheduler=Schedule(service)
            scheduler.save(dict(enabled=True,frequency='interval',interval_days=1,keep_count=1,keep_days=3650,when_offline='skip'),service.store.catalog()['revision'])
            for index in range(3):now[0]+=86401;scheduler.tick()
            assert service.store.catalog()['settings']['schedule']['retention_pending']
        finally:
            if process.poll() is None:
                try:request('quit')
                except (OSError,ValueError):pass
            try:process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.terminate();process.wait(timeout=10)
        # Offline application must restore known bytes on both private disks.
        service.processes=lambda:[]
        scheduler.tick()
        assert not service.store.catalog()['settings']['schedule']['retention_pending']
        assert len([group for group in service.store.catalog()['groups'] if group.get('source')=='schedule'])==1
        catalog=service.read_catalog(False,data)
        offline=OfflinePreparation(service)
        offline_plan=offline.plan(dict(label='Offline preparation',description='Private copies only'))
        originals=[path.read_bytes() for path in paths]
        with offline.prepared(offline_plan) as batch:
            assert batch['published'] is False and len(batch['disks'])==2
            for disk in batch['disks']:
                info=json.loads(subprocess.check_output([image,'info','--output=json',disk['clone']],creationflags=hidden))
                assert sum(row['name']==offline_plan['tag'] for row in info.get('snapshots',[]))==1
        assert originals==[path.read_bytes() for path in paths]
        assert all(not list(path.parent.glob('.qemum-stage-'+offline_plan['id']+'-*')) for path in paths)
        io=shutil.which('qemu-io')
        if not io:raise RuntimeError('qemu-io required')
        for path in paths:subprocess.run([io,'-f','qcow2','-c','write -P 0x55 0 4096',str(path)],check=True,stdout=subprocess.DEVNULL,creationflags=hidden)
        selected=catalog['rows'][0]
        for path,member in zip(paths,selected['disks']):
            info=json.loads(subprocess.check_output([image,'info','--output=json',str(path)],creationflags=hidden))
            matches=[row for row in info.get('snapshots',[]) if row.get('name')==selected['name']]
            current=identity(path)
            if current['path']!=member['path'] or current['file_id']!=member['file_id']:
                raise AssertionError('Order/path/file identity mismatch: '+str((current['path']==member['path'],current['file_id'],member['file_id'])))
            if not matches or str(matches[0].get('id'))!=member['snapshot_id']:
                raise AssertionError('QMP/offline ID mismatch: '+str((member['snapshot_id'],matches)))
        checkpoint=journalled_apply(root,name,paths,selected,image,guard=lambda args:None)
        assert checkpoint.startswith('before-restore-')
        for path in paths:subprocess.run([io,'-f','qcow2','-c','read -P 0x00 0 4096',str(path)],check=True,stdout=subprocess.DEVNULL,creationflags=hidden)
        assert_can_start(root,name)
        assert any(row['state']=='completed' and row['action']=='restore' for row in Store(root,name).operations())
        # Start only our own competing QEMU in the gap before offline apply.
        for path in paths:subprocess.run([io,'-f','qcow2','-c','write -P 0x66 0 4096',str(path)],check=True,stdout=subprocess.DEVNULL,creationflags=hidden)
        competing=[];other_name='race-'+uuid.uuid4().hex[:10]
        with socket.socket() as listener:listener.bind(('127.0.0.1',0));other_port=listener.getsockname()[1]
        other_data=dict(data,qmp_port=other_port)
        def race_guard(args):
            if args[:2]==['snapshot','-a'] and not competing:
                holder=subprocess.Popen([qemu,*vm.build_args(other_name,other_data,accelerator='tcg')],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=hidden)
                competing.append(holder);deadline=time.monotonic()+15
                while True:
                    try:vm.qmp_command(other_name,other_port,'query-status',timeout=1);break
                    except (OSError,ValueError):
                        if holder.poll() is not None or time.monotonic()>deadline:raise
                        time.sleep(.1)
            if any(holder.poll() is None for holder in competing):
                raise ValueError('Competing owned test QEMU is using the images; write refused')
        try:
            try:journalled_apply(root,name,paths,catalog['rows'][0],image,guard=race_guard)
            except ValueError:pass
            else:raise AssertionError('Offline apply bypassed a competing QEMU lock')
            assert competing
            try:assert_can_start(root,name)
            except ValueError:pass
            else:raise AssertionError('Uncertain restore did not block boot')
        finally:
            for holder in competing:
                if holder.poll() is None:holder.terminate()
                holder.wait(timeout=10)
        for path in paths:subprocess.run([io,'-f','qcow2','-c','read -P 0x66 0 4096',str(path)],check=True,stdout=subprocess.DEVNULL,creationflags=hidden)
        print('Real two-disk create/restore, prelaunch/running/paused, idempotence, byte verification and competing-QEMU guard refusal passed.')


if __name__=='__main__':main()
