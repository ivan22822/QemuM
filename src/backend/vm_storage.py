"""Explicit file deletion plans and named parameter templates; never delete folders recursively."""
from contextlib import contextmanager, ExitStack
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess

def file_key(path):
    return os.path.normcase(str(Path(path).resolve()))

def signature(path):
    stat = path.stat()
    return (stat.st_dev, stat.st_ino, stat.st_size, stat.st_mtime_ns)

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def local_path(value):
    path = Path(value)
    if not path.is_absolute():
        raise ValueError('Для удаления нужен абсолютный путь к файлу: ' + value)
    return path

def option_file(value):
    if value.startswith('{'):
        data = json.loads(value)
        result = []
        def visit(item):
            if isinstance(item, dict):
                for key, child in item.items():
                    if key == 'filename' and isinstance(child, str):
                        result.append(child)
                    elif isinstance(child, (dict, list)):
                        visit(child)
            elif isinstance(item, list):
                for child in item:
                    visit(child)
        visit(data)
        return result
    parts = value.replace(',,', '\x00').split(',')
    return [part.split('=', 1)[1].replace('\x00', ',') for part in parts if part.startswith(('file=', 'file.filename='))]

def media(data):
    if not isinstance(data, dict):
        raise ValueError('Конфигурация VM должна быть объектом JSON.')
    result = []
    disk = data.get('disk_path', data.get('path', '') if data.get('kind') == 'disk' else '')
    iso = data.get('iso_path', data.get('path', '') if data.get('kind') == 'iso' else '')
    if disk:
        result.append((local_path(disk), 'disk'))
    if iso:
        result.append((local_path(iso), 'iso'))
    extras = data.get('extra_args', [])
    for index, flag in enumerate(extras):
        if flag in ('-drive', '-blockdev'):
            if index + 1 >= len(extras):
                raise ValueError('Неполный параметр ' + flag)
            result.extend((local_path(path), 'disk') for path in option_file(extras[index + 1]))
        elif flag in ('-cdrom', '-hda', '-hdb', '-hdc', '-hdd', '-fda', '-fdb', '-sd', '-pflash', '-bios', '-kernel', '-initrd', '-dtb'):
            if index + 1 >= len(extras):
                raise ValueError('Неполный параметр ' + flag)
            result.append((local_path(extras[index + 1]), 'iso' if flag == '-cdrom' else 'disk' if flag in ('-hda', '-hdb', '-hdc', '-hdd', '-fda', '-fdb', '-sd') else 'resource'))
    unique = {}
    for path, kind in result:
        unique.setdefault(file_key(path), (path, kind))
    return list(unique.values())

