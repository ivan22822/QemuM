"""Parameter editor: explicit save, verified configuration backup, atomic replace."""
from copy import deepcopy
from datetime import datetime
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
import uuid

from vm_style import clean, color_for, enable_vt, fit, paint

from vm_profiles import LABELS, PROFILES, profile, label as profile_label
from vm_ui import ask, notice, options, present, screen_frame

VGA = ('std', 'virtio', 'qxl', 'cirrus', 'none')


def save_parameters(path, original, data, validate, keep_legacy=False):
    # Validate before creating any files, keeping custom QEMU options intact.
    validate(data)
    if path.read_bytes() != original:
        raise ValueError('Конфигурация изменилась во время редактирования. Открой параметры заново.')
    data = deepcopy(data)
    if not keep_legacy:
        data.pop('kind', None)
        data.pop('path', None)
    encoded = (json.dumps(data, ensure_ascii=False, indent=2) + '\n').encode('utf-8')
    backup = path.with_name(path.name + '.pre-edit-' + datetime.now().strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:8] + '.bak')
    with backup.open('xb') as stream:
        stream.write(original)
        stream.flush()
        os.fsync(stream.fileno())
    if backup.read_bytes() != original:
        raise ValueError('Проверка копии параметров не прошла. Сохранение отменено.')
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=path.name + '.', suffix='.tmp', delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        if path.read_bytes() != original:
            raise ValueError('Конфигурация изменилась. Сохранение отменено; копия сохранена: ' + str(backup))
        os.replace(temporary, path)
        temporary = None
    finally:
        if temporary is not None:
            temporary.unlink()
    return backup


def number(prompt, current, maximum):
    while True:
        value = ask('{} [{}]: '.format(prompt, current)).strip()
        if not value:
            return current
        if value.isdecimal() and 1 <= int(value) <= maximum:
            return int(value)
        notice('Введи число от 1 до {} или Enter — оставить.'.format(maximum), 'warning')


def media_path(title, current, suffix):
    while True:
        value = ask('{} (Enter — оставить; - — отключить): '.format(title)).strip()
        if not value:
            return current
        if value == '-':
            return ''
        path = Path(os.path.expandvars(value.strip('"'))).expanduser()
        if path.is_file() and path.suffix.lower() == suffix:
            return str(path.resolve())
        notice('Выбери существующий файл {}. Файл не будет изменён.'.format(suffix), 'warning')


def frame(name, data, changed, width=88, ansi=True):
    return screen_frame('Параметры виртуальной машины', 'Применятся при следующем запуске.',
        name=name, data=data, width=width, ansi=ansi, sections=[
            ('Параметры', ['[1] Название: ' + clean(data.get('label', name)),
                         '[2] Оперативная память: {} МБ'.format(data['ram_mb']),
                         '[3] Ядра CPU: ' + str(data['cores']),
                         '[4] Разрешение: {} × {}'.format(data.get('screen_width', 1920), data.get('screen_height', 1080)),
                       '[5] Видеоадаптер: ' + data.get('vga', 'std')]),
            ('Носители и запуск', ['[6] Диск: ' + (data.get('disk_path') or 'не подключён'),
                          '[7] ISO: ' + (data.get('iso_path') or 'не подключён'),
                          '[8] Профиль: ' + profile_label(data),
                        'Есть несохранённые изменения.' if changed else 'Выбери параметр для изменения.'])],
        options=[('S', 'Сохранить с резервной копией'), ('0', 'Назад / отменить изменения')])


def edit(name, data, save):
    original = deepcopy(data)
    data = deepcopy(data)
    ansi = enable_vt()
    while True:
        width = max(24, shutil.get_terminal_size((88, 28)).columns - 1)
        if ansi and __import__('sys').stdout.isatty():
            print('\x1b[2J\x1b[H', end='')
        print('\n'.join(frame(name, data, data != original, width, ansi)))
        choice = ask('Параметр: ').strip().lower()
        if choice in ('0', ''):
            notice('Изменения отменены.')
            return 0
        if choice in ('s', 'ы'):
            if data == original:
                notice('Параметры не изменились.')
                return 0
            if not data.get('disk_path') and not data.get('iso_path'):
                notice('Нужен хотя бы один диск или ISO.', 'warning')
                continue
            try:
                backup = save(data)
            except (OSError, ValueError) as error:
                notice('Сохранение отменено: ' + clean(error), 'error')
                continue
            notice('Параметры сохранены. Применятся при следующем запуске VM.', 'success')
            notice('Копия прежних параметров: ' + str(backup))
            return 0
        if choice == '1':
            value = ask('Название (Enter — оставить): ').strip()
            if value:
                data['label'] = clean(value)
        elif choice == '2':
            data['ram_mb'] = number('RAM в МБ', data['ram_mb'], 1048576)
        elif choice == '3':
            data['cores'] = number('Число ядер CPU', data['cores'], 256)
        elif choice == '4':
            while True:
                value = ask('Разрешение, например 1920x1080 (Enter — оставить): ').strip().lower()
                if not value:
                    break
                match = re.fullmatch(r'(\d+)\s*[xх×]\s*(\d+)', value)
                if match and 320 <= int(match[1]) <= 4095 and 200 <= int(match[2]) <= 4095:
                    data['screen_width'], data['screen_height'] = map(int, match.groups())
                    break
                notice('Ширина: 320–4095, высота: 200–4095 пикселей.', 'warning')
        elif choice == '5':
            present('Видеоадаптер', name=name, data=data, options=[(str(i), value) for i, value in enumerate(VGA, 1)])
            current = VGA.index(data.get('vga', 'std')) + 1
            data['vga'] = VGA[number('Видеоадаптер', current, len(VGA)) - 1]
        elif choice == '6':
            data['disk_path'] = media_path('Диск qcow2', data.get('disk_path', ''), '.qcow2')
        elif choice == '7':
            data['iso_path'] = media_path('ISO', data.get('iso_path', ''), '.iso')
        elif choice == '8':
            present('Профиль запуска', name=name, data=data, options=[(str(index + 1), LABELS[key]) for index, key in enumerate(PROFILES)])
            selected = number('Профиль', PROFILES.index(profile(data)) + 1 if profile(data) in PROFILES else 1, len(PROFILES))
            data['launch_profile'] = PROFILES[selected - 1]
            data.pop('launch_profile_settings', None)
            data.pop('launch_profile_label', None)
        else:
            notice('Выбери 1–8, S или 0.', 'warning')
