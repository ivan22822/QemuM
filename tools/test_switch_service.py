"""Identity, integrity, native updater lock and autonomous helper lifecycle."""
from contextlib import ExitStack
from copy import deepcopy
from pathlib import Path
import json,shutil,socket,subprocess,sys,tempfile,time
from build import ROOT,HIDDEN
from release import compile_launcher
sys.path.insert(0,str(ROOT/'src/backend'))
from vm_switch import Manager,SwitchError,alive
from vm_runtime import ProcessWatcher
from vm_stand_runtime import reserve

def main():
    output=ROOT/'build/switch/UpdateLockTests.exe';compile_launcher(output,'SwitchUpdateLockTests',[ROOT/'tests/switch/UpdateLockTests.cs'])
    checks=[]
    with tempfile.TemporaryDirectory(dir=ROOT/'build',prefix='switch-control-') as temp:
        install=Path(temp).resolve();data=install/'data';data.mkdir();runtime=install/'runtime Юникод space';runtime.mkdir()
        for file in ('QemuM.Switch.exe','switch-manifest.json'):shutil.copy2(ROOT/'build/app'/file,runtime/file)
        manager=Manager(data,runtime/'QemuM.Switch.exe');manager.verify()
        checks.append('helper protocol and manifest verified in Unicode/space path')
        with ExitStack() as leases:
            sockets=[leases.enter_context(reserve()) for _ in range(2)];ports=[s.getsockname()[1] for s in sockets]
        record=manager.start('1'*32,ports)
        try:
            subprocess.run([str(output),str(install),'blocked'],check=True,timeout=10,creationflags=HIDDEN)
            checks.append('native updater blocks replacement while helper lease is alive')
            other=Manager(data,manager.helper);assert other.status(record)['ports'][0]['port']==ports[0]
            checks.append('new controller recovers exact existing helper without restart')
            bad=deepcopy(record);bad['created']+=1
            try:other.status(bad)
            except SwitchError:pass
            else:raise AssertionError('Wrong creation time accepted')
            checks.append('altered process creation time rejected')
            bad=deepcopy(record);bad['pipe']='qemum-switch-'+'0'*32
            try:other.status(bad)
            except SwitchError:pass
            else:raise AssertionError('Foreign pipe accepted')
            checks.append('journal pipe/run identity mismatch rejected')
            owner=subprocess.Popen([sys.executable,'-c','import sys;sys.stdin.read()'],stdin=subprocess.PIPE,creationflags=HIDDEN)
            watcher=ProcessWatcher(owner.pid)
            try:
                manager.authorize(record,0,dict(pid=owner.pid,created=watcher.creation_time))
                try:manager.stop(record)
                except SwitchError:pass
                else:raise AssertionError('Stopped helper with live owned guest')
                checks.append('helper refuses shutdown while any authorized owner lives')
            finally:watcher.close();owner.communicate(b'',timeout=5)
            deadline=time.monotonic()+10
            while alive(record) and time.monotonic()<deadline:time.sleep(.05)
            assert not alive(record);checks.append('helper exits autonomously after final owner terminates')
            subprocess.run([str(output),str(install),'allowed'],check=True,timeout=10,creationflags=HIDDEN)
            checks.append('updater unblocks after helper exit')
        finally:
            if alive(record):manager.stop(record)
        original=manager.helper.read_bytes();manager.helper.write_bytes(original+b'changed')
        try:
            try:manager.verify()
            except SwitchError:pass
            else:raise AssertionError('Tampered helper accepted')
        finally:manager.helper.write_bytes(original)
        checks.append('tampered executable rejected before launch')
        with reserve() as occupied:
            ports=[occupied.getsockname()[1]]
            with reserve() as free:ports.append(free.getsockname()[1])
            try:manager.start('2'*32,ports)
            except (SwitchError,OSError):pass
            else:raise AssertionError('Occupied port accepted')
        checks.append('occupied port fails without retrying another address')
    result=dict(passed=len(checks),checks=checks)
    (ROOT/'build/switch/service-results.json').write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps(result))

if __name__=='__main__':main()