def backing_chain(path, image_exe):
    if not path.is_file() or path.suffix.lower() not in ('.qcow2', '.qcow', '.vdi', '.vmdk', '.vhdx', '.vhd'):
        return []
    result = subprocess.run([image_exe, 'info', '--backing-chain', '--output=json', str(path)],
                            capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=20, shell=False,
                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    if result.returncode:
        raise ValueError('Не удалось проверить зависимости диска. Закрой использующую его VM: ' + str(path))
    chain = json.loads(result.stdout)
    if not isinstance(chain, list):
        raise ValueError('Не удалось прочитать цепочку диска: ' + str(path))
    return [Path(item['filename']) if Path(item['filename']).is_absolute() else path.parent / item['filename'] for item in chain]

def qemu_processes():
    if os.name != 'nt':
        raise ValueError('Проверка запущенных VM поддерживается только в Windows.')
    powershell = shutil.which('powershell.exe') or shutil.which('pwsh.exe')
    if not powershell:
        raise ValueError('Не найден PowerShell для проверки запущенных VM.')
    script = "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; $items=@(Get-CimInstance Win32_Process -Filter \"Name LIKE 'qemu-system-%'\" | Select-Object ProcessId,CommandLine); ConvertTo-Json -InputObject $items -Compress"
    try:
        result = subprocess.run([powershell, '-NoProfile', '-NonInteractive', '-Command', script], capture_output=True,
                                text=True, encoding='utf-8', errors='replace', timeout=15, shell=False,
                                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    except subprocess.TimeoutExpired as error:
        raise ValueError('Проверка процессов QEMU не ответила. Удаление отменено; попробуй ещё раз.') from error
    if result.returncode:
        raise ValueError('Не удалось проверить процессы QEMU. Удаление отменено.')
    records = json.loads(result.stdout or '[]')
    if not isinstance(records, list) or any(not item.get('CommandLine') for item in records):
        raise ValueError('Не видны параметры одного из процессов QEMU. Удаление отменено.')
    return records

def assert_stopped(name, references, query_status, processes):
    try:
        query_status()
    except (ConnectionRefusedError, TimeoutError):
        # A Windows loopback connection may time out even with no QEMU listener.
        # Do not infer stopped from this: require the complete process inventory
        # below, then exclusive file handles again before actual deletion.
        pass
    except OSError as error:
        raise ValueError('Состояние QMP неизвестно; удаление отменено: ' + str(error)) from error
    else:
        raise ValueError('Сначала выключи VM и закрой её панель: ' + name)
    records = processes()
    if not isinstance(records, list):
        raise ValueError('Не удалось проверить все процессы QEMU. Удаление отменено.')
    for process in records:
        raw = process.get('CommandLine') if isinstance(process, dict) else None
        if not isinstance(raw, str) or not raw.strip():
            raise ValueError('Не видны параметры одного из процессов QEMU. Удаление отменено.')
        command = raw.replace('/', '\\').replace(',,', ',').casefold()
        match = re.search(r'(?:^|\s)-name\s+(?:"([^"]*)"|(\S+))', command)
        guest = (match.group(1) or match.group(2)).split(',')[0] if match else None
        if guest and guest.startswith('guest='):
            guest = guest[6:]
        if guest == name.casefold() or any(str(path).replace('/', '\\').casefold() in command for path, kind in references):
            raise ValueError('Файлы VM используются процессом QEMU. Сначала выключи VM: ' + name)
        if not guest:
            raise ValueError('Не удалось определить имя одного из процессов QEMU. Удаление отменено.')

def safe_file(path):
    path = Path(path)
    # Refuse symlinks, junctions and linked ancestor directories.
    if os.path.normcase(os.path.abspath(path)) != os.path.normcase(str(path.resolve())):
        raise ValueError('Удаление через ссылку или перенаправленный каталог запрещено: ' + str(path))
    for part in (path,) + tuple(path.parents):
        if part.exists() and getattr(os.lstat(part), 'st_file_attributes', 0) & 0x400:
            raise ValueError('Файл или каталог является ссылкой: ' + str(path))
    if path.exists() and not path.is_file():
        raise ValueError('Ожидался файл, а не каталог: ' + str(path))

def parameter_name(value):
    name = value.strip()
    if not name or len(name) > 80 or re.search(r'[<>:"/\\|?*\x00-\x1f]', name) or name in ('.', '..') or name.endswith(('.', ' ')):
        raise ValueError('Имя копии: 1–80 символов, без / \\ : * ? " < > | и точки в конце.')
    if re.fullmatch(r'(con|prn|aux|nul|com[0-9]|lpt[0-9])', name.split('.')[0], re.I):
        raise ValueError('Это имя зарезервировано Windows.')
    return name

def templates(folder):
    result = []
    for path in sorted(folder.glob('*.json')):
        try:
            item = json.loads(path.read_text(encoding='utf-8-sig'))
            if item.get('schema_version') == 1 and isinstance(item.get('parameters'), dict):
                result.append((path, item))
        except (OSError, ValueError, AttributeError):
            continue
    return result

def deletion_plan(root, name, data, image_exe, query_status, processes=None, inspect_chain=None):
    processes = processes or qemu_processes
    inspect_chain = inspect_chain or backing_chain
    root = Path(root).resolve()
    references = media(data)
    assert_stopped(name, references, query_status, processes)
    config_files = sorted(set(root.glob('*.json')) | set(root.glob('*.json.disabled')))
    peer_refs = set()
    config_hashes = {str(path): sha(path) for path in config_files}
    for config in config_files:
        if config == root / (name + '.json'):
            continue
        other = json.loads(config.read_text(encoding='utf-8-sig'))
        for path, kind in media(other):
            peer_refs.add(file_key(path))
            if kind == 'disk':
                peer_refs.update(file_key(item) for item in inspect_chain(path, image_exe))
    # A backing image is not owned by the top disk: never erase it implicitly.
    own_backings = set()
    for path, kind in references:
        if kind == 'disk':
            own_backings.update(file_key(item) for item in inspect_chain(path, image_exe) if file_key(item) != file_key(path))
    candidates, kept, missing = [], [], []
    protected = [root.parent / 'parameter-backups', root.parent / 'backups']
    for path, kind in references:
        if not path.exists():
            missing.append(str(path))
            continue
        key = file_key(path)
        if kind in ('iso', 'resource') or path.suffix.lower() == '.iso':
            kept.append((str(path), 'ISO или общий ресурс'))
        elif key in peer_refs or key in own_backings:
            kept.append((str(path), 'используется другой VM или является базовым образом'))
        elif any(folder.resolve() == path.resolve() or folder.resolve() in path.resolve().parents for folder in protected):
            raise ValueError('Нельзя удалять резервные копии менеджера: ' + str(path))
        else:
            if path.suffix.lower() not in ('.qcow2', '.qcow', '.img', '.raw', '.vmdk', '.vhd', '.vhdx', '.vdi'):
                raise ValueError('Неизвестный тип диска; автоматическое удаление запрещено: ' + str(path))
            candidates.append(path)
    for path in root.glob(name + '.json.*'):
        if path.is_file():
            candidates.append(path)
    logs = root.parent / 'logs'
    pattern = re.compile(re.escape(name) + r'-\d{8}-\d{6}-[0-9a-f]{6}\.log')
    if logs.exists():
        candidates.extend(path for path in logs.iterdir() if path.is_file() and pattern.fullmatch(path.name))
    candidates.append(root / (name + '.json'))
    unique = {}
    for path in candidates:
        safe_file(path)
        if path.exists():
            unique.setdefault(file_key(path), path)
    files = list(unique.values())
    config = root / (name + '.json')
    # The active registration is removed last, after the associated files.
    files.sort(key=lambda path: path == config)
    return {'name': name, 'root': root, 'parameters': data, 'references': references,
            'files': files, 'kept': kept, 'missing': missing, 'signatures': {str(path): signature(path) for path in files},
            'config_hashes': config_hashes}

@contextmanager
def exclusive_file(path):
    if os.name != 'nt':
        yield
        return
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    # DELETE + GENERIC_READ; share only deletion, excluding concurrent disk use.
    handle = kernel.CreateFileW(str(path), 0x00010000 | 0x80000000, 0x00000004, None, 3, 0x80, None)
    if handle == wintypes.HANDLE(-1).value:
        raise ValueError('Файл используется или недоступен. Закрой VM и её панель: ' + str(path))
    try:
        yield
    finally:
        kernel.CloseHandle(handle)

def delete_with_template(plan, backup_label, query_status, processes=None):
    processes = processes or qemu_processes
    name, root = plan['name'], plan['root']
    backup_label = parameter_name(backup_label)
    folder = root.parent / 'parameter-backups'
    backup_path = folder / (backup_label + '.json')
    safe_file(backup_path)
    if backup_path.exists():
        raise ValueError('Копия с таким именем уже есть. Выбери другое имя.')
    assert_stopped(name, plan['references'], query_status, processes)
    configs_now = sorted(set(root.glob('*.json')) | set(root.glob('*.json.disabled')))
    if {str(path): sha(path) for path in configs_now} != plan['config_hashes']:
        raise ValueError('Параметры VM изменились после просмотра списка. Повтори удаление.')
    deleted = []
    with ExitStack() as locks:
        for path in plan['files']:
            safe_file(path)
            locks.enter_context(exclusive_file(path))
            if signature(path) != plan['signatures'][str(path)]:
                raise ValueError('Файл изменился после просмотра списка: ' + str(path))
        folder.mkdir(exist_ok=True)
        payload = {'schema_version': 1, 'backup_name': backup_label, 'original_vm_name': name,
                   'created_utc': datetime.now(timezone.utc).isoformat(), 'parameters': plan['parameters'],
                   'deleted_files': [str(path) for path in plan['files']], 'preserved_files': plan['kept'],
                   'missing_files': plan.get('missing', []),
                   'contains_disk_data': False}
        encoded = (json.dumps(payload, ensure_ascii=False, indent=2) + '\n').encode('utf-8')
        with backup_path.open('xb') as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        if backup_path.read_bytes() != encoded:
            raise ValueError('Проверка копии параметров не прошла. Файлы VM не удалены.')
        try:
            for path in plan['files']:
                path.unlink()
                deleted.append(str(path))
        except OSError as error:
            raise ValueError('Удаление прервано. Копия параметров: {}. Уже удалено {} файлов. Причина: {}'.format(backup_path, len(deleted), error)) from error
    # Remove only directories that are empty, never their unknown contents.
    for parent in {path.parent for path in plan['files'] if path.parent not in (root, root.parent, root.parent / 'logs')}:
        try:
            parent.rmdir()
        except OSError:
            pass
    return backup_path
