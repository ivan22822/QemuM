"""QEMU menu for Windows. Python 3.8+, standard library only."""
import hashlib
import errno
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import sys
import time
import tempfile
from vm_runtime import vm_lock
from vm_disk import disk_format, new_disk_format
from vm_network import network_args, validate as validate_network
from vm_ui import ask, choose_machine, notice, options, present
from vm_profiles import accelerator_args, choose, initial_accelerator, profile, validate as validate_profile
from vm_style import assign_color, clean, color_for, enable_vt, library_frame, paint

ROOT = Path(__file__).resolve().parent / 'machines'
RESERVED = {'add', 'list', 'show', 'edit', 'help', 'iso', 'disk', 'status', 'console', 'remove', 'delete', 'restore', 'doctor', 'con', 'prn', 'aux', 'nul'}

def qmp_port(name, data):
    if not isinstance(data, dict):
        raise ValueError('VM configuration must be a JSON object: ' + name)
    # Stable across restarts and additions/removals of other VM configurations.
    default = 49152 + int.from_bytes(hashlib.sha256(name.encode('utf-8')).digest()[:2], 'big') % 16384
    port = data.get('qmp_port', default)
    if type(port) is not int or not 1024 <= port <= 65535:
        raise ValueError('qmp_port must be an integer between 1024 and 65535.')
    return port

def checked_qmp_port(name, data):
    port = qmp_port(name, data)
    for other in entries():
        if other == name:
            continue
        with config_path(other).open(encoding='utf-8-sig') as stream:
            other_data = json.load(stream)
        if qmp_port(other, other_data) == port:
            raise ValueError('QMP port {} conflicts with {}. Set a different qmp_port in the JSON configuration.'.format(port, other))
    return port

def qmp_bind_error(port):
    """Probe the actual Windows bind rules, including excluded TCP ranges."""
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            if hasattr(socket, 'SO_EXCLUSIVEADDRUSE'):
                probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            probe.bind(('127.0.0.1', port))
    except OSError as error:
        return error
    return None

def prepare_qmp_port(name, data, original, stopped):
    """Called under the VM lock, before spawning QEMU; never move a running VM."""
    path = config_path(name)
    if path.read_bytes() != original:
        raise ValueError('Параметры изменились во время подготовки запуска.')
    old = checked_qmp_port(name, data)
    failure = qmp_bind_error(old)
    if failure is None:
        return data, original, old, None
    if failure.errno not in (errno.EACCES, errno.EADDRINUSE, 10013, 10048) and getattr(failure, 'winerror', None) not in (10013, 10048):
        raise ValueError('Не удалось проверить QMP-порт {}: {}'.format(old, failure))
    # A failed bind alone must never cause changes to a running VM's endpoint.
    stopped()
    used = {qmp_port(other, json.loads(config_path(other).read_text(encoding='utf-8-sig')))
            for other in entries() if other != name}
    seed = int.from_bytes(hashlib.sha256(name.encode('utf-8')).digest()[:2], 'big')
    for attempt in range(128):
        port = 20000 + (seed + attempt) % 25000
        if port == old or port in used:
            continue
        error = qmp_bind_error(port)
        if error is not None:
            if error.errno not in (errno.EACCES, errno.EADDRINUSE, 10013, 10048) and getattr(error, 'winerror', None) not in (10013, 10048):
                raise ValueError('Не удалось проверить локальные порты QMP: ' + str(error))
            continue
        from vm_editor import save_parameters
        saved = json.loads(original.decode('utf-8-sig'))
        saved['qmp_port'] = port
        backup = save_parameters(path, original, saved, lambda candidate: checked_qmp_port(name, candidate), keep_legacy=True)
        return dict(data, qmp_port=port), path.read_bytes(), port, backup
    raise ValueError('Не найден доступный локальный QMP-порт. VM не запущена; параметры не изменены.')

