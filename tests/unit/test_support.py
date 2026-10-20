import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch, Mock
import zipfile

APP = Path(__file__).resolve().parents[2] / 'src' / 'backend'
sys.path.insert(0, str(APP))
import vm
from vm_backend import Service
from vm_support import redact, MAX_LOG


class SupportTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(dir=Path(__file__).parent)
        self.root = Path(self.temporary.name).resolve()
        self.service = Service(self.root)
        (self.root / 'machines').mkdir()
        self.data = dict(label='Private VM', disk_path='', iso_path=str(self.root / 'private.iso'),
                         ram_mb=128, cores=1, extra_args=[])
        self.config = self.root / 'machines' / 'guest.json'
        self.config.write_text(json.dumps(self.data), encoding='utf-8')
        (self.root / 'private.iso').write_bytes(b'PRIVATE MEDIA NEVER EXPORT')
        self.original = self.config.read_bytes()

    def tearDown(self):
        self.assertEqual(self.config.read_bytes(), self.original)
        self.service.close()
        self.temporary.cleanup()

    def export(self, sections, **extra):
        args = dict(name='guest', path=str(self.root / 'report.zip'), sections=sections)
        args.update(extra)
        result = self.service.support_export(args)
        with zipfile.ZipFile(result['path']) as archive:
            files = {name: archive.read(name) for name in archive.namelist()}
        return result, files

    def test_logs_only_does_not_query_qmp_or_load_configuration(self):
        folder = self.root / 'logs'; folder.mkdir()
        (folder / 'guest-20260101-120000-abcdef.log').write_text('Ошибка запуска\n', encoding='utf-8')
        (folder / 'other-20260101-120000-abcdef.log').write_text('OTHER VM PRIVATE', encoding='utf-8')
        with patch.object(self.service, 'query', side_effect=AssertionError('QMP forbidden')), patch.object(vm, 'load', side_effect=AssertionError('config forbidden')):
            result, files = self.export(['logs'])
        self.assertEqual(files['live-log.txt'].decode('utf-8').splitlines(), ['Ошибка запуска'])
        self.assertNotIn(b'OTHER VM PRIVATE', b''.join(files.values()))
        self.assertNotIn('parameters.json', files)
        self.assertEqual(result['errors'], {})

    def test_log_tail_is_bounded(self):
        folder = self.root / 'logs'; folder.mkdir()
        (folder / 'guest-20260101-120000-abcdef.log').write_bytes(b'a' * (MAX_LOG + 100) + b'END')
        _, files = self.export(['logs'])
        self.assertEqual(len(files['live-log.txt']), MAX_LOG)
        self.assertTrue(files['live-log.txt'].endswith(b'END'))

    def test_parameters_redact_without_exporting_media(self):
        with patch.object(vm, 'build_args', return_value=['-object', 'secret,id=x,data=never-publish']):
            _, files = self.export(['parameters'])
        joined = b''.join(files.values())
        self.assertNotIn(str(self.root).encode(), joined)
        self.assertNotIn(b'never-publish', joined)
        self.assertNotIn(b'PRIVATE MEDIA NEVER EXPORT', joined)
        self.assertIn('parameters.json', files)

    def test_explicit_raw_parameters(self):
        with patch.object(vm, 'build_args', return_value=[]):
            _, files = self.export(['parameters'], redact=False)
        self.assertEqual(json.loads(files['parameters.json']), self.data)

    def test_qmp_failure_keeps_other_selected_data(self):
        with patch.object(self.service, 'query', side_effect=ConnectionRefusedError('offline')):
            result, files = self.export(['state', 'screenshot', 'diagnostics'])
        self.assertEqual(set(result['errors']), {'state', 'screenshot'})
        self.assertIn('diagnostics.json', files)
        self.assertNotIn('vm-screen.png', files)

    def test_screenshot_uses_only_read_only_command_and_private_destination(self):
        def query(name, data, command, args, timeout):
            self.assertEqual(command, 'screendump')
            self.assertEqual(args['format'], 'png')
            Path(args['filename']).write_bytes(b'\x89PNG\r\n\x1a\nSCREEN')
        with patch.object(self.service, 'query', side_effect=query) as call:
            _, files = self.export(['screenshot'])
        self.assertEqual(call.call_count, 1)
        self.assertEqual(files['vm-screen.png'], b'\x89PNG\r\n\x1a\nSCREEN')
        self.assertFalse(Path(call.call_args.args[3]['filename']).exists())

    def test_existing_target_and_empty_selection_do_not_collect(self):
        target = self.root / 'report.zip'; target.write_bytes(b'KEEP')
        with patch.object(self.service, 'query', side_effect=AssertionError('no collection')):
            with self.assertRaises(ValueError):
                self.export(['state'])
            with self.assertRaises(ValueError):
                self.export([])
        self.assertEqual(target.read_bytes(), b'KEEP')

    def test_redactor_handles_common_secrets(self):
        data = {'password': 'hidden', 'args': ['-token', 'hidden', 'secret,id=k,data=hidden'],
                'url': 'https://' + 'user:hidden' + '@example.test/file', 'path': 'X:\\Private\\disk.qcow2'}
        text = json.dumps(redact(data))
        self.assertNotIn('hidden', text)
        self.assertNotIn('Private', text)


if __name__ == '__main__':
    unittest.main()
