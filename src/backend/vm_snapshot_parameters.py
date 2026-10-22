"""Opt-in versioned launch parameters; no runtime or appearance fields."""
from copy import deepcopy
import json
from pathlib import Path
import re
from vm_storage import media, safe_file

FIELDS={'ram_mb','cores','screen_width','screen_height','vga','disk_path','disk_format','iso_path',
        'launch_profile','launch_profile_settings','launch_profile_label','network_adapters','extra_args'}
SENSITIVE=re.compile(r'(?i)(password|passwd|secret|token|private.key|key-secret|keyval|BEGIN .*PRIVATE KEY)')


def capture(data):
    value={key:deepcopy(value) for key,value in data.items() if key in FIELDS}
    encoded=json.dumps(value,ensure_ascii=False,allow_nan=False)
    if SENSITIVE.search(encoded):raise ValueError('Параметры содержат потенциальные секреты. Создай снимок без сохранения настроек.')
    if len(encoded.encode('utf-8'))>512*1024:raise ValueError('Слишком большой набор параметров.')
    extras=value.get('extra_args',[])
    if not isinstance(extras,list) or any(not isinstance(item,str) for item in extras):raise ValueError('Extra args должны быть массивом строк.')
    return dict(schema_version=1,launch=value,privacy='Локальные пути, MAC и аргументы сохранены только по явному выбору; не включать в публичный отчёт автоматически.')


def restore(saved,current):
    if not isinstance(saved,dict) or saved.get('schema_version')!=1 or not isinstance(saved.get('launch'),dict) or set(saved['launch'])-FIELDS:
        raise ValueError('Неизвестная схема сохранённых параметров.')
    capture(saved['launch'])
    updated=deepcopy(current)
    for key in FIELDS:updated.pop(key,None)
    updated.update(deepcopy(saved['launch']))
    # Existing appearance, registration identity and QMP endpoint remain local.
    for path,kind in media(updated):
        safe_file(path)
        if not path.is_file():raise ValueError('Сохранённый внешний ресурс отсутствует: '+str(path))
    for key in ('ram_mb','cores'):
        if type(updated.get(key)) is not int or updated[key]<1:raise ValueError('Некорректное значение '+key)
    from vm_profiles import profile,validate_settings
    from vm_network import validate
    from vm import resolution,validate_extra_args
    profile(updated)
    if updated.get('launch_profile_settings'):validate_settings(updated['launch_profile_settings'])
    validate(updated);resolution(updated);validate_extra_args(updated)
    return updated


def differences(saved,current):
    if not saved:return []
    launch=saved.get('launch',{})
    return [dict(field=key,current=current.get(key),saved=launch.get(key)) for key in sorted(FIELDS) if current.get(key)!=launch.get(key)]
