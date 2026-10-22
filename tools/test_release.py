"""Smoke-test a built release with its pinned Python in an isolated build fixture."""
import argparse
import json
from pathlib import Path
import subprocess
import zipfile

from build import ROOT, HIDDEN, compiler
from release import OUT, VERSION, LAUNCHER, compile_launcher, hash_file


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--python-archive', type=Path, required=True,
                        help='official python-3.14.8-embed-amd64.zip; hash is checked')
    parser.add_argument('--previous-package',type=Path,help='previous released app ZIP for an isolated upgrade test')
    args = parser.parse_args()
    output = ROOT / 'build' / 'test-results'
    output.mkdir(parents=True, exist_ok=True)
    exe = ROOT / 'build' / 'tests' / 'InstallTests.exe'
    compile_launcher(exe, 'InstallTests',
                     [ROOT / 'tests' / 'gui' / 'InstallTests.cs', LAUNCHER / 'Setup.cs'])
    # The source checkout can be much deeper than a normal installation root.
    # Opt this test process into modern .NET path handling; do not change Windows.
    exe.with_suffix('.exe.config').write_text('<configuration><runtime><AppContextSwitchOverrides value="Switch.System.IO.UseLegacyPathHandling=false;Switch.System.IO.BlockLongPaths=false"/></runtime></configuration>',encoding='utf-8')
    future = ROOT / 'build' / 'tests' / 'Future.exe'
    subprocess.run([compiler(), '/nologo', '/target:winexe', '/out:' + str(future),
                    str(ROOT / 'tests' / 'gui' / 'FutureFrontend.cs')], check=True)
    future_zip = ROOT / 'build' / 'tests' / 'future.zip'
    with zipfile.ZipFile(future_zip, 'w') as archive:
        archive.write(future, 'Future.exe')
        archive.writestr('package.json', json.dumps(dict(schema=1, min_launcher_protocol=1,
            channel='alpha', version=VERSION.rsplit('.',1)[0]+'.'+str(int(VERSION.rsplit('.',1)[1])+1), entrypoint='Future.exe', requires_python=False,
            files={'Future.exe': hash_file(future)})))
    same_zip=ROOT/'build/tests/same-version.zip'
    with zipfile.ZipFile(same_zip,'w') as archive:
        archive.write(future,'Future.exe')
        archive.writestr('package.json',json.dumps(dict(schema=1,min_launcher_protocol=1,channel='alpha',version=VERSION,entrypoint='Future.exe',requires_python=False,files={'Future.exe':hash_file(future)})))
    subprocess.run([str(exe), str(output), str(OUT / f'QemuM-{VERSION}-app.zip'),
                    str(OUT / 'QemuM.exe'), str(args.python_archive.resolve()), '', str(future_zip),
                    str(args.previous_package.resolve()) if args.previous_package else '',str(same_zip)],
                   timeout=75, creationflags=HIDDEN, check=True)
    print('Release install/startup test passed; existing installations untouched.')


if __name__ == '__main__':
    main()
