"""Read-only, bounded L2 diagnostics. No guest commands or packet capture."""
from collections import Counter

CATEGORIES = {'helper-unavailable','helper-start','port-unavailable','permission','identity','protocol','connection',
              'nic-conflict','mac-conflict','missing-nic','missing-vm'}
EVENTS = {'helper-ready','owner-rejected','port-busy','port-connected','malformed-frame','rate-limit',
          'queue-overflow','partial-frame','read-error-or-timeout','connection-error','port-disconnected'}


def number(value):
    return value if type(value) is int and 0 <= value <= 2**63-1 else 0


def summarize(row, configs, states, networks):
    used=Counter((end['vm'],end['nic']) for group in row.get('switches',[]) for end in group['members'])
    for link in row.get('links',[]):
        for side in ('listen','connect'):used[link[side]['vm'],link[side]['nic']]+=1
    for group in row.get('multicast',[]):
        for end in group['members']:used[end['vm'],end['nic']]+=1
    result=[]
    for group in row.get('switches',[]):
        native=networks.get(group['id'],{});status=native.get('status')
        if not isinstance(status,dict):status={}
        ports=status.get('ports',[])
        if not isinstance(ports,list):ports=[]
        category=native.get('category','');members=[];macs=[]
        for index,end in enumerate(group['members']):
            data=configs.get(end['vm']);adapters=(data or {}).get('network_adapters',[])
            nic=next((n for n in adapters if n.get('id')==end['nic']),None);problem=''
            if data is None:problem='missing-vm'
            elif nic is None:problem='missing-nic'
            elif used[end['vm'],end['nic']]>1:problem='nic-conflict'
            elif nic.get('mac') and sum(str(n.get('mac','')).lower()==str(nic['mac']).lower() for n in adapters)>1:problem='mac-conflict'
            mac=str((nic or {}).get('mac','')).lower();macs.append(mac)
            port=ports[index] if index<len(ports) and isinstance(ports[index],dict) else {}
            members.append(dict(vm=end['vm'],nic=end['nic'],mac=mac,model=(nic or {}).get('model',''),
                                vm_state=states.get(end['vm'],'unknown'),problem=problem,
                                connected=port.get('connected') is True,port=number(port.get('port')),
                                nat=[n.get('id','') for n in adapters if n.get('type')=='user' and n.get('id')!=end['nic']]))
        for member in members:
            if member['mac'] and macs.count(member['mac'])>1:member['problem']='mac-conflict'
        problems=[m['problem'] for m in members if m['problem']]
        if problems:category=problems[0]
        events=[]
        raw_events=status.get('events',[])
        if not isinstance(raw_events,list):raw_events=[]
        for event in raw_events[-32:]:
            if isinstance(event,dict) and event.get('category') in EVENTS:
                events.append(dict(time=str(event.get('time',''))[:40],category=event['category'],
                                   port_index=event.get('port_index') if type(event.get('port_index')) is int else -1))
        if not category and any(m['vm_state']=='running' and not m['connected'] for m in members):category='connection'
        if category and category not in CATEGORIES:category='helper-unavailable'
        result.append(dict(id=group['id'],name=group['name'],state=native.get('state','stopped'),category=category,
                           error=str(native.get('error',''))[:1200],members=members,guest_connectivity='not-tested',
                           counters={key:number(status.get(key)) for key in ('received','sent','dropped','rejected','learned')},
                           last_event_utc=str(status.get('last_event_utc',''))[:40],events=events,
                           advice='Исправь состав/NIC/MAC и сформируй новый план.' if problems else
                           'Проверь журнал. При потере коммутатора выключи участников, выполни сверку и создай новый план. Не запускай второй помощник вручную.' if category else
                           'IP/DHCP, маршруты и firewall проверяются внутри гостей. Подключение QEMU не доказывает связь ОС.'))
    return result
