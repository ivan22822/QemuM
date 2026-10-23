"""Confirmed stand plans, cancellable sequential jobs and identity-bound ownership.

No shells and no automatic rollback/kill. Cancellation leaves started guests alive.
Socket listeners are inspected through the OS owner table, never probed by connecting.
"""
from contextlib import ExitStack
from copy import deepcopy
import hashlib
import json
import os
from pathlib import Path
import socket
import tempfile
import threading
import time
import uuid

from vm_stands import Store, identifier, replace_file
from vm_stand_plan import upgrade, plan as topology_plan
from vm_runtime import vm_lock, watch_qmp_process


class Conflict(ValueError): pass
class Unknown(ValueError): pass
class Cancelled(Exception): pass


def fingerprint(value):
    return hashlib.sha256(json.dumps(value,sort_keys=True,ensure_ascii=False).encode()).hexdigest()


def reserve(port=0):
    handle=socket.socket(socket.AF_INET,socket.SOCK_STREAM)
    try:
        if hasattr(socket,'SO_EXCLUSIVEADDRUSE'):handle.setsockopt(socket.SOL_SOCKET,socket.SO_EXCLUSIVEADDRUSE,1)
        handle.bind(('127.0.0.1',port))
        return handle
    except BaseException:
        handle.close();raise


class Host:
    def __init__(self,service):self.service=service
    def config(self,name):
        import vm
        return vm.load(name)
    def config_hash(self,name):
        import vm
        return hashlib.sha256(vm.config_path(name).read_bytes()).hexdigest()
    def lock(self,name):
        import vm
        return vm_lock(vm.ROOT,name)
    def identity(self,name,data):
        import vm
        # QMP validates the VM's name, OS handle anchors PID and creation time.
        try:
            status=self.service.query(name,data,timeout=.4)
            if status.get('status') not in ('running','paused','prelaunch'):raise Unknown('QEMU не готов: '+name+' / '+str(status.get('status')))
        except (ConnectionRefusedError,TimeoutError):
            # Windows may time out instead of refusing an unused loopback port.
            # Only a complete process/media inventory may prove the guest stopped.
            self.service.stopped(name,data)
            return None
        except (OSError,ValueError) as error:raise Unknown('Не удалось подтвердить QMP '+name+': '+str(error)) from error
        with ExitStack() as stack:
            watcher=watch_qmp_process(vm.qmp_port(name,data));stack.callback(watcher.close)
            self.service.query(name,data,timeout=.4)
            after=watch_qmp_process(vm.qmp_port(name,data));stack.callback(after.close)
            if (after.pid,after.creation_time)!=(watcher.pid,watcher.creation_time):raise Unknown('Владелец QMP изменился во время проверки: '+name)
            if watcher.poll() is not None:raise Unknown('Процесс завершился во время проверки: '+name)
            return dict(pid=watcher.pid,created=watcher.creation_time)
    def alive(self,identity):
        from vm_runtime import ProcessWatcher
        try:watcher=ProcessWatcher(identity['pid'])
        except OSError as error:
            if getattr(error,'winerror',None)==87:return False
            raise Unknown('Недостаточно данных о процессе.') from error
        try:return watcher.creation_time==identity['created'] and watcher.poll() is None
        finally:watcher.close()
    def listener(self,port,identity):
        try:
            watcher=watch_qmp_process(port)
            try:return watcher.pid==identity['pid'] and watcher.creation_time==identity['created'] and watcher.poll() is None
            finally:watcher.close()
        except (ValueError,OSError):return False
    def preflight(self,name,data,running):
        import vm
        from vm_backend import normalized
        from vm_snapshot_store import assert_can_start
        from vm_storage import media
        from vm_profiles import choose
        normalized(deepcopy(data));vm.checked_qmp_port(name,data);assert_can_start(vm.ROOT,name)
        exe=vm.qemu_executable()
        if not Path(exe).is_file():raise ValueError('QEMU отсутствует.')
        for path,kind in media(data):
            if kind=='iso' and data.get('disk_path'):continue
            if not path.is_file():raise ValueError('Отсутствует образ '+name+': '+str(path))
        if not running:
            self.service.stopped(name,data)
            if vm.qmp_bind_error(vm.qmp_port(name,data)):raise Conflict('QMP-порт недоступен: '+name)
            accelerator,_=choose(exe,data)
            vm.build_args(name,data,accelerator=accelerator)
    def ports(self,check_names=None):
        import vm
        from vm_network import forwarding
        occupied=set();owners={}
        def add(port,owner):
            if port in owners and check_names and owners[port].split()[0] in check_names and owner.split()[0] in check_names:raise Conflict('Конфликт портов '+str(port)+': '+owners[port]+' / '+owner)
            owners[port]=owner;occupied.add(port)
        for name in vm.entries():
            data=vm.load(name);add(vm.qmp_port(name,data),name+' QMP')
            if data.get('guest_agent_enabled') and data.get('guest_agent_port'):add(data['guest_agent_port'],name+' QGA')
            for nic in data.get('network_adapters',[]):
                for rule in nic.get('hostfwd',[]):
                    parsed=forwarding(rule)
                    if parsed[1]=='tcp':add(int(parsed[3]),name+' hostfwd')
                if nic.get('type')=='socket' and nic.get('mode')=='listen':add(nic.get('port',1234),name+' listener')
        return occupied
    def validate_ports(self,names):self.ports(set(names))
    def start(self,name,data,expected):
        from vm_runtime import ProcessWatcher
        result=self.service.start({'name':name},stand_data=data,expected_hash=expected)
        if 'pid' in result:
            watcher=ProcessWatcher(result['pid'])
            try:result['identity']=dict(pid=watcher.pid,created=watcher.creation_time)
            finally:watcher.close()
        return result
    def stop(self,name,force):
        return self.service.control({'name':name,'action':'quit' if force else 'shutdown','confirm':name})
    def assert_stopped(self,name):
        self.service.stopped(name,self.config(name))
    def capacity(self):
        import vm
        running=[]
        for name in vm.entries():
            data=vm.load(name)
            try:
                if self.identity(name,data):running.append(data)
            except Unknown:pass
        available=None
        if os.name=='nt':
            import ctypes
            class Memory(ctypes.Structure):
                _fields_=[('length',ctypes.c_ulong),('load',ctypes.c_ulong)]+[(n,ctypes.c_ulonglong) for n in ('total','available','page_total','page_available','virtual_total','virtual_available','extended')]
            memory=Memory();memory.length=ctypes.sizeof(Memory)
            if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(memory)):available=memory.available//(1024*1024)
        return dict(host_cores=os.cpu_count(),available_ram_mb=available,running_ram_mb=sum(d.get('ram_mb',0) for d in running),running_cores=sum(d.get('cores',0) for d in running))


