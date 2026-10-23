import sys
from pathlib import Path
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'src/backend'))
import vm_profiles as profiles
from vm_profile_store import defaults


class CpuCompatibilityTests(unittest.TestCase):
    def test_opt_in_mask_is_used_by_both_paths(self):
        preset = next(row for row in defaults() if row['id']=='whpx-cpu-limited')
        expected = ['-accel','whpx','-cpu','host,mpx=off,apxf=off']
        self.assertEqual(profiles.accelerator_args('whpx-cpu-limited'), expected)
        self.assertEqual(profiles.accelerator_args('whpx',preset['settings']), expected)

    def test_existing_profiles_are_unchanged(self):
        self.assertEqual(profiles.accelerator_args('whpx'), ['-accel','whpx','-cpu','host'])
        self.assertEqual(profiles.accelerator_args('whpx-compatible'), ['-accel','whpx,kernel-irqchip=off','-cpu','host'])

    def test_probe_failure_never_relaunches_a_guest_or_falls_back(self):
        check=Mock(return_value={'state':'failed','detail':'WHPX: Unexpected VP exit code 4'})
        with self.assertRaises(ValueError):
            profiles.choose('qemu',{'launch_profile':'whpx-cpu-limited'},check)
        self.assertEqual(check.call_count,1)

    def test_vp_exit_wins_over_feature_warnings(self):
        code,text=profiles.failure_hint('warning: this feature conflicts with MPX\nWHPX: Unexpected VP exit code 4')
        self.assertEqual(code,'whpx-vp-exception')
        self.assertIn('не подтверждённое исправление',text)
        self.assertNotIn('Проверь Windows Hypervisor Platform',text)

    def test_warning_alone_is_not_reported_as_vp_crash(self):
        self.assertEqual(profiles.failure_hint('warning: this feature conflicts with APX')[0],'cpu-feature-conflict')
