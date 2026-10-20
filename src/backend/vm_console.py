"""One live Windows terminal per VM; standard library only."""
import ctypes
from ctypes import wintypes
from datetime import datetime
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import threading
import textwrap
import time
import uuid
from vm_style import clean, color_for, fit, header, paint
from vm_runtime import watch_qmp_process
from vm_ui import ask, notice, present

ESC = '\x1b'
STATE_NAMES = {'running': 'RUNNING', 'paused': 'PAUSED', 'prelaunch': 'NOT STARTED',
               'save-vm': 'SAVING SNAPSHOT', 'shutdown': 'SHUTDOWN', 'suspended': 'SUSPENDED'}
class Terminal:
    def __init__(self, name):
        self.name = name
        self.kernel = None
        self.old_mode = None
        self.old_title = ''
        self.previous_lines = []

    def __enter__(self):
        if os.name != 'nt' or not sys.stdin.isatty() or not sys.stdout.isatty():
            raise ValueError('The live panel requires an interactive Windows terminal. Use vm status NAME for scripts.')
        import msvcrt
        self.keys = msvcrt
        self.kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        self.kernel.GetStdHandle.argtypes = [wintypes.DWORD]
        self.kernel.GetStdHandle.restype = wintypes.HANDLE
        self.kernel.GetConsoleMode.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        self.kernel.SetConsoleMode.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.kernel.GetConsoleTitleW.argtypes = [wintypes.LPWSTR, wintypes.DWORD]
        self.kernel.SetConsoleTitleW.argtypes = [wintypes.LPCWSTR]
        self.handle = self.kernel.GetStdHandle(-11 & 0xffffffff)
        mode = wintypes.DWORD()
        if not self.kernel.GetConsoleMode(self.handle, ctypes.byref(mode)):
            raise ValueError('Cannot access the terminal console.')
        self.old_mode = mode.value
        if not self.kernel.SetConsoleMode(self.handle, mode.value | 0x0004):
            raise ValueError('This terminal does not support the live panel.')
        title = ctypes.create_unicode_buffer(1024)
        self.kernel.GetConsoleTitleW(title, len(title))
        self.old_title = title.value
        sys.stdout.write(ESC + '[?1049h' + ESC + '[?25l' + ESC + '[2J')
        sys.stdout.flush()
        return self

    def title(self, value):
        self.kernel.SetConsoleTitleW(clean(value)[:240])

    def key(self):
        if not self.keys.kbhit():
            return None
        char = self.keys.getwch()
        if char in ('\x00', '\xe0'):
            self.keys.getwch()
            return None
        char = char.lower()
        return {'з': 'p', 'ы': 's', 'д': 'l', 'в': 'd', 'и': 'b', 'н': 'y',
                'к': 'r', 'ч': 'x', 'ф': 'a', 'ь': 'm'}.get(char, char)

    def size(self):
        # Read the actual viewport rather than stale inherited COLUMNS/LINES.
        try:
            return os.get_terminal_size(sys.stdout.fileno())
        except OSError:
            return os.terminal_size((88, 28))

    def draw(self, lines):
        # Redraw changed rows only: the name and menu remain stationary.
        output = []
        limit = max(1, self.size().lines - 1)
        for index in range(min(limit, max(len(lines), len(self.previous_lines)))):
            line = lines[index] if index < len(lines) else ''
            if index >= len(self.previous_lines) or self.previous_lines[index] != line:
                output.append(ESC + '[{};1H'.format(index + 1) + ESC + '[2K' + line)
        if output:
            sys.stdout.write(''.join(output))
            sys.stdout.flush()
        self.previous_lines = list(lines)

    def __exit__(self, *args):
        sys.stdout.write(ESC + '[0m' + ESC + '[?25h' + ESC + '[?1049l')
        sys.stdout.flush()
        self.kernel.SetConsoleTitleW(self.old_title)
        self.kernel.SetConsoleMode(self.handle, self.old_mode)

def snapshot_disks(request):
    blocks = request('query-block')
    disks = []
    for block in blocks:
        inserted = block.get('inserted')
        if not inserted or inserted.get('ro', False):
            continue
        device = block.get('device') or inserted.get('node-name')
        if inserted.get('drv') != 'qcow2' or not device:
            raise ValueError('Disk snapshots require all writable attached disks to use qcow2.')
        disks.append(dict(inserted, device=device))
    if not disks:
        raise ValueError('No writable qcow2 disk is attached (ISO-only VMs have no disk to snapshot).')
    return disks

