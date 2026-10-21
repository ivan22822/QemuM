"""Read-only diagnostics. Export is an explicit, anonymous allowlist."""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import ctypes
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import sys
import textwrap

from vm_profiles import LABELS, failure_hint, probe, profile, validate, label, initial_accelerator
from vm_style import clean, paint, enable_vt
from vm_ui import ask, choose_machine, notice, present, screen_frame


def run(args, timeout=8):
    return subprocess.run([str(arg) for arg in args], capture_output=True, text=True,
                          encoding='utf-8', errors='replace', timeout=timeout, shell=False,
                          creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))


def host_info():
    shell = shutil.which('powershell.exe') or shutil.which('pwsh.exe')
    if not shell:
        raise ValueError('PowerShell не найден.')
    script = '''[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1 Name,VirtualizationFirmwareEnabled,SecondLevelAddressTranslationExtensions
$system = Get-CimInstance Win32_ComputerSystem | Select-Object HypervisorPresent
$os = Get-CimInstance Win32_OperatingSystem | Select-Object BuildNumber,FreePhysicalMemory
$feature = 'unknown'
try { $feature = (Get-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform -ErrorAction Stop).State.ToString() } catch {}
$commands = @(Get-Command vm -All -ErrorAction SilentlyContinue | Select-Object CommandType,Name,Source)
@{cpu=$cpu;system=$system;os=$os;feature=$feature;commands=$commands} | ConvertTo-Json -Depth 5 -Compress'''
    result = run([shell, '-NoProfile', '-NonInteractive', '-Command', script], timeout=12)
    if result.returncode:
        raise ValueError('Не удалось прочитать сведения Windows: ' + result.stderr[-1000:])
    data = json.loads(result.stdout)
    try:
        api = ctypes.WinDLL('WinHvPlatform.dll')
        api.WHvGetCapability.argtypes = [ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p]
        api.WHvGetCapability.restype = ctypes.c_long
        present = ctypes.c_uint32()
        code = api.WHvGetCapability(0, ctypes.byref(present), ctypes.sizeof(present), None)
        data['whv_api'] = bool(present.value) if code == 0 else None
    except (OSError, AttributeError):
        data['whv_api'] = None
    return data