def qmp_command(name, port, command, arguments=None, timeout=3.0):
    """Negotiate QMP and verify the VM identity before sending a command."""
    deadline = time.monotonic() + timeout
    pending = bytearray()
    with socket.create_connection(('127.0.0.1', port), timeout=timeout) as connection:
        def remaining():
            seconds = deadline - time.monotonic()
            if seconds <= 0:
                raise TimeoutError('QMP response timed out.')
            connection.settimeout(seconds)

        def receive():
            while True:
                remaining()
                newline = pending.find(b'\n')
                if newline >= 0:
                    line = bytes(pending[:newline])
                    del pending[:newline + 1]
                    if not line.strip():
                        continue
                    message = json.loads(line.decode('utf-8'))
                    if not isinstance(message, dict):
                        raise ValueError('QMP returned a non-object message.')
                    return message
                if len(pending) >= 1048576:
                    raise ValueError('QMP message is too large.')
                chunk = connection.recv(min(4096, 1048576 - len(pending)))
                if not chunk:
                    raise ValueError('QMP connection closed before its response.')
                pending.extend(chunk)

        def execute(command, arguments=None):
            remaining()
            request = {'execute': command, 'id': command}
            if arguments is not None:
                request['arguments'] = arguments
            connection.sendall((json.dumps(request) + '\r\n').encode('utf-8'))
            while True:
                reply = receive()
                if 'event' in reply:
                    continue
                if 'error' in reply and 'id' not in reply:
                    raise ValueError('QMP protocol error: ' + str(reply['error']))
                if reply.get('id') != command:
                    continue
                if 'error' in reply:
                    raise ValueError('QMP {} failed: {}'.format(command, reply['error']))
                if 'return' not in reply:
                    raise ValueError('Missing QMP command result.')
                return reply['return']

        greeting = receive()
        if not isinstance(greeting.get('QMP'), dict):
            raise ValueError('The selected port did not return a QMP greeting.')
        execute('qmp_capabilities')
        identity = execute('query-name')
        if not isinstance(identity, dict) or identity.get('name') != name:
            raise ValueError('QMP VM name does not match {}: {}'.format(name, identity))
        return execute(command, arguments)

def query_qmp_status(name, port, timeout=3.0):
    result = qmp_command(name, port, 'query-status', timeout=timeout)
    if not isinstance(result, dict) or not isinstance(result.get('status'), str) or type(result.get('running')) is not bool:
        raise ValueError('Invalid QMP status response.')
    return result

def console(name):
    with vm_lock(ROOT, name):
        return _console(name)

def _console(name):
    from vm_console import VMConsole
    data = load(name)
    port = checked_qmp_port(name, data)
    query_qmp_status(name, port)
    from vm_profiles import runtime_accelerator
    try:
        data['_runtime_accelerator'] = runtime_accelerator(port) or 'unknown'
    except (OSError, ValueError, subprocess.SubprocessError):
        data['_runtime_accelerator'] = 'unknown'
    return VMConsole(name, data, port, qmp_command, query_qmp_status).run()

def status(name):
    data = load(name)
    present('Состояние виртуальной машины', name=name, data=data)
    port = checked_qmp_port(name, data)
    try:
        state = query_qmp_status(name, port)
    except (OSError, ValueError) as error:
        notice('QMP недоступен: ' + str(error), 'warning')
        notice('VM может быть выключена, запущена без QMP или использовать другой порт. '
               'Для подключения запусти её через менеджер.')
        return 1
    labels = {'running': 'Работает', 'paused': 'Пауза', 'prelaunch': 'Ожидает запуска', 'shutdown': 'Завершается'}
    notice(labels.get(state['status'], state['status']), 'success')
    notice('{} МБ RAM · {} CPU · QMP 127.0.0.1:{}'.format(data['ram_mb'], data['cores'], port))
    return 0


def valid_name(name):
    return bool(re.fullmatch(r'[a-z][a-z0-9_-]{0,39}', name)) and name not in RESERVED and not re.fullmatch(r'(com|lpt)[0-9]', name)

def config_path(name):
    if not valid_name(name):
        raise ValueError('Name: lowercase Latin letters, digits, - or _; start with a letter.')
    return ROOT / (name + '.json')

def save_new(name, data):
    data = assign_color(ROOT, name, data)
    with config_path(name).open('x', encoding='utf-8') as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2)
        stream.write('\n')

def ensure_colors():
    for name in entries():
        target = config_path(name)
        data = json.loads(target.read_text(encoding='utf-8-sig'))
        colored = assign_color(ROOT, name, data)
        if colored != data:
            temporary = target.with_suffix('.json.color.tmp')
            with temporary.open('x', encoding='utf-8') as stream:
                json.dump(colored, stream, ensure_ascii=False, indent=2)
                stream.write('\n')
            os.replace(temporary, target)

def initialize():
    ROOT.mkdir(parents=True, exist_ok=True)
    marker = ROOT / '.initialized'
    if marker.exists():
        return
    marker.touch()

def migrate_resolution():
    """Add the requested default to old configs, preserving all other values."""
    for target in ROOT.glob('*.json'):
        try:
            with target.open(encoding='utf-8-sig') as stream:
                data = json.load(stream)
            if not isinstance(data, dict):
                raise ValueError('configuration must be a JSON object')
            if 'screen_width' in data and 'screen_height' in data:
                continue
            backup = target.with_suffix('.json.pre-resolution.bak')
            if not backup.exists():
                shutil.copy2(target, backup)
            data.setdefault('screen_width', 1920)
            data.setdefault('screen_height', 1080)
            temporary = target.with_suffix('.json.tmp')
            with temporary.open('w', encoding='utf-8') as stream:
                json.dump(data, stream, ensure_ascii=False, indent=2)
                stream.write('\n')
            os.replace(temporary, target)
        except (OSError, ValueError) as error:
            notice('Не удалось обновить {}: {}'.format(target.name, error), 'error', file=sys.stderr)

