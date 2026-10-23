"""Console-free GUI service. JSON lines over inherited pipes, no network RPC."""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import threading
import time
import uuid

BASE = Path(__file__).resolve().parent
sys.path.insert(0, str(BASE))  # Embeddable Python keeps its stdlib isolated.
settings_path = BASE / 'installation.json'
if settings_path.is_file():
    settings = json.loads(settings_path.read_text(encoding='utf-8-sig'))
    os.environ['PATH'] = settings['qemu_dir'] + os.pathsep + os.environ.get('PATH', '')
    os.environ['VM_MANAGER_DISPLAY'] = settings.get('display', 'sdl')

import vm
from vm_network import import_legacy, network_args, validate as validate_network
from vm_editor import save_parameters
from vm_disk import disk_format, new_disk_format
from vm_library import process_state
from vm_profiles import choose, profile, validate as validate_profile
from vm_runtime import vm_lock
from vm_runtime import watch_qmp_process
from vm_storage import (assert_stopped, deletion_plan, delete_with_template, exclusive_file,
                        media, qemu_processes, safe_file, signature, templates)
from vm_snapshots import (configured_disks, catalog, assert_private, image_tool, image_command,
                          _apply_snapshot, runtime_image_files)

HIDDEN = getattr(subprocess, 'CREATE_NO_WINDOW', 0)


def network_display(data):
    """Describe configured NICs without migrating or writing the configuration."""
    try:
        return import_legacy(data)
    except (ValueError, TypeError, KeyError) as error:
        return dict(adapters=[], warnings=[str(error)], error=str(error))


def normalized(data):
    if not isinstance(data, dict):
        raise ValueError('Конфигурация должна быть объектом JSON.')
    data = dict(data)
    if 'disk_path' not in data and 'iso_path' not in data:
        if data.get('kind') not in ('iso', 'disk'):
            raise ValueError('Нужен диск или ISO.')
        data['disk_path'] = data.get('path', '') if data['kind'] == 'disk' else ''
        data['iso_path'] = data.get('path', '') if data['kind'] == 'iso' else ''
    for key in ('disk_path', 'iso_path'):
        data.setdefault(key, '')
        if not isinstance(data[key], str):
            raise ValueError('Путь должен быть строкой: ' + key)
    for key, maximum in (('ram_mb', 1048576), ('cores', 256)):
        if type(data.get(key)) is not int or not 1 <= data[key] <= maximum:
            raise ValueError('Недопустимое значение ' + key)
    if not data['disk_path'] and not data['iso_path']:
        raise ValueError('Укажи хотя бы один диск или ISO.')
    if not isinstance(data.get('label', ''), str):
        raise ValueError('Название должно быть строкой.')
    if data.get('vga', 'std') not in ('std', 'virtio', 'qxl', 'cirrus', 'none'):
        raise ValueError('Неизвестный видеоадаптер.')
    vm.resolution(data)
    validate_profile(data)
    validate_network(data)
    vm.validate_extra_args(data)
    disk_format(data)
    return data


