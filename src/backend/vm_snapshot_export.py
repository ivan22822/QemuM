"""Offline independent ZIP exports. Only private clones are passed to qemu-img."""
from contextlib import ExitStack
import hashlib
import json
import os
from pathlib import Path
import tempfile
import uuid
import zipfile

from vm_snapshot_lease import lease_images
from vm_snapshot_service import identity, encrypted, TTL, MIN_FREE
from vm_snapshot_store import digest, operation_lock
from vm_snapshots import configured_disks
from vm_storage import safe_file


class Export:
    def __init__(self, service):
        self.service = service
        self.store = service.store

    def info(self, path):
        return json.loads(self.service.run(self.service.exe, ['info', '--output=json', str(path)]))

    def plan(self, group_id=None, options=None):
        options = options or {}
        if set(options)-{'path','format','export_scope','include_config','verify'}:
            raise ValueError('Неизвестные параметры экспорта.')
        fmt, scope = options.get('format','qcow2'), options.get('export_scope','snapshot')
        if fmt not in ('qcow2','raw') or scope not in ('snapshot','current'):
            raise ValueError('Выбери формат qcow2/raw и источник копии.')
        if any(type(options.get(key,False)) is not bool for key in ('include_config','verify')):
            raise ValueError('Проверка и сохранение параметров требуют явного выбора.')
        target = Path(options.get('path',''))
        safe_file(target)
        if not target.is_absolute() or target.suffix.lower()!='.zip' or not target.parent.is_dir() or target.exists():
            raise ValueError('Выбери новый ZIP-файл в существующей локальной папке.')
        if os.name=='nt':
            import ctypes
            if ctypes.windll.kernel32.GetDriveTypeW(str(target.anchor))!=3:
                raise ValueError('Для экспорта нужен локальный несъёмный диск.')
            filesystem=ctypes.create_unicode_buffer(64)
            if not ctypes.windll.kernel32.GetVolumeInformationW(str(target.anchor),None,0,None,None,None,filesystem,len(filesystem)):
                raise OSError('Не удалось проверить файловую систему назначения.')
            if filesystem.value.upper() in ('FAT','FAT32'):
                raise ValueError('FAT/FAT32 не поддерживается: экспорт может превысить 4 ГБ.')
        with operation_lock(self.service.root,self.service.name):
            self.store.assert_clear(); self.service.assert_offline()
            catalog=self.store.catalog(); selected=None
            if scope=='snapshot':
                selected=next((row for row in catalog['groups'] if row['id']==group_id),None)
                if not selected:raise ValueError('Выбранная группа не найдена.')
            graph=catalog['settings'].get('external'); rows=[]; roots=[]
            if graph:
                from vm_snapshot_external import ExternalSnapshots
                ExternalSnapshots(self.service).check_graph(catalog)
                cursor=selected['disks'][0]['snapshot_id'] if selected else graph['active']
                if cursor not in graph['nodes']:raise ValueError('Вершина экспорта не найдена.')
                while cursor is not None:
                    node=graph['nodes'][cursor]; path=Path(node['path']); info=self.info(path)
                    row=dict(identity(path),format=info['format'],size=path.stat().st_size,mtime_ns=path.stat().st_mtime_ns,
                             virtual_size=info['virtual-size'],parent=node.get('parent'),node=cursor,sha256=node.get('sha256'))
                    rows.append(row); cursor=node.get('parent')
                roots=[dict(index=0,snapshot_id=None)]
            else:
                for path in configured_disks(self.service.load()):
                    info=self.info(path)
                    if info.get('format') not in ('qcow2','vdi','raw') or encrypted(info) or info.get('backing-filename'):
                        raise ValueError('Неподдерживаемый или незарегистрированный backing-образ.')
                    row=dict(identity(path),format=info['format'],size=path.stat().st_size,mtime_ns=path.stat().st_mtime_ns,virtual_size=info['virtual-size'])
                    member=next((item for item in (selected or {}).get('disks',[]) if item['path']==row['path'] and item['file_id']==row['file_id']),None)
                    if selected:
                        found=[item for item in info.get('snapshots',[]) if member and str(item.get('id'))==member['snapshot_id'] and item.get('name')==selected['name']]
                        if len(found)!=1:raise ValueError('Неполная или изменённая группа; экспорт запрещён.')
                    roots.append(dict(index=len(rows),snapshot_id=member['snapshot_id'] if member else None)); rows.append(row)
                if selected and len(roots)!=len(selected['disks']):raise ValueError('Состав группы изменился.')
            now=self.service.clock()
            # Include clones, flattened outputs, verification reference and ZIP;
            # do not assume sparse allocation or compressibility.
            required=sum(row['size'] for row in rows)+4*sum(rows[row['index']]['virtual_size'] for row in roots)+MIN_FREE
            if self.service.free(target)<required:raise ValueError('Недостаточно места для независимой копии и проверки.')
            parameters=None
            if options.get('include_config'):
                from vm_snapshot_parameters import capture
                parameters=(selected or {}).get('parameters') if selected else capture(self.service.load())
                if parameters is None:raise ValueError('У этой точки не сохранены параметры. Отключи их включение в копию.')
                # The snapshot was accepted earlier; still validate no secrets at export.
                capture(parameters['launch'])
            return dict(id=str(uuid.uuid4()),name=self.service.name,action='export',created=now,expires=now+TTL,
                        disks=rows,roots=roots,graph=bool(graph),target=str(target),format=fmt,verify=options.get('verify',False),
                        parameters=parameters,group_id=(selected or {}).get('id'),required_bytes=required,
                        config_hash=self.service.config_hash(),peer_hashes=self.service.peer_hashes(),catalog_hash=digest(catalog))

    def execute(self,plan,progress=lambda phase,done=None,total=None:None,cancelled=lambda:False):
        with operation_lock(self.service.root,self.service.name,[digest(row['file_id']) for row in plan['disks']]):
            previous=self.store.operation(plan['id'])
            if previous:return self.service.result(previous)
            self.store.assert_clear(); self.service.assert_offline()
            self.check(plan)
            target=Path(plan['target']);safe_file(target)
            if target.exists():raise ValueError('Файл назначения уже существует.')
            operation=dict(schema_version=1,id=plan['id'],name=self.service.name,action='export',plan=plan,
                           created=self.service.clock(),state='validating',steps=[],mutation_started=False)
            self.service.transition(operation,'validating','Экспорт: исходные диски не изменяются')
            try:
                with ExitStack() as stack:
                    leases=stack.enter_context(lease_images([row['path'] for row in plan['disks']]))
                    owned={tuple(lease.file_id):lease for lease in leases}
                    temporary=Path(stack.enter_context(tempfile.TemporaryDirectory(prefix='.qemum-export-',dir=target.parent)))
                    clones=[]
                    for index,row in enumerate(plan['disks']):
                        lease=owned[tuple(row['file_id'])]
                        if lease.size!=row['size'] or Path(row['path']).stat().st_mtime_ns!=row['mtime_ns']:raise ValueError('Источник экспорта изменился.')
                        clone=temporary/('source-'+str(index)+'.'+row['format']);clones.append(clone)
                        progress('Копирование источника '+str(index+1))
                        lease.copy_to(clone,lambda done,total:progress('Копирование источника '+str(index+1),done,total),cancelled)
                        if row.get('sha256') and self.hash(clone,cancelled,progress)!=row['sha256']:raise ValueError('Изменён запечатанный источник экспорта.')
                    if plan['graph']:
                        by_node={row['node']:index for index,row in enumerate(plan['disks'])}
                        for index in reversed(range(len(clones))):
                            row=plan['disks'][index]
                            if row.get('parent'):
                                parent=by_node[row['parent']]
                                self.service.run(self.service.exe,['rebase','-u','-f',row['format'],'-F',plan['disks'][parent]['format'],'-b',str(clones[parent]),str(clones[index])])
                    manifest=dict(schema=1,kind='independent-disk-backup',group_id=plan['group_id'],ram_included=False,disks=[],parameters=plan['parameters'])
                    outputs=[]
                    for index,root in enumerate(plan['roots']):
                        if cancelled():raise InterruptedError('Экспорт отменён; исходные диски не изменены.')
                        source=clones[root['index']];row=plan['disks'][root['index']]
                        selection=['-l','snapshot.id='+root['snapshot_id']] if root['snapshot_id'] else []
                        output=temporary/('disk-'+str(index)+'.'+plan['format']); outputs.append(output)
                        phase='Разворачивание независимого диска '+str(index+1);progress(phase)
                        self.service.transition(operation,'running',phase)
                        self.service.run(self.service.exe,['convert','-f',row['format'],*selection,'-O',plan['format'],str(source),str(output)])
                        info=self.info(output)
                        if info.get('format')!=plan['format'] or info.get('backing-filename') or info.get('snapshots') or info.get('virtual-size')!=row['virtual_size']:
                            raise ValueError('Независимость или размер результата не подтверждены.')
                        if plan['format']=='qcow2':self.service.run(self.service.exe,['check','--output=json',str(output)])
                        checksum=None
                        if plan['verify']:
                            if cancelled():raise InterruptedError('Проверка экспорта отменена.')
                            reference=temporary/'reference.qcow2';progress('Сверка содержимого диска '+str(index+1))
                            self.service.run(self.service.exe,['convert','-f',row['format'],*selection,'-O','qcow2',str(source),str(reference)])
                            self.service.run(self.service.exe,['compare','-f',plan['format'],'-F','qcow2',str(output),str(reference)]);reference.unlink()
                            checksum=self.hash(output,cancelled,progress)
                        manifest['disks'].append(dict(file=output.name,format=plan['format'],virtual_size=row['virtual_size'],bytes=output.stat().st_size,sha256=checksum))
                    archive=temporary/'backup.zip';progress('Упаковка независимой копии')
                    with zipfile.ZipFile(archive,'w',compression=zipfile.ZIP_STORED,allowZip64=True) as bundle:
                        for output in outputs:
                            with output.open('rb') as source,bundle.open(output.name,'w',force_zip64=True) as dest:
                                while True:
                                    if cancelled():raise InterruptedError('Упаковка отменена.')
                                    block=source.read(4*1024*1024)
                                    if not block:break
                                    dest.write(block)
                        bundle.writestr('manifest.json',json.dumps(manifest,ensure_ascii=False,indent=2))
                        bundle.writestr('README.txt','Extract into a NEW folder. Register a NEW VM with the exported disks. Never replace the source VM. RAM and runtime state are excluded. Review optional parameters before sharing.\n')
                    with archive.open('rb+') as stream:os.fsync(stream.fileno())
                    self.check(plan,expiry=False)
                    if cancelled():raise InterruptedError('Экспорт отменён до публикации.')
                    operation['output_id']=identity(archive)['file_id'];operation['output_bytes']=archive.stat().st_size
                    operation['output_mtime_ns']=archive.stat().st_mtime_ns;operation['cancel_closed']=True
                    self.service.transition(operation,'verifying','Публикация готового ZIP; исходные диски не изменяются')
                    # Windows rename never overwrites an existing destination.
                    archive.rename(target)
                    operation['result']=dict(operation_id=plan['id'],path=str(target),files=[row['file'] for row in manifest['disks']],source_modified=False)
                    self.service.transition(operation,'completed','Независимая копия проверена и опубликована')
                    return operation['result']
            except BaseException as error:
                # Lost completion is reconciled by exact output identity, never repeated.
                published=target.exists() and operation.get('output_id')==identity(target)['file_id']
                self.service.transition(operation,'uncertain' if published else 'cancelled' if isinstance(error,InterruptedError) else 'failed',str(error))
                raise

    def check(self,plan,expiry=True):
        if plan.get('action')!='export' or plan.get('name')!=self.service.name:raise ValueError('План относится к другой операции.')
        if expiry and not plan['created']<=self.service.clock()<=plan['expires']:raise ValueError('План экспорта устарел.')
        if plan['config_hash']!=self.service.config_hash() or plan['peer_hashes']!=self.service.peer_hashes() or plan['catalog_hash']!=digest(self.store.catalog()):raise ValueError('Конфигурация или каталог изменились.')
        if self.service.free(plan['target'])<MIN_FREE:raise ValueError('Закончился резерв свободного места.')

    @staticmethod
    def hash(path,cancelled,progress):
        value=hashlib.sha256();done=0;total=path.stat().st_size
        with path.open('rb') as stream:
            while True:
                if cancelled():raise InterruptedError('Проверка контрольной суммы отменена.')
                block=stream.read(4*1024*1024)
                if not block:break
                value.update(block);done+=len(block);progress('SHA-256 независимой копии',done,total)
        return value.hexdigest()

    def recover(self,operation):
        plan=operation['plan'];target=Path(plan['target'])
        if target.exists():
            if identity(target)['file_id']!=operation.get('output_id') or target.stat().st_size!=operation.get('output_bytes') or target.stat().st_mtime_ns!=operation.get('output_mtime_ns'):raise ValueError('Файл экспорта заменён или изменён; автоматическая сверка запрещена.')
            with zipfile.ZipFile(target) as archive:
                manifest=json.loads(archive.read('manifest.json'))
                if manifest.get('kind')!='independent-disk-backup':raise ValueError('Не подтверждён manifest экспорта.')
            operation['result']=dict(operation_id=operation['id'],path=str(target),source_modified=False)
            self.service.transition(operation,'completed','Готовый экспорт подтверждён после перезапуска')
        else:self.service.transition(operation,'cancelled','Экспорт не опубликован. Исходные диски не изменялись; временная папка может требовать ручной очистки.')
        return self.service.result(operation)