def resolution(data):
    width = data.get('screen_width', 1920)
    height = data.get('screen_height', 1080)
    if type(width) is not int or type(height) is not int or not (320 <= width <= 4095 and 200 <= height <= 4095):
        raise ValueError('Resolution: width 320..4095, height 200..4095; integer pixels.')
    return width, height

def video_args(data):
    width, height = resolution(data)
    vga = data.get('vga', 'std')
    if vga in ('std', 'virtio'):
        device = 'VGA' if vga == 'std' else 'virtio-vga'
        spec = '{},edid=on,xres={},yres={}'.format(device, width, height)
        if vga == 'std':
            spec += ',vgamem_mb=64'
        return ['-vga', 'none', '-device', spec]
    return ['-vga', vga]

def load(name):
    with config_path(name).open(encoding='utf-8-sig') as stream:
        data = json.load(stream)
    if not isinstance(data, dict):
        raise ValueError('Конфигурация должна быть объектом JSON.')
    profile(data)
    # Read old configurations without modifying their files.
    if 'disk_path' not in data and 'iso_path' not in data:
        if data.get('kind') not in ('disk', 'iso'):
            raise ValueError('kind must be disk or iso.')
        data['disk_path'] = data.get('path', '') if data['kind'] == 'disk' else ''
        data['iso_path'] = data.get('path', '') if data['kind'] == 'iso' else ''
    for key in ('ram_mb', 'cores'):
        if type(data.get(key)) is not int or data[key] < 1:
            raise ValueError(key + ' must be a positive integer.')
    for key in ('disk_path', 'iso_path'):
        data.setdefault(key, '')
        if not isinstance(data[key], str):
            raise ValueError(key + ' must be a string.')
    if not data['disk_path'] and not data['iso_path']:
        raise ValueError('Missing disk/ISO path.')
    if data.get('vga', 'std') not in ('std', 'virtio', 'qxl', 'cirrus', 'none'):
        raise ValueError('Unsupported vga setting.')
    resolution(data)
    extras = data.get('extra_args', [])
    if not isinstance(extras, list) or any(not isinstance(item, str) for item in extras):
        raise ValueError('extra_args must be a JSON array of strings.')
    # Import the manual -cdrom/-boot workaround from v1 into managed settings.
    cleaned = []
    index = 0
    while index < len(extras):
        if extras[index] in ('-cdrom', '-boot'):
            if index + 1 >= len(extras):
                raise ValueError('Missing value after ' + extras[index])
            if extras[index] == '-cdrom' and not data['iso_path']:
                data['iso_path'] = extras[index + 1]
            index += 2
        else:
            cleaned.append(extras[index])
            index += 1
    data['extra_args'] = cleaned
    validate_network(data)
    return data

def validate_extra_args(data):
    validate_profile(data)
    managed = ('-qmp', '-qmp-pretty', '-name', '-daemonize', '-pidfile')
    conflicts = [arg.split('=', 1)[0] for arg in data.get('extra_args', []) if arg.split('=', 1)[0] in managed]
    if conflicts:
        raise ValueError('Эти параметры задаются менеджером и недоступны в Extra args: ' + ', '.join(dict.fromkeys(conflicts)))

def build_args(name, data, boot=None, accelerator=None):
    validate_extra_args(data)
    validate_profile(data)
    accelerator = accelerator or initial_accelerator(data)
    boot = boot or ('disk' if data['disk_path'] else 'iso')
    if not data.get(boot + '_path'):
        raise ValueError('No ' + boot + ' configured.')
    port = checked_qmp_port(name, data)
    args = ['-name', name] + accelerator_args(accelerator, data.get('launch_profile_settings')) + ['-machine', 'q35',
            '-smp', str(data['cores']), '-m', str(data['ram_mb']),
            '-display', 'sdl,gl=off',
            '-qmp', 'tcp:127.0.0.1:{},server=on,wait=off'.format(port)]
    args += video_args(data)
    if data['disk_path']:
        # QEMU uses commas as option separators; double literal commas.
        disk = data['disk_path'].replace(',', ',,')
        args += ['-drive', 'file=' + disk + ',format=' + disk_format(data) + ',if=virtio']
    args += network_args(data)
    if boot == 'iso':
        args += ['-cdrom', data['iso_path'], '-boot', 'order=c,once=d' if data['disk_path'] else 'order=d']
    else:
        args += ['-boot', 'order=c']
    return args + data.get('extra_args', [])

def qemu_executable():
    exe = shutil.which('qemu-system-x86_64.exe') or shutil.which('qemu-system-x86_64')
    if not exe:
        fallback = Path(os.environ.get('ProgramFiles', r'C:\Program Files')) / 'qemu' / 'qemu-system-x86_64.exe'
        if fallback.is_file():
            exe = str(fallback)
    if not exe:
        raise ValueError('QEMU not found. Add its installation folder to PATH.')
    return exe

