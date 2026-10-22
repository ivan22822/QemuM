"""Versioned, fail-closed snapshot metadata. No disk or process operations here."""
from contextlib import ExitStack, contextmanager
from copy import deepcopy
import hashlib
import json
import os
from pathlib import Path
import tempfile
import time
import uuid
from vm_runtime import vm_lock
from vm_storage import safe_file

FINAL = {'completed', 'failed', 'cancelled'}
CATALOG_SCHEMA = 2


def migrate_catalog(value):
    """Pure migrations: opening a V1 file never changes it on disk."""
    result = deepcopy(value)
    if result.get('schema_version') == 1:
        result['schema_version'] = 2
        settings = result.setdefault('settings', {})
        if not isinstance(settings, dict):
            raise ValueError('Повреждены настройки V1.')
        settings.setdefault('schedule', dict(enabled=False))
        settings.setdefault('parameters_schema', 1)
    if result.get('schema_version') != CATALOG_SCHEMA or not isinstance(result.get('settings'), dict):
        raise ValueError('Неизвестная или повреждённая схема каталога снимков.')
    return result


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False,
                                     separators=(',', ':')).encode('utf-8')).hexdigest()


def identifier(value):
    try:
        return str(uuid.UUID(value))
    except (ValueError, TypeError, AttributeError) as error:
        raise ValueError('Неверный ID операции снимка.') from error


@contextmanager
def operation_lock(root, name, disks=()):
    # One namespace for GUI and terminals. Serialize snapshot operations before
    # VM and physical-file locks; sorted identities also cover hard-link aliases.
    with ExitStack() as stack:
        stack.enter_context(vm_lock(root, '__snapshot-domain'))
        stack.enter_context(vm_lock(root, name))
        for key in sorted(set(disks)):
            stack.enter_context(vm_lock(root, '__snapshot-disk-' + key))
        yield


