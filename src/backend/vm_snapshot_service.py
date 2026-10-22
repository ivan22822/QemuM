"""Shared planned/journalled disk-only live snapshots. Unsupported modes fail closed."""
from contextlib import contextmanager, nullcontext
from copy import deepcopy
import json
import os
from pathlib import Path
import re
import shutil
import time
import uuid
import ctypes

from vm_snapshot_store import Store, FINAL, digest, identifier, operation_lock
from vm_snapshots import configured_disks, assert_private, image_tool, image_command, runtime_image_files
from vm_storage import file_key, media, safe_file, qemu_processes, backing_chain, assert_stopped
from vm_runtime import watch_qmp_process

TTL = 120
MIN_FREE = 64 * 1024 * 1024
REQUIRED = {'query-block', 'query-status', 'query-qmp-schema', 'transaction', 'blockdev-snapshot-internal-sync'}


def identity(path):
    path = Path(path)
    safe_file(path)
    if not path.is_absolute() or str(path).startswith(('\\\\', '//')):
        raise ValueError('Снимки поддерживают только локальные абсолютные пути, без UNC.')
    if os.name == 'nt' and ctypes.windll.kernel32.GetDriveTypeW(str(path.anchor)) != 3:
        raise ValueError('Для снимков нужен локальный несъёмный диск; сетевые и сменные носители не поддерживаются.')
    stat = path.stat()
    if not path.is_file() or not stat.st_ino:
        raise ValueError('Не удалось определить личность файла диска: ' + str(path))
    return dict(path=file_key(path), file_id=[stat.st_dev, stat.st_ino])


def encrypted(info):
    return bool(info.get('encrypted') or ((info.get('format-specific') or {}).get('data') or {}).get('encrypt'))


def inventory(request):
    """Use actual writable root devices; never infer format from a suffix."""
    disks, excluded = [], []
    for block in request('query-block'):
        inserted = block.get('inserted')
        device = block.get('device') or (inserted or {}).get('node-name')
        if not inserted:
            excluded.append(dict(device=device, reason='нет носителя'))
            continue
        image = inserted.get('image', {})
        if inserted.get('ro'):
            excluded.append(dict(device=device, path=image.get('filename', ''), reason='только чтение / ISO'))
            continue
        if block.get('removable'):
            raise ValueError('Записываемый сменный носитель не поддерживается: ' + str(device))
        if not device or inserted.get('drv') != 'qcow2' or image.get('format') != 'qcow2':
            raise ValueError('Внутренние снимки требуют qcow2 на всех записываемых дисках. Формат: ' + str(inserted.get('drv')) + '. Для VDI/raw используй независимую копию; конвертация автоматически не выполняется.')
        if encrypted(image):
            raise ValueError('Зашифрованные образы пока не поддерживаются снимками QemuM.')
        if image.get('backing-image') or image.get('backing-filename'):
            raise ValueError('Снимки цепочек backing пока отключены; исходная база не изменяется.')
        row = identity(image.get('filename') or inserted.get('file', ''))
        row.update(device=device, role='disk-' + str(len(disks)), format='qcow2', snapshots=image.get('snapshots', []))
        disks.append(row)
    if not disks:
        raise ValueError('Нет записываемых дисков qcow2; у VM только с ISO нечего снимать.')
    keys = [tuple(item['file_id']) for item in disks]
    if len(keys) != len(set(keys)):
        raise ValueError('Один физический файл подключён несколько раз. Снимок запрещён.')
    return disks, excluded


def resources(disks):
    return [{key: disk[key] for key in ('path', 'file_id', 'device', 'role', 'format')} for disk in disks]


def verify_capabilities(request):
    commands = {row['name'] for row in request('query-commands')}
    if not REQUIRED <= commands:
        raise ValueError('Эта сборка QEMU не предоставляет команды внутренних групповых снимков: ' + ', '.join(sorted(REQUIRED - commands)))
    schema = request('query-qmp-schema')
    types = {row['name']: row for row in schema}
    arguments = types.get(types.get('transaction', {}).get('arg-type'), {})
    array_name = next((member.get('type') for member in arguments.get('members', []) if member.get('name') == 'actions'), None)
    action_type = types.get(types.get(array_name, {}).get('element-type'), {})
    if not any(variant.get('case') == 'blockdev-snapshot-internal-sync' for variant in action_type.get('variants', [])):
        raise ValueError('QMP не подтверждает поддержку внутреннего снимка в транзакции.')


