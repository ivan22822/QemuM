"""Boundary and EOF regression checks for the actual switch process."""
import json,socket,struct,subprocess,time
from contextlib import ExitStack
from build import ROOT,HIDDEN
from test_local_switch import send,receive,packet

def main():
    checks=[]
    with ExitStack() as stack:
        p=subprocess.Popen([str(ROOT/'build/switch/QemuM.Switch.exe'),'--test-ports','3'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=HIDDEN)
        def close():
            try:p.communicate(b'quit\n',timeout=5)
            except subprocess.TimeoutExpired:p.kill();p.communicate()
        stack.callback(close);ports=list(map(int,p.stdout.readline().decode().strip().split(',')))
        a=stack.enter_context(socket.create_connection(('127.0.0.1',ports[0])));a.settimeout(3)
        b=stack.enter_context(socket.create_connection(('127.0.0.1',ports[1])));b.settimeout(3)
        # EOF at every header/body offset, followed by a usable replacement client.
        sample=packet('ffffffffffff','525400000003',b'partial')
        wire=struct.pack('!I',len(sample))+sample
        for offset in range(len(wire)):
            with socket.create_connection(('127.0.0.1',ports[2]),timeout=2) as c:
                c.settimeout(2);c.sendall(wire[:offset]);c.shutdown(socket.SHUT_WR);assert c.recv(1)==b''
        checks.append('EOF at every byte of a partial header and frame')
        for length in (0,1,13,65536,0xffffffff):
            with socket.create_connection(('127.0.0.1',ports[2]),timeout=2) as c:
                c.settimeout(2);c.sendall(struct.pack('!I',length));assert c.recv(1)==b''
        checks.append('zero, short, max+1 and uint32-max rejected before body')
        for length in (14,1514,1518,9018,65535):
            frame=packet('ffffffffffff','525400000001',bytes(length-14));send(a,frame);assert receive(b)==frame
        checks.append('minimum, Ethernet, VLAN-sized, jumbo and maximum byte preservation')
        for ethertype in ('0806','0800','86dd','8100'):
            # Contents are opaque: IP source, VLAN tags and DHCP bytes are untouched.
            frame=bytes.fromhex('ffffffffffff525400000001'+ethertype)+b'\x00\x2a\x08\x00fixture-dhcp-source'
            send(a,frame);assert receive(b)==frame
        checks.append('ARP, IPv4, IPv6 and VLAN ethertypes forwarded unchanged')
        with socket.create_connection(('127.0.0.1',ports[2]),timeout=2) as c:
            c.settimeout(7);c.sendall(b'\x00');assert c.recv(1)==b''
        send(a,sample);assert receive(b)==sample
        checks.append('stalled partial header expires; other ports remain usable')
    result=dict(passed=len(checks),checks=checks)
    (ROOT/'build/switch/frame-results.json').write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps(result))

if __name__=='__main__':main()
