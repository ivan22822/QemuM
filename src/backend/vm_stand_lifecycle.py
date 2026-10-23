"""Stand recovery, metadata lifecycle and opt-in reports. No guest mutations."""
from copy import deepcopy
from contextlib import contextmanager
import hashlib
import json
import re
from pathlib import Path
import time
import uuid

from vm_stands import identifier
from vm_stand_plan import upgrade
from vm_stand_runtime import Conflict,Unknown,fingerprint


def idle(runtime,key):
    if any(j['stand_id']==key and j['state'] in ('queued','running') for j in runtime.jobs.values()):
        raise Conflict('Сначала заверши или отмени операцию стенда.')


@contextmanager
def exclusive(runtime,key):
    from vm_runtime import vm_lock
    with runtime.guard:
        idle(runtime,key)
        try:
            with vm_lock(runtime.root,'stand-execution-'+identifier(key)):yield
        except ValueError as error:raise Conflict(str(error)) from error


def reconcile(runtime,key,revision,confirm):
    if confirm!=key:raise ValueError('Подтверди сверку выбранного стенда.')
    from contextlib import ExitStack
    from vm_runtime import vm_lock
    with runtime.guard,ExitStack() as locks:
        idle(runtime,key);locks.enter_context(vm_lock(runtime.root,'stand-execution-'+identifier(key)))
        row=upgrade(runtime.store.read(key))
        if row['revision']!=revision:raise Conflict('Определение изменилось. Открой стенд заново.')
        for name in sorted(row['members']):locks.enter_context(runtime.host.lock(name))
        journal=runtime._journal(key);states={};issues=[]
        for name in row['members']:
            try:
                actual=runtime.host.identity(name,runtime.host.config(name))
                owned=journal.get('owned',{}).get(name,{})
                if actual is None:
                    runtime.host.assert_stopped(name);states[name]='stopped';journal.get('owned',{}).pop(name,None)
                elif actual==owned.get('identity') and owned.get('config_hash')==runtime.host.config_hash(name) and journal.get('topology')==fingerprint(row):states[name]='owned-running'
                else:states[name]='unowned-running';issues.append('Чужая или изменённая конфигурация: '+name)
            except (ValueError,OSError,KeyError) as error:states[name]='unknown';issues.append(name+': '+str(error))
        pending=journal.get('pending')
        if pending:
            name=pending.get('name') if isinstance(pending,dict) else pending
            if name in states and states[name]=='stopped':journal.pop('pending',None)
            elif isinstance(pending,dict) and pending.get('identity'):
                actual=runtime.host.identity(name,runtime.host.config(name))
                if actual==pending['identity'] and pending.get('hash')==runtime.host.config_hash(name) and journal.get('topology')==fingerprint(row):
                    journal.setdefault('owned',{})[name]=dict(identity=actual,config_hash=pending['hash']);journal.pop('pending',None);states[name]='owned-running'
                    issues=[message for message in issues if message!='Чужая или изменённая конфигурация: '+name]
            if journal.get('pending'):issues.append('Неподтверждённый запуск: '+str(name)+'. Автоматическое присвоение процесса запрещено.')
        current=journal.get('current')
        if current and current.get('state') in ('queued','running','interrupted'):
            current.update(state='interrupted',category='recovery-required',error='Менеджер был закрыт. Процессы не перезапускались. Создай новый подтверждённый план после сверки.')
        journal['reconciliation']=dict(time=time.time(),members=states,issues=issues)
        runtime._journal(key,journal)
        return dict(members=states,issues=issues,start_blocked=bool(journal.get('pending')) or any(s=='unknown' for s in states.values()),automatic_actions=False)


def duplicate(runtime,key,revision,name):
    from vm_runtime import vm_lock
    with exclusive(runtime,key):
        idle(runtime,key);row=deepcopy(runtime.store.read(key))
        if row['revision']!=revision:raise Conflict('Стенд изменился. Обнови список.')
        row.update(id=uuid.uuid4().hex,revision=0,name=name)
        return runtime.store.save(row,0)


