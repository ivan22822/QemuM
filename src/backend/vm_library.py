"""Live VM list; status probes never send a machine control command."""
from concurrent.futures import ThreadPoolExecutor, as_completed
import os
import re
import sys
import subprocess
import threading
import time

from vm_console import Terminal
from vm_storage import media, qemu_processes
from vm_style import fit, library_frame


def process_state(name, data, records):
    """Only infer stopped when a complete process inventory rules out this VM."""
    if records is None:
        return 'unavailable'
    disks = [str(path).replace('/', '\\').casefold() for path, kind in media(data) if kind == 'disk']
    uncertain = False
    for record in records:
        command = record.get('CommandLine', '').replace('/', '\\').replace(',,', ',').casefold()
        if not command:
            return 'unavailable'
        match = re.search(r'(?:^|\s)-name\s+(?:"([^"]*)"|(\S+))', command)
        guest = (match.group(1) or match.group(2)).split(',')[0] if match else None
        if guest and guest.startswith('guest='):
            guest = guest[6:]
        if guest == name.casefold() or any(path in command for path in disks):
            return 'no-qmp'
        uncertain = uncertain or guest is None
    return 'unavailable' if uncertain else 'stopped'


class StatusMonitor:
    def __init__(self, query, port, processes=qemu_processes):
        self.query, self.port, self.processes = query, port, processes
        self.lock = threading.Lock()
        self.stop = threading.Event()
        self.active = threading.Event()
        self.rows = []
        self.states = {}
        self.records = None
        self.inventory_time = 0
        self.threads = []

    def start(self):
        for target in (self.poll, self.inventory):
            thread = threading.Thread(target=target, daemon=True)
            thread.start()
            self.threads.append(thread)

    def close(self):
        self.stop.set()
        for thread in self.threads:
            thread.join(timeout=0.1)

    def resume(self, rows):
        with self.lock:
            self.rows = list(rows)
            # Start each visit with fresh QMP results; do not reuse a pre-launch status.
            self.states = {}
        self.active.set()

    def probe(self, name, data, ports):
        if data.get('_invalid'):
            return 'unavailable'
        try:
            port = self.port(name, data)
            if ports.count(port) != 1:
                return 'unavailable'
            return self.query(name, port, timeout=1.5)['status']
        except ConnectionRefusedError:
            return 'refused'
        except (OSError, ValueError, KeyError, TypeError):
            return 'unavailable'

    def poll(self):
        with ThreadPoolExecutor(max_workers=4) as pool:
            while not self.stop.is_set():
                if not self.active.wait(0.2):
                    continue
                with self.lock:
                    rows = self.rows
                ports = []
                for name, data in rows:
                    try:
                        ports.append(self.port(name, data))
                    except (ValueError, TypeError):
                        ports.append(None)
                jobs = {pool.submit(self.probe, name, data, ports): name for name, data in rows}
                for job in as_completed(jobs):
                    with self.lock:
                        if rows is self.rows and self.active.is_set():
                            self.states[jobs[job]] = job.result()
                self.stop.wait(1.5)

    def inventory(self):
        while not self.stop.is_set():
            if self.active.is_set() and time.monotonic() - self.inventory_time >= 10:
                try:
                    records = self.processes()
                except (OSError, ValueError, subprocess.SubprocessError):
                    records = None
                with self.lock:
                    self.records = records
                    self.inventory_time = time.monotonic()
            self.stop.wait(0.2)

    def snapshot(self):
        with self.lock:
            records = self.records if time.monotonic() - self.inventory_time <= 13 else None
            result = {}
            for name, data in self.rows:
                state = self.states.get(name, 'checking')
                if state == 'refused':
                    try:
                        state = process_state(name, data, records)
                    except (OSError, ValueError):
                        state = 'unavailable'
                result[name] = state
            return result


class LibraryMenu:
    def __init__(self, query, port, terminal_factory=Terminal, monitor_factory=StatusMonitor):
        self.live = os.name == 'nt' and sys.stdin.isatty() and sys.stdout.isatty()
        self.terminal_factory = terminal_factory
        self.monitor = monitor_factory(query, port)

    def __enter__(self):
        if self.live:
            self.monitor.start()
        return self

    def __exit__(self, *args):
        self.monitor.close()

    def choose(self, rows, mode, ansi, purpose=None, allow_all=False):
        if not self.live:
            from vm_ui import ask, width
            print('\n'.join(library_frame(rows, width=width(), ansi=ansi, mode=mode,
                                         purpose=purpose, allow_all=allow_all)))
            return ask('Выбор:').strip().lower()
        self.monitor.resume(rows)
        typed, page = '', 0
        try:
            with self.terminal_factory('VM Manager') as terminal:
                terminal.title('QEMU | ' + (purpose or 'Виртуальные машины'))
                while True:
                    size = terminal.size()
                    width = max(24, min(110, size.columns - 1))
                    height = max(1, size.lines - 1)
                    base = len(library_frame([], width, ansi=ansi, mode=mode, purpose=purpose, allow_all=allow_all)) - 2
                    capacity = max(1, (height - base - 3) // 3)
                    pages = max(1, (len(rows) + capacity - 1) // capacity)
                    page = min(page, pages - 1)
                    start = page * capacity
                    frame = library_frame(rows[start:start + capacity], width, ansi=ansi, mode=mode,
                                          statuses=self.monitor.snapshot(), start_index=start + 1,
                                          purpose=purpose, allow_all=allow_all)
                    if pages > 1:
                        frame.append(fit('  [ / ] — страницы  {}/{}'.format(page + 1, pages), width))
                    frame.append(fit('  Номер + Enter; Esc — назад' if purpose else '  Номер + Enter; буквы — сразу', width))
                    frame.append(fit('  Выбор: ' + typed + '▏', width))
                    terminal.draw(frame[:height])
                    key = terminal.key()
                    if key in ('\x03', '\x1b'):
                        return '0'
                    if purpose and allow_all and key == '*':
                        return '*'
                    if purpose and key in ('\r', '\n') and not typed:
                        return '' if allow_all else '0'
                    if not purpose and key in ('a', 'ф', 'd', 'в', 'r', 'к', 'm', 'ь', 'e', 'у', 'h', 'р'):
                        return key
                    if key in ('\r', '\n') and typed:
                        return typed
                    if key == '\x08':
                        typed = typed[:-1]
                    elif key in ('[', 'х'):
                        page = max(0, page - 1)
                    elif key in (']', 'ъ'):
                        page = min(pages - 1, page + 1)
                    elif key and key in '0123456789' and len(typed) < 8:
                        typed += key
                    time.sleep(0.05)
        finally:
            self.monitor.active.clear()

    def acknowledge(self):
        if self.live:
            input('Enter — в список VM...')
