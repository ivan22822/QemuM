"""One-command, local release gates. Never commits, uploads, or touches user VMs."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys
import time

from build import ROOT

OUT = ROOT / 'build' / 'release-prep'
VERSION_FILES = {
    'src/backend/vm_backend.py': r"version='([^']+)'",
    'src/backend/vm_support.py': r"VERSION = '([^']+)'",
    'src/gui/SupportWindow.cs': r'const string Version="([^"]+)"',
}


def check_version(expected):
    version = (ROOT / 'VERSION').read_text(encoding='utf-8').strip()
    if not re.fullmatch(r'\d+\.\d+\.\d+-alpha\.\d+', version):
        raise ValueError('VERSION must be an alpha version')
    if expected and version != expected:
        raise ValueError(f'Expected {expected}, found {version}')
    for name, pattern in VERSION_FILES.items():
        match = re.search(pattern, (ROOT / name).read_text(encoding='utf-8'))
        if not match or match.group(1) != version:
            raise ValueError(f'{name} does not match VERSION')
    build_file = ROOT / 'BUILD-NUMBER'
    build = int(build_file.read_text(encoding='ascii').strip()) if build_file.exists() else 0
    if build < 0:
        raise ValueError('BUILD-NUMBER cannot be negative')
    return version, build


def run(name, args, result):
    log = OUT / (name + '.log')
    started = time.monotonic()
    with log.open('wb') as stream:
        try:
            code = subprocess.run(args, cwd=ROOT, stdout=stream,
                                  stderr=subprocess.STDOUT, timeout=1800).returncode
        except subprocess.TimeoutExpired:
            stream.write(b'\nRelease preparation timed out.\n')
            code = 124
        except OSError as error:
            stream.write(('\n' + str(error)).encode('utf-8'))
            code = 125
    row = dict(stage=name, passed=code == 0, exit_code=code,
               seconds=round(time.monotonic() - started, 2), log=str(log.relative_to(ROOT)))
    result['stages'].append(row)
    (OUT / 'result.json').write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding='utf-8')
    print(('PASS' if code == 0 else 'FAIL') + f' {name} ({row["seconds"]}s): {row["log"]}', flush=True)
    if code:
        raise RuntimeError(f'{name} failed; read {row["log"]}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', help='expected VERSION; this script never changes it')
    parser.add_argument('--full', action='store_true', help='also run disposable QEMU and install/upgrade tests')
    parser.add_argument('--python-archive', type=Path, help='pinned embedded Python ZIP, required with --full')
    parser.add_argument('--previous-package', type=Path, help='previous released application ZIP')
    parser.add_argument('--previous-launcher', type=Path, help='previous released launcher')
    parser.add_argument('--staged', action='store_true', help='audit the Git index after files have been staged')
    parser.add_argument('--check-only', action='store_true', help='validate version fields without running gates')
    args = parser.parse_args()
    version, build = check_version(args.version)
    if args.check_only:
        print(f'VERSION OK {version} build {build}')
        return
    if args.full and (not args.python_archive or not args.python_archive.is_file()):
        parser.error('--full requires an existing --python-archive')
    OUT.mkdir(parents=True, exist_ok=True)
    result = dict(version=version, build=build, full=args.full, stages=[],
                  publication='not performed', install='not performed outside disposable fixtures')
    python = sys.executable
    run('diff', ['git', 'diff', '--check'], result)
    # --qemu already runs the unit suite and native GUI checks.
    run('unit-gui-qemu' if args.full else 'unit-gui',
        [python, 'tools/test.py', '--qemu' if args.full else '--gui'], result)
    if args.full:
        run('switch', [python, 'tools/check_switch.py', '--qemu'], result)
        run('switch-service', [python, 'tools/test_switch_service.py'], result)
        run('switch-stands', [python, 'tools/test_switch_stands.py'], result)
    run('package', [python, 'tools/release.py'], result)
    run('package-audit', [python, 'tools/audit_release.py'], result)
    if args.full:
        command = [python, 'tools/test_release.py', '--python-archive', str(args.python_archive.resolve())]
        if args.previous_package:
            command += ['--previous-package', str(args.previous_package.resolve())]
        if args.previous_launcher:
            command += ['--launcher', str(args.previous_launcher.resolve())]
        run('isolated-install', command, result)
    if args.staged:
        run('staged-audit', [python, 'tools/audit.py', '--staged'], result)
    print(f'READY {version} build {build}: {len(result["stages"])} local gates passed; no GitHub action.', flush=True)


if __name__ == '__main__':
    try:
        main()
    except (ValueError, RuntimeError) as error:
        print('NOT READY: ' + str(error), file=sys.stderr)
        raise SystemExit(1)