def remove(runtime,key,revision,confirm):
    from vm_runtime import vm_lock
    if confirm!=key:raise ValueError('Подтверди удаление выбранной папки стенда.')
    with exclusive(runtime,key):
        idle(runtime,key)
        for token,value in list(runtime.plans.items()):
            if value['key']==key and not value['used']:runtime.discard(token)
        return runtime.store.remove(key,revision)


def report(runtime,key):
    """Allowlist metadata, anonymize members; never read VM media or full configs."""
    from vm_support import redact
    row=upgrade(runtime.store.read(identifier(key)));journal=runtime._journal(key)
    aliases={name:'vm-'+str(index+1) for index,name in enumerate(row['members'])}
    def clean(value):
        value=deepcopy(value)
        if isinstance(value,str):
            for name,alias in sorted(aliases.items(),key=lambda p:len(p[0]),reverse=True):value=re.sub(r'(?<![A-Za-z0-9_-])'+re.escape(name)+r'(?![A-Za-z0-9_-])',alias,value)
            return redact(value,[runtime.root,Path.home()])
        if isinstance(value,list):return [clean(item) for item in value]
        if isinstance(value,dict):return {aliases.get(k,k):clean(v) for k,v in value.items()}
        return value
    nics={}
    def endpoint(value):
        key=(value['vm'],value['nic'])
        if key not in nics:nics[key]='nic-'+str(len(nics)+1)
        return dict(vm=aliases[value['vm']],nic=nics[key])
    links=[dict(id=link['id'],listen=endpoint(link['listen']),connect=endpoint(link['connect'])) for link in row['links']]
    operations=[]
    history=journal.get('history',[])+([journal['current']] if journal.get('current') else [])
    seen=set()
    for job in history:
        if job.get('id') in seen:continue
        seen.add(job.get('id'))
        safe={k:job.get(k) for k in ('id','state','category','phase','current','steps')}
        safe['events']=[{k:event.get(k) for k in ('time','phase','vm')} for event in job.get('events',[])[-100:] if isinstance(event,dict)]
        operations.append(clean(safe))
    with runtime.guard:
        plans=[v for v in runtime.plans.values() if v['key']==key]
        planned=plans[-1]['public'] if plans else {}
        summary={k:planned.get(k) for k in ('action','order','already_running','requested_ram_mb','requested_cores','resources')}
    value=dict(schema=1,type='qemum-stand-support',definition=dict(schema_version=2,id=key,name='Стенд',description='Описание исключено из отчёта',
               members=list(aliases.values()),order=[aliases[n] for n in row['order']],links=links,startup=clean(row['startup'])),
               plan=clean(summary),operations=operations,
               notes=['Диски, RAM, дампы, гостевые файлы, пароли, конфигурации VM и командные строки не собираются.',
                      'Имена участников и адаптеров заменены. Проверь текст перед передачей.',
                      'Это диагностический отчёт, а не резервная копия или импортируемый стенд.',
                      'Отказ QEMU, kernel panic внутри гостя и ожидание сети гостя — разные причины; QMP не определяет их автоматически.'])
    return json.dumps(value,ensure_ascii=False,indent=2)


def save_report(runtime,token,path,expected):
    from vm_storage import safe_file
    with runtime.guard:
        value=runtime.exports.get(identifier(token))
        if not value or value['expires']<time.monotonic():raise Conflict('Предпросмотр отчёта истёк. Создай его заново.')
        content=value['text'].encode('utf-8')
        if hashlib.sha256(content).hexdigest()!=expected:raise Conflict('Отчёт изменился. Проверь предпросмотр.')
        target=Path(path)
        if target.suffix.lower()!='.json':raise ValueError('Сохрани отчёт как JSON.')
        safe_file(target)
        with target.open('xb') as stream:stream.write(content);stream.flush();__import__('os').fsync(stream.fileno())
        runtime.exports.pop(token,None)
        return dict(path=str(target),sha256=expected)
