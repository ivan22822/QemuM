"""Versioned switch controller. Native identities and private named pipes, no VM files."""
import ctypes
from ctypes import wintypes as W
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
import uuid
from vm_runtime import ProcessWatcher,kernel_api
from vm_stands import Store,identifier

class SwitchError(ValueError):
    def __init__(self,message,category='helper-unavailable'):
        super().__init__(message);self.category=category if category in ('helper-unavailable','helper-start','port-unavailable','permission','identity','protocol','connection') else 'helper-unavailable'

def alive(record):
    try:watcher=ProcessWatcher(record['pid'])
    except OSError as error:
        if getattr(error,'winerror',None)==87:return False
        raise SwitchError('Не удалось проверить владельца коммутатора.') from error
    try:return watcher.creation_time==record['created'] and watcher.poll() is None
    finally:watcher.close()

def connect_pipe(api,name):
    # The single-instance server recreates its pipe between requests. Windows
    # returns FILE_NOT_FOUND immediately during this gap, without waiting.
    deadline=time.monotonic()+1.5
    while True:
        remaining=deadline-time.monotonic()
        if remaining<=0:raise SwitchError('Канал коммутатора недоступен.','connection')
        if api.WaitNamedPipeW(name,max(1,int(remaining*1000))):
            handle=api.CreateFileW(name,0xc0000000,0,None,3,0x40000000,None)
            if handle!=W.HANDLE(-1).value:return handle
        error=ctypes.get_last_error()
        if error not in (2,231):raise SwitchError('Нет доступа к каналу коммутатора.','permission' if error==5 else 'connection')
        time.sleep(min(.01,max(0,deadline-time.monotonic())))

def pipe_call(record,command):
    """Bounded overlapped IO; verify the server PID before transmitting a command."""
    if not alive(record):raise SwitchError('Коммутатор завершился; автоматического перезапуска нет.')
    api=kernel_api()
    class Overlapped(ctypes.Structure):
        _fields_=[('internal',ctypes.c_size_t),('high',ctypes.c_size_t),('offset',W.DWORD),('offset_high',W.DWORD),('event',W.HANDLE)]
    api.CreateFileW.argtypes=[W.LPCWSTR,W.DWORD,W.DWORD,W.LPVOID,W.DWORD,W.DWORD,W.HANDLE];api.CreateFileW.restype=W.HANDLE
    api.WaitNamedPipeW.argtypes=[W.LPCWSTR,W.DWORD]
    api.GetNamedPipeServerProcessId.argtypes=[W.HANDLE,ctypes.POINTER(W.ULONG)]
    api.CreateEventW.argtypes=[W.LPVOID,W.BOOL,W.BOOL,W.LPCWSTR];api.CreateEventW.restype=W.HANDLE
    api.ReadFile.argtypes=api.WriteFile.argtypes=[W.HANDLE,W.LPVOID,W.DWORD,ctypes.POINTER(W.DWORD),ctypes.POINTER(Overlapped)]
    api.GetOverlappedResult.argtypes=[W.HANDLE,ctypes.POINTER(Overlapped),ctypes.POINTER(W.DWORD),W.BOOL]
    api.CancelIoEx.argtypes=[W.HANDLE,ctypes.POINTER(Overlapped)]
    name='\\\\.\\pipe\\'+record['pipe']
    handle=connect_pipe(api,name)
    try:
        owner=W.ULONG()
        if not api.GetNamedPipeServerProcessId(handle,ctypes.byref(owner)) or owner.value!=record['pid'] or not alive(record):raise SwitchError('Канал принадлежит другому процессу.')
        def io(function,data):
            event=api.CreateEventW(None,True,False,None)
            if not event:raise ctypes.WinError(ctypes.get_last_error())
            ov=Overlapped();ov.event=event;count=W.DWORD();buffer=ctypes.create_string_buffer(data if isinstance(data,bytes) else data)
            length=len(data) if isinstance(data,bytes) else data
            try:
                if not function(handle,buffer,length,ctypes.byref(count),ctypes.byref(ov)):
                    if ctypes.get_last_error()!=997:raise ctypes.WinError(ctypes.get_last_error())
                    if api.WaitForSingleObject(event,3000)!=0:
                        api.CancelIoEx(handle,ctypes.byref(ov));api.GetOverlappedResult(handle,ctypes.byref(ov),ctypes.byref(count),True)
                        raise SwitchError('Тайм-аут управляющего канала коммутатора.')
                    if not api.GetOverlappedResult(handle,ctypes.byref(ov),ctypes.byref(count),False):raise ctypes.WinError(ctypes.get_last_error())
                return buffer.raw[:count.value]
            finally:api.CloseHandle(event)
        data=(json.dumps(dict(command,protocol=1,run_id=record['run_id']))+'\n').encode()
        if len(data)>4096:raise SwitchError('Слишком большая команда коммутатора.')
        if len(io(api.WriteFile,data))!=len(data):raise SwitchError('Неполная запись управляющей команды.')
        response=b''
        while b'\n' not in response:
            part=io(api.ReadFile,65536-len(response))
            if not part or len(response)+len(part)>=65536:raise SwitchError('Повреждён ответ коммутатора.')
            response+=part
        reply=json.loads(response.split(b'\n',1)[0])
        if reply.get('protocol')!=1 or reply.get('run_id')!=record['run_id']:raise SwitchError('Версия или идентичность коммутатора не совпадает.')
        if 'error' in reply:raise SwitchError('Коммутатор: '+str(reply['error']),reply.get('category','protocol'))
        if (reply.get('pid'),reply.get('created'))!=(record['pid'],record['created']):raise SwitchError('Идентичность коммутатора изменилась.')
        return reply['network']
    except (OSError,KeyError,TypeError,json.JSONDecodeError) as error:raise SwitchError('Ошибка протокола коммутатора: '+str(error)) from error
    finally:api.CloseHandle(handle)

