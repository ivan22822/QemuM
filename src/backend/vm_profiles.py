"""Launch profiles and disposable, diskless accelerator probes (stdlib only)."""
import json
from contextlib import suppress
import os
import queue
import re
import subprocess
import tempfile
import threading
import time

PROFILES = ('whpx', 'auto', 'tcg', 'whpx-compatible')
LABELS = {'whpx': 'WHPX · аппаратное ускорение', 'auto': 'Авто · проверить WHPX, иначе TCG',
          'tcg': 'TCG · программная эмуляция, медленнее',
          'whpx-compatible': 'WHPX · совместимый (kernel-irqchip=off)'}


def profile(data):
    value = data.get('launch_profile', 'whpx')
    if value not in PROFILES and not (isinstance(value, str) and re.fullmatch(r'custom-[0-9a-f]{32}', value)
                                     and data.get('launch_profile_settings')):
        raise ValueError('Неизвестный профиль запуска. Выбери профиль в параметрах VM.')
    return value


def label(data):
    return data.get('launch_profile_label') or LABELS.get(profile(data), 'Пользовательский профиль')


def validate_settings(settings):
    if not isinstance(settings, dict) or set(settings) != {'accelerator', 'options', 'cpu'}:
        raise ValueError('Профиль должен содержать ускоритель, его опции и CPU.')
    if settings['accelerator'] not in ('whpx', 'tcg'):
        raise ValueError('Выбери ускоритель WHPX или TCG.')
    cpu, options = settings['cpu'], settings['options']
    if not isinstance(cpu, str) or not re.fullmatch(r'[A-Za-z0-9_.,=+:-]{1,512}', cpu):
        raise ValueError('Укажи модель CPU и, при необходимости, её опции без пробелов.')
    if not isinstance(options, str) or len(options) > 1024:
        raise ValueError('Слишком длинные опции ускорителя.')
    keys = set()
    for item in options.split(',') if options else []:
        if not re.fullmatch(r'[A-Za-z][A-Za-z0-9_-]*=[A-Za-z0-9_.:+/-]+', item):
            raise ValueError('Опции ускорителя: имя=значение через запятую, без -accel и пробелов.')
        key = item.split('=', 1)[0]
        if key in keys:
            raise ValueError('Повторная опция ускорителя: ' + key)
        keys.add(key)


def conflicts(extras):
    found = []
    for index, arg in enumerate(extras):
        flag = arg.split('=', 1)[0]
        if flag in ('-accel', '-cpu', '-readconfig'):
            found.append(flag)
        if flag in ('-machine', '-M'):
            value = arg.split('=', 1)[1] if '=' in arg else extras[index + 1] if index + 1 < len(extras) else ''
            if 'accel=' in value:
                found.append(flag + ' accel=')
    return list(dict.fromkeys(found))


def validate(data):
    requested = profile(data)
    if data.get('launch_profile_settings') is not None:
        if requested == 'auto':
            raise ValueError('Авто использует встроенный алгоритм выбора ускорителя.')
        validate_settings(data['launch_profile_settings'])
    extra = data.get('extra_args', [])
    if not isinstance(extra, list) or any(not isinstance(item, str) for item in extra):
        raise ValueError('extra_args должен быть списком строк.')
    found = conflicts(extra)
    if found:
        raise ValueError('Конфликт профиля с extra_args: ' + ', '.join(found) +
                         '. Удали конфликт вручную; остальные параметры сохраняются.')


def initial_accelerator(data):
    requested = profile(data)
    if data.get('launch_profile_settings'):
        return data['launch_profile_settings']['accelerator']
    return 'whpx' if requested == 'auto' else requested


def accelerator_args(accelerator, settings=None):
    if settings is not None:
        validate_settings(settings)
        if accelerator != settings['accelerator']:
            raise ValueError('Ускоритель не соответствует сохранённому профилю.')
        return ['-accel', accelerator + (',' + settings['options'] if settings['options'] else ''),
                '-cpu', settings['cpu']]
    if accelerator not in ('whpx', 'whpx-compatible', 'tcg'):
        raise ValueError('Сначала выбери фактическое ускорение WHPX или TCG.')
    value = {'whpx': 'whpx', 'whpx-compatible': 'whpx,kernel-irqchip=off',
             'tcg': 'tcg,thread=multi'}[accelerator]
    return ['-accel', value, '-cpu', 'max' if accelerator == 'tcg' else 'host']


def failure_hint(message):
    value = message.lower()
    if 'nested virtualization' in value:
        return ('nested', 'QEMU не смог включить вложенную виртуализацию WHPX. '
                'Нужна проверка совместимости QEMU, Windows и процессора; можно выбрать TCG.')
    if 'hypervisor' in value or 'whpx' in value:
        return ('whpx', 'Не удалось создать WHPX VM. Проверь Windows Hypervisor Platform и виртуализацию; '
                'системные настройки Doctor не меняет. Можно выбрать TCG.')
    if any(word in value for word in ('invalid accelerator', 'not supported', 'unsupported', 'invalid parameter', 'invalid option')):
        return ('unsupported', 'Эта сборка QEMU не принимает выбранные параметры. Проверь версию и доступные ускорители.')
    return ('qemu-error', 'QEMU завершил проверку с ошибкой. Подробности ниже; автоматического исправления нет.')


