"""Check the exact release payload against sources and scan distributable bytes."""
import hashlib
import json
import zipfile

from audit import SECRETS, PERSONAL_PATHS
from release import ROOT, OUT, VERSION, hash_file


def main():
    package = OUT / f'QemuM-{VERSION}-app.zip'
    expected = {path.name: path for path in (ROOT / 'src' / 'backend').iterdir()
                if path.suffix in ('.py', '.cmd')}
    expected.update({'visual-assets/' + path.name: path
                     for path in (ROOT / 'assets' / 'visual-assets').iterdir() if path.is_file()})
    for name in ('ALPHA-GUIDE.md', 'UPDATE-PROTOCOL.md','SNAPSHOTS.md'):
        expected[name] = ROOT / 'docs' / name
    findings = []
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)) or set(names) != set(expected) | {'VMManager.exe', 'QemuM.Update.exe', 'package.json'}:
            findings.append('Unexpected package file list')
        manifest = json.loads(archive.read('package.json'))
        if manifest['version'] != VERSION or set(manifest['files']) != set(expected) | {'VMManager.exe', 'QemuM.Update.exe'}:
            findings.append('Unexpected manifest')
        for name in names:
            content = archive.read(name)
            if name in expected and content != expected[name].read_bytes():
                findings.append('Source mismatch: ' + name)
            if name != 'package.json' and hashlib.sha256(content).hexdigest() != manifest['files'].get(name):
                findings.append('Hash mismatch: ' + name)
            if name.endswith('.png'):
                continue
            variants = [content]
            if name.endswith('.exe'):
                variants.append(content.decode('utf-16-le', 'ignore').encode('utf-8'))
            if any(pattern.search(value) for pattern in [PERSONAL_PATHS, *SECRETS] for value in variants):
                findings.append('Possible private material: ' + name)
    for name in (f'QemuM-{VERSION}-Setup.exe', 'QemuM.exe','QemuM-Refresh.exe'):
        content = (OUT / name).read_bytes()
        variants = [content, content.decode('utf-16-le', 'ignore').encode('utf-8')]
        if any(pattern.search(value) for pattern in [PERSONAL_PATHS, *SECRETS] for value in variants):
            findings.append('Possible private material: ' + name)
    feed = json.loads((OUT / 'update.json').read_text(encoding='utf-8'))
    if feed['sha256'] != hash_file(package) or feed['version'] != VERSION:
        findings.append('Feed mismatch')
    for line in (OUT / 'SHA256SUMS.txt').read_text(encoding='ascii').splitlines():
        digest, name = line.split('  ', 1)
        if digest != hash_file(OUT / name):
            findings.append('Release checksum mismatch: ' + name)
    print(json.dumps(dict(package_files=len(names), findings=findings), indent=2))
    return 1 if findings else 0


if __name__ == '__main__':
    raise SystemExit(main())
