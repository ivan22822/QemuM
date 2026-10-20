"""Explicit NIC configuration and conservative, opt-in legacy import."""
from copy import deepcopy
import ipaddress
import re

IDENT = re.compile(r'^[A-Za-z_][A-Za-z0-9_.-]{0,63}$')
MODEL = re.compile(r'^[A-Za-z0-9_][A-Za-z0-9_.-]{0,79}$')


def parts(text):
    if not isinstance(text, str) or any(c in text for c in '\x00\r\n'):
        raise ValueError('Сетевые опции должны быть одной строкой без нулевых символов.')
    if not text:
        return []
    result, value, index = [], [], 0
    while index < len(text):
        if text[index] == ',' and index + 1 < len(text) and text[index + 1] == ',':
            value.append(',,'); index += 2
        elif text[index] == ',':
            result.append(''.join(value)); value = []; index += 1
        else:
            value.append(text[index]); index += 1
    result.append(''.join(value))
    return result


def pairs(text):
    result = []
    for part in parts(text):
        if '=' not in part:
            raise ValueError('Дополнительные сетевые опции: ожидается ключ=значение.')
        key, value = part.split('=', 1)
        if not re.fullmatch(r'[A-Za-z0-9_.-]+', key):
            raise ValueError('Недопустимое имя сетевой опции: ' + key)
        result.append((key, value.replace(',,', ',')))
    return result


def option(key, value):
    return key + '=' + str(value).replace(',', ',,')


def string(row, key, default=''):
    value = row.get(key, default)
    if not isinstance(value, str) or any(c in value for c in '\x00\r\n'):
        raise ValueError('Недопустимое строковое поле адаптера: ' + key)
    return value


def forwarding(rule):
    if not isinstance(rule, str):
        raise ValueError('Правило перенаправления должно быть строкой.')
    match = re.fullmatch(r'(tcp|udp):([^:]*):(\d+)-([^:]*):(\d+)', rule)
    if not match:
        raise ValueError('Проброс порта: tcp:127.0.0.1:2222-:22 или udp:127.0.0.1:5353-:53.')
    for address in (match[2], match[4]):
        if address:
            ipaddress.IPv4Address(address)
    if any(not 1 <= int(match[index]) <= 65535 for index in (3, 5)):
        raise ValueError('Порты перенаправления: от 1 до 65535.')
    return match


def adapter_specs(row):
    if not isinstance(row, dict):
        raise ValueError('Адаптер должен быть объектом JSON.')
    identifier, backend, model = string(row, 'id'), string(row, 'type', 'user'), string(row, 'model', 'virtio-net-pci')
    if not IDENT.fullmatch(identifier) or not MODEL.fullmatch(backend) or not MODEL.fullmatch(model):
        raise ValueError('Проверь ID, тип сети и модель адаптера: ' + identifier)
    values = [('id', identifier)]
    if backend == 'user':
        network = string(row, 'net')
        subnet = ipaddress.IPv4Network(network, strict=False) if network else None
        if subnet is not None:
            values.append(('net', str(subnet)))
        start = string(row, 'dhcpstart')
        if start:
            address = ipaddress.IPv4Address(start)
            if subnet is not None and address not in subnet:
                raise ValueError('Начало DHCP должно входить в подсеть адаптера.')
            values.append(('dhcpstart', start))
        if type(row.get('restrict', False)) is not bool:
            raise ValueError('Изоляция NAT должна быть логическим значением.')
        if row.get('restrict'):
            values.append(('restrict', 'on'))
        rules = row.get('hostfwd', [])
        if not isinstance(rules, list):
            raise ValueError('Правила проброса портов должны быть списком.')
        for rule in rules:
            forwarding(rule)
            values.append(('hostfwd', rule))
    elif backend == 'socket' and row.get('mode') in ('listen', 'connect'):
        address = string(row, 'address', '127.0.0.1')
        if not re.fullmatch(r'[A-Za-z0-9_.-]+', address):
            raise ValueError('Для TCP-сети укажи IPv4-адрес или имя узла без двоеточий.')
        port = row.get('port', 1234)
        if type(port) is not int or not 1 <= port <= 65535:
            raise ValueError('Порт TCP-сети: от 1 до 65535.')
        values.append((row['mode'], address + ':' + str(port)))
    elif backend == 'tap':
        interface = string(row, 'ifname')
        if not interface:
            raise ValueError('Укажи имя уже существующего TAP-адаптера Windows.')
        values.extend([('ifname', interface), ('script', 'no'), ('downscript', 'no')])
    extra = pairs(string(row, 'backend_options'))
    occupied = {key for key, value in values} | {'id'}
    if any(key in occupied for key, value in extra):
        raise ValueError('Дополнительная сетевая опция дублирует поле адаптера ' + identifier)
    device = [('netdev', identifier)]
    mac = string(row, 'mac')
    if mac:
        if not re.fullmatch(r'(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}', mac) or int(mac[:2], 16) & 1 or mac == '00:00:00:00:00:00':
            raise ValueError('Нужен индивидуальный MAC, например 52:54:00:12:34:56.')
        device.append(('mac', mac.lower()))
    properties = pairs(string(row, 'device_options'))
    if any(key in ('netdev', 'mac') for key, value in properties):
        raise ValueError('netdev и mac задаются отдельными полями адаптера.')
    return backend + ',' + ','.join(option(*item) for item in values + extra), model + ',' + ','.join(option(*item) for item in device + properties)


