"""Logical stand definitions. No guest, disk or network mutations."""
import json
import os
from pathlib import Path
import re
import tempfile
import time
import uuid
from vm_runtime import vm_lock

MAX_BYTES = 1024 * 1024


def replace_file(source,target,safe):
    """Windows scanners can briefly deny replacement with 5 as well as 32/33.

    Retry the same atomic rename only. Never remove the destination or change ACLs.
    """
    for attempt in range(6):
        safe(target)
        try:os.replace(source,target);return
        except OSError as error:
            if getattr(error,'winerror',None) not in (5,32,33) or attempt==5:raise
            time.sleep(.02*(attempt+1))


def identifier(value):
    if not isinstance(value, str) or not re.fullmatch(r'[0-9a-f]{32}', value):
        raise ValueError('Некорректный ID стенда.')
    return value


def validate(row):
    required = {'schema_version', 'id', 'revision', 'name', 'description', 'members', 'links', 'order'}
    if not isinstance(row, dict) or type(row.get('schema_version')) is not int or row['schema_version'] not in (1, 2) or set(row) != (required | ({'startup'} if row['schema_version']==2 else set())):
        raise ValueError('Неизвестная схема стенда.')
    identifier(row['id'])
    if type(row['revision']) is not int or row['revision'] < 0:
        raise ValueError('Некорректная ревизия.')
    for key, limit in [('name', 120), ('description', 4000)]:
        value = row[key]
        if not isinstance(value, str) or len(value) > limit or '\0' in value:
            raise ValueError('Некорректное поле: ' + key)
    if not row['name'].strip() or any(ord(c) < 32 for c in row['name']):
        raise ValueError('Укажи название стенда одной строкой.')
    members = row['members']
    if not isinstance(members, list) or not 1 <= len(members) <= 64:
        raise ValueError('Выбери от 1 до 64 машин.')
    if any(not isinstance(m, str) or not re.fullmatch(r'[a-zA-Z0-9_-]{1,120}', m) for m in members):
        raise ValueError('Некорректная ссылка на VM.')
    if len(set(members)) != len(members) or len(set(m.casefold() for m in members)) != len(members):
        raise ValueError('Повторяющиеся VM.')
    if not isinstance(row['order'], list) or any(not isinstance(m, str) for m in row['order']) or len(row['order']) != len(members) or set(row['order']) != set(members):
        raise ValueError('Порядок должен включать каждую VM ровно один раз.')
    if row['schema_version']==1 and row['links'] != []:
        raise ValueError('Для связей нужна схема стенда 2.')
    if row['schema_version']==2:
        from vm_stand_plan import validate_topology
        validate_topology(row)
    return row


class Store:
    def __init__(self, root):
        self.root = Path(root)
        self.folder = self.root / 'stands'

    def safe(self, path):
        for part in (path, *path.parents):
            if part.is_symlink() or (part.exists() and getattr(part.stat(), 'st_file_attributes', 0) & 1024):
                raise ValueError('Ссылки и перенаправленные каталоги запрещены.')

    def read(self, key):
        path = self.folder / (identifier(key) + '.json')
        self.safe(path)
        if path.stat().st_nlink > 1:
            raise ValueError('Жёсткие ссылки на стенды запрещены.')
        if path.stat().st_size > MAX_BYTES:
            raise ValueError('Слишком большой файл стенда.')
        row = validate(json.loads(path.read_text(encoding='utf-8')))
        if row['id'] != key:
            raise ValueError('ID файла не совпадает с содержимым.')
        return row

    def list(self):
        self.safe(self.folder)
        rows, errors = [], []
        if self.folder.exists():
            for path in sorted(self.folder.glob('*.json')):
                try:
                    rows.append(self.read(path.stem))
                except (ValueError, OSError) as error:
                    errors.append(dict(file=path.name, error=str(error)))
        return dict(rows=rows, errors=errors, schema_version=2)

    def save(self, incoming, expected):
        row = validate(dict(incoming))
        with vm_lock(self.root, 'stand-' + row['id']):
            self.safe(self.folder)
            self.folder.mkdir(parents=True, exist_ok=True)
            path = self.folder / (row['id'] + '.json')
            self.safe(path)
            old = self.read(row['id']) if path.exists() else None
            actual = old['revision'] if old else 0
            if type(expected) is not int or expected != actual or row['revision'] != actual:
                raise ValueError('Стенд изменился. Обнови список перед сохранением.')
            if old is None and expected != 0:
                raise ValueError('Стенд не найден.')
            row['revision'] = actual + 1
            row['name'] = row['name'].strip()
            content = json.dumps(row, ensure_ascii=False).encode('utf-8')
            if len(content) > MAX_BYTES:
                raise ValueError('Слишком большой стенд.')
            temporary = None
            try:
                with tempfile.NamedTemporaryFile(dir=self.folder, suffix='.tmp', delete=False) as stream:
                    temporary = Path(stream.name)
                    stream.write(content)
                    stream.flush()
                    os.fsync(stream.fileno())
                replace_file(temporary,path,self.safe)
            finally:
                if temporary is not None and temporary.exists():
                    temporary.unlink()
            return row

    def remove(self,key,expected):
        with vm_lock(self.root,'stand-'+identifier(key)):
            row=self.read(key)
            if type(expected) is not int or row['revision']!=expected:raise ValueError('Стенд изменился. Обнови список.')
            path=self.folder/(key+'.json');self.safe(path);path.unlink()
            return dict(removed=key,vm_files_changed=False,runtime_preserved=True)


def create(name, members):
    return dict(schema_version=1, id=uuid.uuid4().hex, revision=0, name=name,
                description='', members=members, links=[], order=list(members))