class Store:
    def __init__(self, root, name):
        self.name = name
        self.root = Path(root)
        self.folder = Path(root).parent / 'snapshot-state' / hashlib.sha256((name.casefold() if os.name == 'nt' else name).encode()).hexdigest()
        self.path = self.folder / 'catalog.json'

    @staticmethod
    def read(path):
        safe_file(path)
        if not path.exists():
            return None
        try:
            if path.stat().st_size > 16 * 1024 * 1024:
                raise ValueError('файл метаданных превышает допустимый объём')
            value = json.loads(path.read_text(encoding='utf-8'))
            if not isinstance(value, dict) or value.get('schema_version') not in (1, 2):
                raise ValueError('неизвестная схема')
            return value
        except (ValueError, UnicodeError) as error:
            raise ValueError('Журнал или каталог снимков повреждён/несовместим. Файл сохранён; запуск заблокирован: ' + str(path)) from error

    def catalog(self):
        value = self.read(self.path)
        if value is None:
            return dict(schema_version=CATALOG_SCHEMA, name=self.name, revision=0, groups=[], settings=dict(schedule=dict(enabled=False), parameters_schema=1))
        value = migrate_catalog(value)
        if str(value.get('name', '')).casefold() != self.name.casefold() or not isinstance(value.get('groups'), list) or type(value.get('revision')) is not int:
            raise ValueError('Некорректная принадлежность или структура каталога снимков.')
        seen = set()
        for group in value['groups']:
            if not isinstance(group, dict):
                raise ValueError('Повреждена запись группы снимков.')
            identifier(group.get('id'))
            if group['id'] in seen or not isinstance(group.get('name'), str) or not group['name']:
                raise ValueError('Неверный или повторяющийся ID группы снимков.')
            seen.add(group['id'])
            if not isinstance(group.get('disks'), list) or not group['disks']:
                raise ValueError('Повреждён состав группы снимков.')
            for disk in group['disks']:
                if not isinstance(disk, dict) or not isinstance(disk.get('file_id'), list) or len(disk['file_id']) != 2 or not all(type(part) is int for part in disk['file_id']) or not isinstance(disk.get('path'), str) or not isinstance(disk.get('snapshot_id'), str):
                    raise ValueError('Повреждена идентичность диска в каталоге снимков.')
        return value

    def atomic_bytes(self, path, encoded):
        safe_file(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        safe_file(path)
        descriptor, temporary = tempfile.mkstemp(prefix='.snapshot-', suffix='.tmp', dir=path.parent)
        try:
            with os.fdopen(descriptor, 'wb') as stream:
                stream.write(encoded)
                stream.flush()
                os.fsync(stream.fileno())
            # Windows scanners and concurrent readonly catalogue polling can
            # briefly hold a non-delete-shared handle. Retry publication only;
            # never replay an image mutation or discard the durable intent.
            for attempt in range(10):
                try:
                    safe_file(path)
                    os.replace(temporary, path)
                    break
                except PermissionError:
                    if attempt==9:raise
                    time.sleep(.02*(attempt+1))
            # fsync+replace is a publication protocol, not a power-loss guarantee.
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)

    def atomic(self, path, value):
        self.atomic_bytes(path, json.dumps(value, ensure_ascii=False, sort_keys=True, allow_nan=False).encode('utf-8'))

    def migrate(self):
        with operation_lock(self.root, self.name):
            self.assert_clear()
            raw = self.read(self.path)
            current = self.catalog()
            if raw is None or raw['schema_version'] == CATALOG_SCHEMA:
                return False
            original = self.path.read_bytes()
            backup = self.folder / 'catalog.v1.json.bak'
            safe_file(backup)
            if backup.exists() and backup.read_bytes() != original:
                raise ValueError('Копия V1 уже существует и отличается; миграция остановлена.')
            if not backup.exists():
                self.atomic_bytes(backup, original)
            if self.path.read_bytes() != original:
                raise ValueError('Каталог изменился во время миграции.')
            self.atomic(self.path, current)
            return True

    def publish(self, group, expected):
        current = self.catalog()
        existing = next((item for item in current['groups'] if item['id'] == group['id']), None)
        if existing:
            if existing != group:
                raise ValueError('UUID группы уже занят другими метаданными.')
            return
        if not self.matches_catalog(expected):
            raise ValueError('Каталог снимков изменился. Требуется повторная сверка.')
        if self.path.exists():
            original = self.path.read_bytes()
            if self.read(self.path)['schema_version'] == 1:
                backup = self.folder / 'catalog.v1.json.bak'
                if not backup.exists():
                    self.atomic_bytes(backup, original)
            self.atomic_bytes(self.folder / 'catalog.previous.json', original)
        current['groups'].append(group)
        current['revision'] += 1
        self.atomic(self.path, current)

    def matches_catalog(self, expected):
        current=self.catalog()
        if digest(current)==expected:return True
        raw=self.read(self.path)
        if raw is not None:
            return raw['schema_version']==1 and digest(raw)==expected
        # A V1 operation can have been journalled before the first catalog write.
        legacy=dict(schema_version=1,name=self.name,revision=0,groups=[])
        return digest(legacy)==expected

    def replace_catalog(self, value, expected):
        current=self.catalog()
        if digest(current)!=expected:
            raise ValueError('Каталог изменился. Обнови список и повтори действие.')
        if value.get('schema_version')!=CATALOG_SCHEMA or value.get('name')!=current['name'] or value.get('revision')!=current['revision']+1:
            raise ValueError('Некорректная версия обновляемого каталога.')
        if self.path.exists():
            original=self.path.read_bytes()
            if self.read(self.path)['schema_version']==1 and not (self.folder/'catalog.v1.json.bak').exists():
                self.atomic_bytes(self.folder/'catalog.v1.json.bak',original)
            self.atomic_bytes(self.folder/'catalog.previous.json',original)
        self.atomic(self.path,value)

    def operation_path(self, operation_id):
        return self.folder / 'operations' / (identifier(operation_id) + '.json')

    def operation(self, operation_id):
        value = self.read(self.operation_path(operation_id))
        if value and (value.get('schema_version') != 1 or value.get('id') != identifier(operation_id) or str(value.get('name', '')).casefold() != self.name.casefold() or
                      value.get('state') not in FINAL | {'planned', 'validating', 'running', 'verifying', 'partial', 'uncertain', 'recovery-required'} or
                      not isinstance(value.get('steps'), list) or value.get('action') not in ('create', 'restore', 'adopt', 'prepare-offline', 'metadata', 'offline-create', 'offline-delete', 'offline-restore','schedule','external','ram-create','ram-restore','export')):
            raise ValueError('Некорректный журнал операции. Запуск заблокирован.')
        if value and value['action'] in ('create', 'prepare-offline') and (not isinstance(value.get('plan'), dict) or value['plan'].get('id') != value['id'] or str(value['plan'].get('name', '')).casefold() != self.name.casefold()):
            raise ValueError('Повреждён план в журнале. Запуск заблокирован.')
        return value

    def operations(self):
        safe_file(self.folder / 'operations' / '.path-check')
        result = []
        for path in sorted((self.folder / 'operations').glob('*.json')):
            value = self.operation(path.stem)
            if value:
                result.append(value)
        return result

    def save_operation(self, value):
        # Never rotate recovery assets or pending journals. Bound admission of
        # NEW operations instead; existing intents must always remain writable.
        path=self.operation_path(value['id'])
        if not path.exists() and sum(1 for _ in path.parent.glob('*.json'))>=2000:
            raise ValueError('Лимит 2000 журналов снимков. Сохрани диагностику и архивируй завершённую историю вручную; незавершённые журналы не удаляй.')
        self.atomic(self.operation_path(value['id']), value)

    def agent_pending(self):
        result=[]
        for path in sorted((self.folder/'agent').glob('*.json')):
            value=self.read(path)
            if value and (value.get('owned') or value.get('state') in ('freeze-intent','frozen','thaw-intent','thaw-required')) and value.get('state')!='thawed':
                result.append(value)
        return result

    def assert_clear(self):
        self.catalog()
        pending = [item for item in self.operations() if item['state'] not in FINAL]
        if pending:
            raise ValueError('Запуск заблокирован: нужна сверка операции снимков ' + pending[0]['id'] + '. Открой «Незавершённые операции».')
        if self.agent_pending():raise ValueError('Guest Agent: thaw не подтверждён. Журнал сохранён; проверь гостевую файловую систему перед продолжением.')


def assert_can_start(root, name):
    store=Store(root,name);store.assert_clear()
    if store.catalog()['settings'].get('external'):
        from vm_snapshot_external import ExternalSnapshots
        from vm_snapshot_service import manager_service
        service=manager_service(root,name);graph=ExternalSnapshots(service).check_graph(store.catalog())
        if __import__('vm_storage').file_key(service.load()['disk_path'])!=graph['nodes'][graph['active']]['path']:
            raise ValueError('Конфигурация не соответствует зарегистрированной вершине внешней цепочки.')
