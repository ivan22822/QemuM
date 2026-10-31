"""Check native TCP owner matching on own loopback processes and fixture frames."""
import json,os,socket,struct,subprocess,sys,time
from contextlib import ExitStack
from build import ROOT,HIDDEN
from test_local_switch import packet,send,receive,quiet
sys.path.insert(0,str(ROOT/'src/backend'))
from vm_runtime import ProcessWatcher

def main():
    checks=[]
    with ExitStack() as stack:
        child=subprocess.Popen([str(ROOT/'build/switch/QemuM.Switch.exe'),'--guarded-ports','3'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=HIDDEN)
        def close():
            try:child.communicate(b'{"protocol":1,"action":"quit"}\n',timeout=5)
            except subprocess.TimeoutExpired:child.kill();child.communicate()
        stack.callback(close)
        status=json.loads(child.stdout.readline());ports=[row['port'] for row in status['ports']]
        def request(action,**values):
            child.stdin.write((json.dumps(dict(protocol=1,action=action,**values))+'\n').encode());child.stdin.flush()
            return json.loads(child.stdout.readline())
        watcher=ProcessWatcher(os.getpid());stack.callback(watcher.close)
        created=watcher.creation_time
        assert 'error' in request('authorize',index=0,pid=os.getpid(),created=created+1)
        checks.append('creation-time mismatch fails closed')
        for index in range(3):assert 'error' not in request('authorize',index=index,pid=os.getpid(),created=created)
        clients=[stack.enter_context(socket.create_connection(('127.0.0.1',port),timeout=2)) for port in ports]
        for client in clients:client.settimeout(2)
        until=time.monotonic()+3
        while not all(p['connected'] for p in request('status')['ports']):
            assert time.monotonic()<until;time.sleep(.01)
        frame=packet('ffffffffffff','525400000001')
        send(clients[0],frame);assert receive(clients[1])==frame and receive(clients[2])==frame
        checks.append('native reverse TCP tuple plus PID and creation time accepted')
        duplicate=stack.enter_context(socket.create_connection(('127.0.0.1',ports[0]),timeout=2));duplicate.settimeout(2)
        assert duplicate.recv(1)==b''
        checks.append('second client cannot replace connected owner')
        assert 'error' not in request('authorize',index=0,pid=os.getpid(),created=created)  # Reattach by the same native owner is idempotent.
        # A different process has a valid PID, but owns none of the client sockets.
        other=subprocess.Popen([sys.executable,'-c','import sys;sys.stdin.read()'],stdin=subprocess.PIPE,creationflags=HIDDEN)
        def stop_other():other.communicate(b'',timeout=5)
        stack.callback(stop_other);other_identity=ProcessWatcher(other.pid);stack.callback(other_identity.close)
        clients[2].close()
        until=time.monotonic()+3
        while request('status')['ports'][2]['connected']:
            assert time.monotonic()<until;time.sleep(.01)
        request('authorize',index=2,pid=other.pid,created=other_identity.creation_time)
        intruder=stack.enter_context(socket.create_connection(('127.0.0.1',ports[2]),timeout=2));intruder.settimeout(2)
        assert intruder.recv(1)==b''
        checks.append('foreign process blocked despite valid authorized PID')
        child.stdin.write(b'{"protocol":2,"action":"status"}\n');child.stdin.flush();assert 'error' in json.loads(child.stdout.readline())
        checks.append('incompatible control protocol rejected')
        assert request('status')['received']==1
        checks.append('status reports counters without Ethernet payload')
    result=dict(passed=len(checks),checks=checks)
    (ROOT/'build/switch/guard-results.json').write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps(result))

if __name__=='__main__':main()
