"""Offline disk-only snapshot selection and recoverable application."""
from datetime import datetime
from vm_ui import ask, notice, present
from contextlib import contextmanager
import json
import locale
import os
from pathlib import Path
import shutil
import signal
import subprocess
import uuid
from vm_storage import assert_stopped, backing_chain, file_key, media, qemu_processes, safe_file

def image_tool(qemu_executable):
    return shutil.which('qemu-img.exe') or shutil.which('qemu-img') or str(Path(qemu_executable()).with_name('qemu-img.exe'))

def image_command(exe, arguments):
    result = subprocess.run([exe] + arguments, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            shell=False,
                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0), timeout=120)
    def decode(value):
        try:return value.decode('utf-8')
        except UnicodeDecodeError:return value.decode(locale.getencoding(),errors='replace')
    if result.returncode:
        raise ValueError('qemu-img: ' + decode(result.stderr).strip())
    return decode(result.stdout)

def configured_disks(data):
    extras = data.get('extra_args', [])
    if '-blockdev' in extras:
        raise ValueError('Выбор снимков пока не поддерживает произвольные графы -blockdev. Используй обычный запуск.')
    readonly = set()
    for index, argument in enumerate(extras[:-1]):
        if argument == '-drive':
            spec = extras[index + 1]
            if any(flag in spec.split(',') for flag in ('readonly=on', 'readonly=yes', 'readonly=true', 'read-only=on')):
                from vm_storage import option_file, local_path
                readonly.update(file_key(local_path(value)) for value in option_file(spec))
    result = [path for path, kind in media(data) if kind == 'disk' and file_key(path) not in readonly]
    if not result:
        raise ValueError('У этой VM нет записываемых дисков. Выбери режим простого запуска.')
    for path in result:
        safe_file(path)
        if not path.is_file():
            raise ValueError('Диск не найден: ' + str(path))
    return result

def catalog(disks, exe, run=image_command):
    images = []
    for disk in disks:
        info = json.loads(run(exe, ['info', '--output=json', str(disk)]))
        if info.get('format') != 'qcow2':
            raise ValueError('Для выбора снимков нужны диски qcow2: ' + str(disk))
        by_name = {}
        for item in info.get('snapshots', []):
            if item.get('vm-state-size', 0) != 0:
                continue
            tag = item.get('name')
            if tag:
                if tag in by_name:
                    raise ValueError('В диске несколько снимков с одинаковым именем: ' + tag)
                by_name[tag] = item
        images.append(by_name)
    common = set(images[0]) if images else set()
    for image in images[1:]:
        common.intersection_update(image)
    return sorted([{'name': tag, 'date': images[0][tag].get('date-sec', 0),
                    'ids': [image[tag]['id'] for image in images]} for tag in common],
                  key=lambda item: (item['date'], item['name']), reverse=True)

def runtime_image_files(blocks):
    result = []
    def visit(image):
        if image.get('filename'):
            result.append(Path(image['filename']))
        if image.get('backing-image'):
            visit(image['backing-image'])
    for block in blocks:
        image = block.get('inserted', {}).get('image')
        if image:
            visit(image)
    return result

def assert_private(root, name, disks, exe, inspect=backing_chain, runtime_refs=None):
    chosen = {file_key(path) for path in disks}
    for config in sorted(set(root.glob('*.json')) | set(root.glob('*.json.disabled'))):
        if config.name == name + '.json':
            continue
        data = json.loads(config.read_text(encoding='utf-8-sig'))
        configured = media(data)
        references = [path for path, kind in configured]
        live = runtime_refs(config.stem, data) if runtime_refs and config.suffix == '.json' else None
        if live is not None:
            references.extend(live)
        else:
            for path, kind in configured:
                if kind == 'disk':
                    references.extend(inspect(path, exe))
        if any(file_key(ref) in chosen for ref in references):
            raise ValueError('Откат затронет диск другой VM ({}). Общие диски не откатываются автоматически.'.format(config.stem))
    for disk in disks:
        if any(file_key(other) in chosen and file_key(other) != file_key(disk) for other in inspect(disk, exe)):
            raise ValueError('Диски этой VM зависят друг от друга как базовые образы. Автоматический откат отключён.')

@contextmanager
def deferred_interrupt():
    cancelled = []
    previous = signal.getsignal(signal.SIGINT)
    def request_cancel(signum, frame):
        if not cancelled:
            notice('Отмена запрошена. Ожидаю безопасного завершения текущей операции…', 'warning')
        cancelled.append(True)
    signal.signal(signal.SIGINT, request_cancel)
    try:
        yield cancelled
    finally:
        signal.signal(signal.SIGINT, previous)

def apply_snapshot(disks, selected, exe, run=image_command):
    with deferred_interrupt() as cancelled:
        return _apply_snapshot(disks, selected, exe, run, cancelled)

