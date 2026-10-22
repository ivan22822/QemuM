import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(os.name == 'nt' and shutil.which('powershell.exe'), 'Windows PowerShell required')
class SetupDiagnosticsTests(unittest.TestCase):
    def test_report_is_read_only_redacted_and_contains_no_vm_files(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as temporary:
            work = Path(temporary)
            install = work / 'installation'
            (install / 'downloads').mkdir(parents=True)
            (install / 'machines').mkdir()
            (install / 'machines' / 'private.json').write_text('NEVER-COLLECT-VM-SETTINGS', encoding='utf-8')
            (install / 'downloads' / 'qemu-setup.exe').write_bytes(b'synthetic diagnostics fixture')
            private_path = 'X:' + '/Users/' + 'Diagnostic Person/QEMU'
            (install / 'runtime.json').write_text(json.dumps({'qemu_dir': private_path}), encoding='utf-8')
            def inventory():
                return {str(p.relative_to(install)): hashlib.sha256(p.read_bytes()).hexdigest()
                        for p in install.rglob('*') if p.is_file()}
            before = inventory()
            output = work / 'reports'
            result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                '-File', str(ROOT / 'tools' / 'QemuM-Diagnose.ps1'), '-InstallPath', str(install),
                '-OutputDirectory', str(output), '-ObserveSeconds', '1', '-EventHours', '1'],
                capture_output=True, timeout=90, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            self.assertEqual(result.returncode, 0, result.stderr.decode('utf-8', 'replace'))
            self.assertEqual(before, inventory())
            archives = list(output.glob('*.zip'))
            self.assertEqual(len(archives), 1)
            with zipfile.ZipFile(archives[0]) as archive:
                self.assertEqual(set(archive.namelist()),
                    {'README.txt', 'report.json', 'files.json', 'processes.json', 'events.json', 'checksum.json'})
                for name in archive.namelist():
                    content = archive.read(name).decode('utf-8-sig')
                    if name.endswith('.json'):
                        json.loads(content)
                    self.assertNotIn('NEVER-COLLECT-VM-SETTINGS', content)
                    self.assertNotIn('Diagnostic Person', content)