def launch(name, boot=None, iso_override=None):
    with vm_lock(ROOT, name):
        return _launch(name, boot, iso_override)

def _launch(name, boot=None, iso_override=None, prepared_data=None):
    data = load(name) if prepared_data is None else prepared_data
    original = config_path(name).read_bytes()
    if data.get('vga', 'std') not in ('std', 'virtio'):
        notice('Видеоадаптер не получает предпочитаемое разрешение. Выбери его в гостевой ОС или используй std/virtio.', 'warning')
    if iso_override:
        data['iso_path'] = str(expand_path(iso_override))
    if boot is None:
        if data['disk_path'] and data['iso_path']:
            selected = options('Источник загрузки', [('1', 'Виртуальный диск'), ('2', 'Установочный / Live ISO')],
                               default='1', name=name, data=data)
            if selected is None:
                return 0
            boot = 'disk' if selected == '1' else 'iso'
        else:
            boot = 'disk' if data['disk_path'] else 'iso'
    required = ([data['disk_path']] if data['disk_path'] else [])
    if boot == 'iso':
        required.append(data['iso_path'])
    for path in required:
        if not path or not Path(path).is_file():
            raise ValueError('File not found: ' + path + '\nFix it with: vm edit ' + name)
    from vm_console import VMConsole
    port = checked_qmp_port(name, data)
    # Reuse the running VM instead of opening its disk a second time.
    try:
        query_qmp_status(name, port, timeout=1.0)
    except OSError:
        pass
    else:
        from vm_profiles import runtime_accelerator
        try:
            data['_runtime_accelerator'] = runtime_accelerator(port) or 'unknown'
        except (OSError, ValueError, subprocess.SubprocessError):
            data['_runtime_accelerator'] = 'unknown'
        return VMConsole(name, data, port, qmp_command, query_qmp_status).run()
    exe = qemu_executable()
    present('Запуск виртуальной машины', name=name, data=data)
    notice('Проверяю профиль запуска…')
    accelerator, checked = choose(exe, data)
    notice('Ускорение: ' + accelerator.upper(), 'success')
    if profile(data) == 'auto' and accelerator == 'tcg':
        notice(checked['detail'], 'warning')
    from vm_storage import assert_stopped, media, qemu_processes
    old_data = data
    data, original, port, backup = prepare_qmp_port(name, data, original,
        lambda: assert_stopped(name, media(old_data), lambda: query_qmp_status(name, checked_qmp_port(name, old_data)), qemu_processes))
    if backup:
        notice('Недоступный QMP-порт заменён на {}. Копия: {}'.format(port, backup))
    data['_runtime_accelerator'] = accelerator
    command = [exe] + build_args(name, data, boot, accelerator=accelerator)
    return VMConsole(name, data, port, qmp_command, query_qmp_status).run(command)

def expand_path(value):
    return Path(os.path.expandvars(value.strip().strip('"'))).expanduser().resolve()

def ask_file(suffix):
    suffixes = (suffix,) if isinstance(suffix, str) else suffix
    while True:
        path = expand_path(ask('Путь к существующему файлу ' + '/'.join(suffixes) + ':'))
        if path.is_file() and path.suffix.lower() in suffixes:
            return str(path)
        notice('Укажи существующий файл ' + '/'.join(suffixes) + '.', 'warning')