class Service:
    def stand_remove(self,args):
        from vm_stand_lifecycle import remove
        return remove(self.stand_runtime,args.get('id'),args.get('revision'),args.get('confirm'))

    def stand_duplicate(self,args):
        from vm_stand_lifecycle import duplicate
        return duplicate(self.stand_runtime,args.get('id'),args.get('revision'),args.get('name'))

    def stand_reconcile(self,args):
        from vm_stand_lifecycle import reconcile
        return reconcile(self.stand_runtime,args.get('id'),args.get('revision'),args.get('confirm'))

    def stand_report_preview(self,args):
        from vm_stand_lifecycle import report
        text=report(self.stand_runtime,args.get('id'))
        with self.stand_runtime.guard:
            self.stand_runtime._cleanup()
            if len(self.stand_runtime.exports)>=16:raise ValueError('Закрой старые предпросмотры или дождись их истечения.')
            token=uuid.uuid4().hex;self.stand_runtime.exports[token]=dict(text=text,expires=time.monotonic()+120)
        return dict(id=token,text=text,sha256=hashlib.sha256(text.encode('utf-8')).hexdigest())

    def stand_report_save(self,args):
        from vm_stand_lifecycle import save_report
        return save_report(self.stand_runtime,args.get('id'),args.get('path'),args.get('sha256'))

    def stand_plan(self,args):
        return self.stand_runtime.make_plan(args.get('id'),args.get('action','start'),args.get('include_existing',False),args.get('force',False))

    def stand_execute(self,args):
        return self.stand_runtime.execute(args.get('plan_id'),args.get('confirm'))

    def stand_status(self,args):
        return self.stand_runtime.status(args.get('operation_id'))

    def stand_cancel(self,args):
        return self.stand_runtime.cancel(args.get('operation_id'))

    def stand_overview(self,args):
        return self.stand_runtime.overview(args.get('id'))

    def stand_discard(self,args):
        return self.stand_runtime.discard(args.get('plan_id'))

    def stand_activity(self,args):
        return self.stand_runtime.activity()

    def stand_list(self, args):
        from vm_stands import Store
        return Store(self.root).list()

    def stand_save(self, args):
        from vm_stands import Store, validate
        row = args.get('stand')
        if not isinstance(row, dict):
            raise ValueError('Нужен объект стенда.')
        validate(row)
        with vm_lock(self.root,'stand-execution-'+row['id']):
            return self._stand_save(args,row)

    def _stand_save(self,args,row):
        from vm_stands import Store
        available = {path.stem for path in vm.ROOT.glob('*.json')}
        if any(name not in available for name in row.get('members', [])):
            raise ValueError('Одна из выбранных VM отсутствует. Обнови библиотеку.')
        if row['schema_version']==2:
            from vm_stand_plan import effective
            configs={name:vm.load(name) for name in row['members']}
            effective(row,configs,{link['id']:20000+index for index,link in enumerate(row['links'])})
            for name,data in configs.items():
                self.stopped(name,data)
        return Store(self.root).save(row, args.get('revision'))

    def profile_list(self, args):
        from vm_profile_store import read
        return read(self.root)

    def profile_save(self, args):
        from vm_profile_store import change
        return change(self.root, args.get('hash'), row=args.get('profile'))

    def profile_delete(self, args):
        from vm_profile_store import change
        if not isinstance(args.get('id'), str) or not args['id']:
            raise ValueError('Выбери пользовательский профиль.')
        return change(self.root, args.get('hash'), remove=args['id'])

    def __init__(self, root=None):
        self.root = Path(root or BASE).resolve()
        vm.ROOT = self.root / 'machines'
        self.sessions = {}
        self.watchers = {}
        self.events = {}
        self.plans = {}
        self.snapshot_plans = {}
        from vm_snapshot_jobs import Jobs
        self.snapshot_jobs=Jobs(self)
        self.reports = {}
        self.inventory = None
        self.inventory_time = 0
        self.inventory_running = False
        self.pool = ThreadPoolExecutor(max_workers=4)
        self.schedule_stop=threading.Event()
        from vm_stand_runtime import Runtime,Host
        self.stand_runtime=Runtime(self.root,Host(self))

    def start_schedules(self):
        def work():
            from vm_snapshot_service import manager_service
            from vm_snapshot_schedule import Schedule
            while not self.schedule_stop.wait(30):
                for path in sorted(vm.ROOT.glob('*.json')):
                    if self.schedule_stop.is_set():return
                    try:Schedule(manager_service(vm.ROOT,path.stem)).tick()
                    except Exception as error:self.event(path.stem,'Расписание: '+str(error))
        threading.Thread(target=work,name='snapshot-schedules',daemon=True).start()

    def snapshot_schedule(self,args):
        from vm_snapshot_service import manager_service
        from vm_snapshot_schedule import Schedule
        return Schedule(manager_service(vm.ROOT,self.name(args))).save(args.get('options'),args.get('revision'))

    def snapshot_chain_plan(self,args):
        from vm_snapshot_service import manager_service
        from vm_snapshot_external import ExternalSnapshots
        return ExternalSnapshots(manager_service(vm.ROOT,self.name(args))).maintenance_plan(args.get('action','merge'))

    def name(self, args):
        name = args.get('name')
        if not isinstance(name, str) or not vm.valid_name(name):
            raise ValueError('Выбери виртуальную машину с допустимым именем.')
        return name

    def event(self, name, message):
        records = self.events.setdefault(name, [])
        sequence = records[-1]['id'] + 1 if records else 1
        records.append(dict(id=sequence, text=datetime.now().strftime('%H:%M:%S') + '  ' + message))
        del records[:-500]

    def query(self, name, data, command='query-status', args=None, timeout=1.2):
        return vm.qmp_command(name, vm.checked_qmp_port(name, data), command, args, timeout=timeout)

    def peer_refs(self, name, data):
        try:
            return runtime_image_files(self.query(name, data, 'query-block'))
        except (ConnectionRefusedError, TimeoutError):
            return None

    def stopped(self, name, data):
        assert_stopped(name, media(data), lambda: self.query(name, data), qemu_processes)

    def _inventory(self):
        try:
            self.inventory = qemu_processes()
        except (OSError, ValueError, subprocess.SubprocessError):
            self.inventory = None
        finally:
            self.inventory_time = time.monotonic()
            self.inventory_running = False

    def list(self, args):
        from vm_snapshot_store import assert_can_start
        rows = []
        for name, data in vm.menu_rows(vm.entries()):
            error = ''
            try:
                assert_can_start(vm.ROOT, name)
            except (OSError, ValueError) as failure:
                error = str(failure)
            rows.append(dict(name=name, data=data, network_view=network_display(data), snapshot_recovery=bool(error), snapshot_error=error))
        return dict(rows=rows,
                    mode=vm.manager_settings()['launch_mode'], version='0.1.0-alpha.5')

    def states(self, args):
        if not self.inventory_running and time.monotonic() - self.inventory_time > 10:
            self.inventory_running = True
            threading.Thread(target=self._inventory, daemon=True).start()
        def one(row):
            name, data = row
            if data.get('_invalid'):
                return name, 'unavailable'
            from vm_snapshot_store import assert_can_start
            try:
                assert_can_start(vm.ROOT, name)
            except (OSError, ValueError):
                return name, 'recovery-required'
            try:
                state = self.query(name, data)['status']
                try:
                    watcher = self.watchers.get(name)
                    if watcher is None or watcher.poll() is not None:
                        if watcher is not None:
                            watcher.close()
                        self.watchers[name] = watch_qmp_process(vm.qmp_port(name, data))
                except (OSError, ValueError):
                    pass
            except (ConnectionRefusedError, TimeoutError):
                records = self.inventory if time.monotonic() - self.inventory_time < 25 else None
                state = process_state(name, data, records)
            except (OSError, ValueError, KeyError, TypeError):
                state = 'unavailable'
            session = self.sessions.get(name)
            watcher = self.watchers.get(name)
            if state in ('unavailable', 'no-qmp', 'stopped'):
                if session and session['process'].poll() is not None:
                    state = 'error' if session['process'].returncode else 'stopped'
                elif watcher is not None:
                    try:
                        if watcher.poll() is not None:
                            state = 'error' if watcher.returncode else 'stopped'
                    except OSError:
                        pass
            if session and session['process'].poll() is not None and not session.get('reported_exit'):
                session['reported_exit'] = True
                self.event(name, 'QEMU завершён, код ' + str(session['process'].returncode))
            return name, state
        return dict(self.pool.map(one, vm.menu_rows(vm.entries())))

    def get(self, args):
        name = self.name(args)
        raw = vm.config_path(name).read_bytes()
        try:
            data, error = vm.load(name), ''
        except (OSError, ValueError, TypeError, KeyError) as failure:
            data, error = {}, str(failure)
        return dict(name=name, data=data, raw=raw.decode('utf-8-sig'),
                    hash=hashlib.sha256(raw).hexdigest(), error=error,
                    network_view=network_display(data) if not error else None)

    def save(self, args):
        name = self.name(args)
        with vm_lock(vm.ROOT, name):
            path = vm.config_path(name)
            raw = path.read_bytes()
            if hashlib.sha256(raw).hexdigest() != args.get('hash'):
                raise ValueError('Параметры изменились после открытия окна. Открой редактор заново.')
            if 'raw' in args:
                data = normalized(json.loads(args['raw']))
            else:
                data = normalized(dict(vm.load(name), **args.get('updates', {})))
            backup = save_parameters(path, raw, data, lambda candidate: vm.build_args(name, candidate))
            self.event(name, 'Параметры сохранены; копия: ' + backup.name)
            return dict(backup=str(backup))

    def template_list(self, args):
        return [dict(item, id=path.name, network_view=network_display(item['parameters']))
                for path, item in templates(self.root / 'parameter-backups')]

    def network_import(self, args):
        data = args.get('data')
        if not isinstance(data, dict):
            raise ValueError('Ожидаются параметры VM для импорта сети.')
        return import_legacy(data)

    def network_validate(self, args):
        data = args.get('data')
        if not isinstance(data, dict):
            raise ValueError('Ожидаются параметры сети.')
        vm.validate_extra_args(data)
        return dict(args=network_args(data) + data.get('extra_args', []))

    def create(self, args):
        name = self.name(args)
        with vm_lock(vm.ROOT, name):
            if vm.config_path(name).exists():
                raise ValueError('Это имя VM уже занято.')
            saved = {}
            template_id = args.get('template')
            if template_id:
                matches = [item for path, item in templates(self.root / 'parameter-backups') if path.name == template_id]
                if len(matches) != 1:
                    raise ValueError('Копия параметров не найдена.')
                saved = dict(matches[0]['parameters'])
            old_media = {key: saved.get(key, saved.get('path', '') if saved.get('kind') == key.split('_')[0] else '')
                         for key in ('disk_path', 'iso_path')}
            saved.update(args.get('data', {}))
            if saved.get('disk_path') != old_media['disk_path'] and 'disk_format' not in args.get('data', {}):
                saved.pop('disk_format', None)
            if 'network_managed' in args:
                if type(args['network_managed']) is not bool:
                    raise ValueError('Режим управления сетью должен быть логическим значением.')
                if not args['network_managed']:
                    saved.pop('network_adapters', None)
            for key, old in old_media.items():
                new_path = saved.get(key, '')
                if old and new_path:
                    saved['extra_args'] = [arg.replace(old.replace(',', ',,'), new_path.replace(',', ',,'))
                                           for arg in saved.get('extra_args', [])]
            for old, replacement in args.get('replacements', {}).items():
                saved['extra_args'] = [arg.replace(old.replace(',', ',,'), replacement.replace(',', ',,'))
                                       for arg in saved.get('extra_args', [])]
            data = normalized(saved)
            data.pop('kind', None)
            data.pop('path', None)
            disk = Path(data['disk_path']) if data['disk_path'] else None
            new = bool(args.get('new_disk'))
            for path, kind in media(data):
                if new and disk is not None and path == disk:
                    continue
                if not path.is_file():
                    raise ValueError('Не найден подключаемый файл: ' + str(path))
            colored = vm.assign_color(vm.ROOT, name, data)
            vm.ROOT.mkdir(parents=True, exist_ok=True)
            created = None
            try:
                if new:
                    size = args.get('size_gb')
                    if disk is None or not disk.is_absolute():
                        raise ValueError('Укажи полный путь нового диска .qcow2 или .vdi.')
                    kind = new_disk_format(disk)
                    colored['disk_format'] = kind
                    if type(size) is not int or not 1 <= size <= 65536:
                        raise ValueError('Размер диска: 1–65536 ГБ.')
                    safe_file(disk)
                    disk.parent.mkdir(parents=True, exist_ok=True)
                    with disk.open('xb'):
                        pass
                    created = signature(disk)
                    image_command(image_tool(vm.qemu_executable), ['create', '-f', kind, str(disk), str(size) + 'G'])
                    created = signature(disk)
                vm.build_args(name, colored)
                with vm.config_path(name).open('x', encoding='utf-8') as stream:
                    json.dump(colored, stream, ensure_ascii=False, indent=2)
                    stream.write('\n')
            except BaseException:
                if created is not None and disk.exists() and signature(disk) == created:
                    with exclusive_file(disk):
                        disk.unlink()
                raise
            self.event(name, 'VM создана')
            return dict(name=name)

    def start(self, args, *, stand_data=None, expected_hash=None):
        name = self.name(args)
        with vm_lock(vm.ROOT, name):
            from vm_snapshot_store import assert_can_start
            assert_can_start(vm.ROOT, name)
            data = vm.load(name)
            before = vm.config_path(name).read_bytes()
            if stand_data is not None and hashlib.sha256(before).hexdigest()!=expected_hash:
                raise ValueError('Параметры VM изменились после плана стенда.')
            try:
                self.query(name, data)
            except (ConnectionRefusedError, TimeoutError):
                pass
            else:
                if args.get('snapshot'):
                    raise ValueError('Сначала выключи VM для восстановления снимка.')
                return dict(attached=True)
            self.stopped(name, data)
            boot = args.get('boot', 'disk' if data['disk_path'] else 'iso')
            if boot not in ('disk', 'iso') or not data.get(boot + '_path'):
                raise ValueError('Этот источник загрузки не настроен.')
            for path, kind in media(data):
                if boot == 'disk' and kind == 'iso' and str(path) == data.get('iso_path'):
                    continue
                if not path.is_file():
                    raise ValueError('Файл не найден: ' + str(path))
            exe = vm.qemu_executable()
            accelerator, checked = choose(exe, data)
            self.event(name, 'Выбрано ускорение ' + accelerator.upper())
            selected = args.get('snapshot')
            checkpoint = None
            if vm.config_path(name).read_bytes() != before:
                raise ValueError('Параметры изменились во время подготовки запуска.')
            old_port = vm.qmp_port(name, data)
            old_data = data
            if stand_data is None:
                data, before, port, backup = vm.prepare_qmp_port(name, data, before, lambda: self.stopped(name, old_data))
            else:
                # A confirmed stand never rewrites standalone configuration to assign ports/network.
                port=vm.checked_qmp_port(name,data);backup=None
                if vm.qmp_bind_error(port):raise ValueError('QMP-порт занят после проверки стенда.')
                from copy import deepcopy
                candidate=deepcopy(data)
                if 'network_adapters' in stand_data:candidate['network_adapters']=stand_data['network_adapters']
                validate_network(candidate);data=candidate
            if backup:
                self.event(name, 'QMP-порт {} недоступен; выбран {}. Копия: {}'.format(old_port, port, backup.name))
            if selected:
                if boot != 'disk':
                    raise ValueError('Снимок применяется при загрузке с диска.')
                disks = configured_disks(data)
                image = image_tool(vm.qemu_executable)
                self.stopped(name, data)
                assert_private(vm.ROOT, name, disks, image, runtime_refs=self.peer_refs)
                from vm_snapshot_service import manager_service
                from vm_snapshot_transaction import OfflineTransaction
                transaction=OfflineTransaction(manager_service(vm.ROOT,name))
                if selected.get('kind')=='external':
                    from vm_snapshot_external import ExternalSnapshots
                    external=ExternalSnapshots(manager_service(vm.ROOT,name))
                    checkpoint=external.execute(external.plan(selected.get('id')))['result']
                    data=vm.load(name);before=vm.config_path(name).read_bytes()
                else:
                    operation_id=args.get('snapshot_operation_id')
                    previous=transaction.store.operation(operation_id) if operation_id else None
                    if previous:
                        if previous['action']!='offline-restore' or previous['plan']['selected']['id']!=selected.get('id'):raise ValueError('ID восстановления принадлежит другому действию.')
                        checkpoint=transaction.service.result(previous)['checkpoint']
                    else:
                        restore_plan=transaction.plan('offline-restore',selected.get('id'))
                        if operation_id:
                            from vm_snapshot_store import identifier
                            restore_plan['id']=identifier(operation_id)
                        checkpoint=transaction.execute(restore_plan,lambda text,*counts:self.event(name,text))['checkpoint']
                self.event(name, 'Снимок применён; страховочная точка: ' + checkpoint)
            try:
                self.stopped(name, data)
                if vm.config_path(name).read_bytes() != before:
                    raise ValueError('Параметры изменились перед запуском. VM не запущена.')
                command = [exe] + vm.build_args(name, data, boot, accelerator=accelerator)
                if args.get('paused') is True:command+=['-S']
                logs = self.root / 'logs';logs.mkdir(exist_ok=True)
                path = logs / ('{}-{}-{}.log'.format(name, datetime.now().strftime('%Y%m%d-%H%M%S'), uuid.uuid4().hex[:6]))
                with path.open('xb') as stream:
                    process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=stream, stderr=subprocess.STDOUT,
                                               shell=False, creationflags=HIDDEN | getattr(subprocess, 'CREATE_NEW_PROCESS_GROUP', 0))
            except Exception as error:
                if checkpoint:raise ValueError('Диски восстановлены; следующий запуск не подтверждён: '+str(error)+'. Страховочная точка: '+checkpoint+'. Автоматического повторного отката нет.') from error
                raise
            self.sessions[name] = dict(process=process, log=path, accelerator=accelerator)
            self.event(name, 'Запущен QEMU, PID ' + str(process.pid))
            # No retry with another accelerator after a real guest process starts.
            return dict(pid=process.pid, accelerator=accelerator, log=str(path), detail=checked['detail'], checkpoint=checkpoint)

    def control(self, args):
        name = self.name(args)
        action = args.get('action')
        if action not in ('toggle', 'shutdown', 'reset', 'quit', 'snapshot'):
            raise ValueError('Неизвестное действие.')
        if action in ('shutdown', 'reset', 'quit') and args.get('confirm') != name:
            raise ValueError('Нужно подтверждение выбранной VM.')
        if action == 'snapshot':
            from vm_snapshot_service import manager_service
            snapshots = manager_service(vm.ROOT, name)
            result = snapshots.create(snapshots.plan_create(), lambda text: self.event(name, text))
            self.event(name, 'Снимок создан: ' + result['result'])
            return result
        with vm_lock(vm.ROOT, name):
            data = vm.load(name)
            current = self.query(name, data)
            request = lambda command, arguments=None: self.query(name, data, command, arguments, timeout=8)
            if action == 'toggle':
                if not current['running']:
                    from vm_snapshot_store import assert_can_start
                    assert_can_start(vm.ROOT, name)
                result = request('stop' if current['running'] else 'cont')
            elif action == 'shutdown':
                if not current['running']:
                    raise ValueError('Сначала продолжи работу VM, затем запроси выключение.')
                result = request('system_powerdown')
            elif action == 'reset':
                result = request('system_reset')
            else:
                from vm_runtime import watch_qmp_process
                watcher = watch_qmp_process(vm.qmp_port(name, data))
                try:
                    try:
                        result = request('quit')
                    except (OSError, ValueError):
                        deadline = time.monotonic() + 2
                        while watcher.poll() is None and time.monotonic() < deadline:
                            time.sleep(0.05)
                        if watcher.poll() is None:
                            raise
                        result = {}
                finally:
                    watcher.close()
            self.event(name, {'toggle': 'Пауза / продолжение', 'shutdown': 'Запрошено выключение гостевой ОС',
                              'reset': 'Жёсткая перезагрузка', 'quit': 'Остановка QEMU', 'snapshot': 'Снимок создан'}[action])
            return dict(result=result)

    def snapshots(self, args):
        name = self.name(args)
        data = vm.load(name)
        from vm_snapshot_service import manager_service
        snapshots = manager_service(vm.ROOT, name)
        try:
            state = self.query(name, data)['status']
        except (ConnectionRefusedError, TimeoutError):
            self.stopped(name, data)
            return snapshots.read_catalog(False, data)
        return snapshots.read_catalog(True, data, state)

    def snapshot_plan(self, args):
        from vm_snapshot_service import manager_service
        name = self.name(args)
        if len(self.snapshot_plans) > 100:
            self.snapshot_plans = {key: value for key, value in self.snapshot_plans.items() if value['expires'] > time.time()}
            if len(self.snapshot_plans) > 100:
                raise ValueError('Слишком много неподтверждённых планов. Закрой лишние окна.')
        snapshots=manager_service(vm.ROOT,name)
        if (args.get('options') or {}).get('kind')=='ram':
            from vm_snapshot_ram import RamSnapshots
            plan=RamSnapshots(snapshots).plan(args.get('options'))
        elif (args.get('options') or {}).get('kind')=='external':
            from vm_snapshot_external import ExternalSnapshots
            plan=ExternalSnapshots(snapshots).plan(options=args.get('options'))
            plan['excluded']=[]
        else:
            try:snapshots.request('query-status')
            except (ConnectionRefusedError,TimeoutError):
                from vm_snapshot_transaction import OfflineTransaction
                plan=OfflineTransaction(snapshots).plan('offline-create',options=args.get('options'))
            else:plan=snapshots.plan_create(args.get('options'),args.get('operation_id'))
        self.snapshot_plans[plan['id']] = plan
        detail={'ram-create':'Полное состояние CPU/RAM/устройств и дисков. Содержит личные данные. Нужна та же сборка QEMU, TCG и настройки.',
                'external':'Внешняя цепочка на приватной базе. Новый каталог: '+plan.get('destination_folder',''),
                'offline-create':'Выключенная VM; полные рабочие копии, непрерывное владение оригиналами и проверка перед публикацией. Прежние образы сохранятся.'}.get(plan['action'],'Только диски, без RAM; уровень подготовки Guest Agent выбран отдельно. Рост снимков требует свободного места.')
        return dict(operation_id=plan['id'], expires=plan['expires'], disks=plan['disks'], excluded=plan['excluded'],
                    detail=detail)

    def snapshot_offline_plan(self, args):
        from vm_snapshot_service import manager_service
        name=self.name(args)
        manager_service(vm.ROOT,name).assert_offline()
        result=self.snapshot_plan(args);plan=self.snapshot_plans[result['operation_id']]
        result.update(can_publish=True,required_copy_bytes=sum(row['size'] for row in plan.get('signatures',[])))
        return result

    def snapshot_operation_plan(self,args):
        from vm_snapshot_transaction import OfflineTransaction
        from vm_snapshot_service import manager_service
        name=self.name(args)
        if args.get('action') not in ('offline-delete','offline-restore'):raise ValueError('Неизвестное действие.')
        snapshots=manager_service(vm.ROOT,name)
        selected=next((row for row in snapshots.store.catalog()['groups'] if row['id']==args.get('group_id')),None)
        if selected and selected['kind']=='external':
            if args['action']!='offline-restore':raise ValueError('Внешние слои с зависимостями не удаляются как независимые файлы.')
            from vm_snapshot_external import ExternalSnapshots
            plan=ExternalSnapshots(snapshots).plan(selected['id'])
        else:plan=OfflineTransaction(snapshots).plan(args['action'],args.get('group_id'),args.get('options'))
        self.snapshot_plans[plan['id']]=plan
        return dict(operation_id=plan['id'],disks=plan['disks'],expires=plan['expires'],excluded=[],config_diff=plan.get('config_diff',[]),
                    detail='VM останется выключенной. Проверенные рабочие копии заменят образы; прежние образы сохранятся для восстановления.')

    def snapshot_export_plan(self,args):
        from vm_snapshot_export import Export
        from vm_snapshot_service import manager_service
        name=self.name(args)
        plan=Export(manager_service(vm.ROOT,name)).plan(args.get('group_id'),args.get('options'))
        self.snapshot_plans[plan['id']]=plan
        return dict(operation_id=plan['id'],disks=plan['disks'],required_bytes=plan['required_bytes'],expires=plan['expires'],
                    detail='Новый независимый ZIP: '+plan['target']+'. VM должна оставаться выключенной. RAM и runtime исключены.')

    def snapshot_job_start(self,args):return self.snapshot_jobs.start(args)
    def snapshot_job_status(self,args):return self.snapshot_jobs.status(args)
    def snapshot_cancel(self,args):return self.snapshot_jobs.cancel(args)

    def snapshot_execute(self, args, progress=None, cancelled=lambda:False):
        from vm_snapshot_service import manager_service
        name = self.name(args)
        snapshots = manager_service(vm.ROOT, name)
        operation_id = args.get('operation_id')
        previous = snapshots.store.operation(operation_id)
        if previous:
            return snapshots.result(previous)
        plan = self.snapshot_plans.get(operation_id)
        if not plan or plan['name'] != name:
            raise ValueError('План не найден. Сформируй новый план снимка.')
        progress=progress or (lambda text,*counts:self.event(name,text))
        if plan['action']=='export':
            from vm_snapshot_export import Export
            return Export(snapshots).execute(plan,progress,cancelled)
        if plan['action'].startswith('offline-'):
            from vm_snapshot_transaction import OfflineTransaction
            return OfflineTransaction(snapshots).execute(plan,progress,cancelled)
        if plan['action']=='external':
            from vm_snapshot_external import ExternalSnapshots
            return ExternalSnapshots(snapshots).execute(plan)
        if plan['action']=='ram-create':
            from vm_snapshot_ram import RamSnapshots
            return RamSnapshots(snapshots).create(plan)
        return snapshots.create(plan,progress)

    def snapshot_ram_restore(self,args):
        from vm_snapshot_ram import RamSnapshots
        from vm_snapshot_service import manager_service
        name=self.name(args);snapshots=manager_service(vm.ROOT,name)
        selected=next((row for row in snapshots.store.catalog()['groups'] if row['id']==args.get('group_id') and row['kind']=='ram'),None)
        if not selected:raise ValueError('Полное состояние не найдено.')
        from vm_snapshot_parameters import capture
        if capture(vm.load(name))['launch']!=selected['fingerprint']['launch']:raise ValueError('Текущие настройки отличаются от полного состояния. VM не запускалась.')
        with vm_lock(vm.ROOT,name):
            try:self.query(name,vm.load(name))
            except (ConnectionRefusedError,TimeoutError):
                self.start(dict(name=name,boot='disk',paused=True))
                deadline=time.monotonic()+15
                while True:
                    try:self.query(name,vm.load(name));break
                    except (ConnectionRefusedError,TimeoutError):
                        if time.monotonic()>deadline:raise ValueError('Приостановленный QEMU не подключился. Полное состояние не загружалось.')
                        time.sleep(.05)
            result=RamSnapshots(manager_service(vm.ROOT,name)).restore(selected['id'])
            if args.get('start_after') is True:self.control(dict(name=name,action='toggle'))
            return result

    def snapshot_recover(self, args):
        from vm_snapshot_service import manager_service
        name = self.name(args)
        return manager_service(vm.ROOT, name).recover_create(args.get('operation_id'), lambda: self.stopped(name, vm.load(name)))

    def snapshot_adopt(self, args):
        from vm_snapshot_service import manager_service
        name = self.name(args)
        return manager_service(vm.ROOT, name).adopt(args.get('tag'), args.get('confirm'), lambda: self.stopped(name, vm.load(name)))

    def snapshot_metadata(self, args):
        from vm_snapshot_service import manager_service
        name=self.name(args)
        return manager_service(vm.ROOT,name).metadata(args.get('group_id'),args.get('revision'),args.get('action'),args.get('options'))

    def delete_plan(self, args):
        name = self.name(args)
        remove_files = args.get('remove_files', True)
        if type(remove_files) is not bool:
            raise ValueError('Выбери способ удаления VM.')
        with vm_lock(vm.ROOT, name):
            data = vm.load(name)
            image = image_tool(vm.qemu_executable) if remove_files else None
            plan = deletion_plan(vm.ROOT, name, data, image, lambda: self.query(name, data), remove_files=remove_files)
            token = uuid.uuid4().hex
            self.plans = {token: (time.monotonic(), plan)}
            return dict(token=token, name=name, files=[str(path) for path in plan['files']],
                        kept=plan['kept'], missing=plan['missing'], remove_files=remove_files)

    def delete(self, args):
        name = self.name(args)
        save_backup = args.get('save_backup', True)
        if type(save_backup) is not bool:
            raise ValueError('Выбери, сохранять ли копию параметров.')
        if args.get('confirm') != name:
            raise ValueError('Для удаления введи точное имя VM.')
        saved = self.plans.get(args.get('token'))
        if not saved or time.monotonic() - saved[0] > 600 or saved[1]['name'] != name:
            raise ValueError('План удаления устарел. Открой просмотр файлов заново.')
        with vm_lock(vm.ROOT, name):
            data = vm.load(name)
            path = delete_with_template(saved[1], args.get('backup_name', ''), lambda: self.query(name, data), save_backup=save_backup)
            self.plans.clear()
            return dict(backup=str(path) if path else None)

    def mode(self, args):
        value = args.get('value')
        if value not in ('normal', 'snapshot'):
            raise ValueError('Неизвестный режим запуска.')
        data = vm.manager_settings()
        data['launch_mode'] = value
        self.root.mkdir(parents=True, exist_ok=True)
        import tempfile
        with tempfile.NamedTemporaryFile(dir=self.root, prefix='gui-settings-', suffix='.tmp',
                                         mode='w', encoding='utf-8', delete=False) as stream:
            path = Path(stream.name)
            json.dump(data, stream, ensure_ascii=False, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        try:
            os.replace(path, self.root / 'manager-settings.json')
        finally:
            if path.exists():
                path.unlink()
        return dict(mode=value)

    def doctor(self, args):
        from vm_doctor import Doctor
        name = self.name(args) if args.get('name') else None
        report = Doctor(vm).collect(name)
        token = uuid.uuid4().hex
        self.reports[token] = report
        while len(self.reports) > 8:
            self.reports.pop(next(iter(self.reports)))
        return dict(token=token, checks=report.items, probes=report.probes)

    def report_save(self, args):
        report = self.reports.get(args.get('token'))
        if report is None:
            raise ValueError('Выполни диагностику заново.')
        return dict(path=str(report.save()))

    def support_export(self, args):
        from vm_support import export
        return export(self, args)

    def logs(self, args):
        name = self.name(args)
        record = self.sessions.get(name)
        path = record['log'] if record else None
        if path is None:
            pattern = re.compile(re.escape(name) + r'-\d{8}-\d{6}-[0-9a-f]{6}\.log')
            paths = [item for item in (self.root / 'logs').glob('*.log') if pattern.fullmatch(item.name)]
            path = max(paths, key=lambda item: item.stat().st_mtime_ns) if paths else None
        offset = args.get('offset', 0)
        if type(offset) is not int or offset < 0:
            raise ValueError('Некорректная позиция журнала.')
        content = b''
        if path is not None:
            safe_file(path)
            if args.get('file') != path.name or offset > path.stat().st_size:
                offset = 0
            with path.open('rb') as stream:
                stream.seek(offset)
                content = stream.read(32768)
        since = args.get('event', 0)
        return dict(file=path.name if path else '', offset=offset + len(content),
                    content=base64.b64encode(content).decode('ascii'),
                    events=[item for item in self.events.get(name, []) if item['id'] > since])

    def close(self):
        self.stand_runtime.close()
        # Closing the frontend never terminates detached guest processes.
        self.schedule_stop.set()
        self.pool.shutdown(wait=False)
        for watcher in self.watchers.values():
            watcher.close()
        self.watchers.clear()

    def ping(self, args):
        return dict(protocol=1, python=sys.version.split()[0], pid=os.getpid(), root=str(self.root))


COMMANDS = {'snapshot_export_plan','snapshot_job_start','snapshot_job_status','snapshot_cancel','profile_list', 'profile_save', 'profile_delete', 'ping', 'list', 'states', 'get', 'save', 'template_list', 'network_import', 'network_validate', 'create', 'start', 'control',
            'snapshots', 'snapshot_plan', 'snapshot_offline_plan', 'snapshot_operation_plan', 'snapshot_execute', 'snapshot_recover', 'snapshot_adopt', 'snapshot_metadata', 'snapshot_schedule', 'snapshot_chain_plan', 'snapshot_ram_restore', 'delete_plan', 'delete', 'mode', 'doctor', 'report_save', 'logs', 'support_export'}


COMMANDS |= {'stand_list', 'stand_save','stand_plan','stand_execute','stand_status','stand_cancel','stand_overview','stand_discard','stand_activity'}
COMMANDS |= {'stand_remove','stand_duplicate','stand_reconcile','stand_report_preview','stand_report_save'}


def serve(root=None):
    service = Service(root)
    service.start_schedules()
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8', errors='replace')
    try:
        while True:
            line = sys.stdin.readline(1048577)
            if not line:
                break
            request = {}
            try:
                if len(line) > 1048576:
                    raise ValueError('Запрос слишком большой.')
                request = json.loads(line)
                if not isinstance(request, dict) or request.get('command') not in COMMANDS or not isinstance(request.get('args', {}), dict):
                    raise ValueError('Некорректный запрос GUI.')
                result = getattr(service, request['command'])(request.get('args', {}))
                reply = dict(id=request.get('id'), ok=True, result=result)
            except Exception as error:
                from vm_snapshot_errors import describe
                reply = dict(id=request.get('id') if isinstance(request, dict) else None, ok=False,
                             error=str(error) or type(error).__name__,failure=describe(error))
            print(json.dumps(reply, ensure_ascii=False, default=str), flush=True)
    finally:
        service.close()


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument('--root')
    serve(parser.parse_args().root)
