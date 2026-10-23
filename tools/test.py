"""Run isolated source tests; QEMU is used only with the explicit --qemu flag."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from build import ROOT, compile_program, copy_runtime, HIDDEN


def fixture(root):
    copy_runtime(root)
    machines = root / 'machines'
    machines.mkdir()
    iso = root / 'test.iso'
    sentinel = b'Placeholder; never boot this ISO'
    iso.write_bytes(sentinel)
    (machines / 'test.json').write_text(json.dumps(dict(iso_path=str(iso), disk_path='',
        label='Исходное имя', ram_mb=256, cores=1, custom={'kept': True}), ensure_ascii=False), encoding='utf-8')
    return iso, sentinel


def gui_tests(include_qemu=False,only=None):
    if os.name != 'nt':
        raise RuntimeError('Native GUI tests require Windows.')
    if include_qemu:
        missing = [name for name in ('qemu-system-x86_64', 'qemu-img', 'qemu-io') if not shutil.which(name)]
        if missing:
            raise RuntimeError('QEMU tools missing from PATH: ' + ', '.join(missing))
    output = ROOT / 'build' / 'test-results'
    output.mkdir(parents=True, exist_ok=True)
    from release import compile_launcher
    update_tests = ROOT / 'build' / 'tests' / 'UpdateTests.exe'
    update_tests.parent.mkdir(parents=True, exist_ok=True)
    compile_launcher(update_tests, 'UpdateTests', [ROOT / 'tests' / 'gui' / 'UpdateTests.cs'])
    subprocess.run([str(update_tests), str(output)], timeout=60, creationflags=HIDDEN, check=True)
    print('UpdateTests: passed')
    bootstrap = ROOT / 'build' / 'tests' / 'BootstrapTests.exe'
    compile_launcher(bootstrap, 'BootstrapTests', [ROOT / 'tests/gui/BootstrapTests.cs', ROOT / 'src/launcher/Setup.cs', ROOT / 'src/launcher/Launcher.cs'])
    subprocess.run([str(bootstrap), str(output), str(output)], timeout=60, creationflags=HIDDEN, check=True)
    print('BootstrapTests: passed')
    names = ['EditorTests', 'MenuTests', 'SelectorTests', 'InputStyleTests', 'AppearanceTests', 'SupportTests', 'ProfileTests', 'SnapshotUiTests', 'StandsTests','StandsScaleTests','StandsHierarchyTests']
    if include_qemu:
        names.append('GuiTests')
        names.append('SnapshotRpcTests')
    if only:names=[only]
    for name in names:
        exe = ROOT / 'build' / 'tests' / (name + '.exe')
        compile_program(exe, name, (ROOT / 'tests' / 'gui' / (name + '.cs'),))
        with tempfile.TemporaryDirectory(prefix=name + '-', dir=ROOT / 'build') as temporary:
            root = Path(temporary)
            iso, sentinel = fixture(root)
            if name == 'SupportTests':
                args = [str(output), str(root)]
            elif name in ('AppearanceTests', 'ProfileTests', 'StandsTests','StandsScaleTests','StandsHierarchyTests'):
                args = [str(root), str(output)]
            elif name in ('GuiTests','SnapshotRpcTests'):
                args = [str(output), str(root), sys.executable]
                if name=='SnapshotRpcTests':
                    # Only this fresh fixture's snapshot service receives a fake
                    # empty inventory; no actual user QEMU is inspected/stopped.
                    implementation=root/'vm_backend_impl.py'
                    (root/'vm_backend.py').rename(implementation)
                    (root/'vm_backend.py').write_text(
                        'from vm_backend_impl import *\nimport vm_snapshot_service as snapshots\n'
                        'original=snapshots.manager_service\n'
                        'def private_service(*args,**kwargs):\n'
                        '    value=original(*args,**kwargs); value.processes=lambda:[]; return value\n'
                        'snapshots.manager_service=private_service\n'
                        'import argparse\nparser=argparse.ArgumentParser(); parser.add_argument("--root"); serve(parser.parse_args().root)\n',encoding='utf-8')
            elif name == 'EditorTests':
                args = [str(output / 'editor-controls.json')]
            elif name == 'MenuTests':
                args = [str(output / 'menu-results.json'), str(root)]
            else:
                args = [str(output)]
            subprocess.run([str(exe), *args], timeout=180 if name in ('GuiTests','SnapshotRpcTests','StandsScaleTests') else 60,
                           creationflags=HIDDEN, check=True)
            assert iso.read_bytes() == sentinel, 'Test altered its placeholder ISO'
        print(name + ': passed')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--gui', action='store_true', help='also run native GUI tests')
    parser.add_argument('--qemu', action='store_true', help='also exercise a disposable paused TCG guest')
    parser.add_argument('--snapshot-rpc',action='store_true',help='targeted snapshot window/RPC checks on private offline media')
    args = parser.parse_args()
    if args.snapshot_rpc:
        gui_tests(True,'SnapshotRpcTests')
        return 0
    suite = unittest.defaultTestLoader.discover(str(ROOT / 'tests' / 'unit'))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    if not result.wasSuccessful():
        return 1
    if args.gui or args.qemu:
        gui_tests(args.qemu)
    if args.qemu:
        subprocess.run([sys.executable, str(ROOT / 'tools/test_stands.py'), '--qemu'], check=True)
        subprocess.run([sys.executable, str(ROOT / 'tools/test_stand_faults.py'), '--qemu'], check=True)
        subprocess.run([sys.executable, str(ROOT / 'tools' / 'test_vdi.py')], check=True)
        subprocess.run([sys.executable, str(ROOT / 'tools' / 'test_snapshots.py')], check=True)
        subprocess.run([sys.executable, str(ROOT / 'tools' / 'test_snapshot_offline_transactions.py')], check=True)
        subprocess.run([sys.executable, str(ROOT / 'tools' / 'test_snapshot_external.py')], check=True)
        subprocess.run([sys.executable, str(ROOT / 'tools' / 'test_snapshot_export.py')], check=True)
        subprocess.run([sys.executable, str(ROOT / 'tools' / 'test_snapshot_ram.py')], check=True)
        subprocess.run([sys.executable, str(ROOT / 'tools' / 'test_snapshot_ram.py'), '--cores','2','--ram','128','--vga','none','--cpu','qemu64'], check=True)
        subprocess.run([sys.executable, str(ROOT / 'tools' / 'test_snapshot_ram.py'), '--cpu','qemu32'], check=True)
        subprocess.run([sys.executable, str(ROOT / 'tools' / 'test_snapshot_agent_guardian.py')], check=True)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