def _apply_snapshot(disks, selected, exe, run, cancelled, journal=lambda phase, detail, checkpoint=None: None):
    current = next((item for item in catalog(disks, exe, run) if item['name'] == selected['name']), None)
    if current is None or current['ids'] != selected['ids']:
        raise ValueError('Список снимков изменился. Выбери снимок заново.')
    checkpoint = 'before-restore-' + datetime.now().strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:6]
    journal('checkpoint-intent', 'Создание страховочной группы', checkpoint)
    for disk in disks:
        if cancelled:
            raise ValueError('Отменено до начала отката; текущее содержимое дисков сохранено.')
        try:
            run(exe, ['snapshot', '-c', checkpoint, str(disk)])
            journal('checkpoint-disk', str(disk), checkpoint)
        except (OSError, ValueError) as error:
            raise ValueError('Не удалось создать страховочный снимок {}. Откат не начат. {}'.format(checkpoint, error)) from error
    checkpoint_row = next((item for item in catalog(disks, exe, run) if item['name'] == checkpoint), None)
    if not checkpoint_row or len(checkpoint_row['ids']) != len(disks):
        raise ValueError('Страховочная группа не подтверждена. Откат не начат: ' + checkpoint)
    journal('checkpoint-verified', 'Страховочная группа проверена', checkpoint)
    try:
        for disk, snapshot_id in zip(disks, selected['ids']):
            if cancelled:
                raise ValueError('Запрошена отмена отката.')
            journal('apply-intent', str(disk), checkpoint)
            run(exe, ['snapshot', '-a', snapshot_id, str(disk)])
            journal('apply-disk', str(disk), checkpoint)
        if cancelled:
            raise ValueError('Запрошена отмена отката.')
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        journal('compensating', str(error), checkpoint)
        failures = []
        for disk in disks:
            try:
                run(exe, ['snapshot', '-a', checkpoint, str(disk)])
                journal('compensate-disk', str(disk), checkpoint)
            except (OSError, ValueError, subprocess.SubprocessError) as rollback_error:
                failures.append('{}: {}'.format(disk, rollback_error))
        if failures:
            journal('recovery-required', '; '.join(failures), checkpoint)
            raise ValueError('Откат прерван; восстановление прежнего состояния не завершено. VM не запускается. Страховочный снимок: {}. {}'.format(checkpoint, '; '.join(failures))) from error
        journal('compensated', 'Предыдущее состояние возвращено по подтверждённым ответам qemu-img', checkpoint)
        raise ValueError('Откат отменён, прежнее состояние всех дисков восстановлено. ' + str(error)) from error
    return checkpoint


def journalled_apply(root, name, disks, selected, exe, run=image_command, operation_id=None, guard=None):
    from vm_snapshot_store import Store, digest, operation_lock, identifier
    from vm_snapshot_service import identity, SnapshotService
    import time
    if guard is None:
        raise ValueError('Не подключена проверка владельцев перед каждым шагом офлайн-записи.')
    def guarded_run(executable, arguments):
        if arguments[:1] == ['snapshot']:
            guard(arguments)
        return run(executable, arguments)
    with operation_lock(root, name, [digest(identity(path)['file_id']) for path in disks]):
        store = Store(root, name)
        operation_id = identifier(operation_id) if operation_id else str(uuid.uuid4())
        previous = store.operation(operation_id)
        if previous:
            if previous['action'] != 'restore' or previous.get('group_id') != selected.get('id'):
                raise ValueError('ID операции уже принадлежит другому запросу.')
            if previous['state'] == 'completed':
                return previous['result']['checkpoint']
            raise ValueError('Операция уже существует: ' + operation_id + '. Автоматический повтор отката запрещён.')
        store.assert_clear()
        group = next((row for row in store.catalog()['groups'] if row['id'] == selected.get('id')), None)
        if not group:
            raise ValueError('Принадлежность старого/внешнего снимка не подтверждена. Автоматический откат запрещён.')
        actual = []
        for path in disks:
            info = json.loads(run(exe, ['info', '--output=json', str(path)]))
            from vm_snapshot_service import encrypted
            if info.get('format') != 'qcow2' or encrypted(info) or info.get('backing-filename'):
                raise ValueError('Откат требует отдельных незашифрованных qcow2 без backing chain.')
            actual.append(dict(identity(path), snapshots=info.get('snapshots', [])))
        checked = next(row for row in SnapshotService.reconcile([group], actual) if row['id'] == group['id'])
        if checked['state'] != 'ready':
            raise ValueError('Состав группы изменился; восстановление запрещено. ' + checked['state'] + ': ' + checked.get('reason', 'Не все внутренние ID подтверждены.'))
        chosen = dict(checked)
        def config_hashes():
            return {path.name: digest(path.read_bytes().hex()) for path in sorted(set(Path(root).glob('*.json')) | set(Path(root).glob('*.json.disabled')))}
        expected_configs = config_hashes()
        operation = dict(schema_version=1, id=operation_id, name=name, action='restore', created=time.time(),
                         state='validating', steps=[], disks=group['disks'], group_id=group['id'], mutation_started=False)
        operation['plan'] = dict(config_hashes=expected_configs, catalog_hash=digest(store.catalog()), expires=time.time()+120)
        def journal(phase, detail, checkpoint=None):
            if phase in ('checkpoint-intent', 'apply-intent'):
                if config_hashes() != expected_configs or [identity(path)['file_id'] for path in disks] != [member['file_id'] for member in group['disks']]:
                    raise ValueError('Конфигурация или личность дисков изменились. Запись отменена.')
                if phase == 'checkpoint-intent' and time.time() > operation['plan']['expires']:
                    raise ValueError('План восстановления устарел.')
            operation.update(state='running', phase=phase, detail=detail, updated=time.time())
            if checkpoint:
                operation['checkpoint'] = checkpoint
            operation['mutation_started'] = True
            operation['steps'].append(dict(phase=phase, detail=detail, checkpoint=checkpoint))
            store.save_operation(operation)
            if phase == 'checkpoint-verified':
                checkpoint_row = next(item for item in catalog(disks, exe, run) if item['name'] == checkpoint)
                members = []
                for member, snapshot_id in zip(group['disks'], checkpoint_row['ids']):
                    members.append(dict(member, snapshot_id=str(snapshot_id)))
                checkpoint_group = dict(id=str(uuid.uuid4()), name=checkpoint, label='Перед восстановлением · ' + group.get('label', group['name']),
                    date=int(time.time()), kind='disk', state='ready', disks=members, protected=True, source='checkpoint',
                    checkpoint=True, checkpoint_reason='Страховочная группа перед откатом ' + group['id'], operation_id=operation['id'])
                store.publish(checkpoint_group, digest(store.catalog()))
                operation['checkpoint_group_id'] = checkpoint_group['id']
                store.save_operation(operation)
        store.save_operation(operation)  # intent before creating any checkpoint
        try:
            checkpoint = _apply_snapshot(disks, chosen, exe, guarded_run, [], journal)
            if [identity(path)['file_id'] for path in disks] != [item['file_id'] for item in group['disks']]:
                raise ValueError('Файл диска заменён при восстановлении.')
            operation.update(state='completed', phase='Диски восстановлены', result=dict(checkpoint=checkpoint), updated=time.time())
            store.save_operation(operation)
            return checkpoint
        except BaseException as error:
            operation.update(state='failed' if not operation['mutation_started'] or operation.get('phase') == 'compensated' else 'recovery-required',
                             phase=str(error), updated=time.time())
            store.save_operation(operation)
            raise