class Runtime:
    def __init__(self,root,host):
        self.root=Path(root);self.host=host;self.store=Store(root)
        self.guard=threading.RLock();self.plans={};self.jobs={};self.exports={};self.closed=False
    def _journal(self,key,value=None):
        path=self.root/'stand-runtime'/(identifier(key)+'.json');self.store.safe(path)
        if path.exists() and (path.stat().st_nlink!=1 or path.stat().st_size>2*1024*1024):raise Unknown('Недопустимый журнал стенда.')
        if value is None:
            result=json.loads(path.read_text(encoding='utf-8')) if path.exists() else dict(owned={},ports={})
            if not isinstance(result,dict) or not isinstance(result.get('owned',{}),dict) or not isinstance(result.get('ports',{}),dict) or not isinstance(result.get('history',[]),list) or len(result.get('history',[]))>20 or ('current' in result and not isinstance(result['current'],dict)):
                raise Unknown('Повреждён журнал стенда. Файл не изменён; нужна ручная проверка.')
            records=result.get('history',[])+([result['current']] if 'current' in result else [])
            if any(not isinstance(item,dict) or not isinstance(item.get('id'),str) or not isinstance(item.get('state'),str) or not isinstance(item.get('events',[]),list) for item in records) or any(not isinstance(value,dict) for value in result.get('owned',{}).values()) or ('pending' in result and not isinstance(result['pending'],(dict,str))):
                raise Unknown('Повреждены записи журнала стенда. Файл не изменён; нужна ручная проверка.')
            return result
        path.parent.mkdir(parents=True,exist_ok=True)
        temporary=None
        try:
            with tempfile.NamedTemporaryFile(dir=path.parent,delete=False,mode='w',encoding='utf-8') as stream:
                temporary=Path(stream.name);json.dump(value,stream,ensure_ascii=False);stream.flush();os.fsync(stream.fileno())
            replace_file(temporary,path,self.store.safe)
        finally:
            if temporary and temporary.exists():temporary.unlink()
    def _cleanup(self):
        for key,value in list(self.exports.items()):
            if value['expires']<time.monotonic():del self.exports[key]
        for key,value in list(self.plans.items()):
            if value['expires']<time.monotonic() and not value.get('used'):
                for handle in value['leases'].values():handle.close()
                del self.plans[key]
        terminal=[key for key,job in self.jobs.items() if job['state'] not in ('queued','running')]
        for key in terminal[:-20]:
            self.jobs.pop(key,None);self.plans.pop(key,None)
    def make_plan(self,key,action='start',include_existing=False,force=False):
        if action not in ('start','stop') or type(include_existing) is not bool or type(force) is not bool:raise ValueError('Некорректное действие стенда.')
        with self.guard:
            self._cleanup()
            if self.closed:raise Conflict('Сервис закрывается.')
            if len(self.plans)>=32:raise Conflict('Слишком много планов. Дождись истечения старых планов.')
            row=upgrade(self.store.read(identifier(key)));journal=self._journal(key)
            configs={n:self.host.config(n) for n in row['members']};hashes={n:self.host.config_hash(n) for n in configs}
            identities={n:self.host.identity(n,data) for n,data in configs.items()}
            pending=journal.get('pending')
            if pending:
                if isinstance(pending,dict) and pending.get('identity'):
                    n=pending['name'];identity=pending['identity']
                    if n in identities and identities[n]==identity and journal.get('topology')==fingerprint(row) and pending.get('hash')==hashes[n]:
                        journal.setdefault('owned',{})[n]=dict(identity=identity,config_hash=hashes[n]);journal.pop('pending');self._journal(key,journal)
                    elif not self.host.alive(identity):journal.pop('pending');self._journal(key,journal)
                    else:raise Unknown('Предыдущий запуск не подтверждён через QMP: '+n)
                else:raise Unknown('Предыдущий запуск не подтверждён. Проверь журнал и процессы, повтор заблокирован.')
            ports={};leases={};owned=journal.get('owned',{})
            try:
                used=self.host.ports()
                if action=='start' and hasattr(self.host,'validate_ports'):self.host.validate_ports(configs)
                for link in row['links']:
                    names=[link[r]['vm'] for r in ('listen','connect')]
                    active=[n for n in names if identities[n]]
                    if active:
                        if journal.get('topology')!=fingerprint(row):raise Conflict('Работающая VM использует другую топологию. Сначала выключи её.')
                        for n in active:
                            if owned.get(n,{}).get('identity')!=identities[n]:raise Conflict('Работающая VM не принадлежит этой сети: '+n)
                            if owned[n].get('config_hash')!=hashes[n]:raise Conflict('Файл параметров работающей VM изменился: '+n)
                        port=journal.get('ports',{}).get(link['id'])
                        if not port:raise Unknown('Неизвестен порт работающей связи.')
                    else:
                        handle=reserve()
                        while handle.getsockname()[1] in used:
                            handle.close();handle=reserve()
                        port=handle.getsockname()[1];leases[link['id']]=handle
                    used.add(port);ports[link['id']]=port
                topology=topology_plan(row,configs,ports)
                if action=='start':
                    for name,data in configs.items():self.host.preflight(name,data,identities[name] is not None)
                    # Two stopped members must not own the same writable image.
                    from vm_storage import media
                    disks={}
                    for name,data in configs.items():
                        for path,kind in media(data):
                            if kind=='iso':continue
                            disk=str(path.resolve()).casefold()
                            if disk in disks:raise Conflict('Общий записываемый диск: '+disks[disk]+' и '+name)
                            disks[disk]=name
                selected=[n for n in reversed(row['order']) if identities[n] and (include_existing or owned.get(n,{}).get('identity')==identities[n])]
                value=dict(id=uuid.uuid4().hex,key=key,action=action,force=force,row=row,hashes=hashes,
                           identities=identities,ports=ports,configs=topology['configs'],leases=leases,
                           selected=selected,expires=time.monotonic()+120,used=False)
                public=dict(id=value['id'],stand_id=key,action=action,force=force,include_existing=include_existing,
                    order=row['order'] if action=='start' else selected,
                    already_running=[n for n in row['order'] if identities[n]],
                    networks=topology['networks'],ports=ports,resources=self.host.capacity(),
                    requested_ram_mb=sum(d.get('ram_mb',0) for n,d in configs.items() if not identities[n]),
                    requested_cores=sum(d.get('cores',0) for n,d in configs.items() if not identities[n]),
                    warning=topology['warning']+' Ресурсы — оценка, а не гарантия производительности. Отмена оставит запущенные VM работать.')
                value['public']=public;self.plans[value['id']]=value
                if public['resources'].get('available_ram_mb') is not None and public['requested_ram_mb']>public['resources']['available_ram_mb']:
                    public['warning']+=' Запрошенная RAM превышает доступную физическую память; возможны ошибки запуска и замедление.'
                if public['resources'].get('host_cores') and public['requested_cores']+public['resources']['running_cores']>public['resources']['host_cores']:
                    public['warning']+=' Сумма vCPU превышает число логических процессоров хоста; возможна конкуренция за CPU.'
                return deepcopy(public)
            except BaseException:
                for handle in leases.values():handle.close()
                raise
    def execute(self,plan_id,confirmation):
        with self.guard:
            if self.closed:raise Conflict('Сервис закрывается.')
            self._cleanup();value=self.plans.get(identifier(plan_id))
            if value is None:raise Conflict('План истёк. Проверь стенд заново.')
            if confirmation!=plan_id:raise ValueError('Нужно подтвердить конкретный план.')
            if plan_id in self.jobs:return self.status(plan_id)
            if any(j['state'] in ('queued','running') and set(j['members'])&set(value['row']['members']) for j in self.jobs.values()):raise Conflict('Участник занят другой операцией стенда.')
            job=dict(id=plan_id,stand_id=value['key'],members=value['row']['members'],state='queued',phase='Ожидание',current=None,steps={},events=[],error=None,category=None,cancel=threading.Event())
            marker=self.root/'stand-runtime'/'active'/(plan_id+'.lock');self.store.safe(marker);marker.parent.mkdir(parents=True,exist_ok=True)
            lease=marker.open('x+b');lease.write(b'1');lease.flush();job['_lease']=(lease,marker)
            self.jobs[plan_id]=job;value['used']=True
            thread=threading.Thread(target=self._run,args=(value,job),daemon=True,name='stand-'+plan_id);job['thread']=thread
            try:thread.start()
            except BaseException:
                lease.close();marker.unlink(missing_ok=True);self.jobs.pop(plan_id,None);value['used']=False;raise
            return self.status(plan_id)
    def discard(self,plan_id):
        with self.guard:
            value=self.plans.get(identifier(plan_id))
            if value and value['used']:raise Conflict('Операция уже начата; используй отмену будущих этапов.')
            if value:
                for handle in value['leases'].values():handle.close()
                del self.plans[plan_id]
            return dict(discarded=True)
    def _event(self,job,phase,name=None):
        with self.guard:
            job['phase']=phase;job['current']=name
            job['events'].append(dict(time=time.time(),phase=phase,vm=name));job['events']=job['events'][-500:]
            self._record(job)
    def _record(self,job):
        journal=job.get('_journal')
        if journal is None:return
        summary=deepcopy({k:v for k,v in job.items() if k not in ('thread','cancel','_journal','_lease')})
        summary['events']=summary['events'][-100:];journal['current']=summary
        if job['state'] not in ('queued','running'):
            history=[item for item in journal.get('history',[]) if item.get('id')!=job['id']]
            history.append(summary);journal['history']=history[-20:]
        self._journal(job['stand_id'],journal)
    def _check_cancel(self,job):
        if self.closed or job['cancel'].is_set():raise Cancelled()
    def _wait(self,job,test,timeout,label):
        deadline=time.monotonic()+timeout
        while True:
            self._check_cancel(job)
            if test():return
            if time.monotonic()>=deadline:raise TimeoutError(label)
            job['cancel'].wait(.1)
    def _run(self,value,job):
        row=value['row'];key=value['key'];journal=None
        try:
            with ExitStack() as locks:
                try:
                    locks.enter_context(vm_lock(self.root,'stand-execution-'+key))
                    for name in sorted(row['members']):locks.enter_context(self.host.lock(name))
                except ValueError as error:raise Conflict(str(error)) from error
                with self.guard:job['state']='running'
                if upgrade(self.store.read(key))!=row:raise Conflict('Определение стенда изменилось после проверки.')
                for name in row['members']:
                    if self.host.config_hash(name)!=value['hashes'][name]:raise Conflict('Параметры VM изменились: '+name)
                    if self.host.identity(name,self.host.config(name))!=value['identities'][name]:raise Conflict('Состояние VM изменилось: '+name)
                journal=self._journal(key)
                if journal.get('pending'):raise Unknown('В журнале есть неподтверждённый запуск '+str(journal['pending'])+'. Сверь процессы перед повтором.')
                journal.update(topology=fingerprint(row),ports=value['ports']);journal.setdefault('owned',{})
                job['_journal']=journal;self._record(job)
                sequence=row['order'] if value['action']=='start' else value['selected']
                confirmed={n:i for n,i in value['identities'].items() if i}
                for name in sequence:
                    self._check_cancel(job);data=value['configs'][name];step=row['startup'][name]
                    for previous,identity in confirmed.items():
                        if not self.host.alive(identity):raise Unknown('Участник завершился между этапами: '+previous)
                    if value['action']=='stop':
                        identity=value['identities'][name]
                        if self.host.identity(name,self.host.config(name))!=identity:raise Conflict('Процесс изменился перед выключением: '+name)
                        self._event(job,'Принудительная остановка' if value['force'] else 'Штатное выключение',name)
                        job['steps'][name]='stopping';self.host.stop(name,value['force'])
                        self._wait(job,lambda:not self.host.alive(identity),step['timeout'],'VM не завершилась: '+name)
                        confirmed.pop(name,None);journal['owned'].pop(name,None);self._journal(key,journal);job['steps'][name]='stopped';continue
                    if name in confirmed:job['steps'][name]='already-running';continue
                    self._event(job,'Задержка перед запуском (не готовность ОС)',name)
                    if job['cancel'].wait(step['delay']):raise Cancelled()
                    self._check_cancel(job)
                    for link in row['links']:
                        if link['connect']['vm']==name:
                            listener=link['listen']['vm'];identity=confirmed.get(listener)
                            if identity is None:raise Conflict('Слушатель ещё не готов: '+listener)
                            self._event(job,'Ожидание слушателя '+listener,name)
                            self._wait(job,lambda:self.host.listener(value['ports'][link['id']],identity),step['timeout'],'Слушатель недоступен: '+listener)
                    for link in row['links']:
                        if link['listen']['vm']==name:
                            handle=value['leases'].pop(link['id'],None)
                            if handle:handle.close()
                    self._event(job,'Создание процесса',name);job['steps'][name]='starting'
                    journal['pending']=dict(name=name,hash=value['hashes'][name]);self._journal(key,journal)
                    try:result=self.host.start(name,data,value['hashes'][name])
                    except Exception:
                        # Clear intent only after proving that no guest was created.
                        try:self.host.assert_stopped(name)
                        except Exception:pass
                        else:journal.pop('pending',None);self._journal(key,journal)
                        raise
                    if result.get('attached'):raise Conflict('VM уже запущена другим действием: '+name)
                    if result.get('identity'):journal['pending']['identity']=result['identity'];self._journal(key,journal)
                    self._event(job,'Процесс создан; проверка QMP',name);job['steps'][name]='process-created'
                    observed={}
                    def ready():
                        try:identity=self.host.identity(name,data)
                        except (Unknown,ValueError,OSError):
                            if result.get('identity') and not self.host.alive(result['identity']):raise Unknown('Процесс завершился до QMP: '+name)
                            return False
                        if identity:
                            if identity['pid']!=result['pid'] or (result.get('identity') and identity!=result['identity']):raise Unknown('QMP принадлежит другому процессу: '+name)
                            observed.update(identity);return True
                        return False
                    # Identity is required even for process readiness; a PID alone is insufficient.
                    self._wait(job,ready,step['timeout'],'QMP не готов: '+name)
                    confirmed[name]=dict(observed)
                    journal['owned'][name]=dict(identity=dict(observed),config_hash=value['hashes'][name]);journal.pop('pending',None);self._journal(key,journal)
                    job['steps'][name]='qmp-ready'
                    if step['ready']=='tcp':
                        self._event(job,'Ожидание TCP-сервиса (не всей ОС)',name)
                        def tcp():
                            if not self.host.alive(observed):raise Unknown('VM завершилась: '+name)
                            try:
                                with socket.create_connection(('127.0.0.1',step['tcp_port']),timeout=.3):return True
                            except OSError:return False
                        self._wait(job,tcp,step['timeout'],'TCP-сервис не ответил: '+name);job['steps'][name]='tcp-ready'
                self._event(job,'Операция завершена; готовность ОС не измеряется')
                with self.guard:job['state']='completed'
        except Cancelled:
            with self.guard:job.update(state='cancelled',category='cancelled',error='Будущие этапы отменены. Уже запущенные VM оставлены работать.')
        except Exception as error:
            category='timeout' if isinstance(error,TimeoutError) else 'conflict' if isinstance(error,Conflict) else 'unknown' if isinstance(error,Unknown) else 'vm-error'
            with self.guard:job.update(state='failed',category=category,error=str(error)+'; проверь участника и сформируй новый план. Работающие VM не выключены.')
        finally:
            try:
                with self.guard:self._record(job)
            except (OSError,ValueError) as error:
                with self.guard:job.update(state='failed',category='unknown',error='Не удалось сохранить журнал. Выполни сверку: '+str(error))
            for handle in value['leases'].values():handle.close()
            value['leases'].clear()
            lease=job.get('_lease')
            if lease:
                lease[0].close()
                try:lease[1].unlink(missing_ok=True)
                except OSError:pass  # Unlocked stale marker is safe for the updater.
    def status(self,operation):
        with self.guard:
            job=self.jobs.get(identifier(operation))
            if job is None:
                folder=self.root/'stand-runtime';self.store.safe(folder)
                invalid=0
                for path in sorted(folder.glob('*.json')):
                    try:journal=self._journal(path.stem)
                    except (OSError,ValueError):invalid+=1;continue
                    for saved in [journal.get('current',{})]+journal.get('history',[]):
                        if not isinstance(saved,dict):continue
                        if saved.get('id')==operation:
                            result=deepcopy(saved)
                            if result['state'] in ('queued','running'):result.update(state='interrupted',category='recovery-required',error='Операция прервана закрытием менеджера. Выполни сверку стенда; автоматического повторного запуска нет.')
                            return result
                raise ValueError('Операция не найдена. Повреждённых журналов: '+str(invalid))
            return deepcopy({k:v for k,v in job.items() if k not in ('thread','cancel','_journal','_lease')})
    def cancel(self,operation):
        with self.guard:
            job=self.jobs.get(identifier(operation))
            if job is None:raise ValueError('Операция не найдена.')
            job['cancel'].set();return self.status(operation)
    def overview(self,key):
        row=self.store.read(identifier(key));states={}
        for name in row['members']:
            try:states[name]='running' if self.host.identity(name,self.host.config(name)) else 'stopped'
            except (Unknown,OSError,ValueError):states[name]='unknown'
        with self.guard:
            jobs=[j for j in self.jobs.values() if j['stand_id']==key]
            latest=jobs[-1] if jobs else None
            recovered=self._journal(key).get('current') if not latest else None
            count=sum(s=='running' for s in states.values())
            state='running' if count==len(states) else 'partial' if count else 'stopped'
            if any(s=='unknown' for s in states.values()):state='unknown'
            if latest and latest['state'] in ('queued','running'):state='starting' if self.plans[latest['id']]['action']=='start' else 'stopping'
            elif latest and latest['state']=='failed':state='error'
            elif recovered and recovered.get('state') in ('queued','running','interrupted'):state='unknown'
            return dict(state=state,members=states,operation_id=latest['id'] if latest else recovered.get('id') if recovered else None)
    def activity(self):
        with self.guard:
            result={}
            for job in self.jobs.values():
                state='error' if job['state']=='failed' else ('starting' if self.plans[job['id']]['action']=='start' else 'stopping') if job['state'] in ('queued','running') else ''
                result[job['stand_id']]=dict(state=state,phase=job['phase'],current=job['current'],operation_id=job['id'])
            return result
    def close(self):
        with self.guard:
            self.closed=True
            for job in self.jobs.values():job['cancel'].set()
            for value in self.plans.values():
                if not value.get('used'):
                    for handle in value['leases'].values():handle.close()
