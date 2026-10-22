"""Independent backup, cancellation and three-disk fault matrix on private media."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
from unittest.mock import patch
import zipfile

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src/backend'))
from vm_snapshot_service import SnapshotService,identity
from vm_snapshot_export import Export
from vm_snapshot_transaction import OfflineTransaction
from vm_snapshot_lease import ImageLease
from vm_snapshot_external import ExternalSnapshots
from vm_snapshot_store import assert_can_start
from vm_snapshots import image_command


def main():
    image=shutil.which('qemu-img');io=shutil.which('qemu-io')
    if not image or not io:raise RuntimeError('QEMU tools required')
    def run(exe,args):
        return image_command(exe,args)
    def stopped(*args):raise ConnectionRefusedError('own offline fixture')
    results={}
    with tempfile.TemporaryDirectory(prefix='export-acceptance-',dir=ROOT/'build') as temporary:
        base=Path(temporary);root=base/'machines';root.mkdir();config=root/'test.json'
        # Unicode and escaped commas exercise the production option parser.
        paths=[base/('диск с пробелом, '+str(index)+'.qcow2') for index in range(3)]
        for path in paths:run(image,['create','-f','qcow2',str(path),'8M'])
        data=dict(disk_path=str(paths[0]),iso_path='',ram_mb=128,cores=1,extra_args=[])
        for path in paths[1:]:data['extra_args']+=['-drive','file='+str(path).replace(',',',,')+',format=qcow2']
        config.write_text(json.dumps(data));service=SnapshotService(root,'test',stopped,lambda:json.loads(config.read_text()),image,processes=lambda:[],run=run)
        transaction=OfflineTransaction(service);export=Export(service)
        begin=time.perf_counter();created=transaction.execute(transaction.plan('offline-create',options=dict(label='Три диска',include_config=True)))
        results['three_disk_empty_create_seconds']=time.perf_counter()-begin
        original=[path.read_bytes() for path in paths]
        for path in paths:run(io,['-f','qcow2','-c','write -P 0x55 0 4096',str(path)])
        target=base/'independent.zip'
        plan=export.plan(created['group_id'],dict(path=str(target),format='raw',verify=True,include_config=True))
        before=[path.read_bytes() for path in paths];events=[]
        result=export.execute(plan,lambda *args:events.append(args))
        assert result==export.execute(plan) and before==[path.read_bytes() for path in paths]
        restored=base/'new-vm';restored.mkdir()
        with zipfile.ZipFile(target) as archive:
            assert archive.testzip() is None
            archive.extractall(restored)
        manifest=json.loads((restored/'manifest.json').read_text())
        assert len(manifest['disks'])==3 and not manifest['ram_included'] and manifest['parameters']
        for disk in manifest['disks']:
            run(io,['-f','raw','-c','read -P 0x00 0 4096',str(restored/disk['file'])])
        # A fresh paused TCG process opens only exported images, never the source VM.
        guest=subprocess.Popen([shutil.which('qemu-system-x86_64'),'-accel','tcg','-S','-display','none','-m','64','-nodefaults',
             '-drive','file='+str(restored/'disk-0.raw')+',format=raw'],stdout=subprocess.DEVNULL,stderr=subprocess.PIPE,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        try:
            time.sleep(.4);assert guest.poll() is None
        finally:
            if guest.poll() is None:guest.terminate()
            guest.wait(timeout=10)
        # Cancellation during an actual copy cannot publish a ZIP or alter source bytes.
        for threshold in (0,1,3):
            cancel_target=base/('cancel-'+str(threshold)+'.zip');plan=export.plan(None,dict(path=str(cancel_target),export_scope='current'))
            count=[0]
            def progress(*args):count[0]+=1
            try:export.execute(plan,progress,lambda:count[0]>=threshold)
            except InterruptedError:pass
            else:raise AssertionError('Cancellation ignored')
            assert not cancel_target.exists() and before==[path.read_bytes() for path in paths]
            assert service.store.operation(plan['id'])['state']=='cancelled'
        # First, middle and last publication failures must roll back exact originals.
        rename=ImageLease.rename_to
        for action in ('offline-create','offline-delete','offline-restore'):
            for fail_at in (2,4,6):
                plan=transaction.plan(action,created['group_id'] if action!='offline-create' else None);count=[0]
                def fail(lease,target):
                    count[0]+=1
                    if count[0]==fail_at:raise OSError('injected publication failure')
                    return rename(lease,target)
                with patch.object(ImageLease,'rename_to',fail):
                    try:transaction.execute(plan)
                    except OSError:pass
                    else:raise AssertionError('Publication failure ignored')
                assert before==[path.read_bytes() for path in paths];assert_can_start(root,'test')
        real_run=service.run
        for action in ('offline-create','offline-delete','offline-restore'):
            plan=transaction.plan(action,created['group_id'] if action!='offline-create' else None)
            def fail_second_private(exe,args):
                if args[0]=='snapshot' and Path(args[-1]).name=='disk-1.qcow2':raise ValueError('injected partial private snapshot failure')
                return real_run(exe,args)
            with patch.object(service,'run',side_effect=fail_second_private):
                try:transaction.execute(plan)
                except ValueError:pass
                else:raise AssertionError('Partial private group accepted')
            assert before==[path.read_bytes() for path in paths] and not service.store.operation(plan['id'])['mutation_started']
        restored_group=transaction.execute(transaction.plan('offline-restore',created['group_id']))
        assert restored_group['disks_restored'] and restored_group['checkpoint']
        for path in paths:run(io,['-f','qcow2','-c','read -P 0x00 0 4096',str(path)])
        before=[path.read_bytes() for path in paths]
        # Explicit cancellation on the second disk retains sources and releases leases.
        plan=transaction.plan('offline-create');count=[0]
        def progress(*args):count[0]+=1
        try:transaction.execute(plan,progress,lambda:count[0]>=3)
        except InterruptedError:pass
        else:raise AssertionError('Multidisk cancellation ignored')
        assert before==[path.read_bytes() for path in paths]
        # Controlled low-space failure uses an injected capacity provider, not a host disk fill.
        service.free=lambda path:0
        try:export.plan(None,dict(path=str(base/'no-space.zip'),export_scope='current'))
        except ValueError:pass
        else:raise AssertionError('No-space export accepted')
        service.free=lambda path:shutil.disk_usage(Path(path).parent).free
        # Two independent service processes contend for the same named locks.
        # Their plans share the same before-catalog; the second must fail stale,
        # never publish an additional snapshot after waiting for the first.
        first_plan=transaction.plan('offline-create');second_plan=transaction.plan('offline-create')
        plan_files=[base/'worker-1.json',base/'worker-2.json']
        for path,plan in zip(plan_files,(first_plan,second_plan)):path.write_text(json.dumps(plan))
        ready=base/'worker-ready';release=base/'worker-release'
        program='''import sys,json,time,shutil
from pathlib import Path
sys.path.insert(0,sys.argv[1])
from vm_snapshot_service import SnapshotService
from vm_snapshot_transaction import OfflineTransaction
from vm_snapshot_lease import ImageLease
root=Path(sys.argv[2]);plan=json.loads(Path(sys.argv[3]).read_text());ready=Path(sys.argv[4]);release=Path(sys.argv[5])
def stopped(*args):raise ConnectionRefusedError()
service=SnapshotService(root,'test',stopped,lambda:json.loads((root/'test.json').read_text()),shutil.which('qemu-img'),processes=lambda:[])
if sys.argv[6]=='first':
    real=ImageLease.copy_to
    def copy(self,*args,**kwargs):
        ready.write_text('leased');deadline=time.monotonic()+20
        while not release.exists():
            if time.monotonic()>deadline:raise TimeoutError('own test coordinator')
            time.sleep(.02)
        return real(self,*args,**kwargs)
    ImageLease.copy_to=copy
try:
    result=OfflineTransaction(service).execute(plan);print(json.dumps(dict(ok=True,result=result)))
except ValueError as error:print(json.dumps(dict(ok=False,error=str(error))))
'''
        children=[]
        try:
            def spawn(index,mode):
                child=subprocess.Popen([sys.executable,'-c',program,str(ROOT/'src/backend'),str(root),str(plan_files[index]),str(ready),str(release),mode],stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0));children.append(child);return child
            first=spawn(0,'first');deadline=time.monotonic()+10
            while not ready.exists():
                if first.poll() is not None or time.monotonic()>deadline:raise AssertionError('First test worker did not acquire lease')
                time.sleep(.02)
            second=spawn(1,'second');time.sleep(.2);assert second.poll() is None
            release.write_text('continue')
            answers=[]
            for child in children:
                output,error=child.communicate(timeout=30);assert child.returncode==0,error.decode(errors='replace');answers.append(json.loads(output))
            assert answers[0]['ok'] and not answers[1]['ok']
        finally:
            release.write_text('continue')
            for child in children:
                if child.poll() is None:child.terminate();child.wait(timeout=10)
        # Catalogue scaling and actual full-vs-empty allocation timings.
        begin=time.perf_counter()
        for index in range(12):transaction.execute(transaction.plan('offline-create'))
        results['twelve_three_disk_create_seconds']=time.perf_counter()-begin
        begin=time.perf_counter();catalog=service.read_catalog(False,service.load())
        results['catalog_groups']=len(catalog['rows']);results['catalog_read_seconds']=time.perf_counter()-begin
        sizes=[path.stat().st_size for path in paths]
        for path in paths:run(io,['-f','qcow2','-c','write -P 0x77 0 4194304',str(path)])
        results['growth_bytes']=[path.stat().st_size-size for path,size in zip(paths,sizes)]
        begin=time.perf_counter();transaction.execute(transaction.plan('offline-create'))
        results['three_disk_filled_create_seconds']=time.perf_counter()-begin
    # Flat exports for all supported source formats and registered backing trees.
    for fmt in ('qcow2','raw','vdi'):
        with tempfile.TemporaryDirectory(prefix='export-'+fmt+'-',dir=ROOT/'build') as temporary:
            base=Path(temporary);root=base/'machines';root.mkdir();source=base/('source.'+fmt);config=root/'test.json'
            run(image,['create','-f',fmt,str(source),'8M']);run(io,['-f',fmt,'-c','write -P 0x33 0 4096',str(source)])
            data=dict(disk_path=str(source),iso_path='',disk_format=fmt,ram_mb=128,cores=1,extra_args=[]);config.write_text(json.dumps(data))
            service=SnapshotService(root,'test',stopped,lambda:json.loads(config.read_text()),image,processes=lambda:[],run=run)
            try:
                external=ExternalSnapshots(service);group=external.execute(external.plan())
                out=base/'external.zip';export=Export(service)
                export.execute(export.plan(group['group_id'],dict(path=str(out),format='qcow2',verify=True)))
                with zipfile.ZipFile(out) as archive:archive.extractall(base/'independent')
                output=base/'independent'/'disk-0.qcow2'
                assert not json.loads(run(image,['info','--output=json',str(output)])).get('backing-filename')
                run(io,['-f','qcow2','-c','read -P 0x33 0 4096',str(output)])
            finally:
                for path in base.rglob('*'):
                    if path.is_file():path.chmod(0o600)
    (ROOT/'build/test-results/snapshot-performance.json').write_text(json.dumps(results,indent=2))
    print('Independent three-disk/raw/VDI/external exports, copied-VM open, real cancellation, first/middle/last failure matrix and bounded performance measurements passed.')


if __name__=='__main__':main()
