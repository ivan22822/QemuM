"""Offline copy/verify/publish transactions. Originals remain leased throughout.

Every rename has durable intent. Retained old images are recovery assets, never
silently removed. Recovery refuses unknown files instead of guessing ownership.
"""
from contextlib import ExitStack
from copy import deepcopy
import json
import os
from pathlib import Path
import uuid
import time

from vm_snapshot_lease import ImageLease, lease_images
from vm_snapshot_offline import OfflinePreparation
from vm_snapshot_service import identity, resources
from vm_snapshot_store import digest, operation_lock, FINAL


class OfflineTransaction:
    def __init__(self, service):
        self.service=service; self.store=service.store; self.preparation=OfflinePreparation(service)

    def plan(self, action, group_id=None, options=None):
        if action not in ('offline-create','offline-delete','offline-restore'):
            raise ValueError('Неизвестная офлайн-операция.')
        with operation_lock(self.service.root,self.service.name):
            plan=self.preparation.plan(options if action=='offline-create' else None)
            import vm
            plan['qmp_port']=vm.qmp_port(self.service.name,self.service.load())
            plan['action']=action
            if action!='offline-create':
                catalog=self.store.catalog()
                group=next((row for row in catalog['groups'] if row['id']==group_id),None)
                if not group:raise ValueError('UUID снимка не найден в подтверждённом каталоге.')
                checked=next(row for row in self.service.reconcile([group],self.preparation.disks()) if row['id']==group_id)
                if checked['state']!='ready':raise ValueError('Состав снимка не подтверждён: '+checked['state'])
                if action=='offline-delete' and group.get('protected'):
                    raise ValueError('Сначала отдельно сними защиту снимка.')
                plan['selected']=deepcopy(group)
                scope=(options or {}).get('restore_scope','disks')
                if scope not in ('disks','disks_and_config'):raise ValueError('Неизвестный объём восстановления.')
                if action=='offline-restore' and scope=='disks_and_config':
                    from vm_snapshot_parameters import restore, differences
                    restored=restore(group.get('parameters'),self.service.load())
                    if {row['path'] for row in plan['disks']}!={identity(path)['path'] for path in __import__('vm_snapshots').configured_disks(restored)}:
                        raise ValueError('Сохранённые настройки меняют набор дисков; такой откат пока запрещён.')
                    plan['restore_config']=restored
                    plan['config_diff']=differences(group.get('parameters'),self.service.load())
                    from vm_snapshot_parameters import capture
                    plan['current_parameters']=capture(self.service.load())
            return plan

    def info(self,path):
        return json.loads(self.service.run(self.service.exe,['info','--output=json',str(path)]))

    def execute(self,plan,progress=lambda text,*counts:None,cancelled=lambda:False):
        if plan.get('name')!=self.service.name or plan.get('action') not in ('offline-create','offline-delete','offline-restore'):
            raise ValueError('План относится к другой VM или операции.')
        with operation_lock(self.service.root,self.service.name,[digest(row['file_id']) for row in plan['disks']]):
            previous=self.store.operation(plan['id'])
            if previous:return self.service.result(previous)
            self.store.assert_clear();self.service.assert_offline();self.preparation.check(plan)
            rows=self.preparation.disks();self.preparation.space(rows)
            if resources(rows)!=plan['disks']:raise ValueError('Состав образов изменился.')
            current=self.store.catalog();updated=deepcopy(current)
            operation=dict(schema_version=1,id=plan['id'],name=self.service.name,action=plan['action'],plan=plan,
                           state='validating',steps=[],created=self.service.clock(),mutation_started=False,
                           swaps=[],before_catalog=current)
            self.service.transition(operation,'validating','Оригиналы ещё не изменялись')
            with ExitStack() as stack:
                originals=stack.enter_context(lease_images([row['path'] for row in plan['disks']]))
                original_by_id={tuple(lease.file_id):lease for lease in originals}
                new_leases=[]
                try:
                    members=[];checkpoint_members=[]
                    for index,(disk,signature) in enumerate(zip(plan['disks'],plan['signatures'])):
                        lease=original_by_id[tuple(disk['file_id'])]
                        if lease.size!=signature['size'] or Path(disk['path']).stat().st_mtime_ns!=signature['mtime_ns']:
                            raise ValueError('Образ изменился перед резервированием.')
                        if cancelled():raise InterruptedError('Отменено до публикации.')
                        folder=Path(disk['path']).parent/('.qemum-stage-'+plan['id']+'-'+str(index));folder.mkdir()
                        clone=folder/('disk-'+str(index)+'.qcow2');backup=folder/'original.qcow2'
                        swap=dict(source=disk['path'],old_id=disk['file_id'],clone=str(clone),backup=str(backup),folder=str(folder))
                        operation['swaps'].append(swap)
                        self.service.transition(operation,'running','Создаю рабочую копию '+str(index+1));progress(operation['phase'])
                        lease.copy_to(clone,lambda done,total:progress('Копирование диска '+str(index+1),done,total),cancelled=cancelled)
                        progress('Проверка рабочей копии диска '+str(index+1))
                        swap['new_id']=identity(clone)['file_id'];self.store.save_operation(operation)
                        before=self.info(clone)
                        if plan['action']=='offline-create':
                            self.service.run(self.service.exe,['snapshot','-c',plan['tag'],str(clone)])
                        else:
                            selected=next(member for member in plan['selected']['disks'] if member['path']==disk['path'] and member['file_id']==disk['file_id'])
                            actual=[item for item in before.get('snapshots',[]) if str(item.get('id'))==selected['snapshot_id'] and item.get('name')==plan['selected']['name']]
                            if len(actual)!=1 or sum(item.get('name')==plan['selected']['name'] for item in before.get('snapshots',[]))!=1:raise ValueError('Внутренний ID снимка изменился или тег неоднозначен.')
                            reference=folder/'reference.qcow2'
                            if plan['action']=='offline-restore':
                                self.service.run(self.service.exe,['convert','-l','snapshot.id='+selected['snapshot_id'],'-O','qcow2',str(clone),str(reference)])
                                self.service.run(self.service.exe,['snapshot','-c',plan['tag'],str(clone)])
                                self.service.run(self.service.exe,['snapshot','-a',selected['snapshot_id'],str(clone)])
                            else:
                                self.service.run(self.service.exe,['convert','-O','qcow2',str(clone),str(reference)])
                                # QEMU 11 snapshot -d accepts a name. Resolve its
                                # unique tag against the exact recorded ID first.
                                self.service.run(self.service.exe,['snapshot','-d',plan['selected']['name'],str(clone)])
                            self.service.run(self.service.exe,['compare','-f','qcow2','-F','qcow2',str(clone),str(reference)])
                            reference.unlink()
                        self.service.run(self.service.exe,['check','--output=json',str(clone)])
                        after=self.info(clone)
                        old_snapshots={(str(item.get('id')),item.get('name')) for item in before.get('snapshots',[])}
                        actual_snapshots={(str(item.get('id')),item.get('name')) for item in after.get('snapshots',[])}
                        expected=old_snapshots.copy()
                        if plan['action']=='offline-delete':expected.remove((selected['snapshot_id'],plan['selected']['name']))
                        else:
                            made=[item for item in after.get('snapshots',[]) if item.get('name')==plan['tag'] and not item.get('vm-state-size',0)]
                            if len(made)!=1:raise ValueError('Новая точка не подтверждена.')
                            expected.add((str(made[0]['id']),plan['tag']))
                            member=dict(disk,file_id=swap['new_id'],snapshot_id=str(made[0]['id']))
                            (checkpoint_members if plan['action']=='offline-restore' else members).append(member)
                        if actual_snapshots!=expected:raise ValueError('Изменился состав других снимков; публикация запрещена.')
                        if cancelled():raise InterruptedError('Отменено до публикации.')
                        new_leases.append(stack.enter_context(ImageLease(clone)))
                        swap.update(new_size=clone.stat().st_size,new_mtime_ns=clone.stat().st_mtime_ns,old_size=signature['size'],old_mtime_ns=signature['mtime_ns'])
                        self.service.transition(operation,'verifying','Рабочая копия проверена '+str(index+1))
                    self.preparation.check(plan,expiry=False)
                    if plan.get('restore_config') is not None:
                        config=self.service.root/(self.service.name+'.json')
                        old=stack.enter_context(ImageLease(config));original_by_id[tuple(old.file_id)]=old
                        folder=config.parent/('.qemum-stage-'+plan['id']+'-config');folder.mkdir()
                        clone=folder/'parameters.json'
                        self.store.atomic(clone,plan['restore_config'])
                        new=stack.enter_context(ImageLease(clone));new_leases.append(new)
                        operation['swaps'].append(dict(source=str(config),old_id=old.file_id,clone=str(clone),backup=str(folder/'original.json'),
                            folder=str(folder),new_id=new.file_id,new_size=clone.stat().st_size,new_mtime_ns=clone.stat().st_mtime_ns,
                            old_size=old.size,old_mtime_ns=config.stat().st_mtime_ns))
                        # Recheck through the held handle rather than reopening
                        # the reserved configuration in a conflicting mode.
                        verify=folder/'config-check.json';old.copy_to(verify)
                        matches=digest(verify.read_bytes().hex())==plan['config_hash'];verify.unlink()
                        if not matches:raise ValueError('Конфигурация изменилась перед резервированием.')
                    for group in updated['groups']:
                        for member in group['disks']:
                            for swap in operation['swaps']:
                                if member['path']==swap['source'] and member['file_id']==swap['old_id']:member['file_id']=swap['new_id']
                    if plan['action']=='offline-delete':updated['groups']=[group for group in updated['groups'] if group['id']!=plan['selected']['id']]
                    else:
                        group=dict(id=plan['group_id'],name=plan['tag'],label=plan['label'] or 'Снимок '+time.strftime('%Y-%m-%d %H:%M',time.localtime(plan['created'])),
                                   description=plan['description'],date=int(plan['created']),kind='disk',state='ready',
                                   disks=members or checkpoint_members,protected=bool(checkpoint_members),checkpoint=bool(checkpoint_members),
                                   checkpoint_reason='Перед восстановлением '+plan.get('selected',{}).get('id','') if checkpoint_members else '',
                                   source='checkpoint' if checkpoint_members else plan.get('source','user'),operation_id=plan['id'],consistency_description='VM выключена; только диски.')
                        group['parameters']=plan.get('parameters')
                        if checkpoint_members and plan.get('restore_config') is not None:
                            group['parameters']=plan['current_parameters']
                        updated['groups'].append(group)
                    updated['revision']+=1;operation['after_catalog']=updated
                    operation['result']=dict(operation_id=plan['id'],group_id=plan.get('selected',{}).get('id',plan['group_id']),
                                             result=plan['tag'],checkpoint=plan['tag'] if checkpoint_members else None,disks_restored=bool(checkpoint_members))
                    if cancelled():raise InterruptedError('Отменено перед публикацией. Оригиналы не изменены.')
                    progress('Публикация проверенных образов; отмена больше недоступна')
                    operation['mutation_started']=True
                    self.service.transition(operation,'running','Публикация проверенных образов; отмена больше недоступна')
                    for swap,new in zip(operation['swaps'],new_leases):
                        old=original_by_id[tuple(swap['old_id'])]
                        self.service.transition(operation,'running','Перед переносом оригинала '+swap['source'])
                        old.rename_to(swap['backup'])
                        self.service.transition(operation,'running','Перед публикацией копии '+swap['source'])
                        new.rename_to(swap['source'])
                        self.service.transition(operation,'verifying','Опубликован образ '+swap['source'])
                    self.store.replace_catalog(updated,digest(current))
                    self.service.transition(operation,'completed','Все образы и каталог опубликованы. VM выключена; прежние образы сохранены в журнале.')
                    return operation['result']
                except BaseException as error:
                    # Retain every recovery file. Recovery uses recorded file IDs,
                    # not phase strings that may lag a completed native rename.
                    self.service.transition(operation,'recovery-required' if operation['mutation_started'] else 'cancelled' if isinstance(error,InterruptedError) else 'failed',str(error))
                    if operation['mutation_started']:
                        try:self._rollback(operation,original_by_id,new_leases)
                        except BaseException as rollback_error:
                            self.service.transition(operation,'recovery-required',str(error)+'; компенсация: '+str(rollback_error))
                            raise ValueError('Публикация прервана. VM заблокирована, файлы сохранены. ID '+plan['id']) from error
                    raise

    def _rollback(self,operation,old_by_id,new_leases):
        current=self.store.catalog()
        if digest(current)!=digest(operation['before_catalog']):
            raise ValueError('Каталог изменён; нужна явная сверка.')
        new_by_id={tuple(lease.file_id):lease for lease in new_leases}
        for swap in reversed(operation['swaps']):
            source=Path(swap['source']);old=old_by_id[tuple(swap['old_id'])]
            if source.exists() and identity(source)['file_id']==swap.get('new_id'):
                new_by_id[tuple(swap['new_id'])].rename_to(swap['clone'])
            if source.exists():
                if identity(source)['file_id']!=swap['old_id']:raise ValueError('Исходный путь занят неизвестным файлом.')
            else:old.rename_to(source)
            self.service.transition(operation,'running','Компенсация подтверждена '+str(source))
        self.service.transition(operation,'failed','Прежние образы и каталог возвращены. Рабочие копии сохранены для проверки.')

    def recover(self,operation):
        with operation_lock(self.service.root,self.service.name):
            from vm_storage import assert_stopped
            plan=operation['plan']
            config=self.service.root/(self.service.name+'.json')
            if config.exists():query=lambda:self.service.request('query-status')
            else:
                import vm
                query=lambda:vm.qmp_command(self.service.name,plan['qmp_port'],'query-status',timeout=2)
            assert_stopped(self.service.name,[(Path(row['path']),'disk') for row in plan['disks']],query,self.service.processes)
            # Missing source paths are expected between two renames. An unknown
            # QEMU is not: recovery never guesses its open disk set.
            if self.service.processes():raise ValueError('Перед сверкой закрой сторонние QEMU.')
            if operation['state'] in FINAL:return self.service.result(operation)
            peers=self.service.peer_hashes();own=self.service.name+'.json'
            expected_peers=dict(plan['peer_hashes']);peers.pop(own,None);expected_peers.pop(own,None)
            allowed_configs={plan['config_hash']}
            if plan.get('restore_config') is not None:
                encoded=json.dumps(plan['restore_config'],ensure_ascii=False,sort_keys=True,allow_nan=False).encode('utf-8')
                allowed_configs.add(digest(encoded.hex()))
            config_path=self.service.root/own
            if (config_path.exists() and self.service.config_hash() not in allowed_configs) or peers!=expected_peers:
                raise ValueError('Конфигурации изменились; автоматическая сверка запрещена.')
            swaps=operation['swaps'];paths=[]
            for swap in swaps:
                paths.extend(Path(path) for path in (swap['source'],swap['backup'],swap['clone']) if Path(path).exists())
            with lease_images(paths) as leases:
                by_id={tuple(lease.file_id):lease for lease in leases}
                known={tuple(swap[key]) for swap in swaps for key in ('old_id','new_id') if key in swap}
                if set(by_id)-known:raise ValueError('На пути восстановления появился неизвестный файл.')
                for swap in swaps:
                    for key,prefix in (('old_id','old'),('new_id','new')):
                        if key not in swap:continue
                        lease=by_id.get(tuple(swap[key]))
                        if lease is None:raise ValueError('Не найден зарегистрированный образ для сверки.')
                        stat=lease.path.stat()
                        if prefix+'_size' in swap and (stat.st_size!=swap[prefix+'_size'] or stat.st_mtime_ns!=swap[prefix+'_mtime_ns']):
                            raise ValueError('Образ изменился после сбоя; автоматическая компенсация запрещена.')
                if operation.get('after_catalog') and digest(self.store.catalog())==digest(operation['after_catalog']):
                    if not all(identity(swap['source'])['file_id']==swap['new_id'] for swap in swaps):raise ValueError('Каталог опубликован, но набор образов отличается.')
                    self.service.transition(operation,'completed','Публикация всех образов и каталога подтверждена после сбоя')
                    return self.service.result(operation)
                self._rollback(operation,{tuple(swap['old_id']):by_id[tuple(swap['old_id'])] for swap in swaps},
                               [by_id[tuple(swap['new_id'])] for swap in swaps if 'new_id' in swap])
                return dict(operation_id=operation['id'],state='failed',reconciled=True)
