"""Pure stand topology validation and effective NIC planning. Never starts guests."""
from copy import deepcopy
import re

from vm_network import import_legacy, validate as validate_network, network_args


def upgrade(row):
    result=deepcopy(row)
    if result['schema_version']==1:
        result['schema_version']=2
        result['startup']={name:dict(delay=0,timeout=60,ready='qmp') for name in result['members']}
    return result


def validate_topology(row):
    members=row['members'];order=row['order']
    if not isinstance(row['links'],list) or len(row['links'])>128:
        raise ValueError('Стенд поддерживает до 128 соединений.')
    occupied=set();ids=set()
    for link in row['links']:
        if not isinstance(link,dict) or set(link)!={'id','listen','connect'} or not isinstance(link['id'],str) or not re.fullmatch('[0-9a-f]{32}',link['id']) or link['id'] in ids:
            raise ValueError('Некорректный или повторяющийся ID соединения.')
        ids.add(link['id']);ends=[]
        for role in ('listen','connect'):
            end=link[role]
            if not isinstance(end,dict) or set(end)!={'vm','nic'} or end['vm'] not in members or not isinstance(end['nic'],str) or not re.fullmatch('[A-Za-z_][A-Za-z0-9_.-]{0,63}',end['nic']):
                raise ValueError('Выбери участника и сетевой адаптер.')
            key=(end['vm'],end['nic'])
            if key in occupied:raise ValueError('Адаптер уже участвует в соединении.')
            occupied.add(key);ends.append(end['vm'])
        if ends[0]==ends[1]:raise ValueError('Соединение должно связывать две разные VM.')
        if order.index(ends[0])>=order.index(ends[1]):
            raise ValueError('Сначала должен запускаться слушатель '+ends[0]+', затем '+ends[1]+'. Порядок не изменён.')
    settings=row.get('startup')
    if not isinstance(settings,dict) or set(settings)!=set(members):raise ValueError('Нужны условия запуска каждого участника.')
    for step in settings.values():
        if not isinstance(step,dict) or set(step)!=({'delay','timeout','ready','tcp_port'} if step.get('ready')=='tcp' else {'delay','timeout','ready'}) or type(step['delay']) is not int or not 0<=step['delay']<=300 or type(step['timeout']) is not int or not 1<=step['timeout']<=600 or step['ready'] not in ('process','qmp','tcp'):
            raise ValueError('Задержка: 0–300 секунд, тайм-аут: 1–600; готовность: process, qmp или tcp.')
        if step['ready']=='tcp' and (type(step['tcp_port']) is not int or not 1024<=step['tcp_port']<=65535):
            raise ValueError('TCP-проверка: локальный порт от 1024 до 65535.')


def effective(row, configs, ports):
    """Replace only explicitly connected NICs in detached copies. External NICs stay intact."""
    row=upgrade(row);validate_topology(row)
    result=deepcopy(configs)
    if len(set(ports.values()))!=len(ports):raise ValueError('Служебные порты должны быть уникальны.')
    for link in row['links']:
        port=ports[link['id']]
        if type(port) is not int or not 1024<=port<=65535:raise ValueError('Некорректный служебный порт.')
        macs=set()
        for role in ('listen','connect'):
            endpoint=link[role];name=endpoint['vm'];data=result[name]
            extras=data.get('extra_args',[])
            for index,arg in enumerate(extras):
                flag=arg.split('=',1)[0]
                value=arg.split('=',1)[1] if '=' in arg else extras[index+1] if index+1<len(extras) else ''
                network_device=flag=='-device' and ('netdev=' in value or re.match(r'^(e1000|igb|rtl8139|ne2k|pcnet|vmxnet|virtio-net)',value))
                if flag in ('-net','-netdev','-nic','-readconfig') or network_device:
                    raise ValueError('Проверь конфликт Extra args и управляемой сети: '+name)
            nics=import_legacy(data)['adapters']
            matches=[nic for nic in nics if nic['id']==endpoint['nic']]
            if len(matches)!=1:raise ValueError('Адаптер отсутствует: '+name+'/'+endpoint['nic'])
            nic=matches[0];mac=nic.get('mac','').lower()
            if not mac or mac in macs:raise ValueError('Для внутренней связи нужны разные явные MAC.')
            macs.add(mac)
            replacement={key:nic[key] for key in ('id','model','mac')}
            replacement.update(type='socket',mode=role,address='127.0.0.1',port=port)
            data['network_adapters']=[replacement if n['id']==nic['id'] else n for n in nics]
            validate_network(data)
    return result


def plan(row, configs, ports):
    row=upgrade(row);validate_topology(row)
    effective_configs=effective(row,configs,ports)
    return dict(stand=row,configs=effective_configs,order=list(row['order']),
                networks={name:network_args(data) for name,data in effective_configs.items()},
                ram_mb=sum(data.get('ram_mb',0) for data in configs.values()),
                cores=sum(data.get('cores',0) for data in configs.values()),
                warning='Выбранные для связей адаптеры временно используют socket вместо прежней сети; для NAT/hostfwd нужен отдельный адаптер. IP и DHCP внутри гостей настраиваются отдельно. QMP готов не означает, что ОС загружена.')
