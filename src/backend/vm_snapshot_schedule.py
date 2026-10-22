"""Opt-in schedules while the backend is open; no service or catch-up queue."""
from copy import deepcopy
from datetime import datetime,timedelta
import time
import uuid
from vm_snapshot_store import digest,operation_lock


def checked(options):
    allowed={'enabled','frequency','time','weekday','interval','interval_days','keep_count','keep_days','when_offline','preserve_protected'}
    if not isinstance(options,dict) or set(options)-allowed:raise ValueError('Неизвестные настройки расписания.')
    result=dict(enabled=options.get('enabled',False),frequency=options.get('frequency','daily'),time=options.get('time','12:00'),
                weekday=options.get('weekday','mon'),interval_days=options.get('interval_days',options.get('interval',1)),
                keep_count=options.get('keep_count',10),keep_days=options.get('keep_days',30),when_offline=options.get('when_offline','skip'),preserve_protected=True)
    if type(result['enabled']) is not bool or options.get('preserve_protected',True) is not True:raise ValueError('Защита страховочных точек обязательна.')
    if result['frequency'] not in ('daily','weekly','interval') or result['weekday'] not in ('mon','tue','wed','thu','fri','sat','sun') or result['when_offline'] not in ('skip','create'):raise ValueError('Некорректная периодичность расписания.')
    try:
        if datetime.strptime(result['time'],'%H:%M').strftime('%H:%M')!=result['time']:raise ValueError()
    except (TypeError,ValueError):raise ValueError('Время нужно указать как ЧЧ:ММ.')
    for key,maximum in (('interval_days',365),('keep_count',10000),('keep_days',3650)):
        if type(result[key]) is not int or not 1<=result[key]<=maximum:raise ValueError('Некорректный лимит '+key)
    return result


def next_due(options,now):
    local=datetime.fromtimestamp(now)
    if options['frequency']=='interval':return now+options['interval_days']*86400
    hour,minute=map(int,options['time'].split(':'))
    candidate=local.replace(hour=hour,minute=minute,second=0,microsecond=0)
    if candidate<=local:candidate+=timedelta(days=1)
    if options['frequency']=='weekly':
        target=('mon','tue','wed','thu','fri','sat','sun').index(options['weekday'])
        candidate+=timedelta(days=(target-candidate.weekday())%7)
    return candidate.timestamp()


def retention_candidates(groups,policy,now):
    # Only auto-created, complete disk snapshots. Never touch manual points,
    # external chains, RAM, protected points or recovery checkpoints.
    eligible=sorted([row for row in groups if row.get('source')=='schedule' and row.get('kind')=='disk' and row.get('state')=='ready'
                     and not row.get('protected') and not row.get('checkpoint')],key=lambda row:(row['date'],row['id']),reverse=True)
    return [row for index,row in enumerate(eligible) if index>=policy['keep_count'] or now-row['date']>policy['keep_days']*86400]


class Schedule:
    def __init__(self,service):self.service=service;self.store=service.store

    def save(self,options,revision):
        policy=checked(options)
        with operation_lock(self.service.root,self.service.name):
            self.store.assert_clear();current=self.store.catalog()
            if current['revision']!=revision:raise ValueError('Каталог изменился. Обнови окно расписания.')
            updated=deepcopy(current);policy['next_due']=next_due(policy,self.service.clock())
            policy['runtime_description']='только пока открыт QemuM; пропущенное время выполняется один раз, без очереди'
            updated['settings']['schedule']=policy;updated['revision']+=1
            operation=dict(schema_version=1,id=str(uuid.uuid4()),name=self.service.name,action='metadata',created=self.service.clock(),state='validating',steps=[],
                           before_hash=digest(current),after_hash=digest(updated),result=dict(schedule=policy))
            self.service.transition(operation,'validating','Изменение расписания по выбору пользователя')
            self.store.replace_catalog(updated,digest(current));self.service.transition(operation,'completed','Расписание сохранено; фоновая служба не устанавливалась')
            return operation['result']

    def tick(self):
        with operation_lock(self.service.root,self.service.name):
            self.store.assert_clear();current=self.store.catalog();policy=current['settings']['schedule'];now=self.service.clock()
            if not policy.get('enabled'):return None
            if now<policy.get('next_due',now+1):
                if policy.get('retention_pending'):
                    try:self.service.assert_offline()
                    except (ValueError,OSError):return None
                    self.cleanup(policy,now)
                return None
            policy=checked({key:value for key,value in policy.items() if key not in ('next_due','runtime_description','retention_pending')})
            updated=deepcopy(current);updated['settings']['schedule']['next_due']=next_due(policy,now);updated['revision']+=1
            # Claim this occurrence before work. A crash skips one occurrence;
            # restarting never floods a guest with missed snapshots.
            operation=dict(schema_version=1,id=str(uuid.uuid4()),name=self.service.name,action='schedule',created=now,state='planned',steps=[],
                           before_hash=digest(current),after_hash=digest(updated),result=dict(claimed=True))
            self.service.transition(operation,'planned','Перед регистрацией одного планового запуска')
            self.store.replace_catalog(updated,digest(current))
            self.service.transition(operation,'running','Плановый запуск; пропущенные интервалы не накапливаются')
            try:
                try:self.service.request('query-status');online=True
                except (ConnectionRefusedError,TimeoutError):self.service.assert_offline();online=False
                if not online and policy['when_offline']=='skip':
                    self.service.transition(operation,'cancelled','Создание пропущено: VM выключена')
                    self.cleanup(policy,now);return None
                # Parent schedule intent is informational, not a mutating disk
                # operation. Finalize it before entering the normal guarded API.
                self.service.transition(operation,'completed','Запуск передан общей службе снимков')
                options=dict(label='Автоматический снимок '+datetime.fromtimestamp(now).strftime('%Y-%m-%d %H:%M'))
                if online:
                    plan=self.service.plan_create(options);plan['source']='schedule';result=self.service.create(plan)
                else:
                    from vm_snapshot_transaction import OfflineTransaction
                    transaction=OfflineTransaction(self.service);plan=transaction.plan('offline-create',options=options);plan['source']='schedule';result=transaction.execute(plan)
                if online:
                    current=self.store.catalog();updated=deepcopy(current);updated['settings']['schedule']['retention_pending']=True;updated['revision']+=1
                    self.store.replace_catalog(updated,digest(current))
                    self.service.transition(operation,'completed','Снимок создан. Очистка отложена до выключения VM.');return result
                self.cleanup(policy,now)
                self.service.transition(operation,'completed','Плановый снимок и подтверждённая очистка завершены')
                return result
            except BaseException as error:
                self.service.transition(operation,'failed','Плановый запуск/очистка не завершены: '+str(error));raise

    def cleanup(self,policy,now):
        self.service.assert_offline();self.store.assert_clear()
        from vm_snapshot_transaction import OfflineTransaction
        transaction=OfflineTransaction(self.service)
        rows=self.service.read_catalog(False,self.service.load())['rows']
        for group in retention_candidates(rows,policy,now):
            transaction.execute(transaction.plan('offline-delete',group['id']))
        current=self.store.catalog()
        if current['settings']['schedule'].get('retention_pending'):
            updated=deepcopy(current);updated['settings']['schedule']['retention_pending']=False;updated['revision']+=1
            self.store.replace_catalog(updated,digest(current))