def list_snapshots(request):
    disks = snapshot_disks(request)
    snapshots = [disk.get('image', {}).get('snapshots', []) for disk in disks]
    common = set(item['name'] for item in snapshots[0])
    for items in snapshots[1:]:
        common.intersection_update(item['name'] for item in items)
    rows = []
    for name in sorted(common):
        copies = [next(item for item in items if item['name'] == name) for items in snapshots]
        ram = sum(item.get('vm-state-size', 0) for item in copies)
        when = datetime.fromtimestamp(copies[0].get('date-sec', 0)).strftime('%Y-%m-%d %H:%M')
        kind = 'DISK ONLY' if not ram else 'RAM {:.1f} MB'.format(ram / 1048576)
        rows.append('{}  |  {}  |  {}'.format(clean(name), when, kind))
    return rows

def save_snapshot(request, progress):
    disks = snapshot_disks(request)
    devices = list(dict.fromkeys(disk['device'] for disk in disks))
    tag = 'disk-' + datetime.now().strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:6]
    actions = [{'type': 'blockdev-snapshot-internal-sync', 'data': {'device': device, 'name': tag}} for device in devices]
    progress('Saving disk snapshot (no RAM): ' + tag)
    # The synchronous transaction includes all writable disks and rolls back on failure.
    try:
        request('transaction', {'actions': actions})
        verified = snapshot_disks(request)
    except OSError as error:
        raise ValueError('Snapshot result not confirmed. Check the list before retrying: ' + tag) from error
    by_device = {disk['device']: disk for disk in verified}
    for device in devices:
        matches = [item for item in by_device.get(device, {}).get('image', {}).get('snapshots', []) if item.get('name') == tag]
        if len(matches) != 1 or matches[0].get('vm-state-size', 0) != 0:
            raise ValueError('Disk snapshot could not be verified. Check the list before retrying: ' + tag)
    return tag