def probe(exe, accelerator, timeout=10, popen=subprocess.Popen, settings=None):
    """No disks, firmware overrides, network backends or inherited guest arguments.

    QMP query-status after machine initialization proves creation, not guest boot.
    QMP requests are sequential; EOF must not abort initialization prematurely.
    kill/wait targets only the Popen child handle.
    """
    args = [str(exe), '-name', 'vm-doctor-probe', '-machine', 'q35'] + accelerator_args(accelerator, settings)
    args += ['-m', '128', '-smp', '1', '-S', '-nodefaults', '-nic', 'none',
             '-display', 'none', '-serial', 'none', '-monitor', 'none', '-qmp', 'stdio']
    child = None
    reader = None
    errors = tempfile.TemporaryFile()
    try:
        child = popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors,
                      shell=False, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        incoming = queue.Queue()
        def read():
            try:
                for line in child.stdout:
                    incoming.put(line)
            finally:
                incoming.put(None)
        reader = threading.Thread(target=read, daemon=True)
        reader.start()
        deadline = time.monotonic() + timeout
        def receive(identifier=None):
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise subprocess.TimeoutExpired(args, timeout)
                try:
                    line = incoming.get(timeout=remaining)
                except queue.Empty:
                    raise subprocess.TimeoutExpired(args, timeout)
                if line is None:
                    raise EOFError('QEMU закрыл QMP.')
                try:
                    item = json.loads(line.decode('utf-8', errors='replace'))
                except ValueError:
                    continue
                if isinstance(item, dict) and (('QMP' in item) if identifier is None else item.get('id') == identifier):
                    if 'error' in item:
                        raise EOFError(str(item['error']))
                    return item
        def request(command):
            child.stdin.write((json.dumps({'execute': command, 'id': command}) + '\n').encode('ascii'))
            child.stdin.flush()
            return receive(command).get('return')
        try:
            receive()
            request('qmp_capabilities')
            status = request('query-status')
            if not isinstance(status, dict) or status.get('status') not in ('paused', 'prelaunch'):
                raise EOFError('Не подтверждена остановленная временная VM.')
            request('quit')
            child.wait(timeout=max(0.05, deadline - time.monotonic()))
            if child.returncode == 0:
                return dict(state='ok', code='ready', accelerator=accelerator,
                            detail='QEMU создал временную VM и ответил на QMP; гостевая ОС не загружалась.')
        except (EOFError, BrokenPipeError):
            child.wait(timeout=max(0.05, deadline - time.monotonic()))
        errors.seek(0)
        detail = errors.read().decode('utf-8', errors='replace')[-4000:].strip()
        if child.returncode == 0 or any(word in detail.lower() for word in ('80070005', 'access is denied', 'access denied', 'permission denied')):
            return dict(state='unknown', code='permission-or-protocol', accelerator=accelerator,
                        detail='Результат не подтверждён; возможен запрет доступа к ускорителю. ' + detail)
        code, advice = failure_hint(detail)
        return dict(state='failed', code=code, accelerator=accelerator, detail=advice + '\n' + detail)
    except subprocess.TimeoutExpired:
        return dict(state='unknown', code='timeout', accelerator=accelerator,
                    detail='Проверка ускорения не ответила вовремя; её процесс будет завершён.')
    except OSError as error:
        return dict(state='unknown', code='process', accelerator=accelerator,
                    detail='Не удалось запустить проверку: ' + str(error))
    finally:
        if child is not None and child.poll() is None:
            child.kill()
            child.wait(timeout=5)
        if reader is not None:
            reader.join(timeout=1)
        if child is not None:
            with suppress(OSError):
                child.stdin.close()
            with suppress(OSError):
                child.stdout.close()
        errors.close()


def choose(exe, data, check=None):
    validate(data)
    requested = profile(data)
    check = check or probe
    first = initial_accelerator(data)
    settings = data.get('launch_profile_settings')
    result = check(exe, first, settings=settings) if settings is not None else check(exe, first)
    if result['state'] == 'ok':
        return first, result
    if requested == 'auto' and result['state'] == 'failed':
        fallback = check(exe, 'tcg')
        if fallback['state'] == 'ok':
            fallback = dict(fallback, detail='WHPX недоступен: ' + result['detail'] +
                            '\nВыбран TCG: программная эмуляция будет медленнее.')
            return 'tcg', fallback
        raise ValueError('Не прошли обе проверки ускорения. WHPX: ' + result['detail'] + '\nTCG: ' + fallback['detail'])
    raise ValueError(result['detail'] + '\nОткрой VM Doctor (H) или выбери другой профиль: E → 8. '
                     'Настоящая VM ещё не запускалась.')


def runtime_accelerator(port, timeout=3):
    """Read the command of the exact QMP listener process, not a saved profile."""
    if os.name != 'nt':
        return None
    from vm_runtime import watch_qmp_process
    watcher = watch_qmp_process(port)
    try:
        import shutil
        powershell = shutil.which('powershell.exe') or shutil.which('pwsh.exe')
        if not powershell:
            return None
        command = ('[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); '
                   '$ErrorActionPreference="Stop"; (Get-CimInstance Win32_Process -Filter "ProcessId = ' +
                   str(int(watcher.pid)) + '").CommandLine')
        result = subprocess.run([powershell, '-NoProfile', '-NonInteractive', '-Command', command],
                                capture_output=True, timeout=timeout, encoding='utf-8', errors='replace',
                                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        import re
        match = re.search(r'(?:^|\s)-accel\s+"?(whpx|tcg)(?=[,\s"]|$)', result.stdout)
        return match[1] if match and result.returncode == 0 else None
    finally:
        watcher.close()