class SnapshotService:
    def __init__(self, root, name, request, load, exe, *, processes=qemu_processes, peer_refs=None,
                 owner=None, run=image_command, clock=time.time, free=None):
        self.root, self.name = Path(root), name
        self.request, self.load, self.exe = request, load, exe
        self.processes, self.peer_refs, self.owner = processes, peer_refs, owner
        self.run, self.clock = run, clock
        self.free = free or (lambda path: shutil.disk_usage(Path(path).parent).free)
        self.store = Store(root, name)

    def config_hash(self):
        path = self.root / (self.name + '.json')
        safe_file(path)
        return digest(path.read_bytes().hex())

    def assert_offline(self):
        data = self.load()
        assert_stopped(self.name, media(data), lambda:self.request('query-status'), self.processes)
        disks = [dict(identity(path), format='qcow2') for path in configured_disks(data)]
        self.assert_owned(disks, None)

    def peer_hashes(self):
        return {path.name: digest(path.read_bytes().hex()) for path in sorted(set(self.root.glob('*.json')) | set(self.root.glob('*.json.disabled')))}

    def validate_runtime(self):
        state = self.request('query-status')['status']
        if state not in ('running', 'paused', 'prelaunch'):
            raise ValueError('Создание доступно для работающей или приостановленной VM, состояние: ' + state)
        data = self.load()
        if '-blockdev' in data.get('extra_args', []):
            raise ValueError('Произвольный граф -blockdev пока не поддерживается снимками.')
        disks, excluded = inventory(self.request)
        expected = [identity(path) for path in configured_disks(data)]
        if sorted((item['path'], item['file_id']) for item in expected) != sorted((item['path'], item['file_id']) for item in disks):
            raise ValueError('Фактический набор записываемых дисков не совпадает с конфигурацией VM.')
        verify_capabilities(self.request)
        return data, state, disks, excluded

    def assert_owned(self, disks, own_pid):
        # Inspect all process identities, including QEMU not registered in QemuM.
        records = self.processes()
        if not isinstance(records, list):
            raise ValueError('Не удалось проверить процессы QEMU.')
        if own_pid is not None and not any(isinstance(row, dict) and row.get('ProcessId') == own_pid for row in records):
            raise ValueError('Процесс выбранной VM отсутствует в полной инвентаризации QEMU.')
        for record in records:
            if not isinstance(record, dict) or not isinstance(record.get('CommandLine'), str):
                raise ValueError('Не видны параметры QEMU; снимок отменён.')
            if record.get('ProcessId') == own_pid:
                continue
            raw = record['CommandLine']
            match = re.search(r'(?:^|\s)-name\s+(?:"([^"]*)"|(\S+))', raw, re.I)
            other = (match.group(1) or match.group(2)).split(',')[0] if match else ''
            if other.startswith('guest='):
                other = other[6:]
            config = self.root / (other + '.json')
            if not other or other == self.name or not config.is_file() or not self.peer_refs:
                raise ValueError('Не удалось проверить владельца другого QEMU. Закрой сторонние QEMU перед снимком.')
            refs = self.peer_refs(other, json.loads(config.read_text(encoding='utf-8-sig')))
            if refs is None:
                raise ValueError('Неизвестны фактические диски работающего QEMU.')
            ids = {tuple(identity(path)['file_id']) for path in refs}
            if ids & {tuple(disk['file_id']) for disk in disks}:
                raise ValueError('Диск открыт другим QEMU; снимок запрещён.')
        # Offline peer configs, disabled configs, and backing references matter too.
        chosen_paths = {item['path'] for item in disks}
        chosen = {tuple(disk['file_id']) for disk in disks}
        for config in sorted(set(self.root.glob('*.json')) | set(self.root.glob('*.json.disabled'))):
            if config.name == self.name + '.json':
                continue
            safe_file(config)
            for path, kind in media(json.loads(config.read_text(encoding='utf-8-sig'))):
                if path.exists() and tuple(identity(path)['file_id']) in chosen:
                    raise ValueError('Физический диск используется другой конфигурацией VM, в том числе через hard link.')
        def inspect(path, exe):
            # Active own images were inspected through QMP, never open them with qemu-img.
            return [path] if file_key(path) in chosen_paths else backing_chain(path, exe)
        assert_private(self.root, self.name, [Path(item['path']) for item in disks], self.exe,
                       inspect=inspect, runtime_refs=self.peer_refs)

    @contextmanager
    def watched(self):
        if self.owner is None:
            raise ValueError('Не подключена проверка личности процесса QMP.')
        watcher = self.owner()
        try:
            if watcher.poll() is not None:
                raise ValueError('QEMU уже завершён.')
            yield watcher
        finally:
            watcher.close()

    def plan_create(self, options=None, operation_id=None):
        options = options or {}
        if not isinstance(options, dict) or set(options) - {'label', 'description', 'kind', 'include_config', 'consistency'}:
            raise ValueError('Неизвестные параметры создания снимка.')
        if 'include_config' in options and type(options['include_config']) is not bool:raise ValueError('Сохранение параметров должно быть явным выбором.')
        if options.get('kind', 'disk') != 'disk' or options.get('consistency', 'crash') not in ('crash','filesystem'):
            raise ValueError('Сейчас поддерживаются только диски без RAM, параметров VM и подготовки Guest Agent.')
        label, description = options.get('label', ''), options.get('description', '')
        if not isinstance(label, str) or not isinstance(description, str) or len(label) > 120 or len(description) > 4000 or any(ord(c) < 32 for c in label):
            raise ValueError('Название — до 120 символов одной строкой; описание — до 4000.')
        with operation_lock(self.root, self.name):
            self.store.assert_clear()
            with self.watched() as owner:
                data, state, disks, excluded = self.validate_runtime()
                self.assert_owned(disks, owner.pid)
                for disk in disks:
                    if self.free(disk['path']) < MIN_FREE:
                        raise ValueError('Недостаточно свободного места: для старта снимка нужен резерв не менее 64 МиБ. Рост снимков требует дополнительного места.')
                now = self.clock()
                op = identifier(operation_id) if operation_id else str(uuid.uuid4())
                plan=dict(schema_version=1, id=op, name=self.name, action='create', created=now, expires=now + TTL,
                            config_hash=self.config_hash(), peer_hashes=self.peer_hashes(), catalog_hash=digest(self.store.catalog()),
                            group_id=str(uuid.uuid4()), tag='qemum-' + str(uuid.uuid4()),
                            disks=resources(disks), excluded=excluded, vm_state=state, owner_pid=owner.pid, owner_created=getattr(owner, 'creation_time', None),
                            label=label, description=description,consistency=options.get('consistency','crash'))
                if options.get('include_config'):
                    from vm_snapshot_parameters import capture
                    plan['parameters']=capture(data)
                return plan

    def transition(self, operation, state, detail, **extra):
        operation.update(state=state, phase=detail, updated=self.clock(), **extra)
        operation['steps'].append(dict(state=state, detail=detail, date=self.clock()))
        if len(operation['steps'])>512:
            operation['steps']=operation['steps'][:64]+operation['steps'][-448:]
            operation['steps_truncated']=True
        self.store.save_operation(operation)

    def check_plan(self, plan, disks, owner):
        if self.clock() > plan['expires'] or self.clock() < plan['created']:
            raise ValueError('План снимка устарел. Обнови его.')
        if plan['config_hash'] != self.config_hash() or plan['peer_hashes'] != self.peer_hashes() or plan['catalog_hash'] != digest(self.store.catalog()):
            raise ValueError('Конфигурация или каталог изменились. Создай новый план.')
        if plan['owner_pid'] != owner.pid or plan.get('owner_created') != getattr(owner, 'creation_time', None) or owner.poll() is not None or plan['disks'] != resources(disks):
            raise ValueError('Процесс QEMU или состав дисков изменился. Создай новый план.')

    def create(self, plan, progress=lambda text: None):
        # Plans are server-owned. API wrappers never accept a client-supplied plan.
        if plan.get('name') != self.name or plan.get('action') != 'create':
            raise ValueError('План принадлежит другой VM или операции.')
        keys = [digest(item['file_id']) for item in plan['disks']]
        with operation_lock(self.root, self.name, keys):
            previous = self.store.operation(plan['id'])
            if previous:
                return self.result(previous)  # no repeated mutation for this UUID
            self.store.assert_clear()
            with self.watched() as owner:
                data, state, disks, excluded = self.validate_runtime()
                self.check_plan(plan, disks, owner)
                self.assert_owned(disks, owner.pid)
                operation = dict(schema_version=1, id=plan['id'], name=self.name, action='create',
                                 state='planned', tag=plan['tag'], group_id=plan['group_id'], plan=deepcopy(plan),
                                 steps=[], created=self.clock(), mutation_started=False)
                self.transition(operation, 'planned', 'План создания принят')
                self.transition(operation, 'validating', 'Проверка состава дисков и свободного места')
                try:
                    for disk in disks:
                        if self.free(disk['path']) < MIN_FREE:
                            raise ValueError('Свободного места недостаточно. Диски не изменены.')
                    self.check_plan(plan, inventory(self.request)[0], owner)
                    self.assert_owned(disks, owner.pid)
                    self.check_plan(plan, inventory(self.request)[0], owner)
                    transport_error = None
                    from vm_snapshot_agent import frozen
                    with (frozen(self,operation,owner) if plan.get('consistency')=='filesystem' else nullcontext(None)) as freeze_alive:
                        operation['mutation_started'] = True
                        self.transition(operation, 'running', 'Отправка групповой транзакции QMP')
                        progress('Создаю снимок дисков · ID ' + plan['id'])
                        try:
                            self.request('transaction', {'actions': [dict(type='blockdev-snapshot-internal-sync', data=dict(device=disk['device'], name=plan['tag'])) for disk in disks]})
                        except (OSError, ValueError) as error:
                            transport_error = str(error)
                        plan['achieved_consistency']='filesystem' if freeze_alive and freeze_alive() else 'crash'
                    self.transition(operation, 'verifying', 'Сверка фактически созданных снимков', transport_error=transport_error)
                    checked, _ = inventory(self.request)
                    if resources(checked) != plan['disks'] or owner.poll() is not None:
                        raise ValueError('Состав дисков или процесс изменился во время создания.')
                    if self.config_hash() != plan['config_hash'] or self.peer_hashes() != plan['peer_hashes']:
                        raise ValueError('Конфигурация изменилась во время операции.')
                    for disk in checked:
                        matches = [item for item in disk['snapshots'] if item.get('name') == plan['tag']]
                        operation['steps'].append(dict(state='verifying', disk=disk['path'],
                            confirmed=len(matches) == 1 and matches[0].get('vm-state-size', 0) == 0,
                            snapshot_ids=[str(item.get('id', '')) for item in matches], date=self.clock()))
                        self.store.save_operation(operation)
                    final_state = self.request('query-status')['status']
                    if final_state != state:
                        raise ValueError('Состояние VM изменилось во время снимка; требуется сверка. QemuM не отправлял stop/cont.')
                    group = self.group(plan, checked)
                    if group is None:
                        raise ValueError('Не на каждом диске подтверждён ровно один disk-only снимок. Повтор автоматически не выполняется.')
                    self.store.publish(group, plan['catalog_hash'])
                    self.transition(operation, 'completed', 'Создание и проверка завершены', result=dict(result=plan['tag'], group_id=group['id'], operation_id=plan['id']))
                except BaseException as error:
                    # Any possible write remains durable and blocks normal boot.
                    self.transition(operation, 'uncertain' if operation['mutation_started'] else 'failed', str(error) or type(error).__name__)
                    if isinstance(error, (KeyboardInterrupt, SystemExit)):
                        raise
                    raise ValueError('Операция снимков ' + plan['id'] + ': ' + str(error) + (' Запуск заблокирован до сверки.' if operation['mutation_started'] else ' Диски не изменялись.')) from error
                return self.result(operation)

    def group(self, plan, disks):
        members = []
        for disk in disks:
            matches = [item for item in disk['snapshots'] if item.get('name') == plan['tag']]
            if len(matches) != 1 or matches[0].get('vm-state-size', 0) != 0 or not matches[0].get('id'):
                return None
            member = {key: disk[key] for key in ('path', 'file_id', 'device', 'role', 'format')}
            member['snapshot_id'] = str(matches[0]['id'])
            members.append(member)
        return dict(id=plan['group_id'], name=plan['tag'], label=plan['label'] or 'Снимок ' + time.strftime('%Y-%m-%d %H:%M', time.localtime(plan['created'])),
                    description=plan['description'], date=int(plan['created']), kind='disk', state='ready',
                    source=plan.get('source','user'), protected=False, consistency_description='Guest Agent: файловые системы были заморожены; это не гарантия согласованности любой базы данных.' if plan.get('achieved_consistency')=='filesystem' else 'Снимок дисков без подготовки Guest Agent; RAM не сохранялась.',
                    disks=members, excluded=plan['excluded'], operation_id=plan['id'],parameters=plan.get('parameters'),consistency=plan.get('achieved_consistency','crash'))

    @staticmethod
    def result(operation):
        if operation['state'] == 'completed':
            return operation['result']
        raise ValueError('Операция ' + operation['id'] + ' уже существует: ' + operation['state'] + '. ' + operation.get('phase', '') + ' Повтор записи запрещён.')

    def read_catalog(self, online, data, state='stopped'):
        """Read-only reconciliation; never adopt unlabelled legacy groups silently."""
        data=dict(data,launch_profile_settings=data.get('launch_profile_settings') or {})
        with operation_lock(self.root, self.name):
            stored = self.store.catalog()
            if stored['settings'].get('external'):
                from vm_snapshot_external import ExternalSnapshots
                return ExternalSnapshots(self).catalog(online)
            operations = self.store.operations()
            pending = [item for item in operations if item['state'] not in FINAL]
            pending_ids={item['id'] for item in pending}
            for agent in self.store.agent_pending():
                if agent['id'] not in pending_ids:pending.append(dict(id=agent['id'],state='thaw-required',phase='Guest Agent: thaw не подтверждён; дождись защитника либо выключи исходную VM для сверки',steps=[]))
            try:
                if online:
                    disks, excluded = inventory(self.request)
                else:
                    disks, excluded = [], []
                    for index, path in enumerate(configured_disks(data)):
                        info = json.loads(self.run(self.exe, ['info', '--output=json', str(path)]))
                        if info.get('format') != 'qcow2' or encrypted(info):
                            raise ValueError('Внутренние снимки доступны только для незашифрованных qcow2; формат: ' + str(info.get('format')))
                        disk = identity(path)
                        disk.update(device='', role='disk-' + str(index), format=info['format'], snapshots=info.get('snapshots', []))
                        disks.append(disk)
                rows = self.reconcile(stored['groups'], disks)
                error = None
            except (OSError, ValueError) as failure:
                if not stored['groups'] and not pending:
                    if online:raise
                    return dict(rows=[],online=False,vm_state='stopped',context=data,revision=stored['revision'],error=str(failure),
                                operations=[],history=[],default_snapshot_kind='external',capabilities=['refresh','external','create_offline','metadata_on_create','export','cancel'])
                rows = [dict(group, state='missing', reason=str(failure)) for group in stored['groups']]
                error = str(failure)
            return dict(rows=rows, online=online, vm_state=state, context=data, revision=stored['revision'],schedule=stored['settings']['schedule'],
                        operations=[dict(id=item['id'], phase=item['state'], detail=item.get('phase', '') + '\n' + json.dumps(item.get('steps', []), ensure_ascii=False)) for item in pending],
                        history=[dict(id=item['id'], date=time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(item.get('updated', item['created']))), action=item['action'], state=item['state'], detail=item.get('phase', '')) for item in operations],
                        recovery_required=bool(pending), error=error, capabilities=['create_live', 'metadata_on_create', 'metadata', 'protect', 'refresh', 'recover', 'adopt','config','schedule','cancel']+(['restore_ram']+(['ram'] if online and state in ('paused','prelaunch') else []) if data.get('launch_profile_settings',{}).get('accelerator',data.get('launch_profile'))=='tcg' else [])+(['guest_freeze'] if online and data.get('guest_agent_enabled') else [])+([] if online else ['create_offline','delete','restore','restore_config','export']+(['external'] if not stored['groups'] else [])))

    def metadata(self, group_id, revision, action, options):
        if action not in ('edit','protect') or not isinstance(options,dict):
            raise ValueError('Неизвестное изменение метаданных снимка.')
        with operation_lock(self.root,self.name):
            self.store.assert_clear()
            current=self.store.catalog()
            if type(revision) is not int or revision!=current['revision']:
                raise ValueError('Каталог изменился. Обнови список снимков.')
            updated=deepcopy(current)
            group=next((row for row in updated['groups'] if row['id']==group_id),None)
            if group is None:raise ValueError('Снимок не найден в подтверждённом каталоге.')
            if action=='edit':
                if set(options)-{'label','description'}:raise ValueError('Неизвестные поля метаданных.')
                label,description=options.get('label',''),options.get('description','')
                if not isinstance(label,str) or not isinstance(description,str) or len(label)>120 or len(description)>4000 or any(ord(char)<32 for char in label):
                    raise ValueError('Название — одна строка до 120 символов; описание — до 4000.')
                group.update(label=label or group['name'],description=description)
            else:
                if set(options)!={'protected'} or type(options['protected']) is not bool:raise ValueError('Укажи состояние защиты снимка.')
                group['protected']=options['protected']
            updated['revision']+=1
            operation=dict(schema_version=1,id=str(uuid.uuid4()),name=self.name,action='metadata',created=self.clock(),
                           state='validating',group_id=group_id,steps=[],mutation_started=False,before_hash=digest(current),after_hash=digest(updated),
                           result=dict(group_id=group_id,revision=updated['revision']))
            self.transition(operation,'validating','Изменение метаданных; диски не изменяются')
            self.store.replace_catalog(updated,digest(current))
            self.transition(operation,'completed','Метаданные сохранены; внутренний тег и данные дисков сохранены')
            return dict(operation['result'],operation_id=operation['id'])

    @staticmethod
    def reconcile(groups, disks):
        rows = []
        known = {group['name'] for group in groups}
        for group in groups:
            row = deepcopy(group)
            actual = {(item['path'], tuple(item['file_id'])): item for item in disks}
            if [(item['path'], item['file_id']) for item in group['disks']] != [(item['path'], item['file_id']) for item in disks]:
                row.update(state='incompatible', reason='Набор или личность дисков изменились; автоматическая перепривязка запрещена.')
            else:
                confirmed = 0
                for member in group['disks']:
                    matches = [item for item in actual[(member['path'], tuple(member['file_id']))]['snapshots'] if item.get('name') == group['name'] and str(item.get('id')) == member['snapshot_id'] and (group.get('kind')=='ram' or item.get('vm-state-size', 0) == 0)]
                    confirmed += len(matches) == 1
                row['state'] = 'ready' if confirmed == len(group['disks']) else 'partial' if confirmed else 'missing'
                if group.get('kind')=='ram' and row['state']=='ready':
                    sizes=[item.get('vm-state-size',0) for disk in disks for item in disk['snapshots'] if item.get('name')==group['name']]
                    if sum(size>0 for size in sizes)!=1:row.update(state='partial',reason='RAM не подтверждена ровно на одном образе группы.')
                row['ids'] = [next(member['snapshot_id'] for member in group['disks'] if member['path'] == item['path'] and member['file_id'] == item['file_id']) for item in disks]
            rows.append(row)
        # Common names are discovery evidence, not proof of a multi-disk group.
        for tag in sorted({item.get('name') for disk in disks for item in disk['snapshots'] if item.get('name')} - known):
            copies = [item for disk in disks for item in disk['snapshots'] if item.get('name') == tag]
            rows.append(dict(id='legacy-' + digest(tag), name=tag, date=copies[0].get('date-sec', 0), kind='ram' if any(item.get('vm-state-size', 0) for item in copies) else 'disk',
                             state='incompatible', reason='Обнаружен внешний или старый снимок. Принадлежность группы не подтверждена; автоматическое принятие запрещено.', disks=[]))
        return sorted(rows, key=lambda row: (row['date'], row['id']), reverse=True)

    def recover_create(self, operation_id, stopped):
        """Only reconcile stopped, unchanged images. Never replay a write."""
        with operation_lock(self.root, self.name):
            operation = self.store.operation(operation_id)
            if not operation:
                raise ValueError('Операция не найдена.')
            agents=[row for row in self.store.agent_pending() if row['id']==operation_id]
            if agents:
                stopped()
                from vm_runtime import ProcessWatcher
                for agent in agents:
                    try:watcher=ProcessWatcher(agent['guardian_pid'])
                    except OSError:watcher=None
                    if watcher is not None:
                        try:
                            if watcher.creation_time==agent['guardian_created'] and watcher.poll() is None:raise ValueError('Независимый защитник ещё работает; дождись его завершения.')
                        finally:watcher.close()
                    agent.update(state='thawed',detail='Исходная VM подтверждённо выключена; активной заморозки в памяти больше нет.',updated=self.clock())
                    self.store.atomic(self.store.folder/'agent'/(operation_id+'.json'),agent)
                if operation['state'] in FINAL:return dict(operation_id=operation_id,state=operation['state'],reconciled=True)
            if operation['state'] in FINAL:
                return self.result(operation)
            if operation['action']=='export':
                from vm_snapshot_export import Export
                return Export(self).recover(operation)
            if operation['action'] in ('ram-create','ram-restore'):
                from vm_snapshot_ram import RamSnapshots
                return RamSnapshots(self).recover(operation)
            if operation['action'].startswith('offline-'):
                from vm_snapshot_transaction import OfflineTransaction
                return OfflineTransaction(self).recover(operation)
            stopped()
            if operation['action']=='external':
                from vm_snapshot_external import ExternalSnapshots
                return ExternalSnapshots(self).recover(operation)
            if operation['action']=='metadata' or (operation['action']=='schedule' and 'before_hash' in operation):
                actual=digest(self.store.catalog())
                if actual==operation['after_hash']:
                    self.transition(operation,'completed','Запись метаданных подтверждена после перезапуска')
                    return self.result(operation)
                if actual==operation['before_hash']:
                    self.transition(operation,'failed','Метаданные не изменились; повторная запись не выполнялась')
                    return dict(operation_id=operation['id'],state='failed',reconciled=True)
                raise ValueError('Каталог отличается от обеих подтверждённых версий. Журнал сохранён.')
            if operation['action'] == 'prepare-offline':
                plan=operation['plan']
                for disk,signature in zip(plan['disks'],plan['signatures']):
                    current=identity(disk['path']);stat=Path(disk['path']).stat()
                    if current['file_id']!=disk['file_id'] or stat.st_size!=signature['size'] or stat.st_mtime_ns!=signature['mtime_ns']:
                        raise ValueError('Исходный образ изменился после подготовки; нужна ручная проверка.')
                self.assert_owned(plan['disks'],None)
                self.transition(operation,'cancelled','Подтверждено: оригиналы не публиковались и не изменялись. Приватные копии сохранены для отдельной очистки.')
                return dict(operation_id=operation['id'],state='cancelled',reconciled=True)
            if operation['action'] == 'adopt':
                group = next((row for row in self.store.catalog()['groups'] if row['id'] == operation['group_id']), None)
                if group is None:
                    self.transition(operation, 'failed', 'Принятие не было опубликовано; диски не изменялись')
                    return dict(operation_id=operation['id'], state='failed', reconciled=True)
                actual = []
                for member in group['disks']:
                    current = identity(member['path'])
                    if current['file_id'] != member['file_id']:
                        raise ValueError('Диск заменён после принятия. Нужна ручная проверка.')
                    info = json.loads(self.run(self.exe, ['info', '--output=json', member['path']]))
                    actual.append(dict(current, snapshots=info.get('snapshots', [])))
                if next(row for row in self.reconcile([group], actual) if row['id'] == group['id'])['state'] != 'ready':
                    raise ValueError('Принятый снимок не подтверждён. Журнал сохранён.')
                self.transition(operation, 'completed', 'Принятие подтверждено при повторной проверке', result=dict(group_id=group['id'], operation_id=operation['id']))
                return self.result(operation)
            if operation['action'] != 'create':
                raise ValueError('Нужна ручная проверка восстановления. ID: ' + operation['id'] + '. Страховочная точка: ' + operation.get('checkpoint', 'не подтверждена') + '. Журнал сохранён; автоматический повтор запрещён.')
            plan = operation['plan']
            disks = []
            for member in plan['disks']:
                if identity(member['path']) != {key: member[key] for key in ('path', 'file_id')}:
                    raise ValueError('Файл перемещён или заменён. Ручная проверка обязательна.')
                info = json.loads(self.run(self.exe, ['info', '--output=json', member['path']]))
                if info.get('format') != 'qcow2' or encrypted(info):
                    raise ValueError('Формат диска изменился.')
                disks.append(dict(member, snapshots=info.get('snapshots', [])))
            self.assert_owned(disks, None)
            group = self.group(plan, disks)
            if group:
                self.store.publish(group, plan['catalog_hash'])
                self.transition(operation, 'completed', 'Снимок подтверждён при офлайн-сверке', result=dict(result=plan['tag'], operation_id=plan['id'], group_id=group['id']))
                return self.result(operation)
            count = sum(sum(item.get('name') == plan['tag'] for item in disk['snapshots']) for disk in disks)
            if count == 0:
                self.transition(operation, 'failed', 'Офлайн-сверка: ни одного результата операции не найдено; повторная запись не выполнялась')
                return dict(operation_id=operation['id'], state='failed', reconciled=True)
            self.transition(operation, 'partial', 'Неполная или несовместимая группа; требуется ручная проверка. Части сохранены.')
            raise ValueError('Частичный результат. Журнал и созданные снимки сохранены; запуск заблокирован. ID: ' + operation['id'])

    def adopt(self, tag, confirmation, stopped):
        """Explicit legacy adoption, only one unambiguous disk; no disk writes."""
        if not isinstance(tag, str) or not tag or confirmation != tag:
            raise ValueError('Для принятия нужен точный внутренний тег старого снимка.')
        with operation_lock(self.root, self.name):
            self.store.assert_clear()
            stopped()
            disks = configured_disks(self.load())
            if len(disks) != 1:
                raise ValueError('Старую многодисковую группу нельзя подтвердить одним совпадающим именем. Принятие разрешено только для одного диска.')
            before, peers = self.config_hash(), self.peer_hashes()
            info = json.loads(self.run(self.exe, ['info', '--output=json', str(disks[0])]))
            if info.get('format') != 'qcow2' or encrypted(info) or info.get('backing-filename'):
                raise ValueError('Принятие требует отдельного незашифрованного qcow2 без backing chain.')
            matches = [item for item in info.get('snapshots', []) if item.get('name') == tag]
            if len(matches) != 1 or matches[0].get('vm-state-size', 0) or not matches[0].get('id'):
                raise ValueError('Нет единственного подтверждённого disk-only снимка с этим тегом.')
            member = dict(identity(disks[0]), device='', role='disk-0', format='qcow2', snapshot_id=str(matches[0]['id']))
            self.assert_owned([dict(member, snapshots=[])], None)
            current = self.store.catalog()
            if any(group['name'] == tag for group in current['groups']):
                raise ValueError('Этот тег уже есть в каталоге.')
            stopped()
            if before != self.config_hash() or peers != self.peer_hashes() or identity(disks[0])['file_id'] != member['file_id']:
                raise ValueError('Ресурсы изменились при проверке. Принятие отменено.')
            group = dict(id=str(uuid.uuid4()), name=tag, label=tag, description='', date=matches[0].get('date-sec', 0),
                         kind='disk', state='ready', disks=[member], source='legacy-adopted', protected=tag.startswith('before-restore-'))
            operation = dict(schema_version=1, id=str(uuid.uuid4()), name=self.name, action='adopt', created=self.clock(),
                             state='validating', group_id=group['id'], steps=[], phase='Принятие старого снимка; данные диска не изменяются')
            self.store.save_operation(operation)
            self.store.publish(group, digest(current))
            operation.update(state='completed', result=dict(group_id=group['id'], operation_id=operation['id']), phase='Старый снимок принят явно пользователем')
            self.store.save_operation(operation)
            return operation['result']


def manager_service(root, name):
    import vm
    data = lambda: vm.load(name)
    request = lambda command, args=None: vm.qmp_command(name, vm.checked_qmp_port(name, data()), command, args, timeout=15)
    def peer_refs(other, config):
        try:
            return runtime_image_files(vm.qmp_command(other, vm.checked_qmp_port(other, config), 'query-block', timeout=3))
        except ConnectionRefusedError:
            return None
    return SnapshotService(root, name, request, data, image_tool(vm.qemu_executable), peer_refs=peer_refs,
                           owner=lambda: watch_qmp_process(vm.checked_qmp_port(name, data())))
