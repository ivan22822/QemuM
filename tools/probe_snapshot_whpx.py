"""Explicit hardware matrix probe, not part of the normal TCG-only test suite.

Only new paused guests and private media. Never enables Windows features,
installs drivers or touches running VMs. No guest CPU execution is requested.
"""
from pathlib import Path
import argparse
import json
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import uuid
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src/backend'))
import vm


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--explicit-hardware-probe',action='store_true',required=True);parser.parse_args()
    qemu,image=shutil.which('qemu-system-x86_64'),shutil.which('qemu-img')
    if not qemu or not image:raise RuntimeError('QEMU required')
    hidden=getattr(subprocess,'CREATE_NO_WINDOW',0);results=[]
    for accelerator in ('whpx','whpx,kernel-irqchip=off'):
        with tempfile.TemporaryDirectory(prefix='whpx-probe-',dir=ROOT/'build') as temporary:
            base=Path(temporary);name='probe-'+uuid.uuid4().hex[:10]
            paths=[base/('disk-'+str(index)+'.qcow2') for index in range(2)]
            for path in paths:subprocess.run([image,'create','-f','qcow2',str(path),'8M'],check=True,stdout=subprocess.DEVNULL,creationflags=hidden)
            with socket.socket() as listener:listener.bind(('127.0.0.1',0));port=listener.getsockname()[1]
            command=[qemu,'-name',name,'-accel',accelerator,'-cpu','host','-m','64','-smp','1','-machine','q35','-display','none','-nic','none','-S',
                     '-qmp','tcp:127.0.0.1:'+str(port)+',server=on,wait=off']
            for path in paths:command+=['-drive','file='+str(path)+',format=qcow2,if=virtio']
            log=(base/'probe.log').open('wb');process=subprocess.Popen(command,stdout=log,stderr=log,creationflags=hidden)
            request=lambda cmd,args=None:vm.qmp_command(name,port,cmd,args,timeout=3)
            result=dict(accelerator=accelerator,launched=False,save=False,load=False)
            try:
                deadline=time.monotonic()+10
                while True:
                    try:status=request('query-status')['status'];break
                    except (OSError,ValueError):
                        if process.poll() is not None or time.monotonic()>deadline:raise ValueError('WHPX partition initialization/QMP did not complete')
                        time.sleep(.05)
                if status not in ('prelaunch','paused'):raise ValueError('Probe guest unexpectedly not paused')
                result['launched']=True
                nodes=[row['inserted']['node-name'] for row in request('query-block') if row.get('inserted') and not row['inserted'].get('ro')]
                tag='probe-'+uuid.uuid4().hex
                for action,key in (('snapshot-save','save'),('snapshot-load','load')):
                    job='probe-'+uuid.uuid4().hex;request(action,{'job-id':job,'tag':tag,'vmstate':nodes[0],'devices':nodes})
                    deadline=time.monotonic()+20
                    while True:
                        state=next((row for row in request('query-jobs') if row['id']==job),None)
                        if state and state['status']=='concluded':
                            request('job-dismiss',{'id':job})
                            if state.get('error'):raise ValueError(str(state['error']))
                            result[key]=True;break
                        if time.monotonic()>deadline:raise ValueError('No conclusion for '+action)
                        time.sleep(.05)
            except Exception as error:result['detail']=str(error)
            finally:
                if process.poll() is None:
                    try:request('quit')
                    except (OSError,ValueError):pass
                try:process.wait(timeout=5)
                except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=5)
                log.close()
                text=(base/'probe.log').read_text(errors='replace').replace(str(base),'TEST_ROOT').replace(str(qemu),'QEMU')
                result['diagnostic']=text[-4000:];results.append(result)
    output=ROOT/'build/test-results/whpx-snapshot-probe.json';output.parent.mkdir(parents=True,exist_ok=True)
    output.write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(results,ensure_ascii=True,indent=2))


if __name__=='__main__':main()
