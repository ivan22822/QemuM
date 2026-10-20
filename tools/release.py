"""Build alpha artifacts from a fresh source-only directory; never publish or install."""
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile

from build import ROOT, GUI, compiler, compile_program, copy_runtime, build

VERSION = (ROOT / 'VERSION').read_text(encoding='utf-8').strip()
OUT = ROOT / 'build' / 'release' / VERSION
LAUNCHER = ROOT / 'src' / 'launcher'


def hash_file(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def compile_launcher(output, main, sources, resources=()):
    args = [compiler(), '/nologo', '/platform:x64', '/optimize+', '/target:winexe',
            '/main:' + main, '/out:' + str(output),
            '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
            '/reference:System.Web.Extensions.dll', '/reference:System.IO.Compression.dll',
            '/reference:System.IO.Compression.FileSystem.dll',
            '/win32manifest:' + str(GUI / 'app.manifest')]
    icon = ROOT / 'build' / 'tools' / 'app.ico'
    if icon.is_file():
        args.append('/win32icon:' + str(icon))
    args.extend('/resource:' + str(path) + ',' + name for path, name in resources)
    subprocess.run(args + [str(LAUNCHER / 'UpdateCore.cs'), str(LAUNCHER / 'BootstrapUI.cs')] + [str(path) for path in sources], check=True)


def release():
    if not re.fullmatch(r'\d+\.\d+\.\d+-alpha\.\d+', VERSION):
        raise ValueError('Unsupported version')
    build()
    OUT.mkdir(parents=True, exist_ok=True)
    launcher = OUT / 'QemuM.exe'
    compile_launcher(launcher, 'Launcher', [LAUNCHER / 'Launcher.cs'])
    package = OUT / ('QemuM-' + VERSION + '-app.zip')
    with tempfile.TemporaryDirectory(prefix='release-source-', dir=ROOT / 'build') as temporary:
        stage = Path(temporary)
        copy_runtime(stage)
        shutil.copy2(launcher, stage / 'QemuM.Update.exe')
        compile_program(stage / 'VMManager.exe', icon=ROOT / 'build' / 'tools' / 'app.ico',
                        manifest=GUI / 'app.manifest')
        shutil.copy2(ROOT / 'docs' / 'ALPHA-GUIDE.md', stage / 'ALPHA-GUIDE.md')
        shutil.copy2(ROOT / 'docs' / 'UPDATE-PROTOCOL.md', stage / 'UPDATE-PROTOCOL.md')
        files = {path.relative_to(stage).as_posix(): hash_file(path)
                 for path in sorted(stage.rglob('*')) if path.is_file()}
        manifest = dict(schema=1, min_launcher_protocol=1, version=VERSION, channel='alpha',
                        entrypoint='VMManager.exe', requires_python=True, files=files)
        (stage / 'package.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
        with zipfile.ZipFile(package, 'w', zipfile.ZIP_DEFLATED) as archive:
            for path in sorted(stage.rglob('*')):
                if path.is_file():
                    archive.write(path, path.relative_to(stage).as_posix())
    digest = hash_file(package)
    digest_path = OUT / 'app.sha256'
    digest_path.write_text(digest + '\n', encoding='ascii')
    setup = OUT / ('QemuM-' + VERSION + '-Setup.exe')
    compile_launcher(setup, 'Setup', [LAUNCHER / 'Setup.cs'],
                     [(package, 'app.zip'), (digest_path, 'app.sha256'), (launcher, 'QemuM.exe')])
    update = dict(schema=1, min_launcher_protocol=1, version=VERSION, channel='alpha',
                  url=f'https://github.com/ivan22822/QemuM/releases/download/v{VERSION}/{package.name}',
                  sha256=digest)
    (OUT / 'update.json').write_text(json.dumps(update, indent=2), encoding='utf-8')
    assets = [setup, package, OUT / 'update.json']
    (OUT / 'SHA256SUMS.txt').write_text(
        ''.join(hash_file(path) + '  ' + path.name + '\n' for path in assets), encoding='ascii')
    print('Release artifacts:', OUT)


if __name__ == '__main__':
    release()