def new_disk():
    present('Создание виртуального диска', 'Укажи новый файл qcow2 или VDI и размер диска.')
    while True:
        path = expand_path(ask('Полный путь нового диска .qcow2 или .vdi:'))
        if path.exists():
            notice('Файл уже существует. Укажи путь нового диска.', 'warning')
        elif path.suffix.lower() not in ('.qcow2', '.vdi'):
            notice('Имя файла должно заканчиваться на .qcow2 или .vdi.', 'warning')
        else:
            break
    size = ask_number('Размер нового диска в ГБ', 60, 65536)
    exe = shutil.which('qemu-img.exe') or shutil.which('qemu-img')
    if not exe:
        exe_path = Path(qemu_executable()).parent / 'qemu-img.exe'
        if not exe_path.is_file():
            raise ValueError('qemu-img not found. Add QEMU to PATH.')
        exe = str(exe_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    # Exclusive creation rejects existing disks, including a concurrent creation.
    with path.open('xb'):
        pass
    try:
        subprocess.run([exe, 'create', '-f', new_disk_format(path), str(path), str(size) + 'G'], check=True, shell=False,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    except BaseException as error:
        path.unlink()
        if isinstance(error, subprocess.CalledProcessError):
            message = (error.stderr or b'').decode('utf-8', errors='replace')
            raise ValueError('Не удалось создать диск: ' + message) from error
        raise
    notice('Диск создан: ' + str(path), 'success')
    return str(path)

def update_config(name, data):
    data = assign_color(ROOT, name, data)
    target = config_path(name)
    backup = target.with_suffix('.json.bak')
    if not backup.exists():
        shutil.copy2(target, backup)
    data.pop('kind', None)
    data.pop('path', None)
    temporary = target.with_suffix('.json.tmp')
    with temporary.open('w', encoding='utf-8') as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2)
        stream.write('\n')
    os.replace(temporary, target)

def attach(name, kind):
    data = load(name)
    present('Подключение ISO' if kind == 'iso' else 'Подключение диска', name=name, data=data)
    if kind == 'iso':
        data['iso_path'] = ask_file('.iso')
    else:
        if data['disk_path']:
            raise ValueError('A disk is already configured. Use vm edit ' + name + ' to change it.')
        choice = int(options('Выбор диска', [('1', 'Подключить существующий диск'), ('2', 'Создать новый диск')],
                             default='2', cancel=False, name=name, data=data))
        data['disk_path'] = ask_file(('.qcow2', '.vdi')) if choice == 1 else new_disk()
        data['disk_format'] = disk_format({'disk_path': data['disk_path']})
    update_config(name, data)
    notice('Параметры сохранены. Запуск: vm ' + name, 'success')
    return 0

def ask_number(prompt, default, maximum):
    while True:
        value = ask('{} [{}]:'.format(prompt, default)).strip() or str(default)
        if value.isdecimal() and 1 <= int(value) <= maximum:
            return int(value)
        notice('Введи целое число от 1 до {}.'.format(maximum), 'warning')


def ask_resolution():
    choice = options('Разрешение экрана', [('1', 'Full HD · 1920 × 1080'), ('2', 'QHD · 2560 × 1440'),
                                          ('3', 'Свои размеры')], default='1', cancel=False)
    if choice == '1':
        return 1920, 1080
    if choice == '2':
        return 2560, 1440
    while True:
        width = ask_number('Ширина в пикселях · 320–4095', 1920, 4095)
        height = ask_number('Высота в пикселях · 200–4095', 1080, 4095)
        if width >= 320 and height >= 200:
            return width, height
        notice('Минимальное разрешение: 320 × 200.', 'warning')


def choose_template(required=False):
    from vm_storage import templates
    saved = templates(ROOT.parent / 'parameter-backups')
    if not saved:
        if required:
            present('Сохранённые параметры', 'Копий пока нет. Они создаются перед удалением VM.', options=[('Enter', 'Назад')])
        return None
    sections = []
    for number, (path, item) in enumerate(saved, 1):
        data = item['parameters']
        sections.append(('{} · {}'.format(number, item.get('backup_name', path.stem)),
                         ['{} МБ RAM · {} CPU'.format(data.get('ram_mb', '?'), data.get('cores', '?')),
                          'Исходная VM: ' + item.get('original_vm_name', 'не указана')]))
    present('Сохранённые параметры', 'Выбери копию для создания VM. Диски и ОС в неё не входят.',
            sections=sections, options=[('0', 'Назад' if required else 'Новые настройки')])
    while True:
        choice = ask('Номер копии [0]:').strip() or '0'
        if choice == '0':
            return None
        if choice.isdecimal() and 1 <= int(choice) <= len(saved):
            return saved[int(choice) - 1][1]
        notice('Выбери номер из списка.', 'warning')


def add(template=None, offer_template=True):
    if template is None and offer_template:
        template = choose_template()
    present('Добавление виртуальной машины', 'Имя → диск / ISO → ресурсы → сохранение')
    original = template.get('original_vm_name', '') if template else ''
    default_name = original if valid_name(original) and not config_path(original).exists() else ''
    while True:
        name = (ask('Короткое имя VM{}:'.format(' [' + default_name + ']' if default_name else '')).strip() or default_name).lower()
        if not valid_name(name):
            notice('Используй латинские буквы, цифры, - и _. Начни с буквы; служебные имена заняты.', 'warning')
        elif config_path(name).exists():
            notice('Это имя уже занято. Выбери другое.', 'warning')
        else:
            break
    saved = dict(template['parameters']) if template else {}
    if not isinstance(saved.get('extra_args', []), list) or any(not isinstance(arg, str) for arg in saved.get('extra_args', [])):
        raise ValueError('В копии неверный список extra_args.')
    if saved.get('vga', 'std') not in ('std', 'virtio', 'qxl', 'cirrus', 'none'):
        raise ValueError('В копии неизвестный видеоадаптер.')
    label_default = saved.get('label', name)
    label = ask('Название [{}]:'.format(label_default)).strip() or label_default
    mode = int(options('Добавление VM · источник', [('1', 'Существующий диск qcow2 / VDI'), ('2', 'Только Live ISO'),
                       ('3', 'Установка ISO на новый диск')], default='3', cancel=False, name=name, data={'label': label}))
    disk_path = ask_file(('.qcow2', '.vdi')) if mode == 1 else ''
    iso_path = ask_file('.iso') if mode in (2, 3) else ''
    if template:
        ram_mb, cores = saved.get('ram_mb'), saved.get('cores')
        if type(ram_mb) is not int or ram_mb < 1 or type(cores) is not int or cores < 1:
            raise ValueError('В копии параметров неверные значения RAM или CPU.')
        width, height = resolution(saved)
        notice('Использую параметры «{}»: {} МБ RAM, {} CPU, {} × {}.'.format(template.get('backup_name', ''), ram_mb, cores, width, height))
    else:
        ram_mb = ask_number('Оперативная память в ГБ', 4, 1024) * 1024
        cores = ask_number('Количество ядер CPU', 2, 256)
        width, height = ask_resolution()
    if mode == 3:
        disk_path = new_disk()
    data = saved
    old_disk = data.get('disk_path', data.get('path', '') if data.get('kind') == 'disk' else '')
    old_iso = data.get('iso_path', data.get('path', '') if data.get('kind') == 'iso' else '')
    extras = list(data.get('extra_args', []))
    # Replace references to the former main media, preserving network/device settings.
    for old_path, new_path in ((old_disk, disk_path), (old_iso, iso_path)):
        if old_path and new_path:
            extras = [arg.replace(old_path.replace(',', ',,'), new_path.replace(',', ',,')) for arg in extras]
    if template:
        from vm_storage import media, file_key
        removed = {file_key(path) for key in ('deleted_files', 'missing_files') for path in template.get(key, [])}
        for extra_path, kind in media({'extra_args': extras}):
            if file_key(extra_path) in removed and not extra_path.exists():
                notice('Укажи замену дополнительного файла: ' + str(extra_path), 'warning')
                replacement = ask_file(extra_path.suffix)
                extras = [arg.replace(str(extra_path).replace(',', ',,'), replacement.replace(',', ',,')) for arg in extras]
    for key in ('kind', 'path'):
        data.pop(key, None)
    data.update(label=label, disk_path=disk_path, disk_format=disk_format({'disk_path': disk_path}), iso_path=iso_path, ram_mb=ram_mb,
                cores=cores, vga=saved.get('vga', 'std'), screen_width=width, screen_height=height, extra_args=extras)
    save_new(name, data)
    present('Виртуальная машина добавлена', name=name, data=data,
            sections=[('Ресурсы', ['{} МБ RAM · {} CPU · {} × {}'.format(ram_mb, cores, width, height)]),
                      ('Носители', ['Диск: ' + (disk_path or 'не подключён'), 'ISO: ' + (iso_path or 'не подключён')])])
    notice('VM добавлена. Запуск: vm ' + name, 'success')
    return 0

def entries():
    return sorted(path.stem for path in ROOT.glob('*.json') if valid_name(path.stem))

def manager_settings():
    path = ROOT.parent / 'manager-settings.json'
    if not path.exists():
        return {'launch_mode': 'normal'}
    data = json.loads(path.read_text(encoding='utf-8-sig'))
    if not isinstance(data, dict) or data.get('launch_mode', 'normal') not in ('normal', 'snapshot'):
        raise ValueError('Неверный режим в manager-settings.json.')
    return dict(data, launch_mode=data.get('launch_mode', 'normal'))

def change_launch_mode():
    present('Режим запуска', 'Выбранный режим сохранится между запусками менеджера.',
            options=[('1', 'Простой запуск'), ('2', 'Выбор снимка перед запуском'), ('0', 'Назад / отмена')])
    while True:
        choice = ask('Режим:').strip()
        if choice in ('', '0'):
            return
        if choice in ('1', '2'):
            break
        notice('Выбери 1, 2 или 0.', 'warning')
    settings = manager_settings()
    settings['launch_mode'] = 'normal' if choice == '1' else 'snapshot'
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=ROOT.parent, prefix='settings-', suffix='.tmp', mode='w', encoding='utf-8', delete=False) as stream:
            temporary = Path(stream.name)
            json.dump(settings, stream, ensure_ascii=False, indent=2)
            stream.write('\n')
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, ROOT.parent / 'manager-settings.json')
        temporary = None
    finally:
        if temporary is not None:
            temporary.unlink()

