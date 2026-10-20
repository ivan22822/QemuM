"""Explicit disk drivers; VDI is never treated as qcow2 or converted implicitly."""
from pathlib import Path


def disk_format(data):
    value = data.get('disk_format') or ('vdi' if Path(data.get('disk_path', '')).suffix.lower() == '.vdi' else 'qcow2')
    if value not in ('qcow2', 'vdi'):
        raise ValueError('Формат основного диска: qcow2 или vdi.')
    return value


def new_disk_format(path):
    suffix = Path(path).suffix.lower()
    if suffix not in ('.qcow2', '.vdi'):
        raise ValueError('Новый диск должен иметь расширение .qcow2 или .vdi.')
    return suffix[1:]
