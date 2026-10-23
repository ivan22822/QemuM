"""Measure a read-only plan on private fixtures; --qemu permits diskless probes."""
import argparse
from collections import defaultdict
from contextlib import ExitStack
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src/backend'))
import vm,vm_backend,vm_profiles
from vm_backend import Service
from vm_stands import Store,create
from vm_stand_plan import upgrade
from vm_stand_runtime import reserve


def run(library):
    image=shutil.which('qemu-img.exe')
    if not image:raise ValueError('QEMU tools required')
    old=vm.ROOT;counts=defaultdict(int);elapsed=defaultdict(float)
    def timed(label,call):
        def invoke(*args,**kwargs):
            counts[label]+=1;start=time.perf_counter()
            try:return call(*args,**kwargs)
            finally:elapsed[label]+=time.perf_counter()-start
        return invoke
    with tempfile.TemporaryDirectory(dir=ROOT/'build',prefix='stand-plan-benchmark-') as folder:
        root=Path(folder);(root/'machines').mkdir();names=['probe'+str(i) for i in range(library)]
        with ExitStack() as ports:
            for i,name in enumerate(names):
                port=ports.enter_context(reserve()).getsockname()[1]
                disk=root/(name+'.qcow2');subprocess.run([image,'create','-f','qcow2',str(disk),'8M'],check=True,capture_output=True)
                data=dict(label=name,disk_path=str(disk),disk_format='qcow2',iso_path='',ram_mb=128,cores=1,launch_profile='tcg',qmp_port=port,
                          extra_args=['-display','none'],vga='none',network_adapters=[dict(id='lan',type='user',model='e1000e',mac='52:54:00:00:05:'+format(i,'02x'))])
                (root/'machines'/(name+'.json')).write_text(json.dumps(data))
        row=upgrade(create('Private plan benchmark',names[:2]));row=Store(root).save(row,0)
        service=Service(root)
        try:
            with patch.object(vm_backend,'qemu_processes',timed('process_inventory',vm_backend.qemu_processes)),patch.object(vm_profiles,'probe',timed('accelerator_probe',vm_profiles.probe)),patch.object(service,'query',timed('qmp_queries',service.query)):
                start=time.perf_counter();plan=service.stand_plan(dict(id=row['id']));total=time.perf_counter()-start
                service.stand_discard(dict(plan_id=plan['id']))
            assert not service.sessions,'Planning must not start any actual VM'
            print(json.dumps(dict(library=library,members=2,total_seconds=round(total,3),calls=dict(counts),seconds={k:round(v,3) for k,v in elapsed.items()},actual_vm_starts=0)))
        finally:service.close();vm.ROOT=old


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--qemu',action='store_true',required=True);parser.add_argument('--library',type=int,default=4);args=parser.parse_args()
    if not 2<=args.library<=64:parser.error('library: 2–64')
    run(args.library)