def launch_from_snapshot(name):
    from vm_snapshots import choose_and_apply, image_tool, runtime_image_files
    with vm_lock(ROOT, name):
        data = load(name)
        port = checked_qmp_port(name, data)
        query = lambda: query_qmp_status(name, port, timeout=1.5)
        def peer_refs(other_name, other_data):
            try:
                blocks = qmp_command(other_name, qmp_port(other_name, other_data), 'query-block', timeout=1.5)
            except ConnectionRefusedError:
                return None
            return runtime_image_files(blocks)
        if not choose_and_apply(ROOT, name, data, image_tool(qemu_executable), query, runtime_refs=peer_refs):
            return 0
        return _launch(name, boot='disk', prepared_data=data)

def edit_parameters(name):
    from vm_editor import edit, save_parameters
    with vm_lock(ROOT, name):
        path = config_path(name)
        original = path.read_bytes()
        data = load(name)
        return edit(name, data, lambda updated: save_parameters(
            path, original, updated, lambda candidate: build_args(name, candidate)))


def menu_rows(names):
    rows = []
    for name in names:
        try:
            data = load(name)
        except (OSError, ValueError, TypeError, KeyError) as error:
            data = dict(label=name + ' · ошибка параметров · H — диагностика', _invalid=True)
        rows.append((name, data))
    return rows


