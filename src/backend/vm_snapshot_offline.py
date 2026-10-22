"""Stage 11: offline plans and isolated preparation; no image publication yet."""
from contextlib import contextmanager
from pathlib import Path
import json
import os
import shutil
import time
import uuid

from vm_snapshot_lease import lease_images
from vm_snapshot_service import identity, encrypted, resources, TTL, MIN_FREE
from vm_snapshot_store import digest, operation_lock
from vm_snapshots import configured_disks


def options_checked(options):
    if not isinstance(options, dict) or set(options)-{'label','description','kind','include_config','consistency'}:
        raise ValueError('Неизвестные настройки офлайн-снимка.')
    if 'include_config' in options and type(options['include_config']) is not bool:raise ValueError('Сохранение параметров должно быть явным выбором.')
    if options.get('kind','disk')!='disk' or options.get('consistency','crash')!='crash':
        raise ValueError('Подготовка офлайн-снимка поддерживает только диски без RAM и сохранения параметров.')
    label,description=options.get('label',''),options.get('description','')
    if not isinstance(label,str) or not isinstance(description,str) or len(label)>120 or len(description)>4000 or any(ord(char)<32 for char in label):
        raise ValueError('Название — одна строка до 120 символов, описание — до 4000.')
    return label,description


class OfflinePreparation:
    def __init__(self, service):
        self.service=service
        self.store=service.store

    def disks(self):
        rows=[]
        data=self.service.load()
        for index,path in enumerate(configured_disks(data)):
            info=json.loads(self.service.run(self.service.exe,['info','--output=json',str(path)]))
            if index==0 and data.get('disk_format') and data['disk_format']!=info.get('format'):
                raise ValueError('Заявленный формат основного диска отличается от фактического; проверь параметры VM.')
            if info.get('format')!='qcow2' or encrypted(info) or info.get('backing-filename'):
                raise ValueError('Офлайн-подготовка требует отдельных незашифрованных qcow2 без backing chain.')
            stat=path.stat()
            rows.append(dict(identity(path),device='',role='disk-'+str(index),format='qcow2',
                             size=stat.st_size,mtime_ns=stat.st_mtime_ns,snapshots=info.get('snapshots',[])))
        return rows

    def space(self, rows):
        volumes={}
        for row in rows:
            volume=str(Path(row['path']).anchor).casefold()
            volumes.setdefault(volume,[]).append(row)
        for rows in volumes.values():
            # Full byte-copy budget, no optimistic sparse/reflink assumption.
            required=sum(row['size'] for row in rows)+MIN_FREE*len(rows)
            if self.service.free(rows[0]['path'])<required:
                raise ValueError('Недостаточно места для полных рабочих копий и резерва. Оригиналы не изменены.')

    def plan(self, options=None):
        label,description=options_checked(options or {})
        with operation_lock(self.service.root,self.service.name):
            self.store.assert_clear();self.service.assert_offline()
            rows=self.disks();self.space(rows)
            now=self.service.clock()
            group_id=str(uuid.uuid4())
            plan=dict(schema_version=1,id=str(uuid.uuid4()),name=self.service.name,action='prepare-offline',
                        created=now,expires=now+TTL,group_id=group_id,tag='qemum-'+group_id,
                        disks=resources(rows),signatures=[dict(size=row['size'],mtime_ns=row['mtime_ns']) for row in rows],
                        config_hash=self.service.config_hash(),peer_hashes=self.service.peer_hashes(),catalog_hash=digest(self.store.catalog()),
                        label=label,description=description,vm_state='stopped',excluded=[])
            if (options or {}).get('include_config'):
                from vm_snapshot_parameters import capture
                plan['parameters']=capture(self.service.load())
            return plan

    @contextmanager
    def prepared(self, plan, progress=lambda phase,done=None,total=None:None, cancelled=lambda:False):
        """Yield verified private disk-only snapshots while every source is leased.

        This scope never publishes replacements. On normal exit/cancel it removes
        only the exact private files it created, then releases source handles.
        A worker crash leaves a journal and source identity for explicit recovery.
        """
        if plan.get('name')!=self.service.name or plan.get('action')!='prepare-offline':
            raise ValueError('План относится к другой VM или операции.')
        paths=[Path(row['path']) for row in plan['disks']]
        with operation_lock(self.service.root,self.service.name,[digest(row['file_id']) for row in plan['disks']]):
            if self.store.operation(plan['id']):
                raise ValueError('Подготовка с этим ID уже выполнялась; повтор не запускается.')
            self.store.assert_clear();self.service.assert_offline()
            self.check(plan)
            rows=self.disks();self.space(rows)
            if resources(rows)!=plan['disks']:
                raise ValueError('Состав дисков изменился.')
            created=[];folders=[];operation=None;owned_ids={}
            try:
                with lease_images(paths) as leases:
                    by_path={os.path.normcase(str(lease.path.resolve())):lease for lease in leases}
                    for disk,signature in zip(plan['disks'],plan['signatures']):
                        lease=by_path[disk['path']]
                        if lease.file_id!=disk['file_id'] or lease.size!=signature['size'] or Path(disk['path']).stat().st_mtime_ns!=signature['mtime_ns']:
                            raise ValueError('Образ изменился перед получением непрерывного владения.')
                    operation=dict(schema_version=1,id=plan['id'],name=self.service.name,action='prepare-offline',
                                   state='validating',plan=plan,created=self.service.clock(),steps=[],mutation_started=False)
                    self.service.transition(operation,'validating','Все оригиналы зарезервированы; готовлю приватные копии')
                    prepared=[]
                    for index,disk in enumerate(plan['disks']):
                        if cancelled():raise InterruptedError('Подготовка отменена до публикации.')
                        folder=Path(disk['path']).parent/('.qemum-stage-'+plan['id']+'-'+str(index))
                        folder.mkdir();folders.append(folder)
                        clone=folder/('disk-'+str(index)+'.qcow2')
                        operation['private_files']=[str(item) for item in created]+[str(clone)]
                        self.service.transition(operation,'running','Копирование диска '+str(index+1))
                        by_path[disk['path']].copy_to(clone,lambda done,total:progress('Копирование диска '+str(index+1),done,total),cancelled)
                        created.append(clone)
                        owned_ids[clone]=identity(clone)['file_id']
                        if cancelled():raise InterruptedError('Подготовка отменена до публикации.')
                        self.service.run(self.service.exe,['snapshot','-c',plan['tag'],str(clone)])
                        self.service.run(self.service.exe,['check','--output=json',str(clone)])
                        info=json.loads(self.service.run(self.service.exe,['info','--output=json',str(clone)]))
                        matches=[item for item in info.get('snapshots',[]) if item.get('name')==plan['tag']]
                        if info.get('format')!='qcow2' or len(matches)!=1 or matches[0].get('vm-state-size',0) or not matches[0].get('id'):
                            raise ValueError('В приватной копии не подтверждён единственный disk-only снимок.')
                        prepared.append(dict(source=disk,clone=str(clone),clone_identity=identity(clone),snapshot_id=str(matches[0]['id'])))
                        operation['steps'].append(dict(disk=disk['path'],private_file=str(clone),confirmed=True,snapshot_id=str(matches[0]['id'])))
                        self.service.transition(operation,'verifying','Проверен приватный снимок диска '+str(index+1))
                    self.check(plan,expiry=False)
                    progress('Приватная группа проверена; публикация ещё не реализована')
                    yield dict(operation_id=plan['id'],tag=plan['tag'],group_id=plan['group_id'],disks=prepared,published=False)
                    self.service.transition(operation,'cancelled','Подготовительный просмотр завершён. В оригиналы ничего не публиковалось.')
            except BaseException as error:
                if operation is not None:
                    self.service.transition(operation,'cancelled' if isinstance(error,InterruptedError) else 'failed',str(error))
                raise
            finally:
                # No recursive deletion. Only owned files, never a borrowed folder.
                cleanup_errors=[]
                for path in reversed(created):
                    try:
                        if path.exists():
                            if identity(path)['file_id']!=owned_ids[path]:raise ValueError('Приватный файл заменён; удаление запрещено.')
                            path.unlink()
                    except (OSError,ValueError) as error:cleanup_errors.append(str(error))
                for folder in reversed(folders):
                    try:folder.rmdir()
                    except OSError as error:cleanup_errors.append(str(error))
                if cleanup_errors:
                    if operation is not None:self.service.transition(operation,'uncertain','Не подтверждена очистка приватных копий: '+'; '.join(cleanup_errors))
                    raise ValueError('Приватные файлы сохранены для проверки. Оригиналы не публиковались. ID: '+plan['id'])

    def check(self, plan, expiry=True):
        now=self.service.clock()
        if expiry and not plan['created']<=now<=plan['expires']:
            raise ValueError('План офлайн-подготовки устарел.')
        if plan['config_hash']!=self.service.config_hash() or plan['peer_hashes']!=self.service.peer_hashes() or plan['catalog_hash']!=digest(self.store.catalog()):
            raise ValueError('Конфигурация или каталог изменились; нужен новый план.')
