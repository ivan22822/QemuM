"""Preparation only: real native leases and fake image operations on own files."""
from copy import deepcopy
import json
import os
from pathlib import Path
import unittest
import test_snapshot_foundation as foundation
from vm_snapshot_offline import OfflinePreparation


class OfflinePreparationTests(unittest.TestCase):
    def setUp(self):
        self.fixture=foundation.SnapshotFoundationTests();self.fixture.setUp();self.addCleanup(self.fixture.doCleanups)
        self.service=self.fixture.service;self.paths=self.fixture.paths;self.service.processes=lambda:[]
        self.service.request=lambda *args:(_ for _ in ()).throw(ConnectionRefusedError('stopped'))
        self.images={os.path.normcase(str(path)):deepcopy(block['inserted']['image']) for path,block in zip(self.paths,self.fixture.blocks)}
        self.fail_second=False
        def run(executable,args):
            path=Path(args[-1]);key=os.path.normcase(str(path))
            if key not in self.images:
                index=int(path.stem.split('-')[-1]);self.images[key]=deepcopy(self.images[os.path.normcase(str(self.paths[index]))])
            info=self.images[key]
            if args[0]=='info':return json.dumps(info)
            if args[0]=='check':return '{}'
            if args[:2]==['snapshot','-c']:
                if self.fail_second and path.stem=='disk-1':raise ValueError('second private image failed')
                info['snapshots'].append(dict(name=args[2],id='private-id',**{'vm-state-size':0}))
                with self.assertRaises(OSError):self.paths[0].open('r+b')
                return ''
            raise AssertionError(args)
        self.service.run=run;self.offline=OfflinePreparation(self.service)

    def test_plan_reads_without_creating_runtime_files(self):
        plan=self.offline.plan(dict(label='Кали',description='Строка\nдва'))
        self.assertEqual(len(plan['disks']),2);self.assertEqual(plan['label'],'Кали')
        self.assertFalse(self.service.store.folder.exists())

    def test_private_group_is_verified_but_never_published(self):
        plan=self.offline.plan();before=[path.read_bytes() for path in self.paths]
        with self.offline.prepared(plan) as batch:
            self.assertFalse(batch['published']);self.assertEqual(len(batch['disks']),2)
            self.assertTrue(all(Path(row['clone']).exists() for row in batch['disks']))
            self.assertTrue(all(row['snapshot_id']=='private-id' for row in batch['disks']))
        self.assertEqual(before,[path.read_bytes() for path in self.paths])
        self.assertEqual(self.service.store.catalog()['groups'],[])
        self.assertEqual(self.service.store.operation(plan['id'])['state'],'cancelled')
        self.assertFalse(list(self.paths[0].parent.glob('.qemum-stage-*')))

    def test_stale_config_is_rejected_before_copy(self):
        plan=self.offline.plan();(self.fixture.root/'test.json').write_text('{}')
        with self.assertRaises(ValueError):
            with self.offline.prepared(plan):pass
        self.assertFalse(list(self.paths[0].parent.glob('.qemum-stage-*')))

    def test_cancel_is_before_publication_and_cleans_only_own_files(self):
        plan=self.offline.plan();before=[path.read_bytes() for path in self.paths]
        with self.assertRaises(InterruptedError):
            with self.offline.prepared(plan,cancelled=lambda:True):pass
        self.assertEqual(before,[path.read_bytes() for path in self.paths])
        self.assertEqual(self.service.store.operation(plan['id'])['state'],'cancelled')

    def test_failure_on_second_copy_keeps_all_originals(self):
        self.fail_second=True;plan=self.offline.plan();before=[path.read_bytes() for path in self.paths]
        with self.assertRaisesRegex(ValueError,'second private'):
            with self.offline.prepared(plan):pass
        self.assertEqual(before,[path.read_bytes() for path in self.paths])
        self.assertFalse(list(self.paths[0].parent.glob('.qemum-stage-*')))
        self.assertEqual(self.service.store.operation(plan['id'])['state'],'failed')

    def test_borrowed_staging_folder_is_not_cleaned(self):
        plan=self.offline.plan();folder=self.paths[0].parent/('.qemum-stage-'+plan['id']+'-0');folder.mkdir()
        sentinel=folder/'keep.txt';sentinel.write_bytes(b'KEEP')
        with self.assertRaises(FileExistsError):
            with self.offline.prepared(plan):pass
        self.assertEqual(sentinel.read_bytes(),b'KEEP')

    def test_existing_writer_is_refused_by_native_lease(self):
        plan=self.offline.plan()
        with self.paths[0].open('r+b'):
            with self.assertRaises(OSError):
                with self.offline.prepared(plan):pass
        self.assertFalse(list(self.paths[0].parent.glob('.qemum-stage-*')))

    def test_insufficient_copy_space_and_ram_request_are_refused(self):
        self.service.free=lambda path:1
        with self.assertRaises(ValueError):self.offline.plan()
        with self.assertRaises(ValueError):self.offline.plan(dict(kind='ram'))


if __name__=='__main__':unittest.main()
