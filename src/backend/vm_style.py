"""Shared terminal theme and reusable color allocation for active VMs."""
import colorsys
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
import re
import sys
import unicodedata

ESC = '\x1b'
PALETTE = ['#F4BD62', '#79B8FF', '#C4A2FF', '#61D6C3', '#F28BA8', '#A3D977', '#FF9F76', '#85D5ED']
DEFAULTS = dict(zip(('ubuntu-server', 'ubuntu-desktop', 'kali', 'tails'), PALETTE))

def clean(value):
    return ''.join(char for char in str(value) if char.isprintable() and unicodedata.category(char) != 'Cf')

def valid_color(value):
    return isinstance(value, str) and re.fullmatch(r'#[0-9A-Fa-f]{6}', value) is not None

def rgb(value):
    return tuple(int(value[index:index + 2], 16) for index in (1, 3, 5))

def next_color(used, preferred=None):
    used = {item.upper() for item in used if valid_color(item)}
    for candidate in ([preferred] if preferred else []) + PALETTE:
        if valid_color(candidate) and candidate.upper() not in used:
            return candidate.upper()
    # Grow the palette, choosing the candidate furthest from occupied colors.
    candidates = []
    for index in range(720):
        channels = colorsys.hls_to_rgb((index * 0.61803398875) % 1, 0.68 if index % 2 else 0.76, 0.65)
        candidate = '#' + ''.join('{:02X}'.format(round(channel * 255)) for channel in channels)
        if candidate not in used:
            distance = min(sum((a - b) ** 2 for a, b in zip(rgb(candidate), rgb(item))) for item in used)
            candidates.append((distance, candidate))
    if not candidates:
        raise ValueError('No free display color; remove an unused VM first.')
    return max(candidates)[1]

def occupied_colors(root, exclude=None):
    result = []
    for path in sorted(root.glob('*.json')):
        if path.stem == exclude:
            continue
        data = json.loads(path.read_text(encoding='utf-8-sig'))
        if valid_color(data.get('ui_color')):
            result.append(data['ui_color'].upper())
    return result

def assign_color(root, name, data):
    used = occupied_colors(root, exclude=name)
    preferred = data.get('ui_color') if valid_color(data.get('ui_color')) else DEFAULTS.get(name)
    result = dict(data)
    result['ui_color'] = next_color(used, preferred)
    return result

def color_for(name, data=None):
    value = (data or {}).get('ui_color')
    if valid_color(value):
        return value.upper()
    return DEFAULTS.get(name, PALETTE[hashlib.sha256(name.encode()).digest()[0] % len(PALETTE)])

def enable_vt():
    if not sys.stdout.isatty() or os.environ.get('NO_COLOR') is not None:
        return False
    if os.name == 'nt':
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.GetStdHandle.argtypes = [wintypes.DWORD]
        kernel.GetStdHandle.restype = wintypes.HANDLE
        kernel.GetConsoleMode.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        kernel.SetConsoleMode.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        mode = wintypes.DWORD()
        handle = kernel.GetStdHandle(-11 & 0xffffffff)
        if not kernel.GetConsoleMode(handle, ctypes.byref(mode)) or not kernel.SetConsoleMode(handle, mode.value | 4):
            return False
    return True

def paint(text, foreground=None, background=None, bold=False, enabled=True):
    if not enabled:
        return text
    codes = ['1'] if bold else []
    if foreground:
        codes.append('38;2;{};{};{}'.format(*rgb(foreground)))
    if background:
        codes.append('48;2;{};{};{}'.format(*rgb(background)))
    return ESC + '[' + ';'.join(codes or ['0']) + 'm' + text + ESC + '[0m'

def fit(text, width):
    text = clean(text)
    return text if len(text) <= width else text[:max(0, width - 1)] + ('…' if width else '')

def header(left, right, width, color):
    left, right = clean(left), clean(right)
    if width < len(left) + len(right) + 5:
        return paint(fit(' ' + left, width).ljust(width), '#111827', color, True)
    return paint(' ' + left + ' ' * (width - len(left) - len(right) - 3) + right + '  ', '#111827', color, True)

def library_frame(entries, width=88, ansi=True, mode='normal', statuses=None, start_index=1, purpose=None, allow_all=False):
    width = max(24, min(width, 110))
    title = [paint('  QEMU', '#F0F4FC', bold=True, enabled=ansi), paint('  Виртуальные машины', '#91A0B7', enabled=ansi)] if width < 35 else [paint('  QEMU', '#F0F4FC', bold=True, enabled=ansi) + paint('  /  Виртуальные машины', '#91A0B7', enabled=ansi)]
    lines = [''] + title + [paint('  ' + '─' * (width - 4), '#384457', enabled=ansi), '']
    mode_name = '1 · Простой запуск' if mode == 'normal' else '2 · Выбор снимка'
    lines.append(paint(fit('  ' + purpose if purpose else '  Режим: ' + mode_name, width),
                       '#79B8FF' if purpose else '#F4BD62' if mode == 'snapshot' else '#91A0B7', enabled=ansi))
    lines.append('')
    for index, (name, data) in enumerate(entries, start_index):
        color = color_for(name, data)
        title = fit(clean(data.get('label', name)), max(6, width - 28))
        first = '  {:>2}  '.format(index)
        lines.append(first + paint('● ', color, enabled=ansi) + paint(title, '#F0F4FC', bold=True, enabled=ansi))
        info = '{}   ·   {} МБ RAM   ·   {} CPU'.format(name, data.get('ram_mb', '?'), data.get('cores', '?'))
        lines.append('        ' + paint(fit(info, width - 10), '#91A0B7', enabled=ansi))
        if statuses is None:
            lines.append('')
        else:
            state = statuses.get(name, 'checking')
            status, foreground = {
                'running': ('Работает', '#A3D977'), 'paused': ('Пауза', '#F4BD62'),
                'stopped': ('Выключена', '#718198'), 'checking': ('Проверка…', '#91A0B7'),
                'no-qmp': ('Запущена · нет QMP', '#F28BA8'),
                'unavailable': ('Состояние неизвестно', '#F28BA8'),
                'prelaunch': ('Ожидает запуска', '#F4BD62'), 'shutdown': ('Завершается', '#F4BD62'),
                'suspended': ('Приостановлена', '#F4BD62')
            }.get(state, (clean(state), '#F4BD62'))
            if width < 36:
                status = {'no-qmp': 'Нет QMP', 'unavailable': 'Неизвестно'}.get(state, status)
            lines.append('        ' + paint(fit(status, width - 10), foreground, enabled=ansi))
    if not entries:
        lines.extend([fit('  Виртуальных машин пока нет.', width), ''])
    lines.append(paint('  ' + '─' * (width - 4), '#384457', enabled=ansi))
    if purpose:
        lines.append(fit('  Номер — выбрать VM', width))
        if allow_all:
            lines.append(fit('  * / Enter — все VM', width))
        lines.extend(['  0 / Esc — назад', ''])
        return lines
    lines.extend(['  Номер — запуск    A — добавить    D — удалить', '  R — из копии параметров           0 — выход'] if width >= 52 else
                 ['  Номер — запуск', '  A — добавить', '  D — удалить', '  R — копии параметров', '  0 — выход'])
    lines.append('  M — сменить режим')
    lines.append('  E — параметры VM')
    lines.append(fit('  H — VM Doctor · диагностика', width))
    lines.append('')
    return lines
