"""Explicit, local-only support bundles. Never snapshot or modify guest disks."""
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import platform
import re
import tempfile
import zipfile

from vm_storage import safe_file

SECTIONS = frozenset(('logs', 'parameters', 'state', 'diagnostics', 'screenshot'))
MAX_LOG = 2 * 1024 * 1024
VERSION = '0.1.0-alpha.1'


def redact(value, paths=()):
    """Best-effort text filtering; screenshots and arbitrary secrets need review."""
    if isinstance(value, dict):
        return {key: '[REDACTED]' if re.search(r'password|passwd|secret|token|credential', key, re.I)
                else redact(item, paths) for key, item in value.items()}
    if isinstance(value, list):
        result = []
        hide_next = False
        for item in value:
            result.append('[REDACTED]' if hide_next else redact(item, paths))
            hide_next = isinstance(item, str) and bool(re.fullmatch(r'--?(password|passwd|secret|token|credential)', item, re.I))
        return result
    if not isinstance(value, str):
        return value
    if re.match(r'^secret(?:,|$)', value, re.I):
        return 'secret,<redacted>'
    for path in sorted(set(str(item) for item in paths if item), key=len, reverse=True):
        value = re.sub(re.escape(path), '<private-path>', value, flags=re.I)
        value = re.sub(re.escape(path.replace('\\', '/')), '<private-path>', value, flags=re.I)
    value = re.sub(r'(?i)\b([a-z][a-z0-9+.-]{0,31}://)[^/\s@]+@', r'\1<credentials>@', value)
    value = re.sub(r'(?i)((?:password|passwd|secret|token|credential)\s*[=:]\s*)(?:"[^"]*"|[^\s,;]+)',
                   r'\1<redacted>', value)
    value = re.sub(r'(?i)[a-z]:[\\/][^\r\n",;]*', '<private-path>', value)
    value = re.sub(r'\\\\[^\\\s]+\\[^\r\n",;]*', '<private-path>', value)
    return value


def export(service, args):
    import vm
    name = service.name(args)
    sections = args.get('sections')
    if not isinstance(sections, list) or not sections or any(item not in SECTIONS for item in sections):
        raise ValueError('Выбери данные для отчёта.')
    if type(args.get('redact', True)) is not bool:
        raise ValueError('Некорректный режим скрытия личных данных.')
    description = args.get('description', '')
    if not isinstance(description, str) or len(description) > 16000:
        raise ValueError('Описание: не более 16000 символов.')
    target = Path(args.get('path', ''))
    if not target.is_absolute() or target.suffix.lower() != '.zip':
        raise ValueError('Выбери полный путь нового ZIP-файла.')
    safe_file(target)
    if target.exists():
        raise ValueError('Файл уже существует. Выбери другое имя.')
    # Reserve the chosen name before doing any collection; never overwrite it.
    with target.open('xb') as output:
        try:
            files, errors = {}, {}
            data = None
            private = [str(Path.home()), str(service.root)]

            def config():
                nonlocal data
                if data is None:
                    data = vm.load(name)
                    private.extend(data.get(key, '') for key in ('disk_path', 'iso_path', 'path'))
                return data

            def add_json(filename, value):
                if args.get('redact', True):
                    value = redact(value, private)
                files[filename] = json.dumps(value, ensure_ascii=False, indent=2).encode('utf-8')

            for section in dict.fromkeys(sections):
                try:
                    if section == 'logs':
                        pattern = re.compile(re.escape(name) + r'-\d{8}-\d{6}-[0-9a-f]{6}\.log')
                        candidates = [p for p in (service.root / 'logs').glob('*.log') if pattern.fullmatch(p.name)]
                        log = max(candidates, key=lambda p: p.stat().st_mtime_ns) if candidates else None
                        if log:
                            safe_file(log)
                            with log.open('rb') as stream:
                                stream.seek(max(0, log.stat().st_size - MAX_LOG))
                                content = stream.read(MAX_LOG).decode('utf-8', 'replace')
                            files['live-log.txt'] = (redact(content, private) if args.get('redact', True)
                                                      else content).encode('utf-8')
                        else:
                            errors['logs'] = 'Журнал QEMU для выбранной VM не найден.'
                        add_json('manager-events.json', service.events.get(name, []))
                    elif section == 'parameters':
                        add_json('parameters.json', config())
                        # This is a preview: Auto is not probed, and no launch preparation runs.
                        add_json('launch-preview.json', dict(
                            note='Предварительные аргументы; Auto и источник загрузки уточняются при запуске.',
                            arguments=vm.build_args(name, config())))
                    elif section == 'state':
                        result = {}
                        for command in ('query-status', 'query-version', 'query-block'):
                            result[command] = service.query(name, config(), command)
                        add_json('qmp-state.json', result)
                    elif section == 'diagnostics':
                        # Passive diagnostics: no accelerator test guests or disk inspection.
                        add_json('diagnostics.json', dict(
                            system=platform.system(), release=platform.release(),
                            windows_version=platform.version(), python=platform.python_version(),
                            configuration_valid=bool(config()),
                            media=[dict(kind=key, exists=Path(config()[key]).is_file())
                                   for key in ('disk_path', 'iso_path') if config().get(key)]))
                    elif section == 'screenshot':
                        with tempfile.TemporaryDirectory(prefix='qemum-screen-') as temporary:
                            screen = Path(temporary) / 'screen.png'
                            service.query(name, config(), 'screendump',
                                          dict(filename=str(screen), format='png'), timeout=10)
                            if screen.stat().st_size > 32 * 1024 * 1024:
                                raise ValueError('Изображение превышает 32 МБ.')
                            content = screen.read_bytes()
                            if not content.startswith(b'\x89PNG\r\n\x1a\n'):
                                raise ValueError('QEMU не вернул PNG.')
                            files['vm-screen.png'] = content
                except Exception as error:
                    errors[section] = str(error) or type(error).__name__
            add_json('report.json', dict(schema=1, version=VERSION,
                created=datetime.now(timezone.utc).isoformat(), vm=name, description=description,
                selected=list(dict.fromkeys(sections)), redacted=args.get('redact', True),
                errors=errors, files=sorted(files)))
            files['README.txt'] = (
                'QemuM — локальный отчёт о проблеме\n\n'
                'Открой report.json: там описание, выбранные разделы и ошибки сбора.\n'
                'Live log ограничен последними 2 МБ. Состояние получено без остановки VM.\n'
                'Проверь каждый файл перед публикацией. Маскирование не гарантирует удаления всех секретов.\n'
                'Скриншот не маскируется: на нём могут быть пароли, документы и переписка.\n'
                'Ничего не отправлено автоматически. Прикрепи проверенный ZIP вручную:\n'
                'https://github.com/ivan22822/QemuM/issues/new/choose\n'
            ).encode('utf-8')
            with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
                for filename, content in files.items():
                    archive.writestr(filename, content)
            output.flush()
            os.fsync(output.fileno())
        except BaseException:
            output.close()
            target.unlink(missing_ok=True)
            raise
    return dict(path=str(target), files=sorted(files), errors=redact(errors, private))
