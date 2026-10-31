"""Opt-in booted Linux fixture; no user VM, writable disks, NAT or host bridge.

Requires a separately supplied official Alpine virt x86_64 ISO and its published
SHA256. This explicit --live test intentionally boots its own guests unpaused;
ordinary --qemu regression tests remain paused. Never invoked by tools/test.py.
"""
import argparse,gzip,hashlib,json,queue,re,shutil,struct,subprocess,sys,threading,time,uuid
from pathlib import Path
from contextlib import ExitStack
from build import ROOT,HIDDEN
sys.path.insert(0,str(ROOT/'src/backend'))
from vm_switch import Manager
from vm_runtime import ProcessWatcher
from vm_stand_runtime import reserve


def iso_boot(data):
    if len(data)>128*1024*1024 or data[32769:32774]!=b'CD001':raise ValueError('Expected a small ISO9660 Alpine virt image')
    def directory(record):
        start=int.from_bytes(record[2:6],'little')*2048;size=int.from_bytes(record[10:14],'little');pos=start
        if start+size>len(data):raise ValueError('Invalid ISO directory')
        result={}
        while pos<start+size:
            length=data[pos]
            if not length:pos=(pos//2048+1)*2048;continue
            row=data[pos:pos+length];pos+=length
            if length<34:raise ValueError('Invalid ISO record')
            name=row[33:33+row[32]].decode('ascii','replace').split(';')[0].lower()
            extra=(33+row[32]+1)&~1
            while extra+4<=len(row):
                field_size=row[extra+2]
                if field_size<4 or extra+field_size>len(row):break
                if row[extra:extra+2]==b'NM' and field_size>=5 and row[extra+4]==0:
                    name=row[extra+5:extra+field_size].decode('utf-8');break
                extra+=field_size
            result[name]=row
        return result
    root=data[32768+156:32768+190];entries=directory(directory(root)['boot']);result={}
    for name in ('vmlinuz-virt','initramfs-virt','modloop-virt'):
        row=entries[name];start=int.from_bytes(row[2:6],'little')*2048;size=int.from_bytes(row[10:14],'little')
        if not size or start+size>len(data):raise ValueError('Invalid boot file')
        result[name]=data[start:start+size]
    return result


def cpio_file(name,content,mode=0o100755):
    name=name.encode()+b'\0'
    values=(1,mode,0,0,1,0,len(content),0,0,0,0,len(name),0)
    value=b'070701'+b''.join(('%08x'%n).encode() for n in values)+name
    value+=b'\0'*((-len(value))%4);value+=content
    return value+b'\0'*((-len(value))%4)


INIT=b'''#!/bin/busybox sh
/bin/busybox mount -t proc proc /proc
/bin/busybox mount -t sysfs sysfs /sys
/bin/busybox mount -t devtmpfs devtmpfs /dev
exec </dev/ttyS0 >/dev/ttyS0 2>&1
/bin/busybox --install -s /bin
export PATH=/bin:/sbin:/usr/bin:/usr/sbin
stty -echo
for module in virtio_pci virtio_net virtio_blk squashfs; do modprobe "$module"; done
mkdir -p /.modloop /tmp
sleep 1
mount -t squashfs -o ro /dev/vda /.modloop || exit 1
mv /lib/modules /lib/modules.boot
ln -s /.modloop/modules /lib/modules
modprobe ipv6
ip link set lo up
echo QEMUM_READY
while IFS= read -r command; do /bin/busybox sh -c "$command"; done
exec /bin/busybox sh
'''


class Guest:
    def __init__(self,command,log,stack):
        self.lines=queue.Queue(maxsize=2000);self.child=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,creationflags=HIDDEN)
        stack.callback(self.close)
        def reader():
            with log.open('wb') as output:
                total=0
                for line in self.child.stdout:
                    if total<2*1024*1024:output.write(line);output.flush();total+=len(line)
                    try:self.lines.put_nowait(line.decode('utf-8','replace').strip())
                    except queue.Full:pass
        self.reader=threading.Thread(target=reader,daemon=True);self.reader.start()
    def wait(self,predicate,timeout):
        end=time.monotonic()+timeout;seen=[]
        while time.monotonic()<end:
            if self.child.poll() is not None:raise RuntimeError('Own guest exited')
            try:line=self.lines.get(timeout=min(.2,max(.01,end-time.monotonic())))
            except queue.Empty:continue
            seen.append(line)
            if predicate(line):return seen
        raise TimeoutError('Guest console deadline; inspect private serial log')
    def run(self,command,timeout=30):
        token='QEMUM_DONE_'+uuid.uuid4().hex
        wire=(command+'; result=$?; echo '+token+':$result\n').encode()
        for offset in range(0,len(wire),8):
            self.child.stdin.write(wire[offset:offset+8]);self.child.stdin.flush();time.sleep(.015)
        lines=self.wait(lambda line:bool(re.fullmatch(token+r':\d+',line)),timeout)
        if lines[-1]!=token+':0':raise RuntimeError('Guest command failed: '+command+'; '+lines[-1])
        return lines[:-1]
    def close(self):
        if self.child.poll() is None:self.child.kill()  # Only the captured child handle.
        self.child.wait(10);self.reader.join(5)
        self.child.stdin.close();self.child.stdout.close()


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--live',action='store_true');parser.add_argument('--idle-scan',action='store_true');parser.add_argument('--iso',type=Path,required=True);parser.add_argument('--sha256',required=True);args=parser.parse_args()
    if not args.live:parser.error('explicit --live required; three own unpaused TCG guests')
    if not re.fullmatch('[0-9a-fA-F]{64}',args.sha256):parser.error('published SHA256 required')
    if args.iso.stat().st_size>128*1024*1024:raise ValueError('ISO exceeds fixture limit')
    data=args.iso.read_bytes()
    if hashlib.sha256(data).hexdigest()!=args.sha256.lower():raise ValueError('ISO SHA256 mismatch; no VM started')
    boot=iso_boot(data);exe=shutil.which('qemu-system-x86_64')
    if not exe:raise RuntimeError('QEMU missing')
    folder=ROOT/'build/live-net'/uuid.uuid4().hex;folder.mkdir(parents=True)
    kernel=folder/'vmlinuz';kernel.write_bytes(boot['vmlinuz-virt'])
    modules=folder/'modloop';modules.write_bytes(boot['modloop-virt'])
    initrd=folder/'initramfs';initrd.write_bytes(boot['initramfs-virt']+gzip.compress(cpio_file('init',INIT)+cpio_file('TRAILER!!!',b'',0),mtime=0))
    results=dict(iso_sha256=args.sha256.lower(),qemu=subprocess.check_output([exe,'--version'],text=True).splitlines()[0],accelerator='tcg',ram_mb=192,cores=1,checks=[],guest_os='Alpine kernel/initramfs fixture; not an installed desktop',checklist_12_4_complete=False,passed=False)
    try:
        with ExitStack() as stack:
            with ExitStack() as reservations:
                sockets=[reservations.enter_context(reserve()) for _ in range(4 if args.idle_scan else 3)];ports=[s.getsockname()[1] for s in sockets]
            manager=Manager(folder,ROOT/'build/app/QemuM.Switch.exe');record=manager.start(uuid.uuid4().hex,ports)
            # Idle probe authorizes this very Python process as the fourth
            # participant. Its lease ends on process exit; never bypass ownership.
            stack.callback(lambda:None if results.get('host_probe_lease') else manager.stop(record))
            guests=[]
            for i,port in enumerate(ports[:3]):
                command=[exe,'-accel','tcg','-m','192','-smp','1','-nodefaults','-display','none','-monitor','none','-serial','stdio','-no-reboot','-kernel',str(kernel),'-initrd',str(initrd),'-append','console=ttyS0 panic=1 net.ifnames=0','-netdev','socket,id=lan,connect=127.0.0.1:'+str(port),'-device','virtio-net-pci,netdev=lan,mac=52:54:00:77:00:0'+str(i+1)]
                command+=['-drive','if=virtio,format=raw,readonly=on,file='+str(modules)]
                guest=Guest(command,folder/('guest-'+str(i+1)+'.log'),stack);guests.append(guest)
                watcher=ProcessWatcher(guest.child.pid)
                try:manager.authorize(record,i,dict(pid=guest.child.pid,created=watcher.creation_time))
                finally:watcher.close()
            for i,guest in enumerate(guests):
                guest.wait(lambda line:line=='QEMUM_READY',90)
                guest.run('ip link set eth0 up && ip addr add 192.0.2.'+str(i+1)+'/24 dev eth0 && ip -6 addr add fd77::'+str(i+1)+'/64 dev eth0')
                guest.run('echo 0 > /proc/sys/net/ipv4/icmp_echo_ignore_broadcasts')
            time.sleep(3)
            if args.idle_scan:
                from idle_fixture import experiment
                results['host_probe_lease']=True
                results['helper_identity']={k:record[k] for k in ('pid','created')}
                results['idle_scan']=experiment(manager,record,ports[3],guests)
                results['experiment_completed']=True
                print(json.dumps(results['idle_scan']))
                return
            for i,guest in enumerate(guests):
                for j in range(3):
                    if i!=j:
                        guest.run('ping -c 3 -W 2 192.0.2.'+str(j+1));guest.run('ping6 -c 3 -W 2 fd77::'+str(j+1))
                neighbors='\n'.join(guest.run('ip neigh show dev eth0'))
                if '52:54:00:77:00:' not in neighbors:raise AssertionError('No learned guest ARP neighbor')
            results['checks'].append('all directed IPv4/IPv6 pairs and actual ARP neighbors')
            broadcast='\n'.join(guests[0].run('ping -c 3 -W 2 192.0.2.255'))
            if not all('from 192.0.2.'+str(n)+':' in broadcast for n in (2,3)):raise AssertionError('Broadcast not acknowledged by both guests')
            results['checks'].append('IPv4 broadcast acknowledged by both other guests')
            results['checklist_12_4_complete']=True
            def transfer(port):
                guests[1].run('nc -l -p '+str(port)+' > /tmp/qemum.payload & echo $! > /tmp/qemum.nc')
                started=time.monotonic();guests[0].run('dd if=/dev/zero bs=65536 count=128 2>/dev/null | nc -w 3 192.0.2.2 '+str(port),40)
                elapsed=time.monotonic()-started;count='\n'.join(guests[1].run('wc -c < /tmp/qemum.payload'))
                if not re.search(r'\b8388608\b',count):raise AssertionError('Bulk payload size mismatch: '+count)
                digest='\n'.join(guests[1].run('sha256sum /tmp/qemum.payload'))
                if hashlib.sha256(bytes(8388608)).hexdigest() not in digest:raise AssertionError('Bulk SHA256 mismatch')
                return dict(bytes=8388608,seconds=elapsed,mbps=8388608*8/elapsed/1000000,includes_serial_and_nc_wait=True,sha256_verified=True)
            results['throughput']=transfer(7777)
            guests[2].close()
            guests[0].run('ping -c 5 -W 2 192.0.2.2')
            results['throughput_after_peer_crash']=transfer(7778)
            results['checks'].append('surviving guests communicate after third captured guest terminates')
            results['network']=manager.status(record);results['passed']=True
    finally:(folder/'results.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
    print('Live guest checks passed; evidence: '+str((folder/'results.json').relative_to(ROOT)))


if __name__=='__main__':main()