def extra_options(extras):
    index = 0
    while index < len(extras):
        value = extras[index]
        flag, separator, inline = value.partition('=')
        if flag in ('-netdev', '-device', '-nic', '-net'):
            if separator:
                yield index, 1, flag, inline
            elif index + 1 < len(extras):
                yield index, 2, flag, extras[index + 1]
                index += 1
            else:
                raise ValueError('Нет значения после ' + flag)
        index += 1


def validate(data):
    extras = data.get('extra_args', [])
    if not isinstance(extras, list) or any(not isinstance(item, str) or '\x00' in item for item in extras):
        raise ValueError('extra_args должен быть списком строк без нулевых символов.')
    if 'network_adapters' not in data:
        return
    adapters = data['network_adapters']
    if not isinstance(adapters, list) or len(adapters) > 32:
        raise ValueError('Список сетевых адаптеров: не более 32 записей.')
    identifiers, macs, forwards = set(), set(), []
    for row in adapters:
        adapter_specs(row)
        identifier = row['id']
        if identifier in identifiers:
            raise ValueError('Повторяется ID сетевого адаптера: ' + identifier)
        identifiers.add(identifier)
        mac = row.get('mac', '').lower()
        if mac and mac in macs:
            raise ValueError('Повторяется MAC сетевого адаптера: ' + mac)
        if mac:
            macs.add(mac)
        for rule in row.get('hostfwd', []) if row.get('type', 'user') == 'user' else []:
            match = forwarding(rule)
            key = (match[1], match[2] or '0.0.0.0', int(match[3]))
            for previous in forwards:
                if key[0] == previous[0] and key[2] == previous[2] and (key[1] == previous[1] or '0.0.0.0' in (key[1], previous[1])):
                    raise ValueError('Пересекаются порты перенаправления на хосте: ' + rule)
            forwards.append(key)
    for index, length, flag, spec in extra_options(extras):
        if flag in ('-netdev', '-nic') and spec != 'none':
            tokens = parts(spec)
            for key, value in pairs(','.join(tokens[1:])):
                if key == 'id':
                    if value in identifiers:
                        raise ValueError('ID сети повторяется в Extra args: ' + value)
                    identifiers.add(value)
        elif flag == '-device' and not spec.startswith('{'):
            for key, value in pairs(','.join(parts(spec)[1:])):
                if key == 'netdev' and any(row['id'] == value for row in adapters):
                    raise ValueError('Для этой сети уже есть устройство в списке адаптеров: ' + value)


def network_args(data):
    validate(data)
    if 'network_adapters' not in data:
        disk = data.get('disk_path', data.get('path', '') if data.get('kind') == 'disk' else '')
        return ['-netdev', 'user,id=net0', '-device', 'virtio-net-pci,netdev=net0'] if disk else []
    result = ['-nic', 'none']
    for row in data['network_adapters']:
        backend, device = adapter_specs(row)
        result.extend(['-netdev', backend, '-device', device])
    return result


