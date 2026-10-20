"""Shared terminal screens, choices and messages; no VM operations."""
import shutil
import sys
import textwrap

from vm_style import clean, color_for, enable_vt, fit, paint

TEXT = '#F0F4FC'
MUTED = '#91A0B7'
LINE = '#384457'
ACCENT = '#79B8FF'
COLORS = {'info': ACCENT, 'success': '#A3D977', 'warning': '#F4BD62', 'error': '#F28BA8'}


def width():
    return max(24, min(110, shutil.get_terminal_size((88, 28)).columns - 1))


def wrapped(value, size):
    result = []
    for paragraph in str(value).splitlines() or ['']:
        result.extend(textwrap.wrap(clean(paragraph), max(1, size), break_on_hyphens=False) or [''])
    return result


def screen_frame(title, subtitle='', sections=(), options=(), name=None, data=None, width=88, ansi=True):
    width = max(24, min(width, 110))
    color = color_for(name, data) if name else ACCENT
    lines = ['']
    def text(value, foreground=TEXT, bold=False):
        lines.extend(paint('  ' + line, foreground, bold=bold, enabled=ansi)
                     for line in wrapped(value, width - 4))
    text('QEMU / VM Manager', MUTED)
    lines.append(paint('  ' + '─' * (width - 4), LINE, enabled=ansi))
    text(title, color, True)
    if name:
        label = (data or {}).get('label', name)
        text('● ' + clean(label) + (' · ' + name if label != name else ''), color)
    if subtitle:
        text(subtitle, MUTED)
    for heading, rows in sections:
        lines.append('')
        if heading:
            text(heading, color, True)
        for row in rows:
            text(row, '#CBD5E5')
    if options:
        lines.append('')
        lines.append(paint('  ' + '─' * (width - 4), LINE, enabled=ansi))
        for key, label in options:
            text('[{}]  {}'.format(key, label), TEXT)
    lines.append('')
    return lines


def present(title, subtitle='', sections=(), options=(), name=None, data=None, clear=True):
    ansi = enable_vt()
    if clear and ansi and sys.stdout.isatty():
        print('\x1b[2J\x1b[H', end='')
    print('\n'.join(screen_frame(title, subtitle, sections, options, name, data, width(), ansi)))


def ask(prompt):
    ansi = enable_vt()
    for line in wrapped(prompt, width() - 4):
        print(paint('  ' + line, MUTED, enabled=ansi))
    return input(paint('  › ', ACCENT, bold=True, enabled=ansi))


def notice(message, kind='info', title=None, file=None):
    color = COLORS.get(kind, ACCENT)
    ansi = enable_vt()
    label = title or {'info': 'ИНФО', 'success': 'ГОТОВО', 'warning': 'ВНИМАНИЕ', 'error': 'ОШИБКА'}[kind]
    print(paint('  ' + fit(label, width() - 4), color, bold=True, enabled=ansi), file=file)
    for line in wrapped(message, width() - 4):
        print(paint('  ' + line, '#CBD5E5', enabled=ansi), file=file)


def options(title, choices, subtitle='', default=None, cancel=True, name=None, data=None):
    """Return a string key. Enter uses the shown default, otherwise cancels."""
    choices = [(str(key), label) for key, label in choices]
    foot = choices + ([('0', 'Назад / отмена')] if cancel else [])
    present(title, subtitle, options=foot, name=name, data=data)
    while True:
        raw = ask('Выбор' + (' [{}]'.format(default) if default is not None else '') + ':').strip().lower()
        if not raw:
            return str(default) if default is not None else None
        if cancel and raw == '0':
            return None
        if raw in [key for key, _ in choices]:
            return raw
        notice('Выбери пункт из списка.', 'warning')


def choose_machine(manager, title, allow_all=False):
    """One card list for every operation; identities come from the displayed rows."""
    from vm_library import LibraryMenu
    with LibraryMenu(manager.query_qmp_status, manager.qmp_port) as library:
        while True:
            rows = manager.menu_rows(manager.entries())
            selected = library.choose(rows, 'normal', enable_vt(), purpose=title, allow_all=allow_all)
            if selected in ('', '0'):
                return '*' if allow_all and selected == '' else None
            if allow_all and selected in ('*', 'all'):
                return '*'
            if selected.isdecimal() and 1 <= int(selected) <= len(rows):
                return rows[int(selected) - 1][0]
            # Retain direct name entry in redirected/scripted terminals.
            if selected in [name for name, _ in rows]:
                return selected
            notice('Выбери номер виртуальной машины или 0 для возврата.', 'warning')
