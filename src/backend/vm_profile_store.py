"""User launch presets; VM configurations keep independent snapshots."""
import hashlib
import json
import os
import re
import tempfile
import uuid

from vm_profiles import PROFILES, LABELS, accelerator_args, validate_settings
from vm_runtime import vm_lock


def defaults():
    rows = []
    for key in PROFILES:
        settings = None
        if key != 'auto':
            args = accelerator_args(key)
            accelerator, _, options = args[1].partition(',')
            settings = dict(accelerator=accelerator, options=options, cpu=args[3])
        rows.append(dict(id=key, label=LABELS[key], settings=settings, builtin=True))
    return rows


def read(root):
    path = root / 'launch-profiles.json'
    raw = path.read_bytes() if path.exists() else b''
    saved = json.loads(raw.decode('utf-8-sig')) if raw else []
    if not isinstance(saved, list):
        raise ValueError('Повреждён каталог профилей запуска.')
    rows = {row['id']: row for row in defaults()}
    for row in saved:
        validate_row(row)
        if row['id'] == 'auto':
            raise ValueError('Автоматический выбор ускорителя не редактируется.')
        rows[row['id']] = dict(row, builtin=row['id'] in PROFILES)
    return dict(profiles=list(rows.values()), hash=hashlib.sha256(raw).hexdigest())


def validate_row(row):
    if not isinstance(row, dict) or not isinstance(row.get('id'), str):
        raise ValueError('Некорректный профиль.')
    if row['id'] not in PROFILES and not re.fullmatch(r'custom-[0-9a-f]{32}', row['id']):
        raise ValueError('Некорректный ID профиля.')
    if (not isinstance(row.get('label'), str) or not 1 <= len(row['label'].strip()) <= 80
            or any(ord(char) < 32 or ord(char) == 127 for char in row['label'])):
        raise ValueError('Название профиля: от 1 до 80 символов.')
    validate_settings(row.get('settings'))


def change(root, expected, row=None, remove=None):
    root.mkdir(parents=True, exist_ok=True)
    with vm_lock(root, 'launch-profile-catalog'):
        current = read(root)
        if current['hash'] != expected:
            raise ValueError('Каталог изменён в другом окне. Открой его заново.')
        rows = {item['id']: item for item in current['profiles']}
        if remove:
            if remove in PROFILES:
                raise ValueError('Встроенные профили удалить нельзя.')
            if remove not in rows:
                raise ValueError('Профиль уже удалён.')
            del rows[remove]
        else:
            row = dict(row)
            row['id'] = row.get('id') or 'custom-' + uuid.uuid4().hex
            validate_row(row)
            if row['id'] == 'auto':
                raise ValueError('Автоматический выбор ускорителя не редактируется.')
            rows[row['id']] = dict(id=row['id'], label=row['label'].strip(), settings=row['settings'])
        # Persist only overrides and custom presets. No VM files are touched.
        saved = [item for item in rows.values() if item['id'] != 'auto']
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(dir=root, suffix='.tmp', mode='w', encoding='utf-8', delete=False) as stream:
                temporary = stream.name
                json.dump(saved, stream, ensure_ascii=False, indent=2)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, root / 'launch-profiles.json')
        finally:
            if temporary and os.path.exists(temporary):
                os.unlink(temporary)
        result = read(root)
        result['selected_id'] = 'whpx' if remove else row['id']
        return result
