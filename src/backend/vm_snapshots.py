"""Offline disk-only snapshot selection and recoverable application."""
from datetime import datetime
from vm_ui import ask, notice, present
from contextlib import contextmanager
import json
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
                            text=True, encoding='utf-8', errors='replace', shell=False,
                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    if result.returncode:
        raise ValueError('qemu-img: ' + result.stderr.strip())
    return result.stdout

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

def _apply_snapshot(disks, selected, exe, run, cancelled):
    current = next((item for item in catalog(disks, exe, run) if item['name'] == selected['name']), None)
    if current is None or current['ids'] != selected['ids']:
        raise ValueError('Список снимков изменился. Выбери снимок заново.')
    checkpoint = 'before-restore-' + datetime.now().strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:6]
    for disk in disks:
        if cancelled:
            raise ValueError('Отменено до начала отката; текущее содержимое дисков сохранено.')
        try:
            run(exe, ['snapshot', '-c', checkpoint, str(disk)])
        except (OSError, ValueError) as error:
            raise ValueError('Не удалось создать страховочный снимок {}. Откат не начат. {}'.format(checkpoint, error)) from error
    try:
        for disk, snapshot_id in zip(disks, selected['ids']):
            if cancelled:
                raise ValueError('Запрошена отмена отката.')
            run(exe, ['snapshot', '-a', snapshot_id, str(disk)])
        if cancelled:
            raise ValueError('Запрошена отмена отката.')
    except (OSError, ValueError) as error:
        failures = []
        for disk in disks:
            try:
                run(exe, ['snapshot', '-a', checkpoint, str(disk)])
            except (OSError, ValueError) as rollback_error:
                failures.append('{}: {}'.format(disk, rollback_error))
        if failures:
            raise ValueError('Откат прерван; восстановление прежнего состояния не завершено. VM не запускается. Страховочный снимок: {}. {}'.format(checkpoint, '; '.join(failures))) from error
        raise ValueError('Откат отменён, прежнее состояние всех дисков восстановлено. ' + str(error)) from error
    return checkpoint

def choose_and_apply(root, name, data, exe, query_status, processes=None, runtime_refs=None):
    config_path = root / (name + '.json')
    config_before = config_path.read_bytes()
    refs = media(data)
    assert_stopped(name, refs, query_status, processes or qemu_processes)
    disks = configured_disks(data)
    assert_private(root, name, disks, exe, runtime_refs=runtime_refs)
    choices = catalog(disks, exe)
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
    checkpoint = apply_snapshot(disks, selected, exe)
    notice('Снимок применён. Предыдущее состояние дисков сохранено: ' + checkpoint, 'success')
    return True
