"""Build an isolated development copy. Never deploy to an installed manager."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'build' / 'app'
GUI = ROOT / 'src' / 'gui'
SOURCES = [GUI / name for name in ('VMManager.cs', 'GuiLaunchSettings.cs', 'PolarTheme.cs',
    'WorkspaceView.cs', 'EditorControls.cs', 'DistroAppearance.cs')]
REFERENCES = ['/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
              '/reference:System.Web.Extensions.dll']
HIDDEN = getattr(subprocess, 'CREATE_NO_WINDOW', 0)


def compiler():
    windows = Path(os.environ.get('WINDIR', r'C:\Windows'))
    for framework in ('Framework64', 'Framework'):
        path = windows / 'Microsoft.NET' / framework / 'v4.0.30319' / 'csc.exe'
        if path.is_file():
            return str(path)
    raise RuntimeError('Windows .NET Framework compiler csc.exe was not found.')


def compile_program(output, main=None, extra=(), icon=None, manifest=None):
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    args = [compiler(), '/nologo', '/platform:x64', '/optimize+', '/target:winexe',
            '/out:' + str(output), *REFERENCES]
    if main:
        args.append('/main:' + main)
    if icon:
        args.append('/win32icon:' + str(icon))
    if manifest:
        args.append('/win32manifest:' + str(manifest))
    subprocess.run(args + [str(path) for path in (*SOURCES, *extra)], check=True)


def copy_runtime(target):
    target = Path(target)
    target.mkdir(parents=True, exist_ok=True)
    for path in (ROOT / 'src' / 'backend').iterdir():
        if path.is_file() and path.suffix in ('.py', '.cmd'):
            shutil.copy2(path, target / path.name)
    shutil.copytree(ROOT / 'assets' / 'visual-assets', target / 'visual-assets', dirs_exist_ok=True)


def build():
    work = ROOT / 'build' / 'tools'
    work.mkdir(parents=True, exist_ok=True)
    helper = work / 'IconBuilder.exe'
    compile_program(helper, 'IconBuilder', (GUI / 'IconBuilder.cs',))
    icon = work / 'app.ico'
    subprocess.run([str(helper), str(icon)], check=True, creationflags=HIDDEN)
    copy_runtime(APP)
    compile_program(APP / 'VMManager.exe', icon=icon, manifest=GUI / 'app.manifest')
    print('Development build:', APP / 'VMManager.exe')


if __name__ == '__main__':
    argparse.ArgumentParser(description=__doc__).parse_args()
    build()
