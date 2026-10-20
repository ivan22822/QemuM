"""Create and attach only a disposable VDI; never boot a user's guest."""
import json
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'src' / 'backend'))
import vm
from vm_disk import new_disk_format
from vm_console import snapshot_disks
from vm_backend import Service


def main():
    hidden = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
    qemu, image = shutil.which('qemu-system-x86_64'), shutil.which('qemu-img')
    if not qemu or not image:
        raise RuntimeError('QEMU tools required')
    with tempfile.TemporaryDirectory(prefix='vdi-', dir=ROOT / 'build') as temporary:
        disk = Path(temporary) / 'disposable.vdi'
        service = Service(temporary)
        try:
            service.create(dict(name='disposable-vdi', new_disk=True, size_gb=1,
                data=dict(disk_path=str(disk), iso_path='', ram_mb=128, cores=1, launch_profile='tcg')))
        finally:
            service.close()
        assert json.loads((Path(temporary) / 'machines' / 'disposable-vdi.json').read_text(encoding='utf-8'))['disk_format'] == 'vdi'
        info = json.loads(subprocess.check_output([image, 'info', '--output=json', str(disk)], creationflags=hidden))
        assert info['format'] == 'vdi'
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
        name = 'vdi-' + uuid.uuid4().hex[:10]
        data = dict(disk_path=str(disk), iso_path='', ram_mb=128, cores=1, launch_profile='tcg',
                    qmp_port=port, extra_args=['-display', 'none', '-S'])
        with subprocess.Popen([qemu, *vm.build_args(name, data, accelerator='tcg')],
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, creationflags=hidden) as child:
            try:
                deadline = time.monotonic() + 15
                while True:
                    try:
                        blocks = vm.qmp_command(name, port, 'query-block', timeout=1)
                        break
                    except (OSError, ValueError):
                        if child.poll() is not None or time.monotonic() >= deadline:
                            raise RuntimeError('Disposable VDI did not start')
                        time.sleep(0.1)
                assert any(block.get('inserted', {}).get('drv') == 'vdi' for block in blocks)
                try:
                    snapshot_disks(lambda command: vm.qmp_command(name, port, command))
                except ValueError:
                    pass
                else:
                    raise AssertionError('VDI internal snapshot was allowed')
                assert vm.qmp_command(name, port, 'query-status')['status'] in ('prelaunch', 'paused')
            finally:
                if child.poll() is None:
                    child.terminate()
                child.wait(timeout=10)
        assert json.loads(subprocess.check_output([image, 'info', '--output=json', str(disk)], creationflags=hidden))['format'] == 'vdi'
    print('Real disposable VDI creation, attachment and snapshot guard passed.')


if __name__ == '__main__':
    main()
