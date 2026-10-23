import copy
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'src/backend'))
from vm_stands import Store, create, validate


class StandTests(unittest.TestCase):
    def test_transient_windows_replace_denial_does_not_remove_old_file(self):
        from vm_stands import replace_file
        denied=PermissionError('temporary sharing conflict');denied.winerror=5
        with patch('vm_stands.os.replace',side_effect=[denied,None]) as rename,patch('vm_stands.time.sleep'):
            replace_file('private-source','private-destination',lambda p:None)
            self.assertEqual(rename.call_count,2)
        with patch('vm_stands.os.replace',side_effect=denied) as rename,patch('vm_stands.time.sleep'):
            with self.assertRaises(PermissionError):replace_file('private-source','private-destination',lambda p:None)
            self.assertEqual(rename.call_count,6)
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.store = Store(self.root)
        self.row = create('Лаборатория FreeBSD и Kali', ['kali', 'bsd'])

    def test_read_does_not_create_folder(self):
        self.assertEqual(self.store.list()['rows'], [])
        self.assertFalse((self.root/'stands').exists())

    def test_round_trip_and_reopen(self):
        result = self.store.save(self.row, 0)
        self.assertEqual(result['revision'], 1)
        self.assertEqual(Store(self.root).read(result['id']), result)
        self.assertEqual(self.row['revision'], 0)

    def test_stale_editor_cannot_overwrite(self):
        first = self.store.save(self.row, 0)
        first['name'] = 'Другая подпись'
        second = self.store.save(first, 1)
        with self.assertRaises(ValueError):
            self.store.save(first, 1)
        self.assertEqual(self.store.read(first['id']), second)

    def test_atomic_failure_keeps_previous(self):
        first = self.store.save(self.row, 0)
        first['name'] = 'Следующая'
        with patch('vm_stands.os.replace', side_effect=OSError('test failure')):
            with self.assertRaises(OSError):
                self.store.save(first, 1)
        self.assertNotEqual(self.store.read(first['id'])['name'], first['name'])
        self.assertEqual(list((self.root/'stands').glob('*.tmp')), [])

    def test_bad_file_isolated_from_good_stand(self):
        first = self.store.save(self.row, 0)
        (self.root/'stands'/'broken.json').write_text('{')
        result = self.store.list()
        self.assertEqual(result['rows'], [first])
        self.assertEqual(len(result['errors']), 1)

    def test_case_collision_and_invalid_members_rejected(self):
        for members in ([], ['a', 'a'], ['Kali', 'kali'], ['../file'], [None], ['a']*65):
            with self.subTest(members=members):
                with self.assertRaises(ValueError):
                    validate(create('test', members))

    def test_schema_fields_limits_and_order(self):
        for key, value in [('schema_version', 2), ('schema_version', True), ('revision', -1),
                           ('name', ''), ('name', 'a'*121), ('name', 'one\ntwo'),
                           ('description', 'a'*4001), ('id', '../escape'),
                           ('order', ['kali', 'kali']), ('order', [{}]), ('links', [{'a':'kali'}])]:
            row = copy.deepcopy(self.row);row[key] = value
            with self.subTest(key=key, value=str(value)[:50]):
                with self.assertRaises(ValueError):validate(row)
        row = dict(self.row, future=True)
        with self.assertRaises(ValueError):validate(row)

    def test_no_machine_or_disk_changes(self):
        disk = self.root/'disk.img';disk.write_bytes(b'PRIVATE SENTINEL')
        machine = self.root/'machine.json';machine.write_text('UNCHANGED')
        self.store.save(self.row, 0)
        self.assertEqual(disk.read_bytes(), b'PRIVATE SENTINEL')
        self.assertEqual(machine.read_text(), 'UNCHANGED')

    def test_file_identity_mismatch(self):
        first = self.store.save(self.row, 0)
        path = self.root/'stands'/(first['id']+'.json')
        first['id'] = 'a'*32;path.write_text(json.dumps(first))
        with self.assertRaises(ValueError):self.store.read(path.stem)

    def test_hard_link_refused(self):
        first = self.store.save(self.row, 0)
        path = self.root/'stands'/(first['id']+'.json')
        os.link(path, self.root/'alias.json')
        with self.assertRaises(ValueError):self.store.read(first['id'])

    def test_shared_members_are_references(self):
        self.store.save(self.row, 0)
        other = create('Другой стенд', ['kali'])
        self.store.save(other, 0)
        self.assertEqual(len(self.store.list()['rows']), 2)
        self.assertFalse((self.root/'machines').exists())