class VMConsole:
    def __init__(self, name, data, port, command, query, terminal_factory=Terminal, watcher_factory=watch_qmp_process):
        self.name, self.data, self.port = name, data, port
        self.command, self.query = command, query
        self.terminal_factory = terminal_factory
        self.watcher_factory = watcher_factory
        self.tasks, self.events = queue.Queue(), queue.Queue()
        self.stop = threading.Event()
        self.state = 'CONNECTING'
        self.message = 'Waiting for QEMU...'
        self.message_is_connection = True
        self.qmp_connected = None
        self.details = []
        self.busy = False
        self.confirm = None
        self.process = None
        self.log_path = None
        self.last_seen = None

    def request(self, command, arguments=None):
        return self.command(self.name, self.port, command, arguments, timeout=5.0)

    def force_quit(self):
        try:
            self.request('quit')
        except (OSError, ValueError):
            # QEMU may exit before sending the reply. Only its exact process handle
            # can confirm success; losing QMP alone does not prove termination.
            deadline = time.monotonic() + 1.0
            while self.process is not None:
                if self.process.poll() is not None:
                    return
                if time.monotonic() >= deadline:
                    break
                self.stop.wait(0.05)
            raise

    def worker(self):
        while not self.stop.is_set():
            try:
                task = self.tasks.get(timeout=1.0)
            except queue.Empty:
                task = None
            if self.stop.is_set():
                break
            if task:
                try:
                    if task == 'toggle':
                        current = self.query(self.name, self.port)
                        self.request('stop' if current['running'] else 'cont')
                        text = 'Pause requested.' if current['running'] else 'Resume requested.'
                    elif task == 'shutdown':
                        current = self.query(self.name, self.port)
                        if not current['running']:
                            raise ValueError('Resume the VM before requesting guest shutdown.')
                        self.request('system_powerdown')
                        text = 'Shutdown requested. Waiting for the guest OS; this may take time.'
                    elif task == 'reset':
                        self.request('system_reset')
                        text = 'Жёсткая перезагрузка запрошена.'
                    elif task == 'force-stop':
                        self.force_quit()
                        text = 'Остановка QEMU запрошена. Ожидаю завершения процесса.'
                    elif task == 'snapshot':
                        tag = save_snapshot(self.request, lambda text: self.events.put(('progress', text)))
                        text = 'Disk snapshot saved: ' + tag
                    elif task == 'list':
                        self.events.put(('details', list_snapshots(self.request)))
                        text = 'Saved snapshots. DISK ONLY = RAM was not saved.'
                    self.events.put(('done', text))
                except (OSError, ValueError) as error:
                    self.events.put(('done', 'Action failed: ' + clean(error)))
            try:
                state = self.query(self.name, self.port)
                self.events.put(('state', state['status']))
            except (OSError, ValueError):
                self.events.put(('state', 'QMP UNAVAILABLE'))

    def drain(self):
        while True:
            try:
                kind, value = self.events.get_nowait()
            except queue.Empty:
                return
            if kind == 'state':
                was_connected = self.qmp_connected
                self.qmp_connected = value != 'QMP UNAVAILABLE'
                self.state = STATE_NAMES.get(value, clean(value).upper())
                if self.qmp_connected:
                    self.last_seen = datetime.now().strftime('%H:%M:%S')
                if self.message_is_connection or (was_connected is True and not self.qmp_connected):
                    self.message = ('QMP подключён. Управление VM доступно.' if self.qmp_connected else
                                    'QMP недоступен. Повторяю подключение...')
                    self.message_is_connection = True
            elif kind == 'progress':
                self.message = value
                self.message_is_connection = False
                self.state = 'SAVING SNAPSHOT'
            elif kind == 'details':
                self.details = value or ['No saved snapshots.']
            elif kind == 'done':
                self.message = value
                self.message_is_connection = False
                self.busy = False

    def handle_key(self, key):
        if self.confirm:
            if key == 'y':
                self.submit(self.confirm)
            else:
                self.message = 'Cancelled.'
                self.message_is_connection = False
            self.confirm = None
            return True
        if key in ('b', '\x03'):
            if self.busy:
                self.message = 'Wait for the current action to finish before leaving the panel.'
                self.message_is_connection = False
                return True
            return False
        if self.busy:
            return True
        if key in ('d', 'r', 'x'):
            self.confirm = {'d': 'shutdown', 'r': 'reset', 'x': 'force-stop'}[key]
        elif key in ('p', 's', 'l'):
            self.submit({'p': 'toggle', 's': 'snapshot', 'l': 'list'}[key])
        return True

    def submit(self, task):
        self.busy = True
        self.message = 'Working...'
        self.message_is_connection = False
        self.details = []
        self.tasks.put(task)

    def render(self, columns, rows):
        width = max(1, columns - 1)
        height = max(1, rows - 1)
        color = color_for(self.name, self.data)
        label = clean(self.data.get('label', self.name))
        lines = [header(self.name.upper(), '[ ' + self.state + ' ]', width, color), '']
        def text(value, foreground='#91A0B7', bold=False):
            lines.append(paint(fit(value, width), foreground, bold=bold))
        text('  ' + label, '#F0F4FC', True)
        text('  {} МБ RAM   ·   {} CPU'.format(self.data.get('ram_mb', '?'), self.data.get('cores', '?')))
        if self.data.get('_runtime_accelerator'):
            actual = self.data['_runtime_accelerator']
            text('  Ускорение: ' + (actual.upper() if actual != 'unknown' else 'не удалось определить'))
        if width < len(self.name) + len(self.state) + 9:
            text('  ' + self.state, color, True)
        text('  ' + '─' * max(0, width - 4), color)
        actions = [('P', 'Пауза / продолжить'), ('S', 'Снимок дисков'), ('L', 'Список снимков'),
                   ('D', 'Выключить VM'), ('R', 'Жёсткая перезагрузка'),
                   ('X', 'Принудительно остановить'), ('B', 'В меню, VM продолжит работать')]
        for key, title in actions:
            text('  [{}]  {}'.format(key, title), '#F0F4FC')
        text('  ' + '─' * max(0, width - 4), '#384457')
        confirmation = {'shutdown': 'Выключить {}?',
                        'reset': 'Жёстко перезагрузить {}? Несохранённые данные будут потеряны.',
                        'force-stop': 'Принудительно остановить {}? Несохранённые данные будут потеряны.'}
        message = confirmation[self.confirm].format(self.name) if self.confirm else self.message
        for line in textwrap.wrap(clean(message), max(1, width - 4))[:3]:
            text('  ' + line, color if self.confirm else '#CBD5E5')
        if self.confirm:
            text('  Y — да · другая клавиша — отмена', color, True)
        text('  Обновлено: ' + (self.last_seen or 'ожидание') + ('  ·  Выполняется действие' if self.busy else ''), '#718198')
        available = max(0, height - len(lines) - 1)
        detail_lines = [line for item in self.details for line in textwrap.wrap(clean(item), max(1, width - 4))]
        for line in detail_lines[:available]:
            text('  ' + line, '#CBD5E5')
        if len(detail_lines) > available:
            text('  Ещё {} строк — увеличь окно.'.format(len(detail_lines) - available))
        if height < 20 and self.confirm:
            compact = [header(self.name.upper(), '[ ' + self.state + ' ]', width, color)]
            compact.extend(paint(fit(line, width), color) for line in
                           textwrap.wrap(clean(message), max(1, width))[:max(0, height - 2)])
            compact.append(paint(fit('Y — да · другая — отмена', width), color, bold=True))
            return compact[:height]
        if height < 20:
            compact = [header(self.name.upper(), '[ ' + self.state + ' ]', width, color)]
            compact.extend(paint(fit(value, width), '#F0F4FC') for value in
                           (label, self.state, '[P] Пауза  [S] Снимок', '[L] Снимки [D] Выкл.',
                            '[R] Сброс [X] Стоп', '[B] Назад', message,
                            'Y — да; другая — отмена' if self.confirm else ''))
            return compact[:height]
        return lines[:height]

    def run(self, launch_command=None):
        log = None
        worker = None
        exited = False
        result = 0
        attached_watcher = None
        try:
            with self.terminal_factory(self.name) as terminal:
                if launch_command:
                    logs = Path(__file__).resolve().parent / 'logs'
                    logs.mkdir(exist_ok=True)
                    self.log_path = logs / ('{}-{}-{}.log'.format(self.name, datetime.now().strftime('%Y%m%d-%H%M%S'), uuid.uuid4().hex[:6]))
                    log = self.log_path.open('xb')
                    flags = getattr(subprocess, 'CREATE_NEW_PROCESS_GROUP', 0) | getattr(subprocess, 'CREATE_NO_WINDOW', 0)
                    self.process = subprocess.Popen(launch_command, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, shell=False, creationflags=flags)
                    self.message = 'VM started. Connecting to QMP...'
                else:
                    attached_watcher = self.watcher_factory(self.port)
                    self.process = attached_watcher
                    self.message = 'Connected to the existing VM.'
                worker = threading.Thread(target=self.worker, daemon=True)
                worker.start()
                previous = None
                previous_title = None
                while True:
                    self.drain()
                    if self.process is not None and self.process.poll() is not None:
                        result = self.process.returncode
                        self.state = 'STOPPED' if result == 0 else 'EXITED: ERROR'
                        self.message = 'QEMU exited. Returning to the main menu.'
                        self.message_is_connection = False
                        exited = True
                        break
                    title = '[{}] {} | VM Manager'.format(self.name.upper(), self.state)
                    if title != previous_title:
                        terminal.title(title)
                        previous_title = title
                    size = terminal.size() if hasattr(terminal, 'size') else shutil.get_terminal_size((88, 28))
                    frame = self.render(size.columns, size.lines)
                    if frame != previous:
                        terminal.draw(frame)
                        previous = frame
                    key = terminal.key()
                    if key is not None:
                        if exited and key in ('b', '\x03', '\r', '\n'):
                            break
                        if not exited and not self.handle_key(key):
                            break
                    time.sleep(0.05)
        except KeyboardInterrupt:
            # The detached QEMU process never receives terminal Ctrl+C.
            pass
        finally:
            self.stop.set()
            if worker is not None:
                worker.join(timeout=0.1)
            if log is not None:
                log.close()
            if attached_watcher is not None:
                attached_watcher.close()
        if exited:
            present('Завершение работы VM', name=self.name, data=self.data)
            notice('QEMU завершён · код {}.'.format(result), 'success' if result == 0 else 'error')
            if result and self.log_path:
                notice('Журнал QEMU: ' + str(self.log_path))
                notice(self.log_path.read_text(encoding='utf-8', errors='replace')[-4000:], 'error')
                if sys.stdin.isatty():
                    try:
                        ask('Enter — в главное меню…')
                    except EOFError:
                        pass
        else:
            notice('Панель закрыта. VM продолжает работать; возвращаюсь в меню.')
        return result
