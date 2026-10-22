import unittest
import subprocess
import test_snapshot_foundation as foundation
from vm_snapshot_ram import RamSnapshots,command_arguments


class RamGuardTests(unittest.TestCase):
    def setUp(self):
        fixture=foundation.SnapshotFoundationTests();fixture.setUp();self.addCleanup(fixture.doCleanups)
        self.fixture=fixture;self.service=fixture.service

    def check_refused(self,command):
        self.service.processes=lambda:[dict(ProcessId=foundation.Owner.pid,CommandLine=command)]
        with self.assertRaises(ValueError):RamSnapshots(self.service).fingerprint(foundation.Owner())
        self.assertNotIn('snapshot-save',self.fixture.calls);self.assertNotIn('snapshot-load',self.fixture.calls)

    def test_whpx_and_compatible_whpx_have_no_ram_mutation_path(self):
        self.check_refused('qemu-system-x86_64 -accel whpx -cpu host')
        self.check_refused('qemu-system-x86_64 -accel whpx,kernel-irqchip=off -cpu host')

    def test_host_cpu_is_never_treated_as_portable(self):
        self.fixture.data['launch_profile_settings']=dict(accelerator='tcg',options='',cpu='host')
        self.check_refused('qemu-system-x86_64 -accel tcg -cpu host')

    def test_actual_cpu_must_match_saved_profile(self):
        self.check_refused('qemu-system-x86_64 -accel tcg -cpu qemu64')

    def test_unvalidated_devices_and_external_state_are_refused(self):
        for flag,value in (('-device','tpm-tis'),('-object','memory-backend-file,id=ram')):
            previous=self.fixture.data['extra_args'][:]
            self.fixture.data['extra_args'] += [flag,value]
            self.check_refused('qemu-system-x86_64 -accel tcg -cpu max')
            self.fixture.data['extra_args']=previous
        self.fixture.data['vga']='qxl';self.check_refused('qemu-system-x86_64 -accel tcg -cpu max')

    def test_guest_agent_external_channel_is_not_silently_included_in_ram(self):
        self.fixture.data['guest_agent_enabled']=True
        self.check_refused('qemu-system-x86_64 -accel tcg -cpu max')

    def test_native_command_parser_preserves_unicode_spaces_quotes_and_backslashes(self):
        args=['qemu-system-x86_64','-name','VM Unicode '+chr(0x416),str(self.fixture.paths[0].parent/'disk with spaces.qcow2'),'quoted"value','trailing\\']
        self.assertEqual(command_arguments(subprocess.list2cmdline(args)),args)
