"""Compact local task context and explicit test profiles. No automatic test skipping."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'build' / 'workflow'
UNIT = {
    'stands': 'test_stand*.py', 'network': 'test_network.py',
    'switch': 'test_stand_switches.py', 'snapshots': 'test_snapshot*.py',
    'workflow': 'test_workflow.py',
}
NATIVE = {'switch-core': 'check_switch.py', 'switch-service': 'test_switch_service.py',
          'switch-integration': 'test_switch_stands.py'}
PROFILES = ['unit', 'gui', 'stands-gui', *UNIT, *NATIVE]


def command(profile, qemu=False):
    if profile not in PROFILES:
        raise ValueError('Unknown test profile')
    if qemu and profile != 'switch-core':
        raise ValueError('--qemu is supported only with switch-core')
    if profile in UNIT:
        return [sys.executable, '-m', 'unittest', 'discover', '-s', 'tests/unit', '-p', UNIT[profile]]
    if profile in NATIVE:
        return [sys.executable, 'tools/' + NATIVE[profile]] + (['--qemu'] if qemu else [])
    if profile == 'stands-gui':
        return [sys.executable, '-c', "import sys;sys.path.insert(0,'tools');from test import gui_tests;gui_tests(only='StandsTests')"]
    return [sys.executable, 'tools/test.py'] + (['--gui'] if profile == 'gui' else [])


def fingerprint(root=ROOT):
    """Source-only stamp: never traverse build, VM data or backups."""
    digest = hashlib.sha256()
    for folder in ('src', 'tests', 'tools'):
        for path in sorted((root / folder).rglob('*')):
            if path.is_file() and path.suffix in ('.py', '.cs') and '__pycache__' not in path.parts:
                digest.update(path.relative_to(root).as_posix().encode())
                digest.update(b'\0' + path.read_bytes() + b'\0')
    return digest.hexdigest()


def run(cmd, log, timeout=900):
    with log.open('wb') as output:
        try:
            return subprocess.run(cmd, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT,
                                  timeout=timeout).returncode
        except subprocess.TimeoutExpired:
            output.write(b'\nWORKFLOW: test process timed out.\n')
            return 124
        except OSError as error:
            output.write(('\nWORKFLOW: ' + str(error)).encode('utf-8'))
            return 125


def main():
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['context', 'check', 'last'])
    parser.add_argument('profile', nargs='?', choices=PROFILES)
    parser.add_argument('--qemu', action='store_true', help='diskless paused QEMU in switch-core')
    parser.add_argument('--allow-native', action='store_true', help='allow explicit socket/GUI/disposable VM profiles')
    args = parser.parse_args()
    if args.action == 'context':
        print((ROOT / 'docs' / 'WORKING-CONTEXT.md').read_text(encoding='utf-8'))
        for options in (['branch', '--show-current'], ['status', '--short']):
            result = subprocess.run(['git', *options], cwd=ROOT, capture_output=True, text=True)
            lines = result.stdout.splitlines()
            print('\n'.join(lines[:12]))
            if len(lines) > 12: print('... %d more changed paths' % (len(lines) - 12))
        return 0
    if not args.profile: parser.error('check/last requires a profile')
    try: cmd = command(args.profile, args.qemu)
    except ValueError as error: parser.error(str(error))
    OUT.mkdir(parents=True, exist_ok=True)
    key = args.profile + ('-qemu' if args.qemu else '')
    receipt = OUT / (key + '.json')
    if args.action == 'last':
        if not receipt.exists(): print('No recorded result.'); return 1
        value = json.loads(receipt.read_text(encoding='utf-8'))
        summary = {name: value[name] for name in ('profile', 'passed', 'exit_code', 'finished_utc', 'seconds', 'log')}
        summary.update(source_matches=value['source_sha256'] == fingerprint(), environment_rechecked=False)
        print(json.dumps(summary, ensure_ascii=False, indent=2))
        return 0
    native = args.profile in NATIVE or args.profile in ('gui', 'stands-gui')
    if native and not args.allow_native:
        parser.error('this profile needs --allow-native; uses isolated fixtures only')
    stamp = fingerprint(); started = time.time()
    log = OUT / (key + '-' + str(time.time_ns()) + '.log')
    code = run(cmd, log)
    unchanged = stamp == fingerprint()
    value = dict(profile=key, passed=code == 0 and unchanged, exit_code=code,
                 source_sha256=stamp, source_unchanged_during_run=unchanged,
                 python=sys.version.split()[0], platform=platform.platform(),
                 finished_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
                 seconds=round(time.time()-started, 2), command=cmd,
                 log=log.relative_to(ROOT).as_posix())
    receipt.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    print('%s %s (%ss); log: %s' % ('PASS' if value['passed'] else 'FAIL', key, value['seconds'], value['log']))
    return code if code else (0 if unchanged else 2)


if __name__ == '__main__':
    raise SystemExit(main())
