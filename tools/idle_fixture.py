"""Fixed-address idle-scan experiment for our disposable loopback-only guests."""
import os,socket,struct,time
from test_local_switch import send,receive

ATT='192.0.2.10';ZOMBIE='192.0.2.3';TARGET='192.0.2.2'
MAC=bytes.fromhex('525400770010');ZM=bytes.fromhex('525400770003');TM=bytes.fromhex('525400770002')

def checksum(data):
    if len(data)%2:data+=b'\0'
    n=sum(struct.unpack('!%dH'%(len(data)//2),data))
    while n>>16:n=(n&65535)+(n>>16)
    return (~n)&65535

def packet(dstmac,src,dst,sport,dport,flags,seq=12345,ack=67890):
    a=socket.inet_aton(src);b=socket.inet_aton(dst)
    tcp=struct.pack('!HHIIBBHHH',sport,dport,seq,ack,80,flags,8192,0,0)
    c=checksum(a+b+struct.pack('!BBH',0,6,len(tcp))+tcp);tcp=tcp[:16]+struct.pack('!H',c)+tcp[18:]
    ip=struct.pack('!BBHHHBBH4s4s',69,0,40,10,0,64,6,0,a,b);ip=ip[:10]+struct.pack('!H',checksum(ip))+ip[12:]
    return dstmac+MAC+b'\x08\x00'+ip+tcp

def decode(frame):
    if len(frame)<54 or frame[12:14]!=b'\x08\x00':return None
    ip=frame[14:];n=(ip[0]&15)*4
    if n<20 or len(ip)<n+20 or ip[9]!=6:return None
    tcp=ip[n:]
    return dict(src=socket.inet_ntoa(ip[12:16]),dst=socket.inet_ntoa(ip[16:20]),ipid=int.from_bytes(ip[4:6],'big'),
                sport=int.from_bytes(tcp[:2],'big'),dport=int.from_bytes(tcp[2:4],'big'),seq=int.from_bytes(tcp[4:8],'big'),ack=int.from_bytes(tcp[8:12],'big'),flags=tcp[13])

def experiment(manager,record,port,guests):
    from vm_runtime import ProcessWatcher
    watcher=ProcessWatcher(os.getpid())
    try:manager.authorize(record,3,dict(pid=os.getpid(),created=watcher.creation_time))
    finally:watcher.close()
    with socket.create_connection(('127.0.0.1',port),timeout=3) as client:
        client.settimeout(.2)
        guests[2].run('arp -s '+ATT+' 52:54:00:77:00:10')
        guests[1].run('nc -l -p 7777 > /tmp/idle-target & echo $! > /tmp/idle-target.pid')
        def wait(predicate):
            until=time.monotonic()+3
            while time.monotonic()<until:
                try:r=decode(receive(client))
                except socket.timeout:continue
                if r and predicate(r):return r
            raise TimeoutError('No expected TCP response inside own fixture')
        counter=0
        def probe():
            nonlocal counter
            counter+=1;nonce=90000+counter
            send(client,packet(ZM,ATT,ZOMBIE,40000,61000,18,ack=nonce))
            return wait(lambda r:r['src']==ZOMBIE and r['dport']==40000 and r['seq']==nonce and r['flags']&4)['ipid']
        baseline=[probe() for _ in range(6)]
        # Confirm spoof transport with a temporary return-neighbor override, then
        # restore the real zombie MAC before performing the blind side channel.
        guests[1].run('arp -s '+ZOMBIE+' 52:54:00:77:00:10')
        send(client,packet(TM,ZOMBIE,TARGET,41000,7777,2,seq=55555,ack=0))
        response=wait(lambda r:r['src']==TARGET and r['dst']==ZOMBIE and r['dport']==41000 and r['ack']==55556 and r['flags']&18==18)
        send(client,packet(TM,ZOMBIE,TARGET,41000,7777,4,seq=55556,ack=0))
        guests[1].run('arp -s '+ZOMBIE+' 52:54:00:77:00:03')
        def resets():
            rows=[x.split() for x in guests[2].run('cat /proc/net/snmp') if x.startswith('Tcp:')]
            return int(rows[1][rows[0].index('OutRsts')])
        trials=[]
        for target_port in (7777,7778,7777,7778):
            before=probe();rst_before=resets()
            send(client,packet(TM,ZOMBIE,TARGET,42000,target_port,2,seq=12345,ack=0));time.sleep(.3)
            rst_after=resets();after=probe()
            trials.append(dict(port=target_port,before=before,after=after,delta=(after-before)&65535,zombie_extra_rsts=rst_after-rst_before))
        suitable=all((b-a)&65535==1 for a,b in zip(baseline,baseline[1:]))
        return dict(baseline_ipids=baseline,spoof_syn_reached_target=True,spoof_response=response,trials=trials,
                    zombie_suitable=suitable,idle_port_classification='not reliable' if not suitable else 'inspect trial deltas; baseline alone is insufficient',
                    tool='custom fixed-lab TCP/IPID probe, not Nmap',actual_linux_zombie=True)
