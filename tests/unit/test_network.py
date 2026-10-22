from copy import deepcopy
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parents[1] / 'src' / 'backend'))
import vm
import vm_network as network
from vm_backend import Service


class NetworkTests(unittest.TestCase):
    def nic(self, **updates):
        return dict(dict(id='net0', type='user', model='e1000e', net='10.0.2.0/24', mac='52:54:00:12:34:56'), **updates)

    def test_legacy_network_is_byte_for_byte_equivalent_and_read_only(self):
        data = dict(disk_path='disk.qcow2', extra_args=['-device', 'usb-tablet'])
        before = deepcopy(data)
        self.assertEqual(network.network_args(data), ['-netdev', 'user,id=net0', '-device', 'virtio-net-pci,netdev=net0'])
        self.assertEqual(network.network_args(dict(iso_path='live.iso')), [])
        self.assertEqual(data, before)

    def test_empty_managed_list_disables_default_network(self):
        self.assertEqual(network.network_args(dict(disk_path='disk.qcow2', network_adapters=[])), ['-nic', 'none'])

    def test_multiple_nics_and_forwarding_have_no_implicit_net0(self):
        data = dict(network_adapters=[self.nic(id='internet', hostfwd=['tcp:127.0.0.1:2222-:22']),
            self.nic(id='lab', mac='52:54:00:12:34:57', type='socket', mode='listen', address='127.0.0.1', port=1234)])
        args = network.network_args(data)
        self.assertEqual(args[:2], ['-nic', 'none'])
        self.assertEqual(args.count('-netdev'), 2)
        self.assertIn('user,id=internet,net=10.0.2.0/24,hostfwd=tcp:127.0.0.1:2222-:22', args)
        self.assertIn('socket,id=lab,listen=127.0.0.1:1234', args)

    def test_invalid_id_mac_network_and_dhcp_are_rejected(self):
        for changes in (dict(id='id,foo'), dict(mac='ff:ff:ff:ff:ff:ff'), dict(mac='00:00:00:00:00:00'),
                        dict(net='bad'), dict(dhcpstart='192.168.1.2'), dict(port=70000, type='socket', mode='listen')):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                network.network_args(dict(network_adapters=[self.nic(**changes)]))

    def test_duplicate_ids_and_macs_are_rejected(self):
        for rows in ([self.nic(), self.nic(mac='52:54:00:12:34:57')], [self.nic(), self.nic(id='net1')]):
            with self.assertRaises(ValueError):
                network.network_args(dict(network_adapters=rows))

    def test_conflicting_host_ports_are_rejected_but_udp_and_tcp_are_distinct(self):
        rows = [self.nic(hostfwd=['tcp:127.0.0.1:2222-:22']), self.nic(id='net1', mac='', hostfwd=['tcp:0.0.0.0:2222-:80'])]
        with self.assertRaises(ValueError):
            network.network_args(dict(network_adapters=rows))
        rows[1]['hostfwd'] = ['udp:127.0.0.1:2222-:53']
        network.network_args(dict(network_adapters=rows))

    def test_bad_forwarding_and_raw_id_conflicts_are_rejected(self):
        for rule in ('tcp::0-:22', 'http:localhost:5-:22', 'tcp:127.0.0.1:70000-:22', 'tcp:127.0.0.1:2222-:0'):
            with self.assertRaises(ValueError):
                network.network_args(dict(network_adapters=[self.nic(hostfwd=[rule])]))
        for extras in (['-netdev', 'user,id=net0'], ['-netdev=user,id=net0'], ['-device', 'e1000,netdev=net0']):
            with self.assertRaises(ValueError):
                network.network_args(dict(network_adapters=[self.nic()], extra_args=extras))

    def test_import_existing_lab_keeps_non_network_args_and_all_options(self):
        extras = ['-device', 'usb-tablet', '-netdev', 'user,id=files,net=10.20.0.0/24,dhcpstart=10.20.0.15,hostfwd=tcp:127.0.0.1:8001-:8000,dns=10.20.0.3',
            '-device', 'virtio-net-pci,netdev=files,mac=52:54:00:12:34:61,bootindex=3', '-netdev', 'socket,id=net1,connect=127.0.0.1:1234',
            '-device', 'e1000,netdev=net1,mac=52:54:00:12:34:57', '-display', 'gtk,clipboard=on']
        data = dict(disk_path='disk.qcow2', extra_args=extras)
        before = deepcopy(data)
        result = network.import_legacy(data)
        self.assertEqual(data, before)
        self.assertEqual(result['extra_args'], ['-device', 'usb-tablet', '-display', 'gtk,clipboard=on'])
        self.assertEqual([row['id'] for row in result['adapters']], ['net0', 'files', 'net1'])
        self.assertEqual(result['adapters'][1]['backend_options'], 'dns=10.20.0.3')
        self.assertEqual(result['adapters'][1]['device_options'], 'bootindex=3')
        self.assertEqual(result['adapters'][2]['mode'], 'connect')
        self.assertEqual(result['warnings'], [])

    def test_import_preserves_escaped_commas_and_custom_backend(self):
        row = network.from_specs('vhost-user,id=custom,chardev=char0', 'virtio-net-pci,netdev=custom,romfile=C:\\a,,b.rom')
        self.assertEqual(row['device_options'], 'romfile=C:\\a,,b.rom')
        self.assertEqual(network.adapter_specs(row), ('vhost-user,id=custom,chardev=char0', 'virtio-net-pci,netdev=custom,romfile=C:\\a,,b.rom'))

    def test_opaque_and_scripted_tap_are_not_removed_on_import(self):
        extras = ['-netdev', 'tap,id=tap0,ifname=lab,script=my-script', '-device', 'e1000,netdev=tap0', '-nic', 'none']
        result = network.import_legacy(dict(extra_args=extras))
        self.assertEqual(result['extra_args'], extras)
        self.assertTrue(result['warnings'])

    def test_iso_default_network_is_preserved_on_import(self):
        result = network.import_legacy(dict(iso_path='live.iso', extra_args=['-device', 'usb-tablet']))
        self.assertEqual(result['adapters'][0]['type'], 'user')
        self.assertEqual(result['extra_args'], ['-device', 'usb-tablet'])

    def test_tap_requires_interface_and_never_runs_host_scripts(self):
        with self.assertRaises(ValueError):
            network.adapter_specs(self.nic(type='tap', ifname=''))
        backend, device = network.adapter_specs(self.nic(type='tap', ifname='LabTap'))
        self.assertIn('script=no,downscript=no', backend)

    def test_extra_backend_cannot_reassign_id_or_device_netdev(self):
        for changes in (dict(backend_options='id=other'), dict(device_options='netdev=other'), dict(device_options='mac=52:54:00:22:33:44')):
            with self.assertRaises(ValueError):
                network.adapter_specs(self.nic(**changes))

    def test_creation_validation_happens_before_new_disk_creation(self):
        with tempfile.TemporaryDirectory(dir=HERE) as temporary:
            root = Path(temporary)
            service = Service(root)
            try:
                for changes in (dict(network_adapters=[self.nic(), self.nic()]), dict(extra_args=['-qmp', 'stdio']), dict(extra_args=['-name', 'wrong'])):
                    data = dict(disk_path=str(root / 'new.qcow2'), iso_path='', ram_mb=128, cores=1, **changes)
                    with patch('vm_backend.image_command') as create, self.assertRaises(ValueError):
                        service.create(dict(name='new', data=data, new_disk=True, size_gb=1))
                    create.assert_not_called()
                    self.assertFalse((root / 'new.qcow2').exists())
            finally:
                service.close()

    def test_network_rpc_is_read_only_and_returns_only_network_args(self):
        with tempfile.TemporaryDirectory(dir=HERE) as temporary:
            root = Path(temporary)
            service = Service(root)
            try:
                result = service.network_validate(dict(data=dict(network_adapters=[self.nic()])))
                self.assertIn('-netdev', result['args'])
                service.network_import(dict(data=dict(disk_path='test.qcow2', extra_args=[])))
                self.assertEqual(list(root.iterdir()), [])
            finally:
                service.close()

    def test_named_template_inherits_network_or_explicitly_switches_to_legacy(self):
        with tempfile.TemporaryDirectory(dir=HERE) as temporary:
            root = Path(temporary);service = Service(root)
            try:
                iso = root / 'live.iso';iso.write_bytes(b'never boot')
                folder = root / 'parameter-backups';folder.mkdir()
                parameters = dict(iso_path=str(iso), disk_path='', ram_mb=128, cores=1, network_adapters=[self.nic()], extra_args=['-device', 'usb-tablet'])
                (folder / 'saved.json').write_text(json.dumps(dict(schema_version=1, parameters=parameters, backup_name='Сеть')), encoding='utf-8')
                service.create(dict(name='inherited', template='saved.json', data={}))
                self.assertEqual(vm.load('inherited')['network_adapters'], parameters['network_adapters'])
                service.create(dict(name='legacy', template='saved.json', data={}, network_managed=False))
                self.assertNotIn('network_adapters', vm.load('legacy'))
                self.assertEqual(vm.load('legacy')['extra_args'], parameters['extra_args'])
            finally:
                service.close()


if __name__ == '__main__':
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromModule(sys.modules[__name__]))
    (HERE / 'network-unit-results.json').write_text(json.dumps(dict(tests=result.testsRun,
        failures=[str(test) for test, _ in result.failures], errors=[str(test) for test, _ in result.errors]), indent=2), encoding='utf-8')
    raise SystemExit(0 if result.wasSuccessful() else 1)