def from_specs(backend, device):
    back, front = parts(backend), parts(device)
    values, properties = pairs(','.join(back[1:])), pairs(','.join(front[1:]))
    def take(items, key, default=''):
        matches = [value for name, value in items if name == key]
        if len(matches) > 1:
            raise ValueError('Повторяющаяся опция: ' + key)
        items[:] = [(name, value) for name, value in items if name != key]
        return matches[0] if matches else default
    row = dict(type=back[0], model=front[0], id=take(values, 'id'), mac=take(properties, 'mac'))
    if take(properties, 'netdev') != row['id']:
        raise ValueError('Не совпадает ID сетевого устройства.')
    if row['type'] == 'user':
        row.update(net=take(values, 'net'), dhcpstart=take(values, 'dhcpstart'))
        restriction = take(values, 'restrict', 'off')
        if restriction not in ('on', 'off'):
            raise ValueError('Не распознана опция restrict.')
        row['restrict'] = restriction == 'on'
        row['hostfwd'] = [value for key, value in values if key == 'hostfwd']
        values[:] = [(key, value) for key, value in values if key != 'hostfwd']
    elif row['type'] == 'socket':
        modes = [key for key, value in values if key in ('listen', 'connect')]
        if len(modes) == 1:
            endpoint = take(values, modes[0])
            host, port = endpoint.rsplit(':', 1)
            row.update(mode=modes[0], address=host or '127.0.0.1', port=int(port))
    elif row['type'] == 'tap':
        if any(key in ('script', 'downscript') and value != 'no' for key, value in values):
            raise ValueError('TAP со сценариями оставлен в Extra args.')
        row['ifname'] = take(values, 'ifname')
        take(values, 'script'); take(values, 'downscript')
    row['backend_options'] = ','.join(option(*item) for item in values)
    row['device_options'] = ','.join(option(*item) for item in properties)
    adapter_specs(row)
    return row


def import_legacy(data):
    extras = list(data.get('extra_args', []))
    records = list(extra_options(extras))
    existing = deepcopy(data.get('network_adapters', []))
    disk = data.get('disk_path', data.get('path', '') if data.get('kind') == 'disk' else '')
    if 'network_adapters' not in data:
        if not disk and not any(flag in ('-netdev', '-nic', '-net') for index, length, flag, spec in records):
            existing = [dict(id='net0', type='user', model='e1000e')]
        elif disk:
            existing = [dict(id='net0', type='user', model='virtio-net-pci')]
    backends, devices, warnings = {}, {}, []
    for record in records:
        index, length, flag, spec = record
        try:
            tokens = parts(spec)
            options = pairs(','.join(tokens[1:]))
            if flag == '-netdev':
                identifier = next(value for key, value in options if key == 'id')
                backends.setdefault(identifier, []).append(record)
            elif flag == '-device':
                identifier = next(value for key, value in options if key == 'netdev')
                devices.setdefault(identifier, []).append(record)
        except (ValueError, StopIteration):
            continue
    removed = set()
    for identifier, matches in backends.items():
        if len(matches) != 1 or len(devices.get(identifier, [])) != 1:
            warnings.append('Сеть ' + identifier + ' оставлена в Extra args: нет однозначной пары netdev/device.')
            continue
        backend, device = matches[0], devices[identifier][0]
        try:
            row = from_specs(backend[3], device[3])
            if any(item['id'] == row['id'] for item in existing):
                raise ValueError('Этот ID уже занят.')
        except (ValueError, TypeError, KeyError) as error:
            warnings.append(identifier + ': ' + str(error))
            continue
        existing.append(row)
        for index, length, flag, spec in (backend, device):
            removed.update(range(index, index + length))
    remaining = [value for index, value in enumerate(extras) if index not in removed]
    if any(flag in ('-nic', '-net') for index, length, flag, spec in records):
        warnings.append('Параметры -nic/-net оставлены в Extra args; проверь итоговую сеть перед сохранением.')
    candidate = dict(data, network_adapters=existing, extra_args=remaining)
    validate(candidate)
    return dict(adapters=existing, extra_args=remaining, warnings=warnings)