def choose_and_apply(root, name, data, exe, query_status, processes=None, runtime_refs=None):
    config_path = root / (name + '.json')
    config_before = config_path.read_bytes()
    refs = media(data)
    assert_stopped(name, refs, query_status, processes or qemu_processes)
    disks = configured_disks(data)
    assert_private(root, name, disks, exe, runtime_refs=runtime_refs)
    from vm_snapshot_service import manager_service
    choices = [item for item in manager_service(root, name).read_catalog(False, data)['rows'] if item['state'] == 'ready']
    if not choices:
        present('Снимки дисков', name=name, data=data)
        notice('Общих снимков дисков нет. Создай снимок через S в панели VM или выбери простой запуск.', 'warning')
        return False
    sections = []
    for index, item in enumerate(choices, 1):
        date = datetime.fromtimestamp(item['date']).strftime('%Y-%m-%d %H:%M')
        sections.append(('{} · {}'.format(index, item['name']), [date + ' · только диски, без RAM']))
    present('Снимки дисков', 'Выбери точку восстановления перед запуском.',
            sections=sections, options=[('0', 'Назад / отмена')], name=name, data=data)
    while True:
        raw = ask('Номер снимка:').strip()
        if raw in ('', '0'):
            return False
        if raw.isdecimal() and 1 <= int(raw) <= len(choices):
            selected = choices[int(raw) - 1]
            break
        notice('Выбери номер снимка или 0.', 'warning')
    present('Подтверждение восстановления', name=name, data=data, sections=[
        ('Выбранный снимок', [selected['name']]),
        ('Что произойдёт', ['Диски будут возвращены к снимку. RAM не восстанавливается.',
                           'Перед откатом сохранится страховочный снимок текущего состояния дисков.'])],
        options=[('Y', 'Применить снимок и запустить VM'), ('Enter', 'Отмена')])
    if ask('Подтверждение [y/N]:').strip().lower() not in ('y', 'yes', 'д', 'да'):
        return False
    if config_path.read_bytes() != config_before:
        raise ValueError('Параметры VM изменились. Выбери снимок заново.')
    assert_stopped(name, refs, query_status, processes or qemu_processes)
    assert_private(root, name, disks, exe, runtime_refs=runtime_refs)
    from vm_snapshot_service import manager_service
    if selected.get('kind')=='external':
        raise ValueError('Для переключения внешней цепочки используй окно снимков; обычный терминальный запуск не меняет граф зависимостей.')
    from vm_snapshot_transaction import OfflineTransaction
    transaction=OfflineTransaction(manager_service(root,name))
    checkpoint=transaction.execute(transaction.plan('offline-restore',selected['id']))['checkpoint']
    notice('Снимок применён. Предыдущее состояние дисков сохранено: ' + checkpoint, 'success')
    return True
