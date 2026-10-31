"""Offline fixture construction checks; never boot a guest or download anything."""
import contextlib,io,sys,tempfile,unittest
from pathlib import Path
from unittest.mock import patch
from unittest.mock import Mock
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'tools'))
import test_switch_live as live


class LiveFixtureTests(unittest.TestCase):
    def test_pipe_recreation_gap_retried_before_any_command(self):
        from vm_switch import connect_pipe
        api=Mock();api.WaitNamedPipeW.side_effect=[False,True];api.CreateFileW.return_value=123
        with patch('vm_switch.ctypes.get_last_error',return_value=2):self.assertEqual(connect_pipe(api,'fixture'),123)
        self.assertEqual(api.WaitNamedPipeW.call_count,2);api.CreateFileW.assert_called_once()
    def test_pipe_denied_is_not_retried(self):
        from vm_switch import connect_pipe,SwitchError
        api=Mock();api.WaitNamedPipeW.return_value=False
        with patch('vm_switch.ctypes.get_last_error',return_value=5),self.assertRaises(SwitchError) as error:connect_pipe(api,'fixture')
        self.assertEqual(error.exception.category,'permission');api.WaitNamedPipeW.assert_called_once();api.CreateFileW.assert_not_called()
    def test_cpio_header_lengths_alignment_and_permissions(self):
        archive=live.cpio_file('init',b'#!/bin/sh\n')
        self.assertEqual(archive[:6],b'070701');self.assertEqual(len(archive)%4,0)
        self.assertEqual(int(archive[14:22],16),0o100755)
        size=int(archive[54:62],16);length=int(archive[94:102],16)
        start=(110+length+3)&~3
        self.assertEqual(archive[110:110+length],b'init\0');self.assertEqual(archive[start:start+size],b'#!/bin/sh\n')
    def test_cpio_trailer_has_no_body(self):
        value=live.cpio_file('TRAILER!!!',b'',0)
        self.assertEqual(int(value[54:62],16),0);self.assertIn(b'TRAILER!!!\0',value)
    def test_bad_iso_rejected(self):
        with self.assertRaises(ValueError):live.iso_boot(b'not an ISO')
    def test_rock_ridge_boot_names_and_directory_bounds(self):
        data=bytearray(28*2048);data[32769:32774]=b'CD001'
        def record(name,sector,size,real=None):
            encoded=name.encode();row=bytearray(33+len(encoded));row[2:6]=sector.to_bytes(4,'little');row[10:14]=size.to_bytes(4,'little');row[32]=len(encoded);row[33:]=encoded
            if len(row)%2:row+=b'\0'
            if real is not None:
                value=real.encode();row+=b'NM'+bytes([5+len(value),1,0])+value
            row[0]=len(row);return row
        root=record('\0',20,2048);data[32768+156:32768+190]=root
        entries=record('\0',20,2048,'.')+record('BOOT',21,2048,'boot');data[20*2048:20*2048+len(entries)]=entries
        entries=bytearray()
        for i,name in enumerate(('vmlinuz-virt','initramfs-virt','modloop-virt')):
            entries+=record(name.upper().replace('-','_')+'.;1',22+i,1,name);data[(22+i)*2048]=65+i
        data[21*2048:21*2048+len(entries)]=entries
        self.assertEqual(live.iso_boot(data),{'vmlinuz-virt':b'A','initramfs-virt':b'B','modloop-virt':b'C'})
    def test_checksum_failure_never_starts_a_process(self):
        folder=live.ROOT/'build/live-net';folder.mkdir(parents=True,exist_ok=True)
        with tempfile.TemporaryDirectory(dir=folder) as temp:
            iso=Path(temp)/'private-fixture.iso';iso.write_bytes(b'fixture')
            with patch.object(sys,'argv',['test','--live','--iso',str(iso),'--sha256','0'*64]),patch.object(live.subprocess,'Popen') as spawn:
                with self.assertRaisesRegex(ValueError,'SHA256'):live.main()
                spawn.assert_not_called()
    def test_explicit_live_flag_required(self):
        with patch.object(sys,'argv',['test','--iso','unused','--sha256','0'*64]),contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as stopped:live.main()
        self.assertEqual(stopped.exception.code,2)
