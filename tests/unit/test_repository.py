"""Checks for the source/runtime boundary and redacted audit rules."""
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'src' / 'backend'))
import vm

spec = importlib.util.spec_from_file_location('source_audit', ROOT / 'tools' / 'audit.py')
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class RepositoryTests(unittest.TestCase):
    def test_first_cli_run_creates_no_personal_or_example_vms(self):
        original = vm.ROOT
        try:
            fixtures = ROOT / 'build' / 'test-fixtures'
            fixtures.mkdir(parents=True, exist_ok=True)
            with tempfile.TemporaryDirectory(dir=fixtures) as temporary:
                vm.ROOT = Path(temporary) / 'machines'
                vm.initialize()
                self.assertTrue((vm.ROOT / '.initialized').is_file())
                self.assertEqual(list(vm.ROOT.glob('*.json')), [])
                vm.initialize()
                self.assertEqual(list(vm.ROOT.glob('*.json')), [])
        finally:
            vm.ROOT = original

    def test_audit_detects_windows_and_unix_home_paths(self):
        cases = [('C:' + chr(92) + 'Users' + chr(92) + 'example' + chr(92) + 'secret.txt'),
                 ('/' + 'home' + '/' + 'example/file'), ('/' + 'Users' + '/' + 'example/file')]
        for value in cases:
            with self.subTest(value=value):
                self.assertIsNotNone(audit.PERSONAL_PATHS.search(value.encode()))

    def test_audit_detects_synthetic_credentials(self):
        cases = ['ghp_' + 'a' * 36, 'github_pat_' + 'a' * 40,
                 '-----BEGIN ' + 'PRIVATE KEY-----', 'https://' + 'user:pass' + '@example.invalid']
        for value in cases:
            self.assertTrue(any(pattern.search(value.encode()) for pattern in audit.SECRETS))

    def test_audit_accepts_generic_test_media_paths(self):
        self.assertIsNone(audit.PERSONAL_PATHS.search(b'C:/VMs/example.iso'))


if __name__ == '__main__':
    unittest.main()
