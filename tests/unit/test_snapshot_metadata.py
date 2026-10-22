"""Metadata changes must never mutate image bytes, tags, or internal IDs."""
import unittest
from unittest.mock import patch
import test_snapshot_foundation as foundation


class MetadataTests(unittest.TestCase):
    def setUp(self):
        self.fixture=foundation.SnapshotFoundationTests();self.fixture.setUp();self.addCleanup(self.fixture.doCleanups)
        self.service=self.fixture.service
        self.fixture.create();self.original=self.service.store.catalog()['groups'][0]
        self.before=[path.read_bytes() for path in self.fixture.paths]

    def test_edit_unicode_keeps_qemu_identity_and_disk_bytes(self):
        revision=self.service.store.catalog()['revision']
        self.service.metadata(self.original['id'],revision,'edit',dict(label='Новое имя «Кали»',description='Первая\nВторая'))
        current=self.service.store.catalog()['groups'][0]
        self.assertEqual(current['name'],self.original['name']);self.assertEqual(current['disks'],self.original['disks'])
        self.assertEqual(current['description'],'Первая\nВторая');self.assertEqual(current['label'],'Новое имя «Кали»')
        self.assertEqual(self.before,[path.read_bytes() for path in self.fixture.paths])

    def test_stale_revision_unknown_id_and_bad_keys_never_write(self):
        before=self.service.store.path.read_bytes()
        for group,revision,options in [(self.original['id'],-1,{}),('unknown',1,{}),(self.original['id'],1,dict(internal_tag='unsafe'))]:
            with self.assertRaises(ValueError):self.service.metadata(group,revision,'edit',options)
        self.assertEqual(before,self.service.store.path.read_bytes())

    def test_protection_is_persisted_and_can_be_changed_explicitly(self):
        for protected in (True,False):
            revision=self.service.store.catalog()['revision']
            self.service.metadata(self.original['id'],revision,'protect',dict(protected=protected))
            self.assertEqual(self.service.store.catalog()['groups'][0]['protected'],protected)
        self.assertEqual(self.before,[path.read_bytes() for path in self.fixture.paths])

    def test_crash_after_catalog_write_can_be_reconciled(self):
        transition=self.service.transition
        def fail(operation,state,detail,**extra):
            if state=='completed':raise OSError('lost final journal write')
            return transition(operation,state,detail,**extra)
        with patch.object(self.service,'transition',side_effect=fail):
            with self.assertRaises(OSError):self.service.metadata(self.original['id'],1,'edit',dict(label='Saved before crash'))
        operation=next(row for row in self.service.store.operations() if row['action']=='metadata')
        self.service.recover_create(operation['id'],self.fixture.stopped)
        self.service.store.assert_clear()
        self.assertEqual(self.service.store.catalog()['groups'][0]['label'],'Saved before crash')


if __name__=='__main__':unittest.main()
