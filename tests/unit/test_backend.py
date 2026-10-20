import base64
import errno
from contextlib import contextmanager
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

HERE = Path(__file__).resolve().parent
APP = HERE.parents[1] / 'src' / 'backend'
sys.path.insert(0, str(APP))
import vm_backend as backend
import vm
import vm_storage
import vm_profiles


class BackendTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=HERE)
        self.root = Path(self.temp.name)
        self.service = backend.Service(self.root)
        vm.ROOT.mkdir()
        self.iso = self.root / 'live.iso'
        self.iso.write_bytes(b'ISO sentinel: do not boot')
        self.data = dict(iso_path=str(self.iso), disk_path='', label='Тестовая машина', ram_mb=256,
                         cores=1, extra_args=['-device', 'usb-tablet'], ui_color='#79B8FF', custom={'preserved': True})
        self.path = vm.ROOT / 'test.json'
        self.raw = json.dumps(self.data, ensure_ascii=False).encode('utf-8')
        self.path.write_bytes(self.raw)
        available = patch.object(vm, 'qmp_bind_error', return_value=None)
        available.start()
        self.addCleanup(available.stop)
        # Unit tests mock process creation; discovery must not depend on host QEMU.
        self.qemu_path = str(self.root / 'tools' / 'qemu-system-x86_64.exe')
        executable = patch.object(vm, 'qemu_executable', return_value=self.qemu_path)
        executable.start()
        self.addCleanup(executable.stop)

    def tearDown(self):
        self.service.close()
        self.temp.cleanup()

    def test_opening_library_does_not_initialize_or_migrate(self):
        before = {path.name: path.read_bytes() for path in vm.ROOT.iterdir()}
        result = self.service.list({})
        self.assertEqual(result['rows'][0]['name'], 'test')
        self.assertEqual(before, {path.name: path.read_bytes() for path in vm.ROOT.iterdir()})
        self.assertFalse((vm.ROOT / '.initialized').exists())

    def test_current_network_is_described_without_migration_or_disk_access(self):
        data = dict(self.data, disk_path=str(self.root / 'never-open.qcow2'), extra_args=[
            '-netdev', 'socket,id=lab,listen=127.0.0.1:12001',
            '-device', 'e1000,netdev=lab', '-device', 'usb-tablet'])
        raw = json.dumps(data).encode('utf-8')
        self.path.write_bytes(raw)
        before = {path.name: path.read_bytes() for path in vm.ROOT.iterdir()}
        with patch.object(backend, 'image_command', side_effect=AssertionError('No disk inspection')):
            opened = self.service.get({'name': 'test'})
            listed = self.service.list({})['rows'][0]
        for result in (opened, listed):
            self.assertEqual([nic['id'] for nic in result['network_view']['adapters']], ['net0', 'lab'])
            self.assertNotIn('network_adapters', result['data'])
            self.assertEqual(result['data']['extra_args'], data['extra_args'])
        self.assertEqual(before, {path.name: path.read_bytes() for path in vm.ROOT.iterdir()})
        self.assertFalse((self.root / 'never-open.qcow2').exists())

    def test_network_display_keeps_managed_and_opaque_settings_unchanged(self):
        data = dict(self.data, network_adapters=[], extra_args=['-nic', 'none'])
        before = json.dumps(data, sort_keys=True)
        result = backend.network_display(data)
        self.assertEqual(result['adapters'], [])
        self.assertTrue(result['warnings'])
        self.assertEqual(json.dumps(data, sort_keys=True), before)

    def test_free_qmp_port_keeps_exact_config_without_backup(self):
        stopped = Mock()
        data, raw, port, backup = vm.prepare_qmp_port('test', self.data, self.raw, stopped)
        self.assertIsNone(backup)
        self.assertEqual(self.path.read_bytes(), self.raw)
        self.assertEqual(raw, self.raw)
        self.assertEqual(port, vm.qmp_port('test', self.data))
        stopped.assert_not_called()

    def test_reserved_qmp_port_is_replaced_with_exact_backup_and_legacy_fields_preserved(self):
        legacy = dict(label='Legacy', kind='iso', path=str(self.iso), ram_mb=256, cores=1, custom={'network': 'kept'})
        raw = json.dumps(legacy).encode()
        self.path.write_bytes(raw)
        stopped = Mock()
        replies = iter([PermissionError(errno.EACCES, 'reserved'), None])
        with patch.object(vm, 'qmp_bind_error', side_effect=lambda port: next(replies)):
            data, saved, port, backup = vm.prepare_qmp_port('test', vm.load('test'), raw, stopped)
        self.assertEqual(backup.read_bytes(), raw)
        self.assertEqual(json.loads(saved), dict(legacy, qmp_port=port))
        self.assertEqual(vm.qmp_port('test', vm.load('test')), port)
        self.assertEqual(data['iso_path'], str(self.iso))
        stopped.assert_called_once()

    def test_port_selection_skips_other_vm_ports(self):
        seed = int.from_bytes(hashlib.sha256(b'test').digest()[:2], 'big')
        used = 20000 + seed % 25000
        (vm.ROOT / 'other.json').write_text(json.dumps(dict(self.data, qmp_port=used)), encoding='utf-8')
        replies = iter([OSError(errno.EADDRINUSE, 'busy'), None])
        with patch.object(vm, 'qmp_bind_error', side_effect=lambda port: next(replies)) as probe:
            data, saved, port, backup = vm.prepare_qmp_port('test', self.data, self.raw, Mock())
        self.assertNotEqual(port, used)
        self.assertNotIn(unittest.mock.call(used), probe.call_args_list)

    def test_running_vm_cannot_have_its_qmp_port_moved(self):
        with patch.object(vm, 'qmp_bind_error', return_value=PermissionError(errno.EACCES, 'reserved')):
            with self.assertRaisesRegex(ValueError, 'still running'):
                vm.prepare_qmp_port('test', self.data, self.raw, Mock(side_effect=ValueError('still running')))
        self.assertEqual(self.path.read_bytes(), self.raw)
        self.assertFalse(list(vm.ROOT.glob('*.bak')))

    def test_no_available_ports_preserves_parameters_and_does_not_launch_qemu(self):
        with patch.object(vm, 'qmp_bind_error', return_value=PermissionError(errno.EACCES, 'reserved')), \
                patch.object(backend.subprocess, 'Popen') as launch:
            with self.assertRaisesRegex(ValueError, 'Не найден доступный'):
                vm.prepare_qmp_port('test', self.data, self.raw, Mock())
        launch.assert_not_called()
        self.assertEqual(self.path.read_bytes(), self.raw)
        self.assertFalse(list(vm.ROOT.glob('*.bak')))

    def test_port_repair_rejects_external_config_change_and_network_failures(self):
        self.path.write_bytes(self.raw + b'\n')
        with self.assertRaisesRegex(ValueError, 'Параметры изменились'):
            vm.prepare_qmp_port('test', self.data, self.raw, Mock())
        self.path.write_bytes(self.raw)
        stopped = Mock()
        with patch.object(vm, 'qmp_bind_error', return_value=OSError(errno.ENETDOWN, 'network failed')):
            with self.assertRaisesRegex(ValueError, 'Не удалось проверить'):
                vm.prepare_qmp_port('test', self.data, self.raw, stopped)
        stopped.assert_not_called()
        self.assertEqual(self.path.read_bytes(), self.raw)

    def test_bad_json_does_not_hide_other_machines(self):
        (vm.ROOT / 'broken.json').write_text('[]', encoding='utf-8')
        rows = self.service.list({})['rows']
        self.assertEqual(len(rows), 2)
        self.assertTrue(rows[0]['data']['_invalid'])

    def test_save_copies_exact_bytes_and_preserves_custom_fields(self):
        result = self.service.save(dict(name='test', hash=hashlib.sha256(self.raw).hexdigest(), updates={'label': 'Новое имя'}))
        self.assertEqual(Path(result['backup']).read_bytes(), self.raw)
        data = json.loads(self.path.read_bytes())
        self.assertEqual(data['label'], 'Новое имя')
        self.assertEqual(data['extra_args'], self.data['extra_args'])
        self.assertEqual(data['custom'], self.data['custom'])

    def test_stale_editor_cannot_overwrite_external_changes(self):
        self.path.write_bytes(self.raw + b'\n')
        with self.assertRaises(ValueError):
            self.service.save(dict(name='test', hash=hashlib.sha256(self.raw).hexdigest(), updates={'cores': 4}))
        self.assertEqual(self.path.read_bytes(), self.raw + b'\n')
        self.assertEqual(len(list(vm.ROOT.iterdir())), 1)

    def test_invalid_json_save_keeps_original(self):
        with self.assertRaises(ValueError):
            self.service.save(dict(name='test', hash=hashlib.sha256(self.raw).hexdigest(), raw='{bad'))
        self.assertEqual(self.path.read_bytes(), self.raw)

    def test_advanced_editor_can_repair_broken_config(self):
        self.path.write_text('{bad', encoding='utf-8')
        saved = self.service.get(dict(name='test'))
        self.assertTrue(saved['error'])
        self.service.save(dict(name='test', hash=saved['hash'], raw=json.dumps(self.data)))
        self.assertEqual(vm.load('test')['label'], self.data['label'])

    def test_create_keeps_existing_iso_and_does_not_start_qemu(self):
        with patch.object(backend.subprocess, 'Popen') as start:
            self.service.create(dict(name='new-vm', data=self.data))
        start.assert_not_called()
        self.assertEqual(self.iso.read_bytes(), b'ISO sentinel: do not boot')
        self.assertTrue((vm.ROOT / 'new-vm.json').is_file())

    def test_duplicate_name_cannot_overwrite(self):
        with self.assertRaises(ValueError):
            self.service.create(dict(name='test', data=self.data))
        self.assertEqual(self.path.read_bytes(), self.raw)

    def test_missing_extra_file_aborts_creation(self):
        data = dict(self.data, extra_args=['-drive', 'file=' + str(self.root / 'missing.qcow2')])
        with self.assertRaisesRegex(ValueError, 'Не найден'):
            self.service.create(dict(name='new-vm', data=data))
        self.assertFalse((vm.ROOT / 'new-vm.json').exists())

    def test_create_from_template_replaces_primary_media_and_keeps_network(self):
        folder = self.root / 'parameter-backups'
        folder.mkdir()
        old = str(self.root / 'old.iso')
        parameters = dict(self.data, iso_path=old, extra_args=['-cdrom', old, '-netdev', 'user,id=lab'])
        (folder / 'saved.json').write_text(json.dumps(dict(schema_version=1, backup_name='Копия', parameters=parameters)), encoding='utf-8')
        self.service.create(dict(name='restored', template='saved.json', data={'iso_path': str(self.iso)}))
        data = json.loads((vm.ROOT / 'restored.json').read_text(encoding='utf-8'))
        self.assertNotIn(old, data['extra_args'])
        self.assertIn(str(self.iso), data['extra_args'])
        self.assertIn('user,id=lab', data['extra_args'])

    def test_destructive_controls_require_matching_name_before_any_qmp(self):
        for action in ('shutdown', 'reset', 'quit'):
            with self.subTest(action=action), patch.object(self.service, 'query') as query, self.assertRaises(ValueError):
                self.service.control(dict(name='test', action=action, confirm='other'))
            query.assert_not_called()

    def test_toggle_uses_existing_qmp_commands(self):
        with patch.object(self.service, 'query', side_effect=[{'running': True}, {}]) as query:
            self.service.control(dict(name='test', action='toggle'))
        self.assertEqual(query.call_args_list[-1].args[2], 'stop')

    def test_running_vm_is_reused_without_starting_again(self):
        with patch.object(self.service, 'query', return_value={'running': True}), patch.object(backend.subprocess, 'Popen') as start:
            self.assertTrue(self.service.start(dict(name='test'))['attached'])
        start.assert_not_called()

    def test_running_vm_cannot_be_silently_restored(self):
        with patch.object(self.service, 'query', return_value={'running': True}), self.assertRaises(ValueError):
            self.service.start(dict(name='test', snapshot={'name': 'old'}))

    def test_failed_preflight_does_not_start_real_guest(self):
        with patch.object(self.service, 'query', side_effect=TimeoutError()), patch.object(self.service, 'stopped'), \
                patch.object(backend, 'choose', side_effect=ValueError('preflight failed')), \
                patch.object(backend.subprocess, 'Popen') as start, self.assertRaisesRegex(ValueError, 'preflight'):
            self.service.start(dict(name='test'))
        start.assert_not_called()

    def test_guest_and_helpers_are_launched_without_console(self):
        process = Mock(pid=1234)
        process.poll.return_value = None
        with patch.object(self.service, 'query', side_effect=TimeoutError()), patch.object(self.service, 'stopped'), \
                patch.object(backend, 'choose', return_value=('tcg', {'detail': 'ok'})), \
                patch.object(backend.subprocess, 'Popen', return_value=process) as start:
            value = self.service.start(dict(name='test'))
        self.assertEqual(value['pid'], 1234)
        self.assertTrue(start.call_args.kwargs['creationflags'] & subprocess.CREATE_NO_WINDOW)
        self.assertEqual(start.call_args.args[0][0], self.qemu_path)
        self.assertIs(start.call_args.kwargs['shell'], False)
        self.service.close()
        process.kill.assert_not_called()
        process.terminate.assert_not_called()

    def test_disk_boot_does_not_require_an_unused_installer_iso(self):
        disk = self.root / 'placeholder.qcow2'
        disk.write_bytes(b'never boot: mock Popen')
        self.path.write_text(json.dumps(dict(self.data, disk_path=str(disk), iso_path=str(self.root / 'removed.iso'))), encoding='utf-8')
        process = Mock(pid=1234)
        process.poll.return_value = None
        with patch.object(self.service, 'query', side_effect=TimeoutError()), patch.object(self.service, 'stopped'), \
                patch.object(backend, 'choose', return_value=('tcg', {'detail': 'ok'})), \
                patch.object(backend.subprocess, 'Popen', return_value=process) as start:
            self.service.start(dict(name='test', boot='disk'))
        self.assertNotIn('-cdrom', start.call_args.args[0])

    def test_known_qemu_exit_is_observed_without_waiting_for_cim_cache(self):
        process = Mock(returncode=0)
        process.poll.return_value = 0
        self.service.sessions['test'] = dict(process=process)
        self.service.inventory_running = True
        with patch.object(self.service, 'query', side_effect=TimeoutError()):
            self.assertEqual(self.service.states({})['test'], 'stopped')

    def test_known_failed_qemu_is_shown_as_an_error_with_logs_available(self):
        process = Mock(returncode=1)
        process.poll.return_value = 1
        self.service.sessions['test'] = dict(process=process)
        self.service.inventory_running = True
        with patch.object(self.service, 'query', side_effect=TimeoutError()):
            self.assertEqual(self.service.states({})['test'], 'error')

    def test_logs_are_incremental_and_do_not_control_guest(self):
        logs = self.root / 'logs'
        logs.mkdir()
        path = logs / 'test-20261018-120000-aabbcc.log'
        path.write_bytes('Привет\n'.encode('utf-8'))
        self.service.event('test', 'Событие')
        with patch.object(self.service, 'query') as query:
            first = self.service.logs(dict(name='test'))
            second = self.service.logs(dict(name='test', file=first['file'], offset=first['offset'], event=1))
        self.assertEqual(base64.b64decode(first['content']).decode('utf-8'), 'Привет\n')
        self.assertEqual(second['content'], '')
        self.assertEqual(second['events'], [])
        query.assert_not_called()

    def test_new_log_resets_the_offset(self):
        logs = self.root / 'logs'
        logs.mkdir()
        path = logs / 'test-20261018-120000-aabbcc.log'
        path.write_bytes(b'new log')
        result = self.service.logs(dict(name='test', file='previous.log', offset=9000))
        self.assertEqual(base64.b64decode(result['content']), b'new log')
        self.assertEqual(result['offset'], 7)

    def test_delete_preview_and_exact_confirmation_preserve_iso(self):
        with patch.object(self.service, 'query', side_effect=TimeoutError()), \
                patch.object(backend, 'qemu_processes', return_value=[]), patch.object(vm_storage, 'qemu_processes', return_value=[]):
            plan = self.service.delete_plan(dict(name='test'))
            with self.assertRaises(ValueError):
                self.service.delete(dict(name='test', token=plan['token'], backup_name='Моя копия', confirm='wrong'))
            self.assertTrue(self.path.exists())
            result = self.service.delete(dict(name='test', token=plan['token'], backup_name='Моя копия', confirm='test'))
        self.assertTrue(Path(result['backup']).exists())
        self.assertFalse(self.path.exists())
        self.assertEqual(self.iso.read_bytes(), b'ISO sentinel: do not boot')

    def test_stale_delete_plan_cannot_be_used(self):
        with self.assertRaises(ValueError):
            self.service.delete(dict(name='test', token='stale', backup_name='x', confirm='test'))
        self.assertTrue(self.path.exists())

    def test_mode_changes_only_explicitly(self):
        self.assertEqual(self.service.list({})['mode'], 'normal')
        self.assertFalse((self.root / 'manager-settings.json').exists())
        self.service.mode(dict(value='snapshot'))
        self.assertEqual(self.service.list({})['mode'], 'snapshot')

    def test_invalid_names_do_not_escape_config_folder(self):
        for name in ('../other', 'C:/secret', 'doctor', None, 123):
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.service.get(dict(name=name))

    def test_rpc_is_json_only_and_rejects_unrecognized_commands(self):
        messages = [dict(id=1, command='ping'), dict(id=2, command='list'),
                    dict(id=3, command='shell', args={'command': 'anything'}), [],
                    dict(id=5, command='get', args={'name': 'test'})]
        result = subprocess.run([sys.executable, '-B', '-X', 'utf8', str(APP / 'vm_backend.py'), '--root', str(self.root)],
                                input=''.join(json.dumps(item) + '\n' for item in messages), capture_output=True,
                                encoding='utf-8', timeout=15, creationflags=backend.HIDDEN)
        self.assertEqual(result.returncode, 0, result.stderr)
        replies = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(len(replies), 5)
        self.assertEqual([item['ok'] for item in replies], [True, True, False, False, True])
        self.assertEqual(replies[-1]['result']['data']['label'], self.data['label'])


if __name__ == '__main__':
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromModule(sys.modules[__name__]))
    (HERE / 'unit-results.json').write_text(json.dumps(dict(tests=result.testsRun,
        errors=[str(test) for test, _ in result.errors], failures=[str(test) for test, _ in result.failures]), indent=2), encoding='utf-8')
    raise SystemExit(0 if result.wasSuccessful() else 1)
