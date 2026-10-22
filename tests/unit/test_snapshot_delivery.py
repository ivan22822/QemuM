import errno
import json
from pathlib import Path
import subprocess
import threading
import time
import unittest
from unittest.mock import patch
import uuid

import test_snapshot_foundation as foundation
import test_support as support
from vm_snapshot_errors import describe
from vm_snapshot_export import Export
from vm_snapshot_store import Store,assert_can_start
from vm_backend import Service


class SnapshotDeliveryTests(unittest.TestCase):
    def setUp(self):
        self.fixture=foundation.SnapshotFoundationTests();self.fixture.setUp();self.addCleanup(self.fixture.doCleanups)
        self.service=self.fixture.service

    def offline(self):
        self.service.processes=lambda:[]
        self.service.request=lambda *args:(_ for _ in ()).throw(ConnectionRefusedError())
        return self.service

    def test_null_optional_profile_does_not_break_snapshot_catalog_or_rewrite_config(self):
        self.fixture.data['launch_profile_settings']=None
        original=(self.fixture.root/'test.json').read_bytes()
        result=self.service.read_catalog(True,self.fixture.data)
        self.assertIn('rows',result)
        self.assertIsNone(self.fixture.data['launch_profile_settings'])
        self.assertEqual((self.fixture.root/'test.json').read_bytes(),original)

    def test_null_format_specific_is_not_encryption(self):
        from vm_snapshot_service import encrypted
        self.assertFalse(encrypted({'format-specific':None}))
        self.assertFalse(encrypted({'format-specific':{'data':None}}))

    def test_error_categories_preserve_uncertain_vs_no_space_access_and_device(self):
        for error,code in ((TimeoutError(),'RESULT_UNCONFIRMED'),(subprocess.TimeoutExpired('tool',1),'RESULT_UNCONFIRMED'),
                           (OSError(errno.ENOSPC,'space'),'NO_SPACE'),(PermissionError(),'ACCESS_DENIED'),
                           (FileNotFoundError(),'DEVICE_UNAVAILABLE'),(InterruptedError(),'CANCELLED'),
                           (ValueError('План устарел'),'STALE_PLAN'),(ValueError('Неполная группа'),'INCOMPLETE_GROUP'),
                           (ValueError('Формат VDI не поддерживается'),'UNSUPPORTED_FORMAT')):
            self.assertEqual(describe(error)['code'],code)

    def test_export_rejects_existing_nonzip_relative_and_unknown_options_without_writes(self):
        export=Export(self.offline());target=self.fixture.paths[0].parent/'keep.zip';target.write_bytes(b'keep')
        for options in (dict(path=str(target)),dict(path=str(target.with_suffix('.raw'))),dict(path='relative.zip'),dict(path=str(target),secret=True)):
            with self.assertRaises((ValueError,OSError)):export.plan(None,options)
        self.assertEqual(target.read_bytes(),b'keep');self.assertFalse(export.store.folder.exists())

    def test_export_unknown_actual_format_encryption_and_backing_fail_closed(self):
        export=Export(self.offline())
        for info in (dict(format='unknown'),dict(format='qcow2',encrypted=True),dict(format='qcow2',**{'backing-filename':'unregistered'})):
            with patch.object(export,'info',return_value=info):
                with self.assertRaises(ValueError):export.plan(None,dict(path=str(self.fixture.paths[0].parent/'copy.zip'),export_scope='current'))
        self.assertFalse(export.store.folder.exists())

    def test_declared_format_mismatch_is_rejected_before_offline_mutation(self):
        from vm_snapshot_offline import OfflinePreparation
        self.offline();self.fixture.data['disk_format']='vdi'
        with self.assertRaisesRegex(ValueError,'Заявленный формат'):OfflinePreparation(self.service).disks()

    def test_corrupt_operation_never_disappears_or_allows_start(self):
        store=self.service.store;path=store.operation_path(str(uuid.uuid4()));path.parent.mkdir(parents=True);path.write_bytes(b'{partial')
        with self.assertRaises(ValueError):assert_can_start(self.fixture.root,'test')
        self.assertEqual(path.read_bytes(),b'{partial')

    def test_duplicate_tag_does_not_substitute_a_different_internal_id(self):
        plan,_=self.fixture.create();group=self.service.store.catalog()['groups'][0]
        blocks=self.fixture.blocks
        blocks[0]['inserted']['image']['snapshots'][0]['id']='different-id'
        rows=self.service.read_catalog(True,self.fixture.data)['rows']
        self.assertEqual(rows[0]['state'],'partial')
        blocks[0]['inserted']['image']['snapshots']=[dict(name=group['name'],id=group['disks'][0]['snapshot_id'])]*2
        self.assertEqual(self.service.read_catalog(True,self.fixture.data)['rows'][0]['state'],'partial')

    def test_qemu_img_timeout_leaves_sources_unpublished_and_visible_as_unconfirmed(self):
        from vm_snapshot_transaction import OfflineTransaction
        service=self.offline();transaction=OfflineTransaction(service);plan=transaction.plan('offline-create')
        before=[path.read_bytes() for path in self.fixture.paths]
        def run(exe,args):
            if args[0]=='snapshot':raise subprocess.TimeoutExpired(exe,120)
            return json.dumps(self.fixture.blocks[0]['inserted']['image'])
        # Real private cloning, fake qemu-img command: no guest or user's image.
        with patch.object(service,'run',side_effect=run):
            with self.assertRaises(subprocess.TimeoutExpired):transaction.execute(plan)
        self.assertEqual(before,[path.read_bytes() for path in self.fixture.paths])
        operation=service.store.operation(plan['id']);self.assertFalse(operation['mutation_started'])
        self.assertEqual(operation['state'],'failed');self.assertIn('120',operation['phase'])

    def test_capacity_failure_during_private_copy_does_not_publish_or_change_sources(self):
        from vm_snapshot_transaction import OfflineTransaction
        from vm_snapshot_lease import ImageLease
        service=self.offline();transaction=OfflineTransaction(service);plan=transaction.plan('offline-create')
        before=[path.read_bytes() for path in self.fixture.paths]
        with patch.object(ImageLease,'copy_to',side_effect=OSError(errno.ENOSPC,'controlled test capacity exhausted')):
            with self.assertRaises(OSError):transaction.execute(plan)
        self.assertEqual(before,[path.read_bytes() for path in self.fixture.paths]);self.assertFalse(service.store.operation(plan['id'])['mutation_started'])

    def test_process_inventory_failure_and_unknown_peer_fail_before_qmp_transaction(self):
        for records in (None,[dict(ProcessId=12345,CommandLine='qemu -name test'),dict(ProcessId=222,CommandLine='qemu -name unknown')]):
            self.service.processes=lambda:records
            with self.assertRaises(ValueError):self.service.plan_create()
        self.assertNotIn('transaction',self.fixture.calls)

    def test_cancelled_hash_never_reads_entire_image(self):
        with self.assertRaises(InterruptedError):Export.hash(self.fixture.paths[0],lambda:True,lambda *args:None)

    def test_completed_history_admission_does_not_block_start_or_existing_recovery_writes(self):
        store=self.service.store;op=dict(schema_version=1,id=str(uuid.uuid4()),name='test',action='metadata',state='failed',steps=[])
        with patch.object(Path,'glob',return_value=iter(range(2000))):
            with self.assertRaises(ValueError):store.save_operation(op)
        store.save_operation(op)
        with patch.object(Path,'glob',return_value=iter(range(2000))):store.save_operation(op)

    def test_jobs_do_not_hold_serial_rpc_and_cancel_only_the_owned_job(self):
        backend=Service(self.fixture.root.parent);self.addCleanup(backend.close)
        key=str(uuid.uuid4());backend.snapshot_plans[key]=dict(id=key,name='test',action='offline-create')
        entered=threading.Event();release=threading.Event()
        def execute(args,progress,cancelled):
            entered.set();release.wait(5)
            if cancelled():raise InterruptedError('test cancelled')
            return dict(result='done')
        self.addCleanup(release.set)
        with patch.object(backend,'snapshot_execute',side_effect=execute):
            started=backend.snapshot_job_start(dict(name='test',operation_id=key));self.assertTrue(entered.wait(2))
            self.assertEqual(started,backend.snapshot_job_start(dict(name='test',operation_id=key)))
            self.assertFalse(backend.snapshot_job_status(dict(name='test',operation_id=key))['done'])
            with self.assertRaises(ValueError):backend.snapshot_cancel(dict(name='other',operation_id=key))
            backend.snapshot_cancel(dict(name='test',operation_id=key));release.set()
            deadline=time.monotonic()+3
            while not backend.snapshot_job_status(dict(name='test',operation_id=key))['done'] and time.monotonic()<deadline:time.sleep(.01)
            result=backend.snapshot_job_status(dict(name='test',operation_id=key));self.assertTrue(result['done']);self.assertEqual(result['failure']['code'],'CANCELLED')

    def test_snapshot_report_is_opt_in_bounded_and_excludes_plan_secrets(self):
        fixture=support.SupportTests();fixture.setUp();self.addCleanup(fixture.tearDown)
        store=Store(fixture.root/'machines','guest')
        operation=dict(schema_version=1,id=str(uuid.uuid4()),name='guest',action='metadata',state='completed',steps=[],
                       created=1,phase='done',plan=dict(secret='DO NOT EXPORT',ram='DO NOT EXPORT'))
        store.save_operation(operation)
        _,files=fixture.export(['snapshot_history'])
        self.assertIn('snapshot-history.json',files)
        self.assertNotIn(b'DO NOT EXPORT',files['snapshot-history.json'])
        self.assertNotIn('parameters.json',files)

    def test_atomic_journal_retry_never_replays_the_operation(self):
        store=self.service.store;real=__import__('os').replace;calls=[]
        def replace(source,target):
            calls.append(1)
            if len(calls)==1:raise PermissionError('transient scanner')
            return real(source,target)
        with patch('vm_snapshot_store.os.replace',side_effect=replace):store.atomic(store.folder/'test.json',dict(schema_version=1))
        self.assertEqual(len(calls),2)


if __name__=='__main__':unittest.main()
