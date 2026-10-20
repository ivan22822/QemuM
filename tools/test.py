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


def gui_tests(include_qemu=False):
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
    names = ['EditorTests', 'MenuTests', 'SelectorTests', 'InputStyleTests', 'AppearanceTests', 'SupportTests']
    if include_qemu:
        names.append('GuiTests')
    for name in names:
        exe = ROOT / 'build' / 'tests' / (name + '.exe')
        compile_program(exe, name, (ROOT / 'tests' / 'gui' / (name + '.cs'),))
        with tempfile.TemporaryDirectory(prefix=name + '-', dir=ROOT / 'build') as temporary:
            root = Path(temporary)
            iso, sentinel = fixture(root)
            if name == 'SupportTests':
                args = [str(output), str(root)]
            elif name == 'AppearanceTests':
                args = [str(root), str(output)]
            elif name == 'GuiTests':
                args = [str(output), str(root), sys.executable]
            elif name == 'EditorTests':
                args = [str(output / 'editor-controls.json')]
            elif name == 'MenuTests':
                args = [str(output / 'menu-results.json'), str(root)]
            else:
                args = [str(output)]
            subprocess.run([str(exe), *args], timeout=180 if name == 'GuiTests' else 60,
                           creationflags=HIDDEN, check=True)
            assert iso.read_bytes() == sentinel, 'Test altered its placeholder ISO'
        print(name + ': passed')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--gui', action='store_true', help='also run native GUI tests')
    parser.add_argument('--qemu', action='store_true', help='also exercise a disposable paused TCG guest')
    args = parser.parse_args()
    suite = unittest.defaultTestLoader.discover(str(ROOT / 'tests' / 'unit'))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    if not result.wasSuccessful():
        return 1
    if args.gui or args.qemu:
        gui_tests(args.qemu)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
