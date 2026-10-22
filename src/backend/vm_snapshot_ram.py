"""Conservative full-state snapshots: paused TCG, fixed build and device graph.

WHPX, host CPU, TPM/NVRAM/passthrough and unrecognised external state are denied.
RAM lives inside the selected qcow2, never in the ordinary support bundle.
"""
from copy import deepcopy
import re
import hashlib
from pathlib import Path
import shutil
import ctypes
from ctypes import wintypes
import time
import uuid
from vm_snapshot_service import inventory,resources,MIN_FREE
from vm_snapshot_store import digest,operation_lock,identifier,FINAL
from vm_snapshot_parameters import capture


def command_arguments(command):
    api=ctypes.WinDLL('shell32',use_last_error=True)
    api.CommandLineToArgvW.argtypes=[wintypes.LPCWSTR,ctypes.POINTER(ctypes.c_int)]
    api.CommandLineToArgvW.restype=ctypes.POINTER(wintypes.LPWSTR)
    kernel=ctypes.WinDLL('kernel32',use_last_error=True);kernel.LocalFree.argtypes=[wintypes.HLOCAL]
    count=ctypes.c_int();pointer=api.CommandLineToArgvW(command,ctypes.byref(count))
    if not pointer:raise ctypes.WinError(ctypes.get_last_error())
    try:return [pointer[index] for index in range(count.value)]
    finally:kernel.LocalFree(pointer)


def launch_signature(arguments):
    result=[];index=0
    while index<len(arguments):
        if arguments[index]=='-S':index+=1;continue
        if arguments[index] in ('-qmp','-qtest'):index+=2;continue
        result.append(arguments[index]);index+=1
    return result


