"""Guard compact runner against false success and accidental broad tests."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('qemum_workflow', Path(__file__).resolve().parents[2] / 'tools/workflow.py')
workflow = importlib.util.module_from_spec(spec)
spec.loader.exec_module(workflow)


class WorkflowTests(unittest.TestCase):
    def temporary(self):
        folder=workflow.ROOT/'build'/'workflow'; folder.mkdir(parents=True,exist_ok=True)
        return tempfile.TemporaryDirectory(dir=folder, prefix='selftest-')
    def test_focused_profile_does_not_expand_to_full_or_qemu(self):
        self.assertEqual(workflow.command('switch')[-1], 'test_stand_switches.py')
        self.assertNotIn('--qemu', workflow.command('switch-core'))
        self.assertIn('--qemu', workflow.command('switch-core', True))
        with self.assertRaises(ValueError): workflow.command('unit', True)
        with self.assertRaises(ValueError): workflow.command('missing')

    def test_stamp_ignores_private_data_but_detects_source_change(self):
        with self.temporary() as folder:
            root = Path(folder); (root/'src').mkdir(); source=root/'src/a.py'; source.write_text('a=1')
            before=workflow.fingerprint(root)
            (root/'build').mkdir(); (root/'build/private.json').write_text('private')
            self.assertEqual(before, workflow.fingerprint(root))
            source.write_text('a=2'); self.assertNotEqual(before, workflow.fingerprint(root))

    def test_failed_child_is_not_reported_as_success(self):
        with self.temporary() as folder, patch.object(workflow.subprocess, 'run', return_value=subprocess.CompletedProcess([], 7)):
            self.assertEqual(workflow.run(['fixture'], Path(folder)/'test.log'), 7)

    def test_timeout_and_missing_executable_are_failures(self):
        with self.temporary() as folder:
            for error, code in ((subprocess.TimeoutExpired('fixture', 1),124),(OSError('fixture denied'),125)):
                with self.subTest(code=code), patch.object(workflow.subprocess, 'run', side_effect=error):
                    self.assertEqual(workflow.run(['fixture'], Path(folder)/'test.log'), code)