def menu():
    from vm_library import LibraryMenu
    ansi = enable_vt()
    with LibraryMenu(query_qmp_status, qmp_port) as library:
        while True:
            names = entries()
            rows = menu_rows(names)
            mode = manager_settings()['launch_mode']
            selection = library.choose(rows, mode, ansi)
            if selection == '0':
                return 0
            if selection in ('h', 'р'):
                from vm_doctor import menu as doctor_menu
                try:
                    doctor_menu(sys.modules[__name__])
                except (OSError, ValueError, KeyboardInterrupt) as error:
                    notice('Диагностика: ' + clean(error), 'error')
                library.acknowledge()
                continue
            if selection in ('m', 'ь'):
                change_launch_mode()
                continue
            if selection in ('a', 'ф'):
                add()
                library.acknowledge()
                continue
            if selection in ('e', 'у'):
                if not names:
                    continue
                chosen = choose_machine(sys.modules[__name__], 'Параметры виртуальной машины:')
                if chosen is None:
                    continue
                try:
                    edit_parameters(chosen)
                except (OSError, ValueError, KeyboardInterrupt) as error:
                    notice('Редактирование отменено: ' + str(error), 'error')
                library.acknowledge()
                continue
            if selection in ('d', 'в'):
                if not names:
                    continue
                chosen = choose_machine(sys.modules[__name__], 'Удаление виртуальной машины')
                if chosen is None:
                    continue
                try:
                    remove_vm(chosen)
                except (OSError, ValueError) as error:
                    notice('Удаление отменено: ' + str(error), 'error')
                library.acknowledge()
                continue
            if selection in ('r', 'к'):
                template = choose_template(required=True)
                if template:
                    add(template=template, offer_template=False)
                    library.acknowledge()
                continue
            if selection.isdecimal() and 1 <= int(selection) <= len(names):
                name = names[int(selection)-1]
                try:
                    launch_from_snapshot(name) if mode == 'snapshot' else launch(name)
                except (OSError, ValueError) as error:
                    notice('Запуск отменён: ' + str(error), 'error')
                    library.acknowledge()
                continue
            notice('Выбери номер, A, D, R, E, M, H или 0.', 'warning')
            library.acknowledge()

def remove_vm(name):
    with vm_lock(ROOT, name):
        return _remove_vm(name)

def _remove_vm(name):
    from vm_storage import deletion_plan, delete_with_template, parameter_name
    data = load(name)
    image_exe = shutil.which('qemu-img.exe') or shutil.which('qemu-img') or str(Path(qemu_executable()).with_name('qemu-img.exe'))
    query = lambda: query_qmp_status(name, checked_qmp_port(name, data), timeout=1.5)
    plan = deletion_plan(ROOT, name, data, image_exe, query)
    sections = []
    if plan.get('missing'):
        sections.append(('Уже отсутствуют · будут отмечены в копии', [str(path) for path in plan['missing']]))
    sections.append(('Будут удалены · {} файлов'.format(len(plan['files'])), [str(path) for path in plan['files']]))
    if plan['kept']:
        sections.append(('Будут сохранены', ['{} — {}'.format(path, reason) for path, reason in plan['kept']]))
    present('Удаление виртуальной машины', 'Проверь файлы перед подтверждением.', name=name, data=data,
            sections=sections, options=[('Enter', 'Отмена при запросе имени копии')])
    notice('Копия сохранит только параметры. Диск и установленная ОС в неё НЕ входят.', 'warning')
    raw_label = ask('Твоё имя копии параметров (Enter — отмена):')
    if not raw_label.strip():
        return 0
    label = parameter_name(raw_label)
    if ask('Для подтверждения удаления введи {}:'.format(name)).strip() != name:
        notice('Отменено. Файлы не удалены.')
        return 0
    backup = delete_with_template(plan, label, query)
    notice('VM удалена. Цвет освобождён. Параметры сохранены: ' + str(backup), 'success')
    notice('Использовать параметры снова: vm add или R в списке VM.')
    return 0