class RamSnapshots:
    def __init__(self,service):self.service=service;self.store=service.store

    def fingerprint(self,owner):
        data,state,disks,excluded=self.service.validate_runtime()
        if state not in ('paused','prelaunch'):raise ValueError('Для полного состояния сначала поставь VM на паузу.')
        records=self.service.processes()
        command=next((row['CommandLine'] for row in records if row.get('ProcessId')==owner.pid),'')
        if not re.search(r'(?:^|\s)-accel\s+"?tcg(?=[,\s"]|$)',command):raise ValueError('RAM проверена только для TCG; для WHPX используй снимок дисков.')
        cpu=data.get('launch_profile_settings',{}).get('cpu','max')
        if cpu not in ('max','qemu64','qemu32'):raise ValueError('RAM требует фиксированной поддерживаемой CPU-модели, без host.')
        actual_cpu=re.search(r'(?:^|\s)-cpu\s+"?([^\s"]+)',command)
        if not actual_cpu or actual_cpu[1]!=cpu:raise ValueError('Фактическая CPU-модель отличается от сохранённой.')
        if data.get('guest_agent_enabled') or data.get('vga','std') not in ('std','none') or any(row.get('type')!='user' or row.get('model','virtio-net-pci')!='virtio-net-pci' for row in data.get('network_adapters',[])):
            raise ValueError('Этот внешний канал, видеоустройство или сетевой backend ещё не проверен для полного состояния.')
        extras=data.get('extra_args',[]);index=0
        while index<len(extras):
            flag=extras[index]
            if flag=='-S':index+=1;continue
            if flag not in ('-display','-drive') or index+1>=len(extras):raise ValueError('Устройство/внешнее состояние из Extra args ещё не проверено для RAM: '+flag)
            if flag=='-display' and extras[index+1] not in ('none','sdl,gl=off','gtk,gl=off'):raise ValueError('RAM пока не поддерживает 3D и внешние clipboard-каналы.')
            if flag=='-drive' and 'if=pflash' in extras[index+1]:raise ValueError('NVRAM/UEFI пока не поддерживается полным состоянием.')
            index+=2
        actual=command_arguments(command)
        import vm
        expected=vm.build_args(self.service.name,data,boot='disk',accelerator='tcg')
        if launch_signature(actual[1:])!=launch_signature(expected):raise ValueError('Настройки отличаются от фактической команды запуска. Сначала перезапусти VM с выбранными параметрами.')
        if len(self.service.request('query-cpus-fast'))!=data['cores'] or self.service.request('query-memory-size-summary')['base-memory']!=data['ram_mb']*1024*1024:
            raise ValueError('Фактические RAM/CPU отличаются от сохранённых настроек.')
        machine=next((row['type'] for row in self.service.request('qom-list',{'path':'/'}) if row['name']=='machine'),None)
        if not machine:raise ValueError('Не определён точный machine type.')
        commands={row['name'] for row in self.service.request('query-commands')}
        if not {'snapshot-save','snapshot-load','query-jobs','job-dismiss'}<=commands:raise ValueError('Нет QMP API полного состояния.')
        path=Path(shutil.which(actual[0]) or actual[0])
        binary=hashlib.sha256()
        with path.open('rb') as stream:
            for block in iter(lambda:stream.read(4*1024*1024),b''):binary.update(block)
        return dict(schema_version=1,qemu=self.service.request('query-version'),qemu_binary_sha256=binary.hexdigest(),machine=machine,cpu=cpu,
                    launch=capture(data)['launch'],guest_agent_enabled=bool(data.get('guest_agent_enabled'))),disks

    def plan(self,options=None):
        options=dict(options or {})
        if options.get('consistency','crash')!='crash':raise ValueError('RAM и Guest Agent freeze одновременно пока не поддерживаются.')
        options['kind']='disk';plan=self.service.plan_create(options)
        with self.service.watched() as owner:
            fingerprint,disks=self.fingerprint(owner)
            self.service.check_plan(plan,disks,owner)
            budget=self.service.load()['ram_mb']*1024*1024*2+MIN_FREE
            if any(self.service.free(row['path'])<budget for row in disks):raise ValueError('Недостаточно места для RAM и резерва.')
            plan.update(action='ram-create',fingerprint=fingerprint,ram_budget=budget)
        return plan

    def job(self,command,tag,nodes,operation):
        job_id='qemum-'+str(uuid.uuid4())
        operation['job_id']=job_id
        self.service.transition(operation,'running','Перед '+command+' · '+job_id)
        self.service.request(command,{'job-id':job_id,'tag':tag,'vmstate':nodes[0],'devices':nodes})
        deadline=time.monotonic()+120
        while time.monotonic()<deadline:
            jobs=self.service.request('query-jobs');job=next((row for row in jobs if row['id']==job_id),None)
            if job and job['status']=='concluded':
                error=job.get('error');self.service.request('job-dismiss',{'id':job_id})
                if error:raise ValueError('QEMU '+command+': '+str(error))
                self.service.transition(operation,'verifying',command+' завершена без ошибки QMP job')
                return
            time.sleep(.05)
        raise ValueError('Нет подтверждения QMP job. Повтор автоматически не выполняется.')

    def nodes(self):
        return [row['inserted']['node-name'] for row in self.service.request('query-block') if row.get('inserted') and not row['inserted'].get('ro')]

    def group(self,plan,disks,checkpoint=False):
        members=[];ram_sizes=[]
        for disk in disks:
            matches=[row for row in disk['snapshots'] if row.get('name')==plan['tag']]
            if len(matches)!=1 or not matches[0].get('id'):raise ValueError('Полная RAM-группа не подтверждена на каждом диске.')
            ram_sizes.append(matches[0].get('vm-state-size',0))
            members.append(dict({key:disk[key] for key in ('path','file_id','device','role','format')},snapshot_id=str(matches[0]['id'])))
        if sum(size>0 for size in ram_sizes)!=1:raise ValueError('RAM должна быть подтверждена ровно на одном образе группы.')
        return dict(id=plan['group_id'],name=plan['tag'],label=plan['label'] or 'Полное состояние '+time.strftime('%Y-%m-%d %H:%M',time.localtime(plan['created'])),
                    description=plan['description'],date=int(plan['created']),kind='ram',state='ready',disks=members,
                    protected=checkpoint,checkpoint=checkpoint,checkpoint_reason='Перед восстановлением RAM' if checkpoint else '',
                    operation_id=plan['id'],fingerprint=plan['fingerprint'],parameters=plan.get('parameters'),ram_size=sum(ram_sizes),
                    consistency_description='CPU, RAM, устройства и диски при паузе. Только та же сборка QEMU/TCG и конфигурация; содержит личные данные.')

    def create(self,plan):
        if plan.get('name')!=self.service.name or plan.get('action')!='ram-create':raise ValueError('Неверная принадлежность RAM-плана.')
        with operation_lock(self.service.root,self.service.name):
            previous=self.store.operation(plan['id'])
            if previous:return self.service.result(previous)
            self.store.assert_clear()
            with self.service.watched() as owner:
                fingerprint,disks=self.fingerprint(owner);self.service.check_plan(plan,disks,owner);self.service.assert_owned(disks,owner.pid)
                if fingerprint!=plan['fingerprint']:raise ValueError('Конфигурация RAM изменилась.')
                if any(self.service.free(row['path'])<plan['ram_budget'] for row in disks):raise ValueError('Место для RAM больше недоступно.')
                operation=dict(schema_version=1,id=plan['id'],name=self.service.name,action='ram-create',plan=plan,state='validating',created=self.service.clock(),steps=[],mutation_started=False)
                self.service.transition(operation,'validating','Проверена совместимость полного состояния')
                try:
                    operation['mutation_started']=True;self.job('snapshot-save',plan['tag'],self.nodes(),operation)
                    checked=inventory(self.service.request)[0]
                    if resources(checked)!=plan['disks'] or owner.poll() is not None or self.service.config_hash()!=plan['config_hash'] or self.service.peer_hashes()!=plan['peer_hashes']:raise ValueError('Процесс, диски или параметры изменились во время сохранения RAM.')
                    if self.service.request('query-status')['status'] not in ('paused','prelaunch'):raise ValueError('Состояние VM изменилось во время сохранения RAM.')
                    group=self.group(plan,checked,plan.get('checkpoint',False));self.store.publish(group,plan['catalog_hash'])
                    operation['result']=dict(result=plan['tag'],group_id=plan['group_id'],operation_id=plan['id'])
                    self.service.transition(operation,'completed','Полное состояние проверено; VM остаётся на паузе')
                    return operation['result']
                except BaseException as error:
                    self.service.transition(operation,'uncertain' if operation['mutation_started'] else 'failed',str(error));raise

    def restore(self,group_id):
        # Called only on a paused process, including a freshly launched -S guest.
        # Capture a full-state checkpoint before changing any RAM or disk.
        with operation_lock(self.service.root,self.service.name):
            self.store.assert_clear();current=self.store.catalog()
            selected=next((row for row in current['groups'] if row['id']==group_id and row['kind']=='ram'),None)
            if not selected:raise ValueError('RAM-точка не найдена.')
            with self.service.watched() as owner:
                fingerprint,disks=self.fingerprint(owner);self.service.assert_owned(disks,owner.pid)
                if fingerprint!=selected['fingerprint']:raise ValueError('Версия QEMU, machine type, CPU, устройства или настройки отличаются от RAM-точки.')
                checked=next(row for row in self.service.reconcile([selected],disks) if row['id']==group_id)
                if checked['state']!='ready':raise ValueError('RAM-группа на дисках изменилась.')
                checkpoint=self.plan(dict(label='Перед восстановлением RAM'))
                checkpoint['checkpoint']=True
                self.create(checkpoint)
                operation=dict(schema_version=1,id=str(uuid.uuid4()),name=self.service.name,action='ram-restore',created=self.service.clock(),state='validating',steps=[],
                    selected=selected,checkpoint=checkpoint['tag'],checkpoint_group_id=checkpoint['group_id'],fingerprint=fingerprint,
                    disks=resources(disks),owner_pid=owner.pid,owner_created=owner.creation_time)
                self.service.transition(operation,'validating','Страховочное полное состояние подтверждено')
                try:
                    self.job('snapshot-load',selected['name'],self.nodes(),operation)
                    self.service.request('stop')
                    if self.fingerprint(owner)[0]!=fingerprint or owner.poll() is not None:raise ValueError('Не подтверждена совместимость после загрузки RAM.')
                    operation['result']=dict(operation_id=operation['id'],result=selected['name'],ram_restored=True,checkpoint=checkpoint['tag'])
                    self.service.transition(operation,'completed','Полное состояние загружено; VM на паузе')
                    return operation['result']
                except BaseException as error:
                    self.service.transition(operation,'recovery-required',str(error))
                    try:
                        self.job('snapshot-load',checkpoint['tag'],self.nodes(),operation);self.service.request('stop')
                        self.service.transition(operation,'failed','Исходная ошибка: '+str(error)+'; возвращено страховочное полное состояние')
                    except BaseException as rollback_error:self.service.transition(operation,'recovery-required','Исходная ошибка: '+str(error)+'; компенсация: '+str(rollback_error))
                    raise

    def recover(self,operation):
        if operation['action']=='ram-create':
            self.service.assert_offline()
            from vm_snapshot_offline import OfflinePreparation
            disks=OfflinePreparation(self.service).disks();plan=operation['plan']
            if self.service.config_hash()!=plan['config_hash'] or resources(disks)!=plan['disks']:raise ValueError('RAM-план изменился после сбоя.')
            group=self.group(plan,disks);self.store.publish(group,plan['catalog_hash'])
            operation['result']=dict(operation_id=plan['id'],group_id=group['id'],result=group['name'])
            self.service.transition(operation,'completed','Полная RAM-группа подтверждена после сбоя');return operation['result']
        with self.service.watched() as owner:
            fingerprint,disks=self.fingerprint(owner)
            if owner.pid!=operation['owner_pid'] or owner.creation_time!=operation['owner_created'] or fingerprint!=operation['fingerprint'] or resources(disks)!=operation['disks']:raise ValueError('Для RAM-компенсации нужен исходный приостановленный процесс и тот же набор дисков.')
            checkpoint=next((row for row in self.store.catalog()['groups'] if row['id']==operation['checkpoint_group_id']),None)
            if not checkpoint:raise ValueError('Страховочное полное состояние не найдено.')
            if next(row for row in self.service.reconcile([checkpoint],disks) if row['id']==checkpoint['id'])['state']!='ready':raise ValueError('Страховочная RAM-группа изменилась.')
            self.job('snapshot-load',operation['checkpoint'],self.nodes(),operation);self.service.request('stop')
            self.service.transition(operation,'failed','По явному запросу возвращено страховочное полное состояние')
            return dict(operation_id=operation['id'],state='failed',reconciled=True)
