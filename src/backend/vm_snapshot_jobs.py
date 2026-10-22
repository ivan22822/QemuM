"""Bounded asynchronous jobs over the existing local JSON RPC connection."""
import threading
from vm_snapshot_store import identifier, Store


class Jobs:
    def __init__(self, service):
        self.service=service; self.rows={}; self.lock=threading.Lock()

    def start(self,args):
        name=self.service.name(args); operation_id=identifier(args.get('operation_id'))
        plan=self.service.snapshot_plans.get(operation_id)
        if not plan or plan['name']!=name:raise ValueError('План не найден. Создай новый план.')
        with self.lock:
            previous=self.rows.get(operation_id)
            if previous:return dict(operation_id=operation_id)
            if any(row['name']==name and not row['done'] for row in self.rows.values()):raise ValueError('Для этой VM уже выполняется операция.')
            if sum(not row['done'] for row in self.rows.values())>=2:raise ValueError('Дождись завершения другой операции снимков.')
            for key in list(self.rows):
                if len(self.rows)<16:break
                if self.rows[key]['done']:del self.rows[key]
            event=threading.Event()
            row=dict(name=name,operation_id=operation_id,done=False,phase='Проверка принятого плана',can_cancel=plan['action'].startswith('offline-') or plan['action']=='export',event=event)
            self.rows[operation_id]=row
        def progress(phase,done=None,total=None):
            with self.lock:row.update(phase=phase,completed=done,total=total)
        def cancelled():
            return event.is_set()
        def work():
            try:
                result=self.service.snapshot_execute(dict(name=name,operation_id=operation_id),progress,cancelled)
                with self.lock:row.update(result=result,done=True,can_cancel=False,phase='Операция завершена')
            except Exception as error:
                from vm_snapshot_errors import describe
                with self.lock:row.update(error=str(error) or type(error).__name__,failure=describe(error),done=True,can_cancel=False)
        self.service.pool.submit(work)
        return dict(operation_id=operation_id)

    def status(self,args):
        import vm
        name=self.service.name(args); key=identifier(args.get('operation_id'))
        with self.lock:
            row=self.rows.get(key)
            if not row or row['name']!=name:raise ValueError('Задача не найдена; обнови каталог для сверки журнала.')
            value={k:v for k,v in row.items() if k!='event'}
        operation=Store(vm.ROOT,name).operation(key)
        if operation:
            value['phase']=operation.get('phase',value['phase']) if value.get('completed') is None else value['phase']
            value['can_cancel']=value['can_cancel'] and not operation.get('mutation_started') and not operation.get('cancel_closed') and operation['state'] not in ('completed','failed','cancelled')
            value['state']=operation['state'];value['disks_changed']=bool(operation.get('mutation_started'))
            value['start_allowed']=operation['state'] in ('completed','failed','cancelled')
        return value

    def cancel(self,args):
        value=self.status(args)
        if not value['can_cancel']:raise ValueError('Отмена на этом этапе недоступна. Дождись завершения или сверки.')
        with self.lock:self.rows[value['operation_id']]['event'].set()
        return dict(operation_id=value['operation_id'],requested=True)
