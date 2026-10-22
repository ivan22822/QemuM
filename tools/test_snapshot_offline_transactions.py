"""Actual offline image transactions and injected faults, disposable media only."""
from pathlib import Path
import json
import shutil
import subprocess
import sys
import tempfile
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src/backend'))
from vm_snapshot_transaction import OfflineTransaction
from vm_snapshot_service import SnapshotService, identity
from vm_snapshot_lease import ImageLease
from vm_snapshot_store import assert_can_start, digest


def main():
    image=shutil.which('qemu-img');io=shutil.which('qemu-io')
    if not image or not io:raise RuntimeError('qemu-img and qemu-io required')
    hidden=getattr(subprocess,'CREATE_NO_WINDOW',0)
    def run(exe,args):
        result=subprocess.run([exe,*args],stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=hidden,timeout=30)
        if result.returncode:raise ValueError(result.stderr.decode(errors='replace'))
        return result.stdout.decode()
    with tempfile.TemporaryDirectory(prefix='offline-transactions-',dir=ROOT/'build') as temporary:
        base=Path(temporary);root=base/'machines';root.mkdir()
        paths=[base/('disk-'+str(index)+'.qcow2') for index in range(2)]
        for path in paths:run(image,['create','-f','qcow2',str(path),'8M'])
        data=dict(disk_path=str(paths[0]),iso_path='',ram_mb=128,cores=1,qmp_port=22345,ui_color='#F4BD62',extra_args=['-drive','file='+str(paths[1])+',format=qcow2'])
        (root/'test.json').write_text(json.dumps(data))
        def stopped(*args):raise ConnectionRefusedError('stopped test')
        service=SnapshotService(root,'test',stopped,lambda:json.loads((root/'test.json').read_text()),image,processes=lambda:[],run=run)
        transaction=OfflineTransaction(service)
        plan=transaction.plan('offline-create',options=dict(label='Первый «снимок»',description='Unicode\nТест',include_config=True))
        result=transaction.execute(plan);assert result==transaction.execute(plan)
        catalog=service.read_catalog(False,data);group=next(row for row in catalog['rows'] if row['id']==result['group_id'])
        assert group['state']=='ready' and len(group['disks'])==2 and group['label']=='Первый «снимок»'
        for path in paths:run(io,['-f','qcow2','-c','write -P 0x77 0 4096',str(path)])
        changed=dict(data,ram_mb=256,cores=2,qmp_port=23456,ui_color='#FFFFFF')
        (root/'test.json').write_text(json.dumps(changed))
        original_config=(root/'test.json').read_bytes()
        restore=transaction.plan('offline-restore',group['id'],dict(restore_scope='disks_and_config'));restored=transaction.execute(restore)
        restored_config=service.load()
        assert restored_config['ram_mb']==128 and restored_config['cores']==1
        assert restored_config['qmp_port']==23456 and restored_config['ui_color']=='#FFFFFF'
        backup=next(swap['backup'] for swap in service.store.operation(restore['id'])['swaps'] if swap['source'].endswith('test.json'))
        assert Path(backup).read_bytes()==original_config
        assert restored['disks_restored'] and restored['checkpoint']
        for path in paths:run(io,['-f','qcow2','-c','read -P 0x00 0 4096',str(path)])
        checkpoint=next(row for row in service.store.catalog()['groups'] if row.get('checkpoint'))
        exact_config=(root/'test.json').read_bytes();exact_disks=[path.read_bytes() for path in paths]
        original_rename=ImageLease.rename_to;rename_count=[0]
        def fail_config_publication(lease,target):
            rename_count[0]+=1
            if rename_count[0]==6:raise OSError('injected config publication failure')
            return original_rename(lease,target)
        config_restore=transaction.plan('offline-restore',checkpoint['id'],dict(restore_scope='disks_and_config'))
        with patch.object(ImageLease,'rename_to',fail_config_publication):
            try:transaction.execute(config_restore)
            except OSError:pass
            else:raise AssertionError('Configuration publication failure swallowed')
        assert (root/'test.json').read_bytes()==exact_config and [path.read_bytes() for path in paths]==exact_disks
        try:transaction.plan('offline-delete',checkpoint['id'])
        except ValueError:pass
        else:raise AssertionError('Protected checkpoint deletion accepted')
        # Deleting the chosen snapshot must preserve the current disk contents,
        # every other snapshot and the checkpoint, and update physical IDs.
        transaction.execute(transaction.plan('offline-delete',group['id']))
        assert not any(row['id']==group['id'] for row in service.store.catalog()['groups'])
        rows=service.read_catalog(False,data)['rows'];assert all(row['state']=='ready' for row in rows)
        for path in paths:run(io,['-f','qcow2','-c','read -P 0x00 0 4096',str(path)])
        before=[path.read_bytes() for path in paths];catalog_before=digest(service.store.catalog())
        # Throw before publishing the second replacement: first replacement and
        # second moved original must both be returned without losing identities.
        real_rename=ImageLease.rename_to;count=[0]
        def fail_once(lease,target):
            count[0]+=1
            if count[0]==4:raise OSError('injected second publication failure')
            return real_rename(lease,target)
        failed=transaction.plan('offline-create')
        with patch.object(ImageLease,'rename_to',fail_once):
            try:transaction.execute(failed)
            except OSError:pass
            else:raise AssertionError('Injected failure swallowed')
        assert before==[path.read_bytes() for path in paths] and digest(service.store.catalog())==catalog_before
        assert service.store.operation(failed['id'])['state']=='failed';assert_can_start(root,'test')
        # A journal failure after catalog publication is reconciled as completed,
        # never repeated as another snapshot or rolled back under a new catalog.
        finished=transaction.plan('offline-create');save=service.transition
        def lost_completion(operation,state,detail,**extra):
            if state=='completed':raise OSError('injected completion write loss')
            return save(operation,state,detail,**extra)
        with patch.object(service,'transition',lost_completion):
            try:transaction.execute(finished)
            except ValueError:pass
            else:raise AssertionError('Lost completion did not require recovery')
        operation=service.store.operation(finished['id']);assert operation['state']=='recovery-required'
        recovered=transaction.recover(operation);assert recovered==transaction.execute(finished)
        assert_can_start(root,'test')
        # Simulate interruption after the original rename, with compensation
        # unavailable. Recovery has to inspect paths rather than stale phase.
        interrupted=transaction.plan('offline-create');count[0]=0
        def fail_after_first_old(lease,target):
            count[0]+=1
            if count[0]>=2:raise OSError('injected interruption')
            return real_rename(lease,target)
        with patch.object(ImageLease,'rename_to',fail_after_first_old):
            try:transaction.execute(interrupted)
            except ValueError:pass
            else:raise AssertionError('Interrupted publication accepted')
        assert not paths[0].exists()
        transaction.recover(service.store.operation(interrupted['id']))
        assert all(path.exists() for path in paths);assert_can_start(root,'test')
        from vm_snapshot_schedule import Schedule
        now=[service.clock()];service.clock=lambda:now[0]
        schedule=Schedule(service);schedule.save(dict(enabled=True,frequency='interval',interval_days=1,keep_count=2,keep_days=3650,when_offline='create'),service.store.catalog()['revision'])
        for index in range(4):
            now[0]+=86401;schedule.tick()
        auto=[row for row in service.store.catalog()['groups'] if row.get('source')=='schedule']
        assert len(auto)==2
        assert any(row.get('checkpoint') and row.get('protected') for row in service.store.catalog()['groups'])
        assert all(row['state']=='ready' for row in service.read_catalog(False,service.load())['rows'])
        print('Offline two-disk create/delete/restore, content comparison, identity rebinding, compensation and restart recovery passed.')


if __name__=='__main__':main()
