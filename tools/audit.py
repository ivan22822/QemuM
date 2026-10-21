"""Audit Git-index file bytes without printing possible secrets or file contents."""
import argparse
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
FORBIDDEN_SUFFIXES = {'.exe', '.dll', '.pdb', '.iso', '.img', '.qcow', '.qcow2', '.vdi', '.vmdk',
    '.vhd', '.vhdx', '.raw', '.nvram', '.zip', '.7z', '.bak', '.log', '.pem', '.key', '.pfx', '.kdbx'}
FORBIDDEN_PARTS = {'machines', 'logs', 'backups', 'parameter-backups', 'runtime', 'build', 'dist',
                   'releases', '__pycache__', 'node_modules', '.venv', 'sources',
                   'data', 'downloads', 'staging', 'health', 'versions'}
SECRETS = [
    re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'),
    re.compile(rb'\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})\b'),
    re.compile(rb'\bAKIA[0-9A-Z]{16}\b'),
    re.compile(rb'https?://[^\s/@:]+:[^\s/@]+@'),
    re.compile(rb'(?i)\b(?:api[_-]?key|access[_-]?token|client[_-]?secret|password)\s*[:=]\s*["\'][^"\'\r\n]{12,}["\']'),
]
PERSONAL_PATHS = re.compile(rb'(?i)(?:[a-z]:[\\/]+Users[\\/]+[^\\/\s"\']+|/'
                            rb'(?:home|Users)/[^/\s"\']+)')


def git(*args):
    return subprocess.check_output(['git', '-C', str(ROOT), *args])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--staged', action='store_true', required=True, help='inspect the full Git index')
    parser.parse_args()
    names = [name.decode('utf-8') for name in git('ls-files', '-z').split(b'\0') if name]
    findings, total = [], 0
    for name in names:
        path = Path(name)
        parts = {part.lower() for part in path.parts}
        if path.suffix.lower() in FORBIDDEN_SUFFIXES or parts & FORBIDDEN_PARTS or path.name.lower() in {
            'appearance.json', 'installation.json', 'manager-settings.json', 'launch-profiles.json', 'runtime.json', 'active.json', '.env'}:
            findings.append(dict(path=name, category='runtime/private/generated file'))
        if path.suffix.lower() == '.png' and not name.startswith('assets/visual-assets/'):
            findings.append(dict(path=name, category='unexpected image'))
        content = git('show', ':' + name)
        total += len(content)
        if path.suffix.lower() == '.png':
            if not content.startswith(b'\x89PNG\r\n\x1a\n'):
                findings.append(dict(path=name, category='invalid bundled PNG'))
            continue
        if b'\0' in content:
            findings.append(dict(path=name, category='unexpected binary'))
            continue
        for pattern in SECRETS:
            if pattern.search(content):
                findings.append(dict(path=name, category='possible credential'))
        if PERSONAL_PATHS.search(content):
            findings.append(dict(path=name, category='personal absolute path'))
    catalog_name = 'assets/visual-assets/catalog.json'
    if catalog_name in names:
        import hashlib
        catalog = json.loads(git('show', ':' + catalog_name))
        expected_pngs = {'assets/visual-assets/' + row['file'] for row in catalog}
        for name in names:
            if name.lower().endswith('.png') and name not in expected_pngs:
                findings.append(dict(path=name, category='image outside the curated logo catalogue'))
        for row in catalog:
            name = 'assets/visual-assets/' + row['file']
            if name not in names or hashlib.sha256(git('show', ':' + name)).hexdigest() != row['sha256']:
                findings.append(dict(path=name, category='bundled logo checksum mismatch'))
    print(json.dumps(dict(files=len(names), bytes=total, findings=findings), ensure_ascii=False, indent=2))
    return 1 if findings else 0


if __name__ == '__main__':
    raise SystemExit(main())
