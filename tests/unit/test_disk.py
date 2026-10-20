from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'src' / 'backend'))
import vm
from vm_disk import disk_format, new_disk_format
from vm_console import snapshot_disks


class DiskTests(unittest.TestCase):
    def test_existing_qcow2_is_unchanged_and_vdi_is_explicit(self):
        data = dict(disk_path='X:/Lab/disk.qcow2', iso_path='', cores=1, ram_mb=128)
        self.assertEqual(disk_format(data), 'qcow2')
        self.assertIn('format=qcow2', vm.build_args('test', data)[vm.build_args('test', data).index('-drive') + 1])
        data['disk_path'] = 'X:/Lab/disk,one.VDI'
        arguments = vm.build_args('test', data)
        self.assertIn('file=X:/Lab/disk,,one.VDI,format=vdi,if=virtio', arguments)
        self.assertNotIn('disk_format', data)

    def test_explicit_driver_and_creation_extension(self):
        self.assertEqual(disk_format(dict(disk_path='image.bin', disk_format='vdi')), 'vdi')
        self.assertEqual(new_disk_format('image.VDI'), 'vdi')
        self.assertEqual(new_disk_format('image.qcow2'), 'qcow2')
        with self.assertRaises(ValueError):
            new_disk_format('image.raw')
        with self.assertRaises(ValueError):
            disk_format(dict(disk_format='raw'))

    def test_vdi_snapshots_are_refused_before_mutation(self):
        calls = []
        def request(command):
            calls.append(command)
            return [dict(device='disk0', inserted=dict(drv='vdi', ro=False))]
        with self.assertRaises(ValueError):
            snapshot_disks(request)
        self.assertEqual(calls, ['query-block'])
