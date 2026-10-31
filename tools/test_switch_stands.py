"""Real stand lifecycle on own private qcow2 media and paused TCG processes."""
from contextlib import ExitStack
from pathlib import Path
from unittest.mock import patch
import json,shutil,socket,subprocess,sys,tempfile,time
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'src/backend'))
import vm,vm_switch
from vm_backend import Service
from vm_stands import Store,create
from vm_stand_plan import upgrade

def main():
    exe=shutil.which('qemu-img')
    if not exe:raise RuntimeError('qemu-img required')
    checks=[];original=vm_switch.subprocess.Popen;helpers=[];services=[]
    def track(args,*a,**kw):
        child=original(args,*a,**kw)
        if isinstance(args,list) and '--serve' in args:helpers.append(child)
        return child
    with tempfile.TemporaryDirectory(dir=ROOT/'build',prefix='switch-stand-') as folder,patch('vm_switch.subprocess.Popen',side_effect=track):
        root=Path(folder).resolve();(root/'machines').mkdir();runtime=root/'runtime space Юникод';runtime.mkdir()
        for file in ('QemuM.Switch.exe','switch-manifest.json'):shutil.copy2(ROOT/'build/app'/file,runtime/file)
        helper=runtime/'QemuM.Switch.exe';previous=vm.ROOT
        with ExitStack() as leases:
            sockets=[leases.enter_context(socket.socket()) for _ in range(4)]
            for s in sockets:s.bind(('127.0.0.1',0))
            ports=[s.getsockname()[1] for s in sockets]
        names=['switch-probe-'+str(i) for i in range(4)]
        for i,name in enumerate(names):
            disk=root/(name+'.qcow2');subprocess.run([exe,'create','-f','qcow2',str(disk),'8M'],capture_output=True,check=True)
            data=dict(label=name,disk_path=str(disk),disk_format='qcow2',iso_path='',ram_mb=128,cores=1,launch_profile='tcg',qmp_port=ports[i],extra_args=['-display','none'],vga='none',network_adapters=[dict(id='lan',type='user',model='e1000e',mac='52:54:00:00:00:'+str(i+1).zfill(2)),dict(id='nat',type='user',model='e1000e',mac='52:54:00:00:01:'+str(i+1).zfill(2))])
            (root/'machines'/(name+'.json')).write_text(json.dumps(data),encoding='utf-8')
        before={p.name:p.read_bytes() for p in (root/'machines').iterdir()}
        def service():
            s=Service(root);s.switch_helper=helper;services.append(s);start=s.start
            s.start=lambda args,**kwargs:start(dict(args,paused=True),**kwargs)
            return s
        s=service()
        def definition(members,title='Fixture'):
            row=upgrade(create(title,members));row.update(schema_version=4,multicast=[],switches=[dict(id=__import__('uuid').uuid4().hex,name='Private network',members=[dict(vm=n,nic='lan') for n in members])]);return Store(root).save(row,0)
        def finish(plan,expected='completed'):
            s.stand_execute(dict(plan_id=plan['id'],confirm=plan['id']));job=s.stand_runtime.jobs[plan['id']];job['thread'].join(90)
            status=s.stand_status(dict(operation_id=plan['id']));assert status['state']==expected,status;return status
        def stop(row):finish(s.stand_plan(dict(id=row['id'],action='stop',force=True)))
        try:
            row=definition(names[:3]);preview=s.stand_plan(dict(id=row['id']));assert not helpers
            reserved=list(preview['ports'].values())
            for port in reserved:
                with socket.socket() as probe:
                    try:probe.bind(('127.0.0.1',port))
                    except OSError:pass
                    else:raise AssertionError('Preview did not reserve port')
            s.stand_discard(dict(plan_id=preview['id']))
            for port in reserved:
                with socket.socket() as probe:probe.bind(('127.0.0.1',port))
            checks.append('preview/discard reserve and release ports without starting helper or VM')
            finish(s.stand_plan(dict(id=row['id'])))
            record=s.stand_runtime._journal(row['id'])['switches'][row['switches'][0]['id']]
            state=s.stand_runtime.host.switch_manager().status(record);assert all(p['connected'] for p in state['ports'])
            for n in names[:3]:
                info=s.query(n,vm.load(n),'human-monitor-command',{'command-line':'info network'});assert 'socket' in info and 'user' in info,info
            assert {p.name:p.read_bytes() for p in (root/'machines').iterdir()}==before
            checks.append('three private paused TCG guests connect; original NAT and config bytes preserved')
            s.close();s=service()
            reopened=s.stand_overview(dict(id=row['id']))
            assert all(n['state']=='connected' and all(m['connected'] for m in n['members']) for n in reopened['network_diagnostics'])
            recovered=s.stand_reconcile(dict(id=row['id'],revision=row['revision'],confirm=row['id']))
            assert not recovered['start_blocked'] and all(p['connected'] for p in recovered['networks'][row['switches'][0]['id']]['ports']),recovered
            checks.append('backend closes without stopping network; new backend verifies process and reconnects')
            corrupt=s.stand_runtime._journal(row['id']);corrupt['switches'][row['switches'][0]['id']]['created']+=1;s.stand_runtime._journal(row['id'],corrupt)
            try:s.stand_plan(dict(id=row['id']))
            except ValueError:pass
            else:raise AssertionError('Wrong identity accepted')
            corrupt['switches'][row['switches'][0]['id']]=record;s.stand_runtime._journal(row['id'],corrupt)
            checks.append('mismatching helper identity blocks attach and launch')
            own=next(p for p in helpers if p.pid==record['pid']);own.kill();own.wait(5);count=len(helpers)
            try:s.stand_plan(dict(id=row['id']))
            except ValueError:pass
            else:raise AssertionError('Lost switch silently restarted')
            assert len(helpers)==count
            checks.append('lost helper with live guests blocks automatic restart')
            stop(row);assert not s.stand_runtime._journal(row['id'])['switches']
            checks.append('stop works after helper crash and removes dead network record')
            left=definition(names[:2],'Left');right=definition(names[2:],'Right')
            finish(s.stand_plan(dict(id=left['id'])));finish(s.stand_plan(dict(id=right['id'])))
            assert helpers[-1].pid!=helpers[-2].pid
            stop(left);assert helpers[-2].poll() is not None and helpers[-1].poll() is None
            stop(right);checks.append('two stands use independent helpers; stopping one preserves the other')
            # Cancel after helper creation but before the first guest starts.
            slow=definition(names[:2],'Cancellation');slow['startup'][names[0]]['delay']=30;slow=Store(root).save(slow,slow['revision'])
            plan=s.stand_plan(dict(id=slow['id']));s.stand_execute(dict(plan_id=plan['id'],confirm=plan['id']))
            deadline=time.monotonic()+15
            while not s.stand_runtime._journal(slow['id']).get('switches'):
                assert time.monotonic()<deadline;time.sleep(.02)
            s.stand_cancel(dict(operation_id=plan['id']));s.stand_runtime.jobs[plan['id']]['thread'].join(15)
            assert s.stand_status(dict(operation_id=plan['id']))['state']=='cancelled'
            assert not s.stand_runtime._journal(slow['id'])['switches']
            checks.append('cancel before first VM stops only the empty owned helper')
            assert {p.name:p.read_bytes() for p in (root/'machines').iterdir()}==before
        finally:
            for owner in services:
                owner.stand_runtime.close()
                for job in owner.stand_runtime.jobs.values():job['thread'].join(15)
                for value in owner.sessions.values():
                    p=value['process']
                    if p.poll() is None:p.kill()
                    p.wait(10)
                owner.close()
            for child in helpers:
                if child.poll() is None:child.kill()
                child.wait(10)
            vm.ROOT=previous
    result=dict(passed=len(checks),checks=checks,guest_ip_delivery='not tested')
    (ROOT/'build/switch/stand-results.json').write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps(result))

if __name__=='__main__':main()
