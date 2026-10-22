"""Real independent Windows guardian against a private fake QGA TCP peer.

Exercises protocol and parent death; not a substitute for an installed guest GA.
"""
from pathlib import Path
import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
import time
import uuid
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src/backend'))
from vm_snapshot_agent import COMMANDS
from vm_snapshot_store import Store
from vm_runtime import ProcessWatcher


def main():
    hidden=getattr(subprocess,'CREATE_NO_WINDOW',0)
    with tempfile.TemporaryDirectory(prefix='agent-guardian-',dir=ROOT/'build') as temporary:
        store=Store(Path(temporary)/'machines','test');operation_id=str(uuid.uuid4());stop=threading.Event();state=['thawed'];calls=[]
        with socket.socket() as listener:
            listener.bind(('127.0.0.1',0));listener.listen();listener.settimeout(.2);port=listener.getsockname()[1]
            def peer():
                while not stop.is_set():
                    try:connection,_=listener.accept()
                    except socket.timeout:continue
                    with connection:
                        connection.settimeout(5);reader=connection.makefile('rb')
                        while not stop.is_set():
                            line=reader.readline()
                            if not line:break
                            request=json.loads(line.lstrip(b'\xff'));command=request['execute'];calls.append(command)
                            if command=='guest-sync-delimited':result=request['arguments']['id']
                            elif command=='guest-info':result=dict(supported_commands=[dict(name=item,enabled=True) for item in COMMANDS])
                            elif command=='guest-fsfreeze-status':result=state[0]
                            elif command=='guest-fsfreeze-freeze':state[0]='frozen';result=1
                            elif command=='guest-fsfreeze-thaw':state[0]='thawed';result=1
                            else:raise AssertionError(command)
                            connection.sendall((b'\xff' if command=='guest-sync-delimited' else b'')+json.dumps({'return':result}).encode()+b'\n')
                        reader.close()
            thread=threading.Thread(target=peer,daemon=True);thread.start()
            parent=subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)'],creationflags=hidden)
            own=ProcessWatcher(os.getpid());parent_watch=ProcessWatcher(parent.pid);guardian=None
            try:
                guardian=subprocess.Popen([sys.executable,str(ROOT/'src/backend/vm_snapshot_agent.py'),str(store.root),'test',operation_id,str(port),str(os.getpid()),str(own.creation_time),str(parent.pid),str(parent_watch.creation_time),'10'],creationflags=hidden)
                status=store.folder/'agent'/(operation_id+'.json');deadline=time.monotonic()+10
                while not status.exists() or Store.read(status)['state']!='frozen':
                    if guardian.poll() is not None or time.monotonic()>deadline:raise AssertionError('Guardian did not freeze its private peer')
                    time.sleep(.05)
                try:store.assert_clear()
                except ValueError:pass
                else:raise AssertionError('Unconfirmed thaw did not block operations')
                parent.terminate();parent.wait(timeout=5)
                guardian.wait(timeout=5)
                assert Store.read(status)['state']=='thawed' and state[0]=='thawed'
                assert calls.count('guest-fsfreeze-freeze')==1 and calls.count('guest-fsfreeze-thaw')==1
                store.assert_clear()
            finally:
                own.close();parent_watch.close()
                if parent.poll() is None:parent.terminate();parent.wait(timeout=5)
                if guardian is not None and guardian.poll() is None:guardian.terminate();guardian.wait(timeout=5)
                stop.set();thread.join(timeout=2)
    print('Actual independent guardian protocol, listener identity, parent death and confirmed thaw passed against private fake QGA.')


if __name__=='__main__':main()
