"""Private VDI/raw/qcow2 imports, external branches and restoration."""
from pathlib import Path
import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
from unittest.mock import patch
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src/backend'))
from vm_snapshot_external import ExternalSnapshots,sha
from vm_snapshot_service import SnapshotService


def main():
    image,io=shutil.which('qemu-img'),shutil.which('qemu-io')
    if not image or not io:raise RuntimeError('QEMU tools required')
    def run(exe,args):
        result=subprocess.run([exe,*args],stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0),timeout=30)
        if result.returncode:raise ValueError(result.stderr.decode(errors='replace'))
        return result.stdout.decode()
    for fmt in ('vdi','raw','qcow2'):
        with tempfile.TemporaryDirectory(prefix='external-'+fmt+'-',dir=ROOT/'build') as temporary:
            base=Path(temporary);root=base/'machines';root.mkdir();original=base/('user-original.'+fmt)
            run(image,['create','-f',fmt,str(original),'8M']);before=sha(original)
            data=dict(disk_path=str(original),iso_path='',disk_format=fmt,ram_mb=128,cores=1,extra_args=[])
            config=root/'test.json';config.write_text(json.dumps(data))
            def stopped(*args):raise ConnectionRefusedError('stopped test')
            service=SnapshotService(root,'test',stopped,lambda:json.loads(config.read_text()),image,processes=lambda:[],run=run)
            external=ExternalSnapshots(service)
            try:
                first=external.execute(external.plan(options=dict(label='Before changes')))
                assert sha(original)==before
                run(io,['-f','qcow2','-c','write -P 0xAA 0 4096',service.load()['disk_path']])
                second=external.execute(external.plan(options=dict(label='A bytes')))
                run(io,['-f','qcow2','-c','write -P 0xBB 0 4096',service.load()['disk_path']])
                external.execute(external.plan(first['group_id']))
                run(io,['-f','qcow2','-c','read -P 0x00 0 4096',service.load()['disk_path']])
                external.execute(external.plan(second['group_id']))
                run(io,['-f','qcow2','-c','read -P 0xAA 0 4096',service.load()['disk_path']])
                graph=external.check_graph(service.store.catalog())
                base_node=next(node for node in graph['nodes'].values() if node['parent'] is None)
                try:run(io,['-f',fmt,'-c','write -P 0xCC 0 4096',base_node['path']])
                except ValueError:pass
                else:raise AssertionError('Sealed base accepted a writer')
                assert sha(original)==before
                assert all(row['state']=='ready' for row in external.catalog(False)['rows'])
                unchanged_config=config.read_bytes();unchanged_catalog=service.store.path.read_bytes()
                for action in ('commit','rebase','merge'):
                    plan=external.maintenance_plan(action)
                    assert plan['executable'] is False and plan['required_bytes']>0 and len(plan['dependencies'])==len(graph['nodes'])
                assert config.read_bytes()==unchanged_config and service.store.path.read_bytes()==unchanged_catalog
                # Interrupt between publishing configuration and graph. The
                # previous writable tip and exact config must be recoverable.
                pending=external.plan(options=dict(label='Interrupted external point'))
                with patch.object(service.store,'replace_catalog',side_effect=OSError('injected graph publication failure')):
                    try:external.execute(pending)
                    except OSError:pass
                    else:raise AssertionError('Graph publication failure swallowed')
                external.recover(service.store.operation(pending['id']))
                assert config.read_bytes()==unchanged_config and service.store.path.read_bytes()==unchanged_catalog
                run(io,['-f','qcow2','-c','read -P 0xAA 0 4096',service.load()['disk_path']])
            finally:
                # Restore attributes only on this test's own generated files.
                for path in base.rglob('*'):
                    if path.is_file():os.chmod(path,stat.S_IREAD|stat.S_IWRITE)
    print('VDI/raw/qcow2 private immutable bases, external side branches and byte-verified restore passed.')


if __name__=='__main__':main()
