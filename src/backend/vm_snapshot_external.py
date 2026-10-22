"""Explicit offline single-disk external branches with an imported private base.

No commit/rebase/merge and no deletion of dependent nodes. Original user media
is copied byte-for-byte, never made readonly, converted or used as backing.
"""
from copy import deepcopy
from pathlib import Path
import hashlib
import json
import os
import stat
import uuid
import time
from vm_snapshot_lease import ImageLease
from vm_snapshot_service import identity,encrypted,TTL
from vm_snapshot_store import digest,operation_lock
from vm_snapshots import configured_disks


def sha(path):
    value=hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda:stream.read(4*1024*1024),b''):value.update(block)
    return value.hexdigest()


class ExternalSnapshots:
    def __init__(self,service):self.service=service;self.store=service.store
    def info(self,path):return json.loads(self.service.run(self.service.exe,['info','--output=json',str(path)]))

    def check_graph(self,catalog,online=False):
        graph=catalog['settings'].get('external')
        if not graph:return None
        nodes=graph['nodes'];seen=set();node_id=graph['active']
        if len({tuple(node['file_id']) for node in nodes.values()})!=len(nodes):raise ValueError('Один физический файл повторяется в графе зависимостей.')
        folder=Path(graph['folder']).resolve()
        for start,node in nodes.items():
            if folder not in Path(node['path']).resolve().parents:raise ValueError('Узел вышел за пределы принадлежащего цепочке каталога.')
            chain=set();cursor=start
            while cursor is not None:
                if cursor in chain or cursor not in nodes:raise ValueError('Цикл или пропавший родитель в графе цепочки.')
                chain.add(cursor);cursor=nodes[cursor].get('parent')
        while node_id is not None:
            if node_id in seen or node_id not in nodes:raise ValueError('Цикл или пропавшая зависимость внешней цепочки.')
            seen.add(node_id);node=nodes[node_id];path=Path(node['path']);actual=identity(path)
            if actual['file_id']!=node['file_id']:raise ValueError('Файл внешней цепочки заменён.')
            if online and node_id==graph['active']:
                node_id=node.get('parent');continue
            info=self.info(path)
            if info.get('format')!=node['format']:raise ValueError('Формат внешнего слоя изменился.')
            if node_id!=graph['active']:
                if not path.stat().st_file_attributes&stat.FILE_ATTRIBUTE_READONLY or sha(path)!=node['sha256']:
                    raise ValueError('Запечатанный базовый слой изменён. Подстановка другого файла запрещена.')
            parent=node.get('parent')
            if parent:
                backing=info.get('full-backing-filename') or info.get('backing-filename')
                if not backing or os.path.normcase(str(Path(backing).resolve()))!=nodes[parent]['path']:
                    raise ValueError('Backing-путь не соответствует зарегистрированной зависимости.')
            elif info.get('backing-filename'):raise ValueError('У базы появилась неизвестная зависимость.')
            node_id=parent
        # Validate sealed side branches too, not merely active ancestors.
        for key,node in nodes.items():
            if key in seen:continue
            path=Path(node['path'])
            if identity(path)['file_id']!=node['file_id'] or not path.stat().st_file_attributes&stat.FILE_ATTRIBUTE_READONLY or sha(path)!=node['sha256']:
                raise ValueError('Боковая ветка цепочки повреждена или изменена.')
            parent=node.get('parent')
            if parent not in nodes:raise ValueError('Пропавшая база боковой ветки.')
            info=self.info(path);backing=info.get('full-backing-filename') or info.get('backing-filename')
            if info.get('format')!=node['format'] or not backing or os.path.normcase(str(Path(backing).resolve()))!=nodes[parent]['path']:
                raise ValueError('Зависимость боковой ветки изменилась.')
        return graph

    def catalog(self,online):
        catalog=self.store.catalog();graph=self.check_graph(catalog,online);rows=[]
        for group in catalog['groups']:
            row=deepcopy(group)
            try:
                node=graph['nodes'][group['external_node']]
                if identity(node['path'])['file_id']!=node['file_id'] or sha(node['path'])!=node['sha256']:raise ValueError('Слой изменился.')
                row.update(state='ready',chain_description='Запечатанный слой; зависимые файлы не удаляются и не сливаются автоматически.')
            except (KeyError,OSError,ValueError) as error:row.update(state='missing',reason=str(error))
            rows.append(row)
        from vm_snapshot_store import FINAL
        operations=self.store.operations();pending=[row for row in operations if row['state'] not in FINAL]
        return dict(rows=sorted(rows,key=lambda row:(row['date'],row['id']),reverse=True),online=online,vm_state='running' if online else 'stopped',
                    revision=catalog['revision'],context=self.service.load(),recovery_required=bool(pending),
                    operations=[dict(id=row['id'],phase=row['state'],detail=row.get('phase','')) for row in pending],
                    history=[dict(id=row['id'],date=str(row['created']),action=row['action'],state=row['state'],detail=row.get('phase','')) for row in operations],
                    default_snapshot_kind='external',capabilities=['refresh','metadata','metadata_on_create','protect','recover','cancel']+([] if online else ['external','create_offline','restore_external','restore','restore_start','export']))

    def maintenance_plan(self,action='merge'):
        if action not in ('commit','rebase','merge'):raise ValueError('Неизвестная операция обслуживания цепочки.')
        with operation_lock(self.service.root,self.service.name):
            self.store.assert_clear();self.service.assert_offline();graph=self.check_graph(self.store.catalog())
            if graph is None:raise ValueError('Внешняя цепочка не зарегистрирована.')
            nodes=graph['nodes'];active=nodes[graph['active']];info=self.info(active['path'])
            virtual=info['virtual-size'];allocated=sum(Path(node['path']).stat().st_size for node in nodes.values())
            # Conservative full-image workspace + separate rollback copy. This
            # is planning only, never permission to modify a shared base.
            required=2*virtual+allocated+128*1024*1024;free=self.service.free(active['path'])
            work=virtual+allocated
            return dict(action=action,executable=False,required_bytes=required,free_bytes=free,space_available=free>=required,
                        duration_seconds_range=[max(1,int(work/(100*1024*1024))),max(1,int(work/(10*1024*1024)))],
                        duration_note='Ориентир при 10–100 МиБ/с, не гарантированная верхняя граница; CPU, носитель и проверка могут занять больше времени.',
                        dependencies=[dict(node=key,parent=node.get('parent'),path=node['path']) for key,node in nodes.items()],
                        reason='Commit/rebase/merge не выполняются автоматически. Нужны отдельный подтверждённый план, проверка всех зависимостей и нового результата; удаление файла не является слиянием.')

    def plan(self,group_id=None,options=None):
        from vm_snapshot_offline import options_checked
        options=options or {};label,description=options_checked(dict(options,kind='disk'))
        if options.get('include_config'):raise ValueError('Внешние цепочки пока восстанавливают только диски.')
        with operation_lock(self.service.root,self.service.name):
            self.store.assert_clear();self.service.assert_offline();catalog=self.store.catalog();graph=self.check_graph(catalog)
            if not graph and catalog['groups']:raise ValueError('Внутренний каталог нельзя молча преобразовать во внешнюю цепочку.')
            paths=configured_disks(self.service.load())
            if len(paths)!=1 or os.path.normcase(str(paths[0].resolve()))!=identity(self.service.load()['disk_path'])['path']:
                raise ValueError('Внешние цепочки поддерживают один управляемый диск без дополнительных записываемых носителей.')
            source=identity(paths[0]);info=self.info(paths[0])
            if not graph and (info.get('format') not in ('vdi','raw','qcow2') or encrypted(info) or info.get('backing-filename')):
                raise ValueError('Для импорта нужна отдельная незашифрованная база VDI/raw/qcow2 без backing chain.')
            if graph and source['file_id']!=graph['nodes'][graph['active']]['file_id']:raise ValueError('Текущий диск не соответствует вершине цепочки.')
            selected=next((row for row in catalog['groups'] if row['id']==group_id),None) if group_id else None
            if group_id and (not selected or selected.get('external_node') not in graph['nodes']):raise ValueError('Внешняя точка не найдена.')
            if self.service.free(paths[0])<paths[0].stat().st_size+128*1024*1024:raise ValueError('Недостаточно места для импорта базы и рабочего слоя.')
            now=self.service.clock()
            operation_id=str(uuid.uuid4())
            destination=str(Path(graph['folder'])/operation_id if graph else paths[0].parent/('.qemum-external-'+operation_id))
            return dict(schema_version=1,id=operation_id,name=self.service.name,action='external',created=now,expires=now+TTL,destination_folder=destination,
                        config_hash=self.service.config_hash(),peer_hashes=self.service.peer_hashes(),catalog_hash=digest(catalog),disks=[source],
                        label=label,description=description,group_id=str(uuid.uuid4()),selected=selected,format=info['format'])

    def execute(self,plan):
        if plan.get('name')!=self.service.name or plan.get('action')!='external':raise ValueError('Неверная принадлежность плана.')
        with operation_lock(self.service.root,self.service.name):
            previous=self.store.operation(plan['id'])
            if previous:return self.service.result(previous)
            self.store.assert_clear();self.service.assert_offline()
            if not plan['created']<=self.service.clock()<=plan['expires'] or self.service.config_hash()!=plan['config_hash'] or self.service.peer_hashes()!=plan['peer_hashes'] or digest(self.store.catalog())!=plan['catalog_hash']:raise ValueError('План внешней цепочки устарел.')
            current=self.store.catalog();updated=deepcopy(current);graph=self.check_graph(current)
            config=self.service.root/(self.service.name+'.json');before=config.read_bytes()
            operation=dict(schema_version=1,id=plan['id'],name=self.service.name,action='external',created=self.service.clock(),state='validating',steps=[],
                           plan=plan,before_catalog=current,before_config=before.hex(),mutation_started=False,files=[])
            self.service.transition(operation,'validating','Создание отдельной внешней ветки')
            try:
                with ImageLease(plan['disks'][0]['path']) as lease:
                    if lease.file_id!=plan['disks'][0]['file_id']:raise ValueError('Исходный диск заменён.')
                    folder=Path(plan['destination_folder'])
                    folder.mkdir()
                    if graph is None:
                        base=folder/('base.'+plan['format']);lease.copy_to(base)
                        imported=self.info(base)
                        if imported.get('format')!=plan['format']:raise ValueError('Формат копии базы отличается.')
                        base_id=str(uuid.uuid4());os.chmod(base,stat.S_IREAD)
                        graph=dict(schema_version=1,folder=str(folder),nodes={base_id:dict(identity(base),format=plan['format'],parent=None,sha256=sha(base))},active=base_id)
                    else:
                        graph=deepcopy(graph);active=graph['nodes'][graph['active']]
                        operation['mutation_started']=True
                        self.service.transition(operation,'running','Перед запечатыванием текущей вершины')
                        os.chmod(active['path'],stat.S_IREAD)
                        # Read/hash through a temporary copy while our handle
                        # prevents external writers; no qemu-img on the source.
                        seal=folder/'sealed-copy.qcow2';lease.copy_to(seal)
                        active['sha256']=sha(seal);seal.unlink()
                        # Windows QEMU opens even readonly backing files without
                        # FILE_SHARE_DELETE. Seal our private node before releasing
                        # its handle; the readonly attribute denies normal writers.
                        lease.close()
                    # Save the previous active branch as a checkpoint when
                    # switching to an earlier point. Never discard current work.
                    parent_id=plan['selected']['external_node'] if plan.get('selected') else graph['active']
                    parent=graph['nodes'][parent_id]
                    tip=folder/'active.qcow2'
                    self.service.run(self.service.exe,['create','-f','qcow2','-F',parent['format'],'-b',parent['path'],str(tip)])
                    self.service.run(self.service.exe,['check','--output=json',str(tip)])
                    tip_id=str(uuid.uuid4());graph['nodes'][tip_id]=dict(identity(tip),format='qcow2',parent=parent_id)
                    point=graph['nodes'][graph['active']]
                    group=dict(id=plan['group_id'],name='external-'+plan['group_id'],label=plan['label'] or 'Внешняя точка '+time.strftime('%Y-%m-%d %H:%M',time.localtime(plan['created'])),
                               description=plan['description'],date=int(plan['created']),kind='external',state='ready',protected=True,
                               external_node=graph['active'],disks=[dict(point,snapshot_id=graph['active'])],operation_id=plan['id'],
                               checkpoint=bool(plan.get('selected')),checkpoint_reason='Предыдущая вершина перед переключением' if plan.get('selected') else '')
                    updated['groups'].append(group);graph['active']=tip_id;updated['settings']['external']=graph;updated['revision']+=1
                    updated['settings']['schedule']['enabled']=False
                    data=self.service.load();data['disk_path']=str(tip);data['disk_format']='qcow2'
                    after=json.dumps(data,ensure_ascii=False,sort_keys=True,allow_nan=False).encode()
                    operation.update(after_catalog=updated,after_config=after.hex(),mutation_started=True,
                                     result=dict(group_id=group['id'],operation_id=plan['id'],result=group['name'],disks_restored=bool(plan.get('selected'))))
                    self.service.transition(operation,'running','Перед публикацией параметров и графа зависимостей')
                    if config.read_bytes()!=before:raise ValueError('Конфигурация изменилась.')
                    self.store.atomic_bytes(self.store.folder/'external-config-backups'/(plan['id']+'.json'),before)
                    self.store.atomic_bytes(config,after)
                    self.store.replace_catalog(updated,digest(current))
                    self.service.transition(operation,'completed','Внешняя цепочка опубликована; оригинальная база не изменялась')
                    return operation['result']
            except BaseException as error:
                if current['settings'].get('external') and config.read_bytes()==before and digest(self.store.catalog())==digest(current):
                    active=current['settings']['external']['nodes'][current['settings']['external']['active']]
                    if identity(active['path'])['file_id']==active['file_id']:
                        os.chmod(active['path'],stat.S_IREAD|stat.S_IWRITE)
                        operation['mutation_started']=False
                self.service.transition(operation,'recovery-required' if operation['mutation_started'] else 'failed',str(error))
                raise

    def recover(self,operation):
        self.service.assert_offline();current=digest(self.store.catalog());config=self.service.root/(self.service.name+'.json')
        if operation.get('after_catalog') and current==digest(operation['after_catalog']) and config.read_bytes().hex()==operation['after_config']:
            self.check_graph(self.store.catalog());self.service.transition(operation,'completed','Граф и параметры сверены после сбоя');return operation['result']
        if current!=digest(operation['before_catalog']) or config.read_bytes().hex() not in (operation['before_config'],operation.get('after_config')):raise ValueError('Внешняя цепочка изменилась; автоматический повтор запрещён.')
        self.store.atomic_bytes(config,bytes.fromhex(operation['before_config']))
        # A previously active node may have become readonly before the crash.
        graph=operation['before_catalog']['settings'].get('external')
        if graph:os.chmod(graph['nodes'][graph['active']]['path'],stat.S_IREAD|stat.S_IWRITE)
        self.service.transition(operation,'failed','Прежняя конфигурация возвращена; новые слои сохранены отдельно')
        return dict(operation_id=operation['id'],state='failed',reconciled=True)
