"""Only private fake media and fake QMP. No user's disks or QEMU processes."""
from copy import deepcopy
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import uuid
import threading
import subprocess

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'src/backend'))
from vm_snapshot_service import SnapshotService, inventory, REQUIRED, identity
from vm_snapshot_store import Store, digest, assert_can_start, operation_lock
from vm_snapshots import journalled_apply


class Owner:
    pid = 12345
    def poll(self): return None
    def close(self): pass


class SnapshotFoundationTests(unittest.TestCase):
    def setUp(self):
        (ROOT / 'build').mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix='snapshot-unit-', dir=ROOT / 'build')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'machines'; self.root.mkdir()
        self.paths = [Path(self.temp.name) / ('disk-' + str(i) + '.qcow2') for i in range(2)]
        for path in self.paths: path.write_bytes(b'FAKE TEST MEDIA')
        self.data = dict(disk_path=str(self.paths[0]), extra_args=['-drive', 'file=' + str(self.paths[1]) + ',format=qcow2'])
        (self.root / 'test.json').write_text(json.dumps(self.data))
        self.blocks = [dict(device='disk' + str(i), inserted=dict(drv='qcow2', ro=False, image=dict(format='qcow2', filename=str(path), snapshots=[]))) for i, path in enumerate(self.paths)]
        self.state = 'paused'; self.calls = []; self.lost = False; self.partial = False; self.no_write = False
        self.service = SnapshotService(self.root, 'test', self.request, lambda:self.data, 'unused-test-tool',
            processes=lambda:[dict(ProcessId=Owner.pid,CommandLine='qemu-system-x86_64 -name test')], owner=Owner, free=lambda path:1024**3, run=self.run_image)

    def request(self, command, args=None):
        self.calls.append(command)
        if command == 'query-commands': return [dict(name=name) for name in REQUIRED]
        if command == 'query-qmp-schema': return [dict(name='transaction', **{'arg-type':'Args'}),dict(name='Args',members=[dict(name='actions',type='Actions')]),dict(name='Actions',**{'element-type':'Action'}),dict(name='Action',variants=[dict(case='blockdev-snapshot-internal-sync')])]
        if command == 'query-status': return dict(status=self.state)
        if command == 'query-block': return deepcopy(self.blocks)
        if command == 'transaction':
            if not self.no_write:
                for index, action in enumerate(args['actions']):
                    if self.partial and index: continue
                    self.blocks[index]['inserted']['image']['snapshots'].append(dict(name=action['data']['name'], id=str(index+10), **{'vm-state-size':0, 'date-sec':1700000000}))
            if self.lost: raise TimeoutError('lost reply')
            return {}
        raise AssertionError(command)

    def run_image(self, exe, args):
        return json.dumps(next(block['inserted']['image'] for block in self.blocks if os.path.normcase(block['inserted']['image']['filename']) == os.path.normcase(args[-1])))

    def create(self):
        plan = self.service.plan_create(dict(label='Обновление «тест»', description='Первая\nВторая'))
        return plan, self.service.create(plan)

    def stopped(self):
        self.service.processes=lambda:[]

    def test_plan_is_read_only_and_lists_every_disk(self):
        before = [path.read_bytes() for path in self.paths]
        plan = self.service.plan_create()
        self.assertEqual(len(plan['disks']), 2)
        self.assertFalse(self.service.store.folder.exists())
        self.assertEqual(before, [path.read_bytes() for path in self.paths])
        self.assertNotIn('transaction', self.calls)

    def test_create_uuid_ids_unicode_durable_idempotent(self):
        plan, result = self.create()
        self.assertEqual(result, self.service.create(plan))
        self.assertEqual(self.calls.count('transaction'), 1)
        group = Store(self.root, 'test').catalog()['groups'][0]
        self.assertEqual(group['id'], plan['group_id'])
        self.assertIsInstance(group['date'],int)
        self.assertEqual([member['snapshot_id'] for member in group['disks']], ['10', '11'])
        self.assertEqual(group['description'], 'Первая\nВторая')
        self.assertEqual(self.state, 'paused')
        assert_can_start(self.root, 'test')

    def test_expired_plan_cannot_write(self):
        plan = self.service.plan_create(); self.service.clock=lambda:plan['expires']+1
        with self.assertRaisesRegex(ValueError, 'устарел'): self.service.create(plan)
        self.assertNotIn('transaction', self.calls)

    def test_config_changed_plan_cannot_write(self):
        plan=self.service.plan_create(); (self.root/'test.json').write_text('{}')
        with self.assertRaisesRegex(ValueError, 'изменились'): self.service.create(plan)
        self.assertNotIn('transaction', self.calls)

    def test_peer_changed_plan_cannot_write(self):
        plan=self.service.plan_create(); (self.root/'other.json.disabled').write_text('{}')
        with self.assertRaises(ValueError): self.service.create(plan)
        self.assertNotIn('transaction', self.calls)

    def test_disk_replaced_same_path_cannot_write(self):
        plan=self.service.plan_create(); self.paths[0].rename(self.paths[0].with_suffix('.old'));self.paths[0].write_bytes(b'NEW')
        with self.assertRaises(ValueError): self.service.create(plan)
        self.assertNotIn('transaction', self.calls)

    def test_lost_reply_with_complete_result_is_verified_not_repeated(self):
        self.lost=True; self.create()
        self.assertEqual(self.calls.count('transaction'), 1)
        self.assertEqual(self.service.store.operations()[0]['state'], 'completed')

    def test_partial_reply_blocks_start_and_preserves_journal(self):
        self.partial=True
        plan=self.service.plan_create()
        with self.assertRaises(ValueError): self.service.create(plan)
        with self.assertRaises(ValueError): assert_can_start(self.root, 'test')
        with self.assertRaises(ValueError): self.service.create(plan)
        self.assertEqual(self.calls.count('transaction'), 1)
        with self.assertRaisesRegex(ValueError, 'Частичный'): self.service.recover_create(plan['id'], self.stopped)
        self.assertEqual(self.service.store.operation(plan['id'])['state'], 'partial')

    def test_zero_result_uncertain_then_stopped_reconciliation(self):
        self.no_write=self.lost=True;plan=self.service.plan_create()
        with self.assertRaises(ValueError):self.service.create(plan)
        with self.assertRaises(ValueError):assert_can_start(self.root,'test')
        self.service.recover_create(plan['id'], self.stopped)
        assert_can_start(self.root,'test')
        self.assertEqual(self.calls.count('transaction'),1)

    def test_journal_failure_before_first_write(self):
        plan=self.service.plan_create()
        with patch.object(self.service.store, 'save_operation', side_effect=OSError('full journal volume')):
            with self.assertRaises(OSError):self.service.create(plan)
        self.assertNotIn('transaction',self.calls)

    def test_missing_qmp_capability_refused(self):
        actual=self.service.request
        self.service.request=lambda command,args=None:[] if command=='query-commands' else actual(command,args)
        with self.assertRaises(ValueError):self.service.plan_create()
        self.assertNotIn('transaction',self.calls)

    def test_missing_transaction_variant_refused(self):
        actual=self.service.request
        self.service.request=lambda command,args=None:[] if command=='query-qmp-schema' else actual(command,args)
        with self.assertRaises(ValueError):self.service.plan_create()

    def test_vdi_raw_encrypted_chain_removable_refused(self):
        for change in ('vdi','raw','encrypted','chain','removable'):
            with self.subTest(change=change):
                block=deepcopy(self.blocks[0]); self.blocks[0]=deepcopy(block)
                if change in ('vdi','raw'):self.blocks[0]['inserted']['drv']=change
                elif change=='encrypted':self.blocks[0]['inserted']['image']['encrypted']=True
                elif change=='chain':self.blocks[0]['inserted']['image']['backing-filename']='base'
                else:self.blocks[0]['removable']=True
                with self.assertRaises(ValueError):self.service.plan_create()
                self.blocks[0]=block

    def test_readonly_iso_excluded_and_iso_only_refused(self):
        self.blocks.append(dict(device='cdrom',inserted=dict(drv='raw',ro=True,image=dict(filename='test.iso'))))
        plan=self.service.plan_create();self.assertEqual(len(plan['excluded']),1)
        self.blocks=self.blocks[2:]
        with self.assertRaises(ValueError):self.service.plan_create()

    def test_free_space_fail_closed(self):
        self.service.free=lambda path:1
        with self.assertRaisesRegex(ValueError,'места'):self.service.plan_create()
        self.assertNotIn('transaction',self.calls)

    def test_shared_disabled_configuration_refused(self):
        (self.root/'peer.json.disabled').write_text(json.dumps(self.data))
        with self.assertRaises(ValueError):self.service.plan_create()

    def test_unknown_external_qemu_refused(self):
        self.service.processes=lambda:[dict(ProcessId=55,CommandLine='qemu-system-x86_64 -drive file=x')]
        with self.assertRaises(ValueError):self.service.plan_create()

    def test_corrupt_catalog_preserved_and_blocks_start(self):
        self.create();path=self.service.store.path;path.write_bytes(b'{broken')
        with self.assertRaises(ValueError):assert_can_start(self.root,'test')
        self.assertEqual(path.read_bytes(),b'{broken')

    def test_unknown_journal_schema_blocks_start(self):
        self.create();operation=self.service.store.operations()[0];path=self.service.store.operation_path(operation['id']);path.write_text('{"schema_version":999}')
        with self.assertRaises(ValueError):assert_can_start(self.root,'test')

    def test_read_catalog_never_writes_and_reports_missing_parts(self):
        self.create();before=self.service.store.path.read_bytes();self.blocks[0]['inserted']['image']['snapshots']=[]
        result=self.service.read_catalog(True,self.data,self.state)
        self.assertEqual(result['rows'][0]['state'],'partial')
        self.assertEqual(before,self.service.store.path.read_bytes())

    def test_same_name_external_groups_not_implicitly_trusted(self):
        for block in self.blocks:block['inserted']['image']['snapshots']=[dict(name='same-name',id='1')]
        rows=self.service.read_catalog(True,self.data,self.state)['rows']
        self.assertEqual(rows[0]['state'],'incompatible')
        self.assertFalse(self.service.store.folder.exists())

    def test_publication_crash_can_reconcile_without_duplicate_group(self):
        plan=self.service.plan_create()
        original=self.service.transition
        def crash(operation,state,detail,**extra):
            if state=='completed':raise OSError('journal failed after catalog publication')
            original(operation,state,detail,**extra)
        with patch.object(self.service,'transition',side_effect=crash):
            with self.assertRaises(ValueError):self.service.create(plan)
        self.service.recover_create(plan['id'],self.stopped)
        self.assertEqual(len(self.service.store.catalog()['groups']),1)

    def test_uuid_path_traversal_refused(self):
        with self.assertRaises(ValueError):self.service.store.operation('../catalog')

    def test_catalog_optimistic_version_check(self):
        self.create();group=deepcopy(self.service.store.catalog()['groups'][0]);group['id']=str(uuid.uuid4())
        with self.assertRaises(ValueError):self.service.store.publish(group,'stale')

    def test_reordered_disks_not_silently_rebound(self):
        self.create();self.blocks.reverse()
        self.assertEqual(self.service.read_catalog(True,self.data,self.state)['rows'][0]['state'],'incompatible')

    def test_recovery_does_not_open_replaced_disk(self):
        self.partial=True;plan=self.service.plan_create()
        with self.assertRaises(ValueError):self.service.create(plan)
        self.paths[0].rename(self.paths[0].with_suffix('.old'));self.paths[0].write_bytes(b'REPLACEMENT')
        with patch.object(self.service,'run') as run:
            with self.assertRaises(ValueError):self.service.recover_create(plan['id'],self.stopped)
            run.assert_not_called()

    def test_two_threads_cannot_operate_on_same_domain(self):
        result=[]
        def other():
            try:
                with operation_lock(self.root,'other'):result.append('acquired')
            except ValueError:result.append('blocked')
        with operation_lock(self.root,'test'):
            worker=threading.Thread(target=other);worker.start();worker.join(5)
        self.assertEqual(result,['blocked'])

    def test_hard_link_alias_is_shared_physical_disk(self):
        alias=Path(self.temp.name)/'alias.qcow2';os.link(self.paths[0],alias)
        (self.root/'peer.json.disabled').write_text(json.dumps(dict(disk_path=str(alias))))
        with self.assertRaisesRegex(ValueError,'Физический диск'):self.service.plan_create()

    def test_previous_catalog_retained_on_later_damage(self):
        self.create();self.create();previous=self.service.store.folder/'catalog.previous.json'
        old=previous.read_bytes();self.service.store.path.write_bytes(b'{truncated')
        with self.assertRaises(ValueError):self.service.plan_create()
        self.assertEqual(old,previous.read_bytes())

    def test_qemu_exit_during_verification_blocks_start(self):
        plan=self.service.plan_create();actual=self.service.request
        def exited(command,args=None):
            if command=='transaction':
                result=actual(command,args)
                self.service.request=lambda command,args=None: (_ for _ in ()).throw(ConnectionRefusedError('exited'))
                return result
            return actual(command,args)
        self.service.request=exited
        with self.assertRaises(ValueError):self.service.create(plan)
        with self.assertRaises(ValueError):assert_can_start(self.root,'test')

    def test_restore_failure_keeps_journal_and_blocks_start(self):
        plan,_=self.create();selected=self.service.store.catalog()['groups'][0]
        with patch('vm_snapshots._apply_snapshot',side_effect=lambda disks,row,exe,run,cancelled,journal:(journal('apply-intent','disk', 'before-restore-test'),(_ for _ in ()).throw(ValueError('partial apply')))[-1]):
            with self.assertRaises(ValueError):journalled_apply(self.root,'test',self.paths,selected,'fake',self.run_image,guard=lambda args:None)
        with self.assertRaises(ValueError):assert_can_start(self.root,'test')
        self.assertTrue(any(row['state']=='recovery-required' for row in self.service.store.operations()))

    def test_explicit_single_disk_legacy_adoption_is_metadata_only(self):
        self.data['extra_args']=[]
        (self.root/'test.json').write_text(json.dumps(self.data))
        self.blocks[0]['inserted']['image']['snapshots']=[dict(name='old-tag',id='7',**{'vm-state-size':0,'date-sec':1700000000})]
        before=self.paths[0].read_bytes()
        result=self.service.adopt('old-tag','old-tag',self.stopped)
        group=self.service.store.catalog()['groups'][0]
        self.assertEqual(result['group_id'],group['id'])
        self.assertEqual(group['disks'][0]['snapshot_id'],'7')
        self.assertEqual(before,self.paths[0].read_bytes())
        self.assertNotIn('transaction',self.calls)

    def test_legacy_adoption_requires_exact_confirmation_and_one_disk(self):
        with self.assertRaises(ValueError):self.service.adopt('old-tag','wrong',self.stopped)
        with self.assertRaisesRegex(ValueError,'многодисковую'):self.service.adopt('old-tag','old-tag',self.stopped)

    def test_unknown_options_are_not_silently_dropped(self):
        with self.assertRaises(ValueError):self.service.plan_create(dict(include_ram=True))

    def test_case_alias_cannot_bypass_pending_journal(self):
        self.partial=True;plan=self.service.plan_create()
        with self.assertRaises(ValueError):self.service.create(plan)
        with self.assertRaises(ValueError):assert_can_start(self.root,'TEST')

    def test_abrupt_worker_exit_leaves_durable_intent(self):
        operation_id=str(uuid.uuid4())
        script="import sys,os;sys.path.insert(0,sys.argv[1]);from vm_snapshot_store import Store;s=Store(sys.argv[2],'test');s.save_operation(dict(schema_version=1,id=sys.argv[3],name='test',action='create',state='running',steps=[],plan=dict(id=sys.argv[3],name='test'),mutation_started=True));os._exit(23)"
        result=subprocess.run([sys.executable,'-c',script,str(ROOT/'src/backend'),str(self.root),operation_id],creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0),timeout=15)
        self.assertEqual(result.returncode,23)
        with self.assertRaises(ValueError):assert_can_start(self.root,'test')
        self.assertEqual(Store(self.root,'test').operation(operation_id)['state'],'running')


if __name__ == '__main__':unittest.main()
