"""Separate QGA channel and independent bounded freeze/thaw guardian.

No guest installation, shell hooks or guest-exec. The guardian owns the QGA
connection for the whole freeze and thaws even if the GUI/backend disappears.
"""
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import uuid
from contextlib import contextmanager
from vm_snapshot_store import Store,identifier
from vm_runtime import watch_qmp_process,ProcessWatcher

COMMANDS={'guest-info','guest-sync-delimited','guest-fsfreeze-status','guest-fsfreeze-freeze','guest-fsfreeze-thaw'}


class AgentChannel:
    def __init__(self,port,pid,created):
        if type(port) is not int or not 1024<=port<=65535:raise ValueError('Некорректный локальный порт Guest Agent.')
        self.owner=watch_qmp_process(port)
        if self.owner.pid!=pid or self.owner.creation_time!=created:
            self.owner.close();raise ValueError('Канал Guest Agent принадлежит другому процессу.')
        try:
            self.socket=socket.create_connection(('127.0.0.1',port),timeout=2)
            self.reader=self.socket.makefile('rb')
            token=uuid.uuid4().int&((1<<53)-1)
            if self.call('guest-sync-delimited',dict(id=token),prefix=True)!=token:raise ValueError('Guest Agent не подтвердил синхронизацию канала.')
            info=self.call('guest-info')
            enabled={row['name'] for row in info.get('supported_commands',[]) if row.get('enabled')}
            if not COMMANDS<=enabled:raise ValueError('Guest Agent не поддерживает необходимые freeze/thaw команды.')
        except BaseException:
            self.close();raise

    def call(self,command,arguments=None,prefix=False):
        if command not in COMMANDS:raise ValueError('Произвольные команды гостя запрещены.')
        payload=dict(execute=command)
        if arguments is not None:payload['arguments']=arguments
        self.socket.sendall((b'\xff' if prefix else b'')+json.dumps(payload).encode()+b'\n')
        line=self.reader.readline(1024*1024)
        if not line:raise ValueError('Канал Guest Agent закрыт.')
        value=json.loads(line.lstrip(b'\xff'))
        if 'error' in value:raise ValueError('Guest Agent: '+str(value['error']))
        if 'return' not in value:raise ValueError('Некорректный ответ Guest Agent.')
        return value['return']

    def close(self):
        if hasattr(self,'reader'):self.reader.close()
        if hasattr(self,'socket'):self.socket.close()
        self.owner.close()


def guardian(store,operation_id,port,pid,created,parent_pid,parent_created,seconds=10):
    operation_id=identifier(operation_id)
    if not 2<=seconds<=15:raise ValueError('Лимит заморозки — 2–15 секунд.')
    folder=store.folder/'agent';status=folder/(operation_id+'.json');release=folder/(operation_id+'.release')
    self_watcher=ProcessWatcher(os.getpid())
    value=dict(schema_version=1,id=operation_id,name=store.name,state='connecting',owned=False,created=time.time(),
               guardian_pid=os.getpid(),guardian_created=self_watcher.creation_time,qemu_pid=pid,qemu_created=created,port=port)
    self_watcher.close()
    def save(state,detail=''):
        value.update(state=state,detail=detail,updated=time.time());store.atomic(status,value)
    channel=None;parent=None;owned=False
    try:
        parent=ProcessWatcher(parent_pid)
        if parent.creation_time!=parent_created:raise ValueError('Личность родительского процесса изменилась.')
        channel=AgentChannel(port,pid,created)
        if channel.call('guest-fsfreeze-status')!='thawed':raise ValueError('Файловые системы уже заморожены другим действием; thaw не отправлен.')
        save('freeze-intent')
        # One exclusive QGA connection remains owned until thaw. A lost freeze
        # reply is ambiguous; the independent worker still attempts its thaw.
        owned=True;value['owned']=True
        count=channel.call('guest-fsfreeze-freeze')
        if type(count) is not int or count<=0:raise ValueError('Нет подтверждения хотя бы одной замороженной файловой системы.')
        if channel.call('guest-fsfreeze-status')!='frozen':raise ValueError('Заморозка файловых систем не подтверждена.')
        save('frozen');deadline=time.monotonic()+seconds
        while time.monotonic()<deadline and not release.exists() and parent.poll() is None:
            time.sleep(.05)
        save('thaw-intent','Завершение операции, таймаут или закрытие процесса')
    except BaseException as error:
        save('error',str(error))
    finally:
        if owned and channel is not None:
            try:
                channel.call('guest-fsfreeze-thaw')
                if channel.call('guest-fsfreeze-status')!='thawed':raise ValueError('Guest Agent не подтвердил thaw.')
                save('thawed')
            except BaseException as error:save('thaw-required',str(error))
            if value['state']=='thaw-required':
                try:
                    channel.close();channel=None
                    channel=AgentChannel(port,pid,created)
                    channel.call('guest-fsfreeze-thaw')
                    if channel.call('guest-fsfreeze-status')!='thawed':raise ValueError('Thaw после переподключения не подтверждён.')
                    save('thawed','Thaw подтверждён после восстановления своего канала')
                except BaseException as error:save('thaw-required',str(error))
        if channel is not None:channel.close()
        if parent is not None:parent.close()


@contextmanager
def frozen(service,operation,owner,seconds=10):
    data=service.load();port=data.get('guest_agent_port')
    if not data.get('guest_agent_enabled'):raise ValueError('Настрой отдельный канал QEMU Guest Agent. Clipboard vdagent для этого не подходит.')
    operation_id=identifier(operation['id']);folder=service.store.folder/'agent'
    release=folder/(operation_id+'.release');status=folder/(operation_id+'.json')
    parent=ProcessWatcher(os.getpid())
    try:
        process=subprocess.Popen([sys.executable,'-B','-X','utf8',str(Path(__file__).resolve()),str(service.root),service.name,operation_id,
            str(port),str(owner.pid),str(owner.creation_time),str(os.getpid()),str(parent.creation_time),str(seconds)],
            stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
    finally:parent.close()
    try:
        deadline=time.monotonic()+5
        while True:
            value=Store.read(status) if status.exists() else None
            if value and value['state']=='frozen':break
            if value and value['state'] in ('error','thawed','thaw-required'):raise ValueError('Подготовка Guest Agent не подтверждена: '+value.get('detail',''))
            if process.poll() is not None or time.monotonic()>deadline:raise ValueError('Нет подтверждения независимого защитника Guest Agent.')
            time.sleep(.05)
        service.transition(operation,'running','Guest Agent: freeze подтверждён; работает независимый thaw-защитник')
        yield lambda:Store.read(status).get('state')=='frozen'
    finally:
        service.store.atomic_bytes(release,b'release')
        try:process.wait(timeout=5)
        except subprocess.TimeoutExpired:raise ValueError('Защитник Guest Agent не завершил thaw. VM требует проверки.')
        value=Store.read(status) if status.exists() else None
        if not value or value['state']!='thawed':raise ValueError('Thaw не подтверждён; сохрани журнал и проверь Guest Agent.')
        service.transition(operation,'verifying','Guest Agent: thaw подтверждён')


if __name__=='__main__':
    guardian(Store(Path(sys.argv[1]),sys.argv[2]),sys.argv[3],*[int(value) for value in sys.argv[4:]])