class Doctor:
    def __init__(self, manager, check=probe, host=host_info, runner=run):
        self.manager, self.check, self.host, self.run = manager, check, host, runner
        self.items = []
        self.probes = {}
        self.versions = {}

    def add(self, scope, code, state, title, detail='', advice=''):
        self.items.append(dict(scope=scope, code=code, state=state, title=title,
                               detail=str(detail), advice=advice))

    def collect(self, name=None):
        if name and not self.manager.valid_name(name):
            raise ValueError('Некорректное имя VM.')
        root = self.manager.ROOT.parent
        self.add('host', 'manager', 'ok', 'Менеджер и Python',
                 str(root) + '\n' + sys.executable + '\nPython ' + sys.version.split()[0])
        self.versions['python'] = sys.version.split()[0]
        exe, image, display_help, info = None, None, '', {}
        # Windows collection overlaps the accelerator checks; both have deadlines.
        with ThreadPoolExecutor(max_workers=3) as pool:
            host_job = pool.submit(self.host)
            try:
                exe = self.manager.qemu_executable()
                source = 'PATH' if shutil.which('qemu-system-x86_64.exe') or shutil.which('qemu-system-x86_64') else 'Program Files'
                for tool, path in [('qemu', exe), ('qemu-img', str(Path(exe).with_name('qemu-img.exe')))]:
                    result = self.run([path, '--version'])
                    if result.returncode:
                        raise ValueError(result.stderr[-1000:] or 'Не удалось получить версию ' + tool)
                    match = re.search(r'version\s+(\d+(?:\.\d+){1,3})', result.stdout)
                    self.versions[tool] = match[1] if match else 'unknown'
                    self.add('host', tool, 'ok', 'QEMU' if tool == 'qemu' else 'qemu-img',
                             path + '\n' + result.stdout.splitlines()[0] + '\nИсточник QEMU: ' + source)
                    if tool == 'qemu-img':
                        image = path
                caps = self.run([exe, '-accel', 'help'])
                self.add('host', 'accelerators', 'ok' if caps.returncode == 0 else 'unknown',
                         'Ускорители в сборке', caps.stdout + caps.stderr)
                screen = self.run([exe, '-display', 'help'])
                display_help = screen.stdout + screen.stderr if screen.returncode == 0 else ''
                self.add('host', 'displays', 'ok' if display_help else 'unknown', 'Оконные интерфейсы', display_help)
            except (OSError, ValueError, subprocess.SubprocessError) as error:
                self.add('host', 'tools', 'error', 'Доступность QEMU', error,
                         'Проверь папку QEMU и команду запуска. Doctor ничего не устанавливает.')
            if exe:
                jobs = {key: pool.submit(self.check, exe, key) for key in ('whpx', 'whpx-compatible', 'tcg')}
                for key, job in jobs.items():
                    try:
                        result = job.result()
                        self.probes[key] = result
                        self.add('host', 'probe-' + key + '-' + result['code'],
                                 {'ok': 'ok', 'failed': 'warning', 'unknown': 'unknown'}[result['state']],
                                 'Создание временной VM: ' + LABELS[key], result['detail'],
                                 'Выбери подходящий профиль в параметрах VM; загрузка гостевой ОС этой пробой не проверяется.')
                    except (OSError, ValueError, subprocess.SubprocessError) as error:
                        self.add('host', 'probe-' + key, 'unknown', 'Проверка ' + key.upper(), error)
            try:
                info = host_job.result()
                cpu = info.get('cpu') or {}
                system = info.get('system') or {}
                self.add('host', 'windows', 'ok', 'Windows и процессор',
                         'Сборка: {}\nCPU: {}\nВиртуализация в прошивке: {}\nГипервизор: {}\nWHP API: {}'.format(
                             (info.get('os') or {}).get('BuildNumber', '?'), cpu.get('Name', '?'),
                             cpu.get('VirtualizationFirmwareEnabled'), system.get('HypervisorPresent'), info.get('whv_api')))
                feature = info.get('feature', 'unknown')
                self.add('host', 'feature', 'ok' if feature == 'Enabled' else 'unknown' if feature == 'unknown' else 'warning',
                         'Windows Hypervisor Platform', feature,
                         'Если сведения недоступны без повышения прав, это не означает, что компонент отключён.')
                commands = info.get('commands') or []
                expected = os.path.normcase(str(root / 'vm.cmd'))
                matches = [str(item.get('Source') or '') for item in commands]
                direct = '& "' + str(root / 'vm.cmd') + '"'
                command_ok = bool(commands) and str(commands[0].get('CommandType')) in ('32', 'Application', 'ExternalScript', '16') and os.path.normcase(matches[0]) == expected
                self.add('host', 'command', 'ok' if command_ok else 'warning', 'Команда vm в новом PowerShell',
                         '\n'.join(matches) if matches else 'Команда не найдена.',
                         'Для прямого запуска: ' + direct + '\nУже открытый PowerShell может иметь другой PATH или собственную функцию vm.')
            except (OSError, ValueError, subprocess.SubprocessError) as error:
                self.add('host', 'windows', 'unknown', 'Сведения Windows', error,
                         'Не удалось прочитать часть сведений. Системные настройки не менялись.')
        if name:
            if not self.manager.valid_name(name):
                raise ValueError('Некорректное имя VM.')
            names = [name]
        else:
            names = sorted(path.stem for path in self.manager.ROOT.glob('*.json'))
        if not names:
            self.add('host', 'empty', 'ok', 'Список VM', 'Виртуальных машин пока нет; общая диагностика выполнена.')
        self.port_owners = {}
        for path in self.manager.ROOT.glob('*.json'):
            try:
                if not self.manager.valid_name(path.stem):
                    continue
                raw = json.loads(path.read_text(encoding='utf-8-sig'))
                port = self.manager.qmp_port(path.stem, raw)
                self.port_owners.setdefault(port, []).append(path.stem)
            except (OSError, ValueError, TypeError):
                pass
        for vm_name in names:
            self.machine(vm_name, image, display_help, info)
        return self

    def machine(self, name, image, displays, host):
        try:
            if not self.manager.valid_name(name):
                raise ValueError('Имя файла не подходит для имени VM.')
            data = self.manager.load(name)
            validate(data)
            self.manager.qmp_port(name, data)
            if any(arg.split('=', 1)[0] == '-qmp' for arg in data.get('extra_args', [])):
                raise ValueError('QMP управляется менеджером; убери отдельный -qmp из extra_args.')
            self.add(name, 'config', 'ok', 'Параметры VM', 'Профиль: ' + label(data))
        except (OSError, ValueError, TypeError, KeyError) as error:
            self.add(name, 'config', 'error', 'Чтение конфигурации', error, 'Проверь JSON и параметры VM; файл не изменялся.')
            return
        requested = profile(data)
        results = self.probes
        candidates = ('whpx', 'tcg') if requested == 'auto' else (requested,)
        if data.get('launch_profile_settings'):
            result = self.check(self.manager.qemu_executable(), initial_accelerator(data),
                                settings=data['launch_profile_settings'])
            ready = result['state'] == 'ok'
            self.add(name, 'custom-profile-probe', 'ok' if ready else 'warning',
                     'Проверка сохранённых параметров профиля', result['detail'])
        else:
            ready = any(results.get(key, {}).get('state') == 'ok' for key in candidates)
        self.add(name, 'profile', 'ok' if ready else 'unknown', 'Доступность профиля',
                 label(data), 'Проба проверяет создание VM с минимальными ресурсами, а не загрузку этой ОС.')
        free = (host.get('os') or {}).get('FreePhysicalMemory')
        if free is not None and str(free).isdecimal():
            self.add(name, 'ram', 'warning' if data['ram_mb'] * 1024 > int(free) else 'ok',
                     'Запас оперативной памяти', 'VM: {} МБ; свободно сейчас: {} МБ'.format(data['ram_mb'], int(free) // 1024),
                     'Свободная память меняется; проверь её перед запуском.')
        if data['ram_mb'] > 1048576 or data['cores'] > 256:
            self.add(name, 'resources', 'warning', 'Ресурсы VM', 'Значения выходят за диапазон редактора.')
        display = os.environ.get('VM_MANAGER_DISPLAY', 'sdl')
        extra = data.get('extra_args', [])
        for index, flag in enumerate(extra[:-1]):
            if flag == '-display':
                display = extra[index + 1]
        display = display.split(',')[0]
        self.add(name, 'display', 'ok' if display in ('none', 'default') or re.search(r'(?m)^\s*' + re.escape(display) + r'\s*$', displays) else 'unknown',
                 'Оконный интерфейс', display, 'Проверяется наличие интерфейса в сборке; вывод реального окна требует обычного запуска VM.')
        from vm_storage import media
        try:
            attachments = media(data)
        except (ValueError, TypeError) as error:
            self.add(name, 'media', 'error', 'Подключённые файлы', error)
            attachments = []
        for path, kind in attachments:
            try:
                with path.open('rb') as stream:
                    stream.read(1)
                self.add(name, 'file-' + kind, 'ok', 'Чтение файла ' + kind, path)
                if any(ord(char) > 127 for char in str(path)):
                    self.add(name, 'path-encoding', 'warning', 'Путь к образу содержит нелатинские символы',
                             'Файл читается из Python; это ещё не подтверждает, что эта сборка QEMU откроет его при запуске.',
                             'Если QEMU пишет File not found для существующего файла, проверь путь без кириллицы на тестовой копии.')
                if kind == 'disk' and image:
                    result = self.run([image, 'info', '--backing-chain', '--output=json', path])
                    if result.returncode:
                        error = result.stderr.strip()
                        code = 'busy' if any(word in error.lower() for word in ('lock', 'in use', 'another process', 'sharing')) else 'image'
                        self.add(name, code, 'warning', 'Цепочка диска', error,
                                 'Занятый диск не означает повреждение; проверь его после штатного выключения VM.')
                    else:
                        chain = json.loads(result.stdout)
                        if not isinstance(chain, list) or not chain:
                            raise ValueError('QEMU не вернул цепочку диска.')
                        self.add(name, 'chain', 'ok', 'Цепочка диска', 'Образов в цепочке: ' + str(len(chain)))
                        for item in chain[1:]:
                            parent = Path(item['filename'])
                            if not parent.is_absolute():
                                parent = path.parent / parent
                            with parent.open('rb') as stream:
                                stream.read(1)
            except FileNotFoundError as error:
                self.add(name, 'missing', 'error', 'Отсутствует файл', error, 'Укажи существующий файл в параметрах VM.')
            except PermissionError as error:
                self.add(name, 'permission', 'warning', 'Доступ к файлу', error, 'Проверь доступ и использование файла другими процессами.')
            except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
                self.add(name, 'file-unknown', 'unknown', 'Проверка файла', error)
        try:
            port = self.manager.qmp_port(name, data)
            if len(self.port_owners.get(port, [])) > 1:
                raise ValueError('Одинаковый QMP-порт у нескольких VM. Задай разные qmp_port.')
            result = self.manager.query_qmp_status(name, port, timeout=1)
            self.add(name, 'qmp', 'ok', 'QMP', 'Состояние: ' + result['status'])
        except ConnectionRefusedError:
            self.add(name, 'qmp-offline', 'unknown', 'QMP', 'Соединение отклонено: VM может быть выключена или работать без QMP.')
        except (OSError, ValueError, KeyError) as error:
            self.add(name, 'qmp-unknown', 'unknown', 'QMP', error,
                     'Недоступность QMP не доказывает, что VM выключена. Проверь статус в главном списке.')

    def show(self):
        width = max(24, min(110, shutil.get_terminal_size((90, 30)).columns - 1))
        ansi = enable_vt()
        present('VM Doctor', 'Диагностика без изменения настроек.')
        current = None
        colors = {'ok': '#A3D977', 'warning': '#F4BD62', 'error': '#F28BA8', 'unknown': '#91A0B7'}
        labels = {'ok': 'OK', 'warning': 'ВНИМАНИЕ', 'error': 'ОШИБКА', 'unknown': 'НЕ ПРОВЕРЕНО'}
        for item in self.items:
            if item['scope'] != current:
                current = item['scope']
                heading = 'Компьютер' if current == 'host' else 'VM: ' + current
                print('\n' + paint('  ' + '─' * (width - 4), '#384457', enabled=ansi))
                notice(heading)
            heading = '[{}] {}'.format(labels[item['state']], item['title'])
            for line in textwrap.wrap(heading, width):
                print(paint(line, colors[item['state']], enabled=ansi))
            for value in (item['detail'], item['advice']):
                for paragraph in str(value).splitlines():
                    paragraph = clean(paragraph)
                    for line in textwrap.wrap(paragraph, max(1, width - 2)):
                        print('  ' + line)
        counts = {key: sum(item['state'] == key for item in self.items) for key in colors}
        notice('Итог: {} ошибок; {} предупреждений; {} непроверенных пунктов.'.format(counts['error'], counts['warning'], counts['unknown']),
               'error' if counts['error'] else 'warning' if counts['warning'] else 'success')
        notice('Профиль меняется через E → VM → 8. Doctor не исправляет файлы и настройки Windows.')
        return 1 if counts['error'] else 0

    def save(self):
        # No raw output, user paths, labels, network arguments, CPU/host identity or exception strings.
        aliases = {}
        checks = []
        for item in self.items:
            scope = item['scope']
            if scope != 'host':
                scope = aliases.setdefault(scope, 'vm-' + str(len(aliases) + 1))
            checks.append({key: value for key, value in dict(scope=scope, code=item['code'], state=item['state']).items()})
        payload = dict(schema=1, created=datetime.now(timezone.utc).isoformat(),
                       versions={key: value for key, value in self.versions.items() if re.fullmatch(r'\d+(?:\.\d+){1,3}', value)},
                       checks=checks, note='Anonymous report: raw errors and private paths are omitted.')
        folder = self.manager.ROOT.parent / 'doctor-reports'
        folder.mkdir(exist_ok=True)
        import uuid
        path = folder / ('doctor-' + datetime.now().strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:6] + '.json')
        with path.open('x', encoding='utf-8') as stream:
            json.dump(payload, stream, ensure_ascii=False, indent=2)
            stream.write('\n')
        return path


def cli(manager, args):
    save = '--save' in args
    names = [arg for arg in args if arg != '--save']
    if len(names) > 1 or any(arg.startswith('-') for arg in names):
        raise ValueError('Использование: vm doctor [ИМЯ] [--save]')
    present('VM Doctor', 'Проверка QEMU, Windows и доступности ускорения.')
    notice('Проверка может занять несколько секунд…')
    doctor = Doctor(manager).collect(names[0] if names else None)
    result = doctor.show()
    if save:
        notice('Обезличенный отчёт: ' + str(doctor.save()), 'success')
    return result


def menu(manager):
    selected = choose_machine(manager, 'VM Doctor: выбор виртуальной машины', allow_all=True)
    if selected is None:
        return
    present('VM Doctor', 'Все виртуальные машины' if selected == '*' else 'Проверка выбранной VM')
    notice('Проверяю QEMU и Windows…')
    doctor = Doctor(manager).collect(None if selected == '*' else selected)
    doctor.show()
    if ask('Сохранить обезличенный отчёт? S — сохранить; Enter — назад:').strip().lower() in ('s', 'ы'):
        notice('Отчёт сохранён: ' + str(doctor.save()), 'success')
