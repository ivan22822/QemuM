"""Real Windows handle isolation and V1 metadata migration, private fixtures only."""
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'src/backend'))
from vm_snapshot_store import Store,digest
from vm_snapshot_lease import ImageLease, lease_images


class MigrationTests(unittest.TestCase):
    def setUp(self):
        (ROOT/'build').mkdir(exist_ok=True)
        temporary=tempfile.TemporaryDirectory(prefix='snapshot-migration-',dir=ROOT/'build');self.addCleanup(temporary.cleanup)
        self.root=Path(temporary.name)/'machines';self.root.mkdir();self.store=Store(self.root,'test')
        self.v1=dict(schema_version=1,name='test',revision=7,groups=[],custom=dict(kept='Unicode: Кали'))
        self.store.atomic(self.store.path,self.v1);self.before=self.store.path.read_bytes()

    def test_read_is_pure_and_preserves_unknown_fields(self):
        current=self.store.catalog()
        self.assertEqual(current['schema_version'],2)
        self.assertEqual(current['custom'],self.v1['custom'])
        self.assertEqual(self.store.path.read_bytes(),self.before)
        self.assertFalse((self.store.folder/'catalog.v1.json.bak').exists())

    def test_explicit_migration_is_backed_up_and_idempotent(self):
        self.assertTrue(self.store.migrate());self.assertFalse(self.store.migrate())
        self.assertEqual((self.store.folder/'catalog.v1.json.bak').read_bytes(),self.before)
        self.assertEqual(json.loads(self.store.path.read_text(encoding='utf-8'))['schema_version'],2)
        self.assertFalse(self.store.catalog()['settings']['schedule']['enabled'])

    def test_interrupted_publication_keeps_v1_and_exact_backup(self):
        original=self.store.atomic
        def fail(path,value):
            if path==self.store.path:raise OSError('simulated interruption')
            return original(path,value)
        with patch.object(self.store,'atomic',side_effect=fail):
            with self.assertRaises(OSError):self.store.migrate()
        self.assertEqual(self.store.path.read_bytes(),self.before)
        self.assertEqual((self.store.folder/'catalog.v1.json.bak').read_bytes(),self.before)
        self.assertTrue(self.store.migrate())

    def test_future_version_is_never_overwritten(self):
        self.store.path.write_text('{"schema_version":999}',encoding='utf-8');before=self.store.path.read_bytes()
        with self.assertRaises(ValueError):self.store.migrate()
        self.assertEqual(before,self.store.path.read_bytes())

    def test_v1_pending_operation_hash_remains_valid(self):
        self.assertTrue(self.store.matches_catalog(digest(self.v1)))
        self.assertFalse(self.store.matches_catalog(digest(dict(self.v1,revision=99))))
        self.assertEqual(self.store.path.read_bytes(),self.before)

    def test_v1_intent_before_first_catalog_remains_valid(self):
        self.store.path.unlink()
        legacy=dict(schema_version=1,name='test',revision=0,groups=[])
        self.assertTrue(self.store.matches_catalog(digest(legacy)))
        self.assertFalse(self.store.path.exists())

    def test_v1_additional_settings_survive_migration(self):
        self.v1['settings']=dict(custom_policy='preserved')
        self.store.atomic(self.store.path,self.v1)
        self.store.migrate()
        self.assertEqual(self.store.catalog()['settings']['custom_policy'],'preserved')


@unittest.skipUnless(os.name=='nt','Windows leases')
class ImageLeaseTests(unittest.TestCase):
    def setUp(self):
        temporary=tempfile.TemporaryDirectory(prefix='snapshot-lease-',dir=ROOT/'build');self.addCleanup(temporary.cleanup)
        self.folder=Path(temporary.name);self.source=self.folder/'original.img';self.source.write_bytes(b'ORIGINAL'*8192)

    def test_owner_denies_writers_and_deletion_but_can_copy(self):
        before=self.source.read_bytes();clone=self.folder/'clone.img'
        with ImageLease(self.source) as lease:
            with self.assertRaises(OSError):self.source.open('r+b')
            with self.assertRaises(OSError):self.source.unlink()
            lease.copy_to(clone)
            self.assertEqual(clone.read_bytes(),before)
            clone.write_bytes(b'PRIVATE MODIFICATION')
            with self.assertRaises(OSError):self.source.open('wb')
        self.assertEqual(self.source.read_bytes(),before)

    def test_cancel_removes_only_own_partial_clone(self):
        clone=self.folder/'clone.img'
        with ImageLease(self.source) as lease:
            with self.assertRaises(InterruptedError):lease.copy_to(clone,cancelled=lambda:True)
        self.assertFalse(clone.exists());self.assertTrue(self.source.exists())

    def test_rename_retains_exclusion_across_publication(self):
        clone=self.folder/'clone.img';clone.write_bytes(b'NEW IMAGE')
        backup=self.folder/'preserved.img'
        with ImageLease(self.source) as old, ImageLease(clone) as new:
            old.rename_to(backup);new.rename_to(self.source)
            with self.assertRaises(OSError):self.source.open('r+b')
            with self.assertRaises(OSError):backup.open('r+b')
            self.assertEqual(new.file_id,[self.source.stat().st_dev,self.source.stat().st_ino])
        self.assertEqual(self.source.read_bytes(),b'NEW IMAGE')
        self.assertTrue(backup.read_bytes().startswith(b'ORIGINAL'))

    def test_existing_target_is_never_replaced(self):
        target=self.folder/'occupied.img';target.write_bytes(b'KEEP')
        with ImageLease(self.source) as lease:
            with self.assertRaises(ValueError):lease.rename_to(target)
            with self.assertRaises(FileExistsError):lease.copy_to(target)
        self.assertEqual(target.read_bytes(),b'KEEP')

    def test_busy_second_disk_releases_first(self):
        other=self.folder/'second.img';other.write_bytes(b'SECOND')
        with ImageLease(other):
            with self.assertRaises(OSError):
                with lease_images([self.source,other]):pass
        with self.source.open('r+b') as stream:stream.write(b'AVAILABLE')


if __name__=='__main__':unittest.main()