def print_help():
    present('Справка и команды', 'Имя VM можно не указывать: откроется список выбора.', sections=[
        ('Начало работы', ['vm · главное меню', 'vm add · добавить VM', 'vm list · список виртуальных машин',
                          'vm ИМЯ · запуск', 'vm ИМЯ --disk · загрузка с диска',
                          'vm ИМЯ --iso [ПУТЬ] · загрузка ISO']),
        ('Работа с машиной', ['vm status [ИМЯ] · состояние', 'vm console [ИМЯ] · панель управления',
                             'vm iso [ИМЯ] · подключить ISO', 'vm disk [ИМЯ] · подключить / создать диск',
                             'vm show [ИМЯ] · команда QEMU', 'vm edit [ИМЯ] · JSON в Блокноте']),
        ('Управление библиотекой', ['E · параметры и профиль запуска VM', 'M · простой запуск / выбор снимка',
                                  'vm remove [ИМЯ] · удаление с именованной копией параметров',
                                  'vm restore · создать VM из сохранённых параметров']),
        ('VM Doctor', ['H · выбрать VM или проверить все', 'vm doctor [ИМЯ] · диагностика',
                       'vm doctor [ИМЯ] --save · сохранить обезличенный отчёт',
                       'Профили WHPX / Авто / TCG выбираются через E → VM → 8.']),
        ('В панели VM', ['P · пауза / продолжить', 'S · сохранить снимок дисков', 'L · список снимков',
                         'D · запросить выключение', 'R · жёсткая перезагрузка', 'X · остановить QEMU',
                         'B · закрыть панель и оставить VM работать']),
        ('Снимки и сохранение', ['Снимки содержат диски без оперативной памяти.',
                                'Перед откатом создаётся страховочный снимок.',
                                'Копия параметров содержит настройки; диски и ОС сохраняются отдельно.',
                                'Сохранение параметров через S создаёт копию прежнего JSON.'])])


def main():
    # Redirected Windows streams otherwise use a legacy codepage that loses UI text.
    for stream in (sys.stdout, sys.stderr):
        if not stream.isatty() and hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8', errors='replace')
    args = sys.argv[1:]
    selectors = {'status': 'Состояние VM', 'console': 'Панель управления VM', 'iso': 'Подключение ISO',
                 'disk': 'Подключение диска', 'show': 'Команда запуска VM', 'edit': 'Редактирование JSON',
                 'remove': 'Удаление VM', 'delete': 'Удаление VM'}
    if len(args) == 1 and args[0] in selectors:
        name = choose_machine(sys.modules[__name__], selectors[args[0]])
        if name is None:
            return 0
        args.append(name)
    # Doctor must bypass all initialization, migrations and color allocation.
    if args and args[0] == 'doctor':
        from vm_doctor import cli
        return cli(sys.modules[__name__], args[1:])
    # A status query must not initialize or migrate configuration files.
    if len(args) == 2 and args[0] == 'status':
        return status(args[1])
    if len(args) == 2 and args[0] == 'console':
        console(args[1])
        return menu()
    initialize()
    migrate_resolution()
    try:
        ensure_colors()
    except (OSError, ValueError, TypeError, AttributeError) as error:
        notice('Не удалось проверить цвета конфигураций; открой H — VM Doctor: ' + clean(error), 'warning')
    if not args:
        return menu()
    if args == ['add']:
        return add()
    if args == ['list']:
        if sys.stdin.isatty() and sys.stdout.isatty():
            return menu()
        print('\n'.join(library_frame([(name, load(name)) for name in entries()], ansi=False, mode=manager_settings()['launch_mode'])))
        return 0
    if len(args) == 2 and args[0] in ('remove', 'delete'):
        return remove_vm(args[1])
    if args == ['restore']:
        template = choose_template(required=True)
        return add(template=template, offer_template=False) if template else 0
    if len(args) == 2 and args[0] in ('iso', 'disk'):
        return attach(args[1], args[0])
    if len(args) == 2 and args[0] in ('show', 'edit'):
        name = args[1]
        data = load(name)
        present('Команда запуска' if args[0] == 'show' else 'Редактирование JSON', name=name, data=data)
        if args[0] == 'show':
            if profile(data) == 'auto':
                notice('Авто: ниже предварительная команда WHPX. Фактический профиль проверяется перед запуском.')
            notice('Команда QEMU · можно скопировать целиком:')
            print(paint(subprocess.list2cmdline(['qemu-system-x86_64'] + build_args(name, data)),
                        '#CBD5E5', enabled=enable_vt()))
            return 0
        return subprocess.call(['notepad.exe', str(config_path(name))], shell=False)
    if len(args) == 1 and args[0] not in RESERVED:
        launch(args[0])
        return menu()
    if len(args) == 2 and args[1] in ('--disk', '--iso'):
        launch(args[0], args[1][2:])
        return menu()
    if len(args) == 3 and args[1] == '--iso':
        launch(args[0], 'iso', args[2])
        return menu()
    if args in (['help'], ['-help'], ['--help']):
        print_help()
        return 0
    print_help()
    return 1

if __name__ == '__main__':
    try:
        sys.exit(main())
    except (KeyboardInterrupt, EOFError):
        notice('Действие отменено.')
        sys.exit(130)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        notice(str(error), 'error', file=sys.stderr)
        sys.exit(1)
