"""Own loopback switch helpers and synthetic Ethernet frames; no user VM access."""
import json
from pathlib import Path
import socket
import struct
import subprocess
import argparse
import shutil
import queue
import threading
import sys
import time
from contextlib import ExitStack
from build import ROOT,compiler,HIDDEN
sys.path.insert(0,str(ROOT/'src/backend'))
from vm_runtime import ProcessWatcher


def exact(client,n):
    result=b''
    while len(result)<n:
        part=client.recv(n-len(result))
        if not part:raise EOFError()
        result+=part
    return result

def receive(client):return exact(client,struct.unpack('!I',exact(client,4))[0])
def packet(dst,src,payload=b'fixture'):
    return bytes.fromhex(dst+src+'0800')+payload
def send(client,data):client.sendall(struct.pack('!I',len(data))+data)
def quiet(client):
    try:client.recv(1)
    except socket.timeout:return
    raise AssertionError('Unexpected frame')

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--qemu',action='store_true');options=parser.parse_args()
    output=ROOT/'build/switch';output.mkdir(parents=True,exist_ok=True)
    exe=output/'QemuM.Switch.exe'
    subprocess.run([compiler(),'/nologo','/target:exe','/platform:x64','/optimize+','/reference:System.Web.Extensions.dll','/out:'+str(exe),str(ROOT/'src/switch/LocalSwitch.cs'),str(ROOT/'src/switch/WindowsPeer.cs')],check=True)
    checks=[]
    with ExitStack() as stack:
        def helper():
            child=subprocess.Popen([str(exe),'--test-ports','3'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=HIDDEN)
            def close():
                try:child.communicate(b'quit\n',timeout=5)
                except subprocess.TimeoutExpired:child.kill();child.communicate()
            stack.callback(close)
            ports=list(map(int,child.stdout.readline().decode().strip().split(',')))
            clients=[]
            for port in ports:
                client=stack.enter_context(socket.create_connection(('127.0.0.1',port),timeout=2));client.settimeout(.3);clients.append(client)
            return clients,ports
        clients,ports=helper();a,b,c=clients;other,_=helper()
        broadcast=packet('ffffffffffff','525400000001')
        wire=struct.pack('!I',len(broadcast))+broadcast
        for byte in wire:a.sendall(bytes([byte]))
        assert receive(b)==broadcast and receive(c)==broadcast;quiet(a)
        checks.append('fragmented broadcast to two peers, no source echo')
        reply=packet('525400000001','525400000002')
        send(b,reply);assert receive(a)==reply;quiet(c)
        checks.append('learned unicast only to destination')
        for peer in other:quiet(peer)
        checks.append('two switch processes isolated')
        multicast=packet('333300000001','525400000003')
        send(c,multicast);assert receive(a)==multicast and receive(b)==multicast
        checks.append('IPv6 multicast MAC flooding')
        for _ in range(3):send(b,reply)
        assert all(receive(a)==reply for _ in range(3));quiet(c)
        checks.append('coalesced frames preserve bytes and order')
        c.sendall(struct.pack('!I',0xffffffff));assert c.recv(1)==b''
        send(b,reply);assert receive(a)==reply
        checks.append('oversized frame disconnects only its sender')
        # Reconnect to the released port; a new MAC must not inherit old learning.
        replacement=stack.enter_context(socket.create_connection(('127.0.0.1',ports[2]),timeout=2));replacement.settimeout(.3)
        send(replacement,packet('ffffffffffff','525400000004'))
        assert receive(a)==packet('ffffffffffff','525400000004') and receive(b)==packet('ffffffffffff','525400000004')
        checks.append('port reconnect resumes forwarding')
        if options.qemu:
            binary=shutil.which('qemu-system-x86_64')
            if not binary:raise RuntimeError('QEMU is required for --qemu')
            # Another helper, with no synthetic clients on its three NIC ports.
            child=subprocess.Popen([str(exe),'--guarded-ports','3'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=HIDDEN)
            def stop_switch():
                try:child.communicate(b'{"protocol":1,"action":"quit"}\n',timeout=5)
                except subprocess.TimeoutExpired:child.kill();child.communicate()
            stack.callback(stop_switch)
            hello=json.loads(child.stdout.readline());assert hello['protocol']==1
            qports=[row['port'] for row in hello['ports']]
            def control(action,**data):
                child.stdin.write((json.dumps(dict(protocol=1,action=action,**data))+'\n').encode());child.stdin.flush()
                reply=json.loads(child.stdout.readline());assert 'error' not in reply,reply;return reply
            guests=[]
            def stop_guest(p):
                if p.poll() is None:p.kill()
                p.communicate(timeout=5)
            for index,port in enumerate(qports):
                command=[binary,'-S','-nodefaults','-accel','tcg','-machine','pc','-m','32','-display','none','-monitor','none','-qmp','stdio','-netdev','socket,id=lan,connect=127.0.0.1:'+str(port),'-device','e1000e,netdev=lan,mac=52:54:00:00:01:0'+str(index+1)]
                p=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=HIDDEN);guests.append(p);stack.callback(stop_guest,p)
                identity=ProcessWatcher(p.pid)
                try:control('authorize',index=index,pid=p.pid,created=identity.creation_time)
                finally:identity.close()
            deadline=time.monotonic()+5
            while not all(row['connected'] for row in control('status')['ports']):
                assert time.monotonic()<deadline;time.sleep(.02)
            for p in guests:
                incoming=queue.Queue()
                def read(process=p,received=incoming):
                    for line in process.stdout:received.put(json.loads(line))
                reader=threading.Thread(target=read,daemon=True);reader.start()
                assert 'QMP' in incoming.get(timeout=12)
                def request(command,arguments=None):
                    p.stdin.write((json.dumps(dict(execute=command,arguments=arguments or {}))+'\n').encode());p.stdin.flush()
                    while True:
                        value=incoming.get(timeout=12)
                        if 'return' in value:return value['return']
                        if 'error' in value:raise AssertionError(value)
                request('qmp_capabilities')
                assert 'socket' in request('human-monitor-command',{'command-line':'info network'})
                p.stdin.write(b'{"execute":"quit"}\n');p.stdin.flush();assert p.wait(timeout=10)==0;reader.join(2)
            checks.append('three concurrent diskless paused TCG QEMU instances accept switch endpoints')
    (output/'results.json').write_text(json.dumps(dict(checks=checks,passed=len(checks)),indent=2),encoding='utf-8')
    print(json.dumps(dict(passed=len(checks),checks=checks)))

if __name__=='__main__':main()