class Manager:
    def __init__(self,root,helper=None):
        self.root=Path(root);self.safe=Store(root).safe
        self.helper=Path(helper) if helper else Path(__file__).with_name('QemuM.Switch.exe')
    def verify(self):
        self.safe(self.helper);manifest=self.helper.with_name('switch-manifest.json');self.safe(manifest)
        if not self.helper.is_file() or not manifest.is_file() or manifest.stat().st_size>8192:raise SwitchError('Коммутатор не собран или отсутствует manifest.')
        try:value=json.loads(manifest.read_text(encoding='utf-8'))
        except (ValueError,UnicodeError) as error:raise SwitchError('Повреждён manifest коммутатора.','protocol') from error
        digest=hashlib.sha256(self.helper.read_bytes()).hexdigest()
        if not isinstance(value,dict) or value.get('protocol')!=1 or value.get('sha256')!=digest:raise SwitchError('Проверка целостности коммутатора не пройдена.','protocol')
        try:
            probe=subprocess.run([str(self.helper),'--probe'],capture_output=True,timeout=5,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
            if probe.returncode or json.loads(probe.stdout)!=dict(protocol=1,helper='qemum-local-switch'):raise SwitchError('Несовместимый помощник коммутатора.','protocol')
        except (OSError,subprocess.TimeoutExpired,ValueError) as error:
            if isinstance(error,SwitchError):raise
            raise SwitchError('Проверка помощника не завершена: '+str(error),'protocol') from error
        return digest
    def check(self,record):
        if not isinstance(record,dict) or record.get('pending') or type(record.get('pid')) is not int or type(record.get('created')) is not int:raise SwitchError('Неподтверждённый запуск коммутатора; нужна сверка процессов.')
        if record.get('protocol')!=1 or record.get('pipe')!='qemum-switch-'+identifier(record.get('run_id')):raise SwitchError('Повреждён журнал коммутатора.')
        if not alive(record):return False
        self.safe(self.helper)
        if str(self.helper.resolve()).casefold()!=str(record.get('executable','')).casefold() or hashlib.sha256(self.helper.read_bytes()).hexdigest()!=record.get('sha256'):raise SwitchError('Исполняемый файл коммутатора изменился.')
        watcher=ProcessWatcher(record['pid'])
        try:
            api=watcher.kernel;api.QueryFullProcessImageNameW.argtypes=[W.HANDLE,W.DWORD,W.LPWSTR,ctypes.POINTER(W.DWORD)]
            buf=ctypes.create_unicode_buffer(32768);size=W.DWORD(len(buf))
            if watcher.creation_time!=record['created'] or not api.QueryFullProcessImageNameW(watcher.handle,0,buf,ctypes.byref(size)) or Path(buf.value).resolve()!=self.helper.resolve():raise SwitchError('Не подтверждён исполняемый процесс коммутатора.')
        finally:watcher.close()
        return True
    def status(self,record):
        if not self.check(record):raise SwitchError('Коммутатор остановлен. Сначала выключи участников сети.')
        state=pipe_call(record,dict(action='status'))
        if not isinstance(state,dict) or not isinstance(state.get('ports'),list) or any(not isinstance(p,dict) for p in state['ports']) or [p.get('port') for p in state['ports']]!=record['ports']:raise SwitchError('Порты коммутатора не совпадают с журналом.','protocol')
        return state
    def start(self,network,ports,persist=None):
        digest=self.verify();identifier(network);run=uuid.uuid4().hex
        folder=self.root/'stand-runtime'/'active';self.safe(folder);folder.mkdir(parents=True,exist_ok=True)
        marker=folder/(run+'.lock');self.safe(marker)
        intent=dict(protocol=1,network_id=network,run_id=run,pipe='qemum-switch-'+run,pid=None,created=None,executable=str(self.helper.resolve()),sha256=digest,ports=ports,pending=True,state='intent')
        if persist:persist(intent)
        logs=self.root/'stand-runtime'/'switch-logs';self.safe(logs);logs.mkdir(parents=True,exist_ok=True);logpath=logs/(run+'.log');self.safe(logpath)
        try:
            with logpath.open('xb') as log:
                child=subprocess.Popen([str(self.helper),'--serve',run,'qemum-switch-'+run,','.join(map(str,ports)),str(marker)],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=log,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        except OSError as error:
            if persist:persist(None)  # CreateProcess failed; there is no child to recover.
            raise SwitchError('Не удалось создать процесс коммутатора: '+str(error),'permission' if getattr(error,'winerror',None)==5 else 'helper-start') from error
        try:
            watcher=ProcessWatcher(child.pid)
            try:record=dict(protocol=1,network_id=network,run_id=run,pipe='qemum-switch-'+run,pid=child.pid,created=watcher.creation_time,executable=str(self.helper.resolve()),sha256=digest,ports=ports,state='starting')
            finally:watcher.close()
            if persist:persist(record)
            deadline=time.monotonic()+8
            while True:
                try:
                    self.status(record);record['state']='ready'
                    if persist:persist(record)
                    return record
                except SwitchError:
                    if child.poll() is not None or time.monotonic()>=deadline:raise
                    time.sleep(.05)
        except BaseException as error:
            if child.poll() is None:child.kill()
            child.wait(timeout=5)
            if persist:persist(None)  # This owned child is proved exited before any guest launch.
            if isinstance(error,Exception):
                with logpath.open('rb') as log:detail=log.read(4096).decode('utf-8','replace')
                category=getattr(error,'category','helper-start')
                try:category=json.loads(detail.splitlines()[-1]).get('category',category)
                except (ValueError,IndexError,AttributeError):pass
                raise SwitchError('Не удалось запустить коммутатор: '+str(error)+' '+detail[-1200:],category) from error
            raise
    def authorize(self,record,index,identity):
        self.status(record)
        return pipe_call(record,dict(action='authorize',index=index,pid=identity['pid'],created=identity['created']))
    def stop(self,record):
        if not self.check(record):return
        try:pipe_call(record,dict(action='quit'))
        except SwitchError:
            # The helper may auto-exit after its final guest and close the pipe
            # between quit and reply. Observe its exact native identity; never kill.
            deadline=time.monotonic()+5
            while alive(record) and time.monotonic()<deadline:time.sleep(.03)
            if alive(record):raise
        deadline=time.monotonic()+5
        while alive(record):
            if time.monotonic()>=deadline:raise SwitchError('Коммутатор не завершился после запроса.')
            time.sleep(.03)
