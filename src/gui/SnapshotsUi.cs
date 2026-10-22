using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

// Views depend on this contract, not on future RPC command names or disk operations.
interface ISnapshotUiService {
    bool Supports(string feature);
    Task<object> Load();
    Task<object> Execute(SnapshotUiRequest request);
    event Action<SnapshotUiProgress> Progress;
}
sealed class SnapshotUiRequest {
    public readonly string Action;public readonly object Snapshot;public readonly Dictionary<string,object> Options;
    public SnapshotUiRequest(string action,object snapshot=null,Dictionary<string,object> options=null){Action=action;Snapshot=snapshot;Options=options??J.O();}
}
sealed class SnapshotUiProgress {
    public string OperationId {get;set;}public string Phase {get;set;}public long? Done {get;set;}public long? Total {get;set;}public bool CanCancel {get;set;}
}
sealed class LegacySnapshotUiService : ISnapshotUiService {
    readonly MainWindow main;readonly string name;
    string[] capabilities=new string[0];
    int revision;
    public LegacySnapshotUiService(MainWindow main,string name){this.main=main;this.name=name;}
    public event Action<SnapshotUiProgress> Progress;
    public bool Supports(string feature){return new[]{"create_live","restore_start","refresh"}.Contains(feature)||capabilities.Contains(feature);}
    Task<object> Call(string message,string command,Dictionary<string,object> args){
        var completion=new TaskCompletionSource<object>();
        if(main.Backend==null||main.Busy){completion.SetException(new InvalidOperationException(main.Busy?"Дождись завершения текущей операции.":"Служба менеджера не подключена."));return completion.Task;}
        main.Run(message,()=>main.Backend.Call(command,args),result=>completion.SetResult(result),error=>completion.SetException(error),false);
        return completion.Task;
    }
    async Task<object> RunPlan(object plan){
        main.SnapshotJobs++;
        try{return await WaitPlan(plan);}
        finally{main.SnapshotJobs--;}
    }
    async Task<object> WaitPlan(object plan){
        string id=J.S(plan,"operation_id");
        await Call("Запускаю подтверждённый план…","snapshot_job_start",J.O("name",name,"operation_id",id));
        for(;;){
            var status=await Call("Операция снимков выполняется…","snapshot_job_status",J.O("name",name,"operation_id",id));
            var handler=Progress;
            if(handler!=null)handler(new SnapshotUiProgress{OperationId=id,Phase=J.S(status,"phase"),CanCancel=J.B(status,"can_cancel"),Done=J.V(status,"completed")==null?(long?)null:J.L(status,"completed"),Total=J.V(status,"total")==null?(long?)null:J.L(status,"total")});
            if(J.B(status,"done")){
                string error=J.S(status,"error");
                if(error.Length>0){if(J.S(J.V(status,"failure"),"code")=="CANCELLED")throw new OperationCanceledException(error);throw new InvalidOperationException(J.S(J.V(status,"failure"),"code")+": "+error+"\r\nID: "+id+"\r\n"+(J.B(status,"disks_changed")?"Запись дисков уже начиналась; проверь журнал.":"Замена исходных образов не начиналась.")+(J.B(status,"start_allowed")?" Операция завершена; перед запуском обнови каталог.":" Запуск заблокирован до сверки."));}
                return J.V(status,"result");
            }
            await Task.Delay(300);
        }
    }
    public async Task<object> Load(){
        var result=await Call("Читаю снимки дисков…","snapshots",J.O("name",name));
        capabilities=J.A(J.V(result,"capabilities")).Select(Convert.ToString).ToArray();
        revision=J.N(result,"revision");
        var catalog=new Dictionary<string,object>(J.D(result));catalog["context"]=J.V(main.Row(name),"data");
        string state=main.State(name);catalog["vm_state"]=J.B(result,"online")?(new[]{"paused","prelaunch","shutdown"}.Contains(state)?state:"running"):"stopped";
        return catalog;
    }
    public async Task<object> Execute(SnapshotUiRequest request){
        if(request.Action=="create"){
            var plan=await Call("Проверяю план снимка…","snapshot_plan",J.O("name",name,"options",request.Options));
            string disks=String.Join("\r\n",J.A(J.V(plan,"disks")).Select(row=>J.S(row,"path")));
            string excluded=String.Join("\r\n",J.A(J.V(plan,"excluded")).Select(row=>J.S(row,"path",J.S(row,"device"))+" · "+J.S(row,"reason")));
            using(var confirmation=new SnapshotPlanDialog("Будут сохранены диски:\r\n"+disks+(excluded.Length>0?"\r\n\r\nНе включаются:\r\n"+excluded:"")+"\r\n\r\n"+J.S(plan,"detail"))){if(confirmation.ShowDialog(main)!=DialogResult.OK)throw new OperationCanceledException("Создание отменено до записи в диски.");}
            return await RunPlan(plan);
        }
        if(request.Action=="cancel")return await Call("Запрашиваю безопасную отмену…","snapshot_cancel",J.O("name",name,"operation_id",J.S(request.Options,"operation_id")));
        if(request.Action=="export"){
            var plan=await Call("Проверяю независимую копию…","snapshot_export_plan",J.O("name",name,"group_id",J.S(request.Snapshot,"id"),"options",request.Options));
            using(var confirmation=new SnapshotPlanDialog(J.S(plan,"detail")+"\r\nКонсервативный резерв места: "+J.L(plan,"required_bytes")+" байт.\r\nИсходные образы не изменяются.")){if(confirmation.ShowDialog(main)!=DialogResult.OK)throw new OperationCanceledException();}
            return await RunPlan(plan);
        }
        if(request.Action=="recover")return await Call("Сверяю результат незавершённой операции…","snapshot_recover",J.O("name",name,"operation_id",J.S(request.Options,"operation_id")));
        if(request.Action=="schedule")return await Call("Сохраняю расписание…","snapshot_schedule",J.O("name",name,"revision",revision,"options",request.Options));
        if(request.Action=="edit"||request.Action=="protect")return await Call("Сохраняю метаданные снимка…","snapshot_metadata",J.O("name",name,"group_id",J.S(request.Snapshot,"id"),"revision",revision,"action",request.Action,"options",request.Options));
        if(request.Action=="adopt")return await Call("Проверяю старый снимок…","snapshot_adopt",J.O("name",name,"tag",J.S(request.Snapshot,"name"),"confirm",J.S(request.Options,"confirm")));
        if(request.Action=="restore_start"||request.Action=="restore"||request.Action=="delete"){
            if(J.S(request.Snapshot,"kind")=="ram"&&request.Action!="delete"){
                using(var confirmation=new SnapshotPlanDialog("Будут восстановлены RAM, CPU, устройства и все диски выбранной группы.\r\nПеред загрузкой создаётся страховочное полное состояние.\r\nЕсли VM выключена, QEMU запустится на паузе; гостевой код до проверки не выполняется.\r\nБез продолжения VM останется на паузе.")){if(confirmation.ShowDialog(main)!=DialogResult.OK)throw new OperationCanceledException("Возобновление отменено.");}
                var full=await Call("Восстанавливаю полное состояние…","snapshot_ram_restore",J.O("name",name,"group_id",J.S(request.Snapshot,"id"),"start_after",request.Action=="restore_start"));main.OpenPanel(name);return full;
            }
            if(!new[]{"disk","external"}.Contains(J.S(request.Snapshot,"kind","disk"))&&!(request.Action=="delete"&&J.S(request.Snapshot,"kind")=="ram"))throw new InvalidOperationException("Для этого типа нужен отдельный режим восстановления.");
            var plan=await Call("Проверяю выбранную группу…","snapshot_operation_plan",J.O("name",name,"action",request.Action=="delete"?"offline-delete":"offline-restore","group_id",J.S(request.Snapshot,"id"),"options",request.Options));
            string disks=String.Join("\r\n",J.A(J.V(plan,"disks")).Select(row=>J.S(row,"path")));
            string changes=String.Join("\r\n",J.A(J.V(plan,"config_diff")).Select(row=>J.S(row,"field")+": "+J.Json().Serialize(J.V(row,"current"))+" → "+J.Json().Serialize(J.V(row,"saved"))));
            using(var confirmation=new SnapshotPlanDialog("Изменяемые диски:\r\n"+disks+"\r\n\r\n"+J.S(plan,"detail")+(changes.Length>0?"\r\n\r\nИзменения параметров:\r\n"+changes:""))){if(confirmation.ShowDialog(main)!=DialogResult.OK)throw new OperationCanceledException("Действие отменено до записи.");}
            var result=await RunPlan(plan);
            if(request.Action=="restore_start"){
                try{await Call("Диски восстановлены. Запускаю VM…","start",J.O("name",name,"boot","disk"));main.OpenPanel(name);}
                catch(Exception error){throw new InvalidOperationException("Диски восстановлены. Следующий запуск не удался: "+error.Message+" Повторный откат не выполнялся.",error);}
            }
            return result;
        }
        throw new InvalidOperationException("Эта операция ещё не подключена к службе снимков.");
    }
}
sealed class SnapshotPlanDialog : Form {
    public SnapshotPlanDialog(string text){
        Text="План снимка";Theme.Form(this,820,640);var content=new Panel{Dock=DockStyle.Fill,Padding=new Padding(24)};
        var details=Theme.Editor(text);details.ReadOnly=true;content.Controls.Add(details);var buttons=Theme.Flow();
        var accept=Theme.Button("Подтвердить действие",()=>{DialogResult=DialogResult.OK;Close();},true);var cancel=Theme.Button("Отмена",Close);
        buttons.Controls.Add(accept);buttons.Controls.Add(cancel);AcceptButton=accept;CancelButton=cancel;
        Theme.Dialog(this,content,buttons,"Проверка перед изменением","Состав дисков подтверждён службой · план действует две минуты");Shown+=(sender,args)=>details.Input.Select(0,0);
    }
}
sealed class SnapshotUiEntry {
    public readonly object Raw;public readonly string Id,Name,Description,Kind,State;public readonly DateTime? Date;
    public SnapshotUiEntry(object raw){
        Raw=raw;Id=J.S(raw,"id",J.S(raw,"name"));Name=J.S(raw,"label",J.S(raw,"name","Без названия"));Description=J.S(raw,"description");Kind=J.S(raw,"kind","disk");State=J.S(raw,"state","ready");
        long seconds=J.L(raw,"date");if(seconds>0)try{Date=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();}catch(ArgumentOutOfRangeException){}
    }
    public string TypeText {get{return Kind=="ram"?"Диски + RAM":Kind=="external"?"Внешний снимок":"Снимок дисков";}}
    public string StateText {get{return State=="ready"?"Доступен":State=="partial"?"Неполная группа":State=="missing"?"Диск недоступен":State=="incompatible"?"Несовместим":State=="uncertain"?"Результат не подтверждён":State=="recovery-required"?"Нужно восстановление":"Состояние: "+State;}}
    public string DateText {get{return Date.HasValue?Date.Value.ToString("dd.MM.yyyy · HH:mm"):"Дата не передана";}}
    public bool Protected {get{return J.D(Raw).ContainsKey("protected")?J.B(Raw,"protected"):J.B(Raw,"checkpoint")||J.S(Raw,"name").StartsWith("before-restore-",StringComparison.Ordinal);}}
    public override string ToString(){return Name;}
}
sealed class SnapshotUiList : ThemedList {
    public SnapshotUiList(){ItemHeight=96;}
    protected override void DrawItem(Graphics graphics,int index,Rectangle bounds,bool active){
        var item=(SnapshotUiEntry)Items[index];using(var fill=new SolidBrush(active?Theme.Card:Theme.Panel))graphics.FillRectangle(fill,Rectangle.Inflate(bounds,-2,-3));
        if(active)using(var fill=new SolidBrush(Theme.Accent))graphics.FillRectangle(fill,bounds.X+2,bounds.Y+13,3,bounds.Height-26);
        TextRenderer.DrawText(graphics,item.Name,Font,new Rectangle(bounds.X+16,bounds.Y+12,bounds.Width-34,24),active?Theme.Accent:Theme.Text,TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
        string detail=item.DateText+"  ·  "+item.TypeText;
        TextRenderer.DrawText(graphics,detail,Font,new Rectangle(bounds.X+16,bounds.Y+40,bounds.Width-34,23),item.State=="ready"?Theme.Muted:Theme.Yellow,TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
        TextRenderer.DrawText(graphics,item.StateText+(item.Protected?" · защищён":""),Font,new Rectangle(bounds.X+16,bounds.Y+65,bounds.Width-34,22),item.State=="ready"?Theme.Muted:Theme.Yellow,TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
    }
}

// Future capabilities unlock the existing forms; unsupported requests never reach Execute.
sealed class SnapshotOperationDialog : Form {
    readonly ISnapshotUiService service;readonly string action;readonly SnapshotUiEntry entry;readonly object catalog;
    readonly Dictionary<string,Control> fields=new Dictionary<string,Control>();readonly Label reason=Theme.Label("",10,Theme.Yellow);
    readonly Button apply;readonly FlowLayoutPanel buttons=Theme.Flow();readonly TableLayoutPanel form=Theme.Fields();
    public SnapshotUiRequest Request {get;private set;}
    public SnapshotOperationDialog(ISnapshotUiService service,string action,SnapshotUiEntry entry,object catalog){
        this.service=service;this.action=action;this.entry=entry;this.catalog=catalog;
        string title=Title(action);Text=title;Theme.Form(this,820,760);
        Build();Theme.Row(form,"",reason,true);apply=Theme.Button(ApplyText(action),Accept,true);apply.Enabled=false;
        buttons.Controls.Add(apply);var dismiss=Theme.Button("Отмена",Close);buttons.Controls.Add(dismiss);AcceptButton=apply;CancelButton=dismiss;
        Theme.Dialog(this,form,buttons,title,entry==null?"Настройки операции · изменения выполняются только после подтверждения":entry.Name);
        foreach(Control control in fields.Values){control.TextChanged+=(sender,args)=>ValidateRequest();var check=control as CheckBox;if(check!=null)check.CheckedChanged+=(sender,args)=>ValidateRequest();var selector=control as ThemedSelector;if(selector!=null)selector.SelectedIndexChanged+=(sender,args)=>ValidateRequest();}
        Shown+=(sender,args)=>{var first=fields.Values.FirstOrDefault(control=>control.Enabled);if(first!=null)first.Focus();};
        ValidateRequest();
    }
    static string Title(string value){switch(value){case "adopt":return "Принятие старого снимка";case "create":return "Новый снимок";case "edit":return "Название и описание";case "restore":return "Восстановление снимка";case "delete":return "Удаление снимка";case "export":return "Независимая резервная копия";case "schedule":return "Расписание и хранение";case "recover":return "Незавершённая операция";default:return "Защита снимка";}}
    static string ApplyText(string value){return value=="create"?"Создать снимок":value=="delete"?"Удалить снимок":value=="restore"?"Подтвердить восстановление":value=="export"?"Создать копию":value=="recover"?"Начать восстановление":"Сохранить";}
    FieldInput Input(string key,string title,string text="",bool full=true){var control=Theme.Input(text);fields[key]=control;Theme.Row(form,title,control,full);return control;}
    ThemedSelector Select(string key,string title,params string[] values){var control=Theme.Combo(values);fields[key]=control;Theme.Row(form,title,control,true);return control;}
    CheckBox Check(string key,string text,bool value=false){var control=new CheckBox{Text=text,Checked=value,AutoSize=true,ForeColor=Theme.Text};fields[key]=control;Theme.Row(form,"",control,true);return control;}
    void Note(string value){Theme.Row(form,"",Theme.Label(value,10,Theme.Muted),true);}
    void TextView(string value,int height=145){var control=Theme.Editor(value);control.Height=height;control.ReadOnly=true;Theme.Row(form,"",control,true);Shown+=(sender,args)=>control.Input.Select(0,0);}
    void Build(){
        string feature=action=="edit"?"metadata":action;
        if(action!="create"&&action!="restore"&&!service.Supports(feature))Note("Эта возможность появится после обновления службы снимков. Пока можно посмотреть настройки; сохранение отключено.");
        if(action=="create"||action=="edit"){
            var title=Input("label","Название",action=="edit"&&entry!=null?entry.Name:"");title.Placeholder="Автоматическое имя, если оставить пустым";
            var description=Theme.Editor(action=="edit"&&entry!=null?entry.Description:"");description.Height=105;fields["description"]=description;Theme.Row(form,"Описание",description,true);
            if(action=="create"){
                var kind=Select("kind","Тип снимка","Диски · без RAM","Диски и оперативная память","Внешний слой диска");kind.SelectedIndex=J.S(catalog,"default_snapshot_kind")=="external"?2:0;
                Check("include_config","Сохранить параметры VM вместе со снимком");
                Select("consistency","Подготовка гостя","Без подготовки гостевой файловой системы","Подготовить через QEMU Guest Agent");
                Note("Снимок дисков не сохраняет открытые программы. Внутренний снимок находится в образе и не заменяет независимую резервную копию.\r\nRAM может содержать пароли и другие личные данные. Её место хранения и необходимое свободное место проверяются службой перед записью.");
            }
        }else if(action=="adopt"){
            TextView(SnapshotUiText.Details(entry,catalog),230);
            Note("Принятие привяжет один старый снимок к проверенному диску и выдаст UUID. Содержимое диска не меняется. Сначала выключи VM. Старые многодисковые группы автоматически не принимаются.");
            Input("confirm","Введи внутренний тег снимка");
        }else if(action=="restore"){
            TextView(SnapshotUiText.Details(entry,catalog),165);
            Select("restore_scope","Что восстановить","Только диски","Диски и параметры VM");
            Check("start_after","Запустить VM после восстановления",true);
            Note("Текущие данные дисков будут заменены выбранной точкой. Перед откатом служба должна создать страховочный снимок.\r\nБез RAM гостевая ОС загрузится заново. Неизвестные и неполные группы восстанавливать нельзя.");
            Note("Офлайн-операции резервируют оригиналы на всё время подготовки и публикации. Прежние образы сохраняются для восстановления. Требуется место для полных рабочих копий; VM останется выключенной, если запуск не выбран.");
            Input("confirm","Для подтверждения введи название снимка");
        }else if(action=="delete"){
            TextView(SnapshotUiText.Details(entry,catalog),180);
            Note("Удаляется выбранная точка восстановления, а не сама VM. Точное освобождение места заранее не гарантируется: блоки могут использоваться другими снимками.\r\nЗащищённые и страховочные точки требуют отдельного снятия защиты.");
            Input("confirm","Для подтверждения введи название снимка");
        }else if(action=="export"){
            var scope=Select("export_scope","Источник копии","Выбранный снимок","Текущее состояние дисков");if(entry==null)scope.SelectedIndex=1;
            var path=Theme.Input();fields["path"]=path;Theme.Row(form,"Новый ZIP · независимые образы и manifest",Theme.Browse(path,true,"Независимая копия ZIP|*.zip"),true);
            Select("format","Формат копии","qcow2","raw");Check("include_config","Добавить параметры VM",true);Check("verify","Проверить результат экспорта",true);
            Note("Независимая копия создаётся отдельно от внутренних снимков. Наличие и размер результата должна подтвердить служба снимков; выбор пути ничего не записывает. RAM в обычную копию не включается.");
        }else if(action=="schedule"){
            var schedule=J.V(catalog,"schedule");Check("enabled","Включить автоматические снимки",J.B(schedule,"enabled"));
            var frequency=Select("frequency","Периодичность","Каждый день","Раз в неделю","Через заданный интервал");frequency.SelectedIndex=Math.Max(0,Array.IndexOf(new[]{"daily","weekly","interval"},J.S(schedule,"frequency","daily")));Input("time","Время запуска · ЧЧ:ММ",J.S(schedule,"time","03:00"));
            var weekday=Select("weekday","День недели · для еженедельного запуска","Понедельник","Вторник","Среда","Четверг","Пятница","Суббота","Воскресенье");weekday.SelectedIndex=Math.Max(0,Array.IndexOf(new[]{"mon","tue","wed","thu","fri","sat","sun"},J.S(schedule,"weekday","mon")));
            fields["interval"]=Theme.Number(J.N(schedule,"interval_days",1),1,365);Theme.Row(form,"Интервал, дней",fields["interval"]);
            fields["keep_count"]=Theme.Number(J.N(schedule,"keep_count",10),1,10000);Theme.Row(form,"Хранить автоматических незащищённых точек",fields["keep_count"]);
            fields["keep_days"]=Theme.Number(J.N(schedule,"keep_days",30),1,3650);Theme.Row(form,"Срок хранения автоматических точек, дней",fields["keep_days"]);
            var offline=Select("when_offline","Если VM выключена","Пропустить запуск","Создать офлайн, если поддерживается");offline.SelectedIndex=J.S(schedule,"when_offline")=="create"?1:0;
            var protect=Check("preserve_protected","Закреплённые и аварийные страховочные точки не удаляются",true);protect.Enabled=false;
            Note("Режим работы расписания: "+J.S(schedule,"runtime_description","служба ещё не передала сведения о фоновом выполнении")+". Пропущенные запуски не должны накапливаться в очередь. Причины пропуска отображаются в истории.");
        }else if(action=="recover"){
            TextView(SnapshotUiText.Operations(catalog),230);Input("operation_id","ID операции для восстановления");
            Note("До подтверждения согласованного состояния дисков запуск VM должен оставаться заблокированным. Эта форма не выполняет слепой повтор предыдущей операции.");
        }else{
            Check("protected","Защитить снимок от удаления и автоматической очистки",entry!=null&&entry.Protected);
            Note("Снятие защиты не должно разрешать удаление точки, которая нужна незавершённому восстановлению.");
        }
    }
    string TextOf(string key){return fields.ContainsKey(key)?fields[key].Text.Trim():"";}
    bool Checked(string key){return fields.ContainsKey(key)&&((CheckBox)fields[key]).Checked;}
    int Index(string key){return ((ThemedSelector)fields[key]).SelectedIndex;}
    string Disabled(string feature,string description){return service.Supports(feature)?null:description+" — пока не поддерживается службой снимков.";}
    internal string ValidateOptions(){
        if(TextOf("label").Length>120||TextOf("description").Length>4000)return "Название — до 120 символов, описание — до 4000.";
        if(TextOf("label").Any(Char.IsControl))return "Название должно быть одной строкой без управляющих символов.";
        bool online=J.B(catalog,"online");
        if(action=="create"){
            if(!online&&!SnapshotUiText.IsStopped(catalog))return "Состояние VM не подтверждено. Сначала обнови список.";
            if(J.B(catalog,"recovery_required"))return "Сначала нужно завершить восстановление предыдущей операции.";
            string missing=Disabled(online?"create_live":"create_offline",online?"Создание снимка":"Создание при выключенной VM");if(missing!=null)return missing;
            if(Index("kind")==1&&(missing=Disabled("ram","Полное состояние с RAM"))!=null)return missing;
            if(Index("kind")==2&&(missing=Disabled("external","Внешние снимки"))!=null)return missing;
            if((TextOf("label").Length>0||TextOf("description").Length>0)&&!service.Supports("metadata_on_create")&&(missing=Disabled("metadata","Сохранение названия и описания"))!=null)return missing;
            if(Checked("include_config")&&(missing=Disabled("config","Сохранение параметров VM"))!=null)return missing;
            if(Index("consistency")==1&&(missing=Disabled("guest_freeze","Подготовка через Guest Agent"))!=null)return missing;
        }else if(action=="adopt"){
            if(!SnapshotUiText.IsStopped(catalog)||J.B(catalog,"recovery_required"))return "Сначала выключи VM и заверши проверку незавершённых операций.";
            if(entry==null||!entry.Id.StartsWith("legacy-",StringComparison.Ordinal))return "Выбери старый снимок без метаданных QemuM.";
            if(TextOf("confirm")!=J.S(entry.Raw,"name"))return "Введи точный внутренний тег снимка.";
            return Disabled("adopt","Принятие старых снимков");
        }else if(action=="restore"){
            if(online&&(entry==null||entry.Kind!="ram"))return "Для восстановления дисков сначала штатно выключи VM.";
            if(!SnapshotUiText.IsStopped(catalog)&&!(online&&entry!=null&&entry.Kind=="ram"&&new[]{"paused","prelaunch"}.Contains(J.S(catalog,"vm_state"))))return "Сначала выключи VM либо поставь её на паузу для полного состояния.";
            if(entry==null||entry.State!="ready"||J.B(catalog,"recovery_required"))return "Восстановление заблокировано: группа не подтверждена или нужна проверка предыдущей операции.";
            if(TextOf("confirm")!=entry.Name)return "Введи точное название выбранного снимка.";
            if(entry.Kind=="ram"&&Index("restore_scope")==1)return "Полное состояние требует тех же настроек. Автоматическая замена параметров доступна для снимков только дисков.";
            string missing=Disabled(entry.Kind=="ram"?"restore_ram":Checked("start_after")?"restore_start":"restore","Выбранный режим восстановления");if(missing!=null)return missing;
            if(Index("restore_scope")==1&&(missing=Disabled("restore_config","Восстановление параметров VM"))!=null)return missing;
            if(entry.Kind=="ram"&&(missing=Disabled("restore_ram","Возобновление полного состояния VM"))!=null)return missing;
            if(entry.Kind=="external"&&(missing=Disabled("restore_external","Восстановление внешней цепочки"))!=null)return missing;
        }else if(action=="delete"){
            if(entry==null)return "Сначала выбери снимок.";if(entry.Protected)return "Точка защищена. Сначала нужно проверить зависимости и снять защиту.";
            if(TextOf("confirm")!=entry.Name)return "Введи точное название выбранного снимка.";
            return Disabled("delete","Удаление снимков");
        }else if(action=="export"){
            if(TextOf("path").Length==0)return "Выбери новый файл для копии.";
            if(File.Exists(TextOf("path")))return "Выбери новый файл: существующий файл не будет перезаписан.";
            return Disabled("export","Независимый экспорт");
        }else if(action=="schedule"){
            TimeSpan time;if(!TimeSpan.TryParseExact(TextOf("time"),@"hh\:mm",System.Globalization.CultureInfo.InvariantCulture,out time))return "Время нужно указать как ЧЧ:ММ.";
            try{foreach(string key in new[]{"interval","keep_count","keep_days"}){decimal value=((NumberInput)fields[key]).Value;}}catch(InvalidOperationException error){return error.Message;}
            return Disabled("schedule","Расписание и очистка");
        }else if(action=="recover"){
            if(TextOf("operation_id").Length==0)return "Укажи ID известной незавершённой операции.";
            if(!J.A(J.V(catalog,"operations")).Any(item=>J.S(item,"id")==TextOf("operation_id")))return "Эта операция не найдена в полученном журнале. Сначала обнови данные.";
            return Disabled("recover","Аварийное восстановление");
        }else return Disabled(action=="edit"?"metadata":"protect",action=="edit"?"Редактирование названия и описания":"Защита снимков");
        return null;
    }
    void ValidateRequest(){if(apply==null)return;string message=ValidateOptions();apply.Enabled=message==null;reason.ForeColor=message==null?Theme.Muted:Theme.Yellow;reason.Text=message??"Настройки проверены. Действие начнётся только после подтверждения.";}
    void Accept(){
        ValidateRequest();if(!apply.Enabled)return;var options=J.O();foreach(var pair in fields){var check=pair.Value as CheckBox;var number=pair.Value as NumberInput;var selector=pair.Value as ThemedSelector;options[pair.Key]=check!=null?(object)check.Checked:number!=null?(object)(int)number.Value:selector!=null?(object)SelectionValue(pair.Key,selector.SelectedIndex):pair.Key=="description"?pair.Value.Text:pair.Value.Text.Trim();}
        string operation=action=="restore"?(Checked("start_after")?"restore_start":"restore"):action;
        Request=new SnapshotUiRequest(operation,entry==null?null:entry.Raw,options);DialogResult=DialogResult.OK;Close();
    }
    static string SelectionValue(string key,int index){
        switch(key){case "kind":return new[]{"disk","ram","external"}[index];case "consistency":return new[]{"crash","filesystem"}[index];case "restore_scope":return new[]{"disks","disks_and_config"}[index];case "format":return new[]{"qcow2","raw"}[index];case "export_scope":return new[]{"snapshot","current"}[index];case "frequency":return new[]{"daily","weekly","interval"}[index];case "weekday":return new[]{"mon","tue","wed","thu","fri","sat","sun"}[index];case "when_offline":return new[]{"skip","create"}[index];default:throw new InvalidOperationException("Неизвестный выбор операции: "+key);}
    }
}

static class SnapshotUiText {
    public static bool IsStopped(object catalog){return J.S(catalog,"vm_state").Length>0?J.S(catalog,"vm_state")=="stopped":J.D(catalog).ContainsKey("online")&&!J.B(catalog,"online");}
    public static string ConfigurationDiff(object current,object saved){
        if(J.D(saved).ContainsKey("launch")){saved=J.V(saved,"launch");current=J.O(J.D(current).Where(pair=>J.D(saved).ContainsKey(pair.Key)).SelectMany(pair=>new object[]{pair.Key,pair.Value}).ToArray());}
        if(current==null||saved==null)return "Сравнение недоступно: текущие или сохранённые параметры не переданы.";
        var keys=J.D(current).Keys.Union(J.D(saved).Keys).OrderBy(key=>key,StringComparer.Ordinal);var changed=keys.Where(key=>Canonical(J.V(current,key))!=Canonical(J.V(saved,key))||J.D(current).ContainsKey(key)!=J.D(saved).ContainsKey(key)).ToArray();
        return changed.Length==0?"Переданные параметры совпадают.":"Отличаются: "+String.Join(", ",changed);
    }
    static string Canonical(object value){var map=value as Dictionary<string,object>;if(map!=null)return "{"+String.Join(",",map.OrderBy(pair=>pair.Key,StringComparer.Ordinal).Select(pair=>J.Json().Serialize(pair.Key)+":"+Canonical(pair.Value)))+"}";var sequence=value as System.Collections.IEnumerable;if(sequence!=null&&!(value is string))return "["+String.Join(",",sequence.Cast<object>().Select(Canonical))+"]";return J.Json().Serialize(value);}
    public static string Details(SnapshotUiEntry item,object catalog){
        if(item==null)return "Выбери снимок, чтобы увидеть его параметры и ограничения.";
        var text=new StringBuilder();text.AppendLine(item.Name);text.AppendLine(item.DateText);text.AppendLine(item.TypeText+" · "+item.StateText);text.AppendLine("ID: "+item.Id);
        text.AppendLine("\r\nОписание");text.AppendLine(item.Description.Length>0?item.Description:"Описание не сохранено.");
        text.AppendLine("\r\nСостав снимка");var disks=J.A(J.V(item.Raw,"disks"));if(disks.Length>0)text.AppendLine("Дисков: "+disks.Length);
        if(disks.Length>0)foreach(var disk in disks)text.AppendLine(disk is string?(string)disk:J.S(disk,"path",J.S(disk,"name","Диск")));
        else{text.AppendLine("Полный перечень дисков не передан службой.");string path=J.S(J.V(catalog,"context"),"disk_path");if(path.Length>0)text.AppendLine("Основной диск из конфигурации: "+path);}
        foreach(string key in new[]{"size_description","consistency_description","compatibility_description","chain_description","checkpoint_reason","created_by"}){string value=J.S(item.Raw,key);if(value.Length>0)text.AppendLine(value);}
        text.AppendLine("\r\nОграничения");text.AppendLine(item.Kind=="ram"?"Продолжение полного состояния VM требует совместимых RAM, CPU и устройств. Совместимость должна быть подтверждена перед возобновлением.":"RAM и открытые программы не восстанавливаются. Снимок не является независимой резервной копией.");
        text.AppendLine("Внутренний снимок хранится внутри образа: потеря или повреждение образа может затронуть и снимки. Страховочная точка тоже не является отдельной резервной копией.");
        var excluded=J.A(J.V(item.Raw,"excluded"));if(excluded.Length>0){text.AppendLine("\r\nНе включены в снимок");foreach(var resource in excluded)text.AppendLine(J.S(resource,"path",J.S(resource,"device"))+" · "+J.S(resource,"reason"));}
        text.AppendLine("Размер внутренних снимков не равен размеру независимых копий: блоки могут быть общими.");
        text.AppendLine(J.V(item.Raw,"parameters")==null?"Параметры VM в этом снимке не сохранены.":"Для снимка переданы сохранённые параметры VM.");
        if(item.Protected)text.AppendLine("Защищённая или страховочная точка.");string reason=J.S(item.Raw,"reason");if(reason.Length>0)text.AppendLine(reason);return text.ToString();
    }
    public static string Operations(object catalog){var operations=J.A(J.V(catalog,"operations"));if(operations.Length==0)return J.D(catalog).ContainsKey("operations")?"Служба не сообщает о незавершённых операциях.":"Служба ещё не передаёт журнал незавершённых операций. Это не подтверждение отсутствия проблем.";return String.Join("\r\n\r\n",operations.Select(item=>"ID: "+J.S(item,"id")+"\r\n"+J.S(item,"phase")+"\r\n"+J.S(item,"detail")));}
}

sealed class SnapshotsWindow : Form {
    readonly ISnapshotUiService service;readonly string name;readonly bool boot;object catalog;
    readonly SnapshotUiList list=new SnapshotUiList();readonly FieldInput search=new FieldInput{SearchIcon=true,Placeholder="Найти снимок…"};
    readonly ThemedSelector filter=Theme.Combo("Все типы","Только диски","С RAM","Внешние"),sort=Theme.Combo("Сначала новые","Сначала старые","По названию");
    readonly Label note=Theme.Label("",10,Theme.Muted),summary=Theme.Label("",9,Theme.Muted),state=Theme.Label("",10,Theme.Accent);
    readonly ThemedEditor detail=Theme.Editor();readonly Button create,restore,remove,refresh,more,cancel;
    readonly Label empty=Theme.Label("",11,Theme.Muted);
    readonly ToolTip tips=new ToolTip();readonly ContextMenuStrip menu=NightMenuRenderer.Create();readonly List<SnapshotUiEntry> rows=new List<SnapshotUiEntry>();
    bool busy,closing,loading,failed;string operationId;SnapshotUiEntry selected;readonly FlowLayoutPanel buttons=Theme.Flow();
    internal SnapshotsWindow(ISnapshotUiService service,string name,object initial=null,bool boot=false){
        this.service=service;this.name=name;this.boot=boot;catalog=initial;Text="Снимки | "+name;Theme.Form(this,1100,800);
        var content=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(24,8,24,0)};
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));content.RowStyles.Add(new RowStyle(SizeType.AutoSize));content.RowStyles.Add(new RowStyle(SizeType.Absolute,66));content.RowStyles.Add(new RowStyle(SizeType.Percent,100));content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading=Theme.Flow(true);heading.Dock=DockStyle.Fill;heading.Controls.Add(state);heading.Controls.Add(summary);content.Controls.Add(heading,0,0);
        var toolbar=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,Margin=Padding.Empty};toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,162));toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,178));toolbar.Controls.Add(search,0,0);toolbar.Controls.Add(filter,1,0);toolbar.Controls.Add(sort,2,0);search.Margin=new Padding(0,0,12,0);filter.Margin=new Padding(0,0,12,0);content.Controls.Add(toolbar,0,1);
        foreach(Control input in new Control[]{search,filter,sort}){input.Dock=DockStyle.Top;input.Height=42;}
        var body=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=Padding.Empty};body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,44));body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,56));var listPanel=new Panel{Dock=DockStyle.Fill,Margin=new Padding(0,0,20,0),BackColor=Theme.Panel};list.Margin=Padding.Empty;listPanel.Controls.Add(list);empty.AutoSize=false;empty.Dock=DockStyle.Fill;empty.TextAlign=ContentAlignment.MiddleCenter;empty.Padding=new Padding(20);listPanel.Controls.Add(empty);body.Controls.Add(listPanel,0,0);detail.ReadOnly=true;detail.Font=new Font("Segoe UI",11F);detail.Margin=Padding.Empty;body.Controls.Add(detail,1,0);content.Controls.Add(body,0,2);
        note.Margin=new Padding(0,14,0,8);content.Controls.Add(note,0,3);
        create=Theme.Button("Создать снимок",()=>OpenOperation("create"));refresh=Theme.Button("Обновить",async()=>await Reload());restore=Theme.Button("Загрузить снимок",()=>OpenOperation("restore"),true);remove=Theme.Button("Удалить",()=>OpenOperation("delete"),false,true);
        more=Theme.Button("Ещё…",()=>menu.Show(more,new Point(0,more.Height)));cancel=Theme.Button("Отменить операцию",async()=>await Cancel());cancel.Visible=false;
        foreach(var button in new[]{create,refresh,restore,remove,more,cancel})buttons.Controls.Add(button);buttons.Controls.Add(Theme.Button("Закрыть",Close));
        NightMenuRenderer.Add(menu,"Подробности",(sender,args)=>ShowInfo("details"));NightMenuRenderer.Add(menu,"Название и описание…",(sender,args)=>OpenOperation("edit"));NightMenuRenderer.Add(menu,"Защита снимка…",(sender,args)=>OpenOperation("protect"));
        NightMenuRenderer.Add(menu,"Независимая копия…",(sender,args)=>OpenOperation("export"));NightMenuRenderer.Add(menu,"Параметры VM · сравнить",(sender,args)=>ShowInfo("config"));
        NightMenuRenderer.Add(menu,"Принять старый снимок…",(sender,args)=>OpenOperation("adopt"));
        NightMenuRenderer.Add(menu,"Расписание и хранение…",(sender,args)=>OpenOperation("schedule"));NightMenuRenderer.Add(menu,"История операций",(sender,args)=>ShowInfo("history"));NightMenuRenderer.Add(menu,"Незавершённые операции…",(sender,args)=>OpenOperation("recover"));NightMenuRenderer.Add(menu,"О снимках и резервных копиях",(sender,args)=>ShowInfo("help"));
        Theme.Dialog(this,content,buttons,boot?"Выбор точки восстановления":"Снимки машины",name+" · диски, состояние и точки восстановления");
        ClientSizeChanged+=(sender,args)=>{buttons.MaximumSize=new Size(Math.Max(1,ClientSize.Width),0);note.MaximumSize=new Size(Math.Max(1,content.ClientSize.Width-48),0);};
        search.TextChanged+=(sender,args)=>RenderList();filter.SelectedIndexChanged+=(sender,args)=>RenderList();sort.SelectedIndexChanged+=(sender,args)=>RenderList();list.SelectedIndexChanged+=(sender,args)=>{selected=list.SelectedItem as SnapshotUiEntry;RenderDetail();};list.DoubleClick+=(sender,args)=>ShowInfo("details");
        service.Progress+=Progress;FormClosing+=(sender,args)=>{if(busy){args.Cancel=true;note.Text="Дождись завершения операции. Закрытие окна не должно прерывать запись дисков.";}else closing=true;};
        KeyDown+=(sender,args)=>{if(args.KeyCode==Keys.Escape&&!busy){Close();args.Handled=true;}else if(args.KeyCode==Keys.F5&&!busy){refresh.PerformClick();args.Handled=true;}};KeyPreview=true;
        if(initial!=null)Fill(initial);else{loading=true;state.Text="Читаю состояние…";UpdateActions();Shown+=async(sender,args)=>await Reload();}
    }
    public SnapshotsWindow(MainWindow main,string name,object result,bool boot=false):this(new LegacySnapshotUiService(main,name),name,result,boot){}
    void Fill(object value){catalog=value;rows.Clear();foreach(var row in J.A(J.V(value,"rows")))rows.Add(new SnapshotUiEntry(row));failed=false;loading=false;RenderList();}
    void RenderList(){
        string previous=selected==null?null:selected.Id;IEnumerable<SnapshotUiEntry> visible=rows;
        string query=search.Text.Trim();if(query.Length>0)visible=visible.Where(row=>(row.Name+" "+row.Description+" "+row.Id).IndexOf(query,StringComparison.CurrentCultureIgnoreCase)>=0);
        if(filter.SelectedIndex>0){string kind=new[]{"disk","ram","external"}[filter.SelectedIndex-1];visible=visible.Where(row=>row.Kind==kind);}
        visible=sort.SelectedIndex==2?visible.OrderBy(row=>row.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(row=>row.Id):sort.SelectedIndex==1?visible.OrderBy(row=>row.Date).ThenBy(row=>row.Id):visible.OrderByDescending(row=>row.Date).ThenBy(row=>row.Id);
        list.Items.Clear();foreach(var row in visible)list.Items.Add(row);int index=list.Items.Cast<SnapshotUiEntry>().ToList().FindIndex(row=>row.Id==previous);if(list.Items.Count>0)list.SelectedIndex=index<0?0:index;else selected=null;
        summary.Text="Снимков: "+rows.Count+" · показано: "+list.Items.Count+" · внутренние снимки — qcow2";
        string vm=J.S(catalog,"vm_state",J.B(catalog,"online")?"running":SnapshotUiText.IsStopped(catalog)?"stopped":"unknown");state.Text=failed?"Не удалось получить список":vm=="paused"?"VM на паузе":vm=="stopped"?"VM выключена":vm=="running"?"VM работает":"Состояние VM не подтверждено";
        RenderDetail();
    }
    void RenderDetail(){
        empty.Visible=failed||list.Items.Count==0;list.Visible=!empty.Visible;empty.Text=failed?"Список снимков недоступен.\r\nНажми «Обновить», чтобы повторить чтение.":rows.Count==0?"Снимков пока нет.\r\nСоздай первую точку восстановления.":"Нет совпадений.\r\nИзмени поиск или фильтр.";
        detail.Text=selected==null?(failed?"Список недоступен. Нажми «Обновить», чтобы повторить чтение.":rows.Count==0?"Снимков пока нет.\r\n\r\nСоздание доступно только для поддерживаемой конфигурации. Снимки дисков не сохраняют RAM и не заменяют резервную копию.":"Нет совпадений. Измени поиск или фильтр."):SnapshotUiText.Details(selected,catalog);
        tips.SetToolTip(list,selected==null?"":selected.Name);UpdateActions();
    }
    void UpdateActions(){
        bool usable=!busy&&!loading&&!failed;create.Enabled=usable;refresh.Enabled=!busy;more.Enabled=usable;remove.Enabled=usable&&selected!=null;
        restore.Enabled=usable&&selected!=null&&selected.State=="ready"&&(SnapshotUiText.IsStopped(catalog)||(selected.Kind=="ram"&&new[]{"paused","prelaunch"}.Contains(J.S(catalog,"vm_state"))))&&!J.B(catalog,"recovery_required");search.Enabled=filter.Enabled=sort.Enabled=list.Enabled=!busy;
        if(!busy&&!failed)note.Text=J.B(catalog,"recovery_required")?"Нужна проверка незавершённой операции. Восстановление заблокировано.":J.B(catalog,"online")?"Снимки дисков доступны онлайн. RAM требует паузы и совместимой TCG-конфигурации.":"VM выключена. Для офлайн-операций требуется место под полные рабочие копии. Внешние слои и полное состояние имеют отдельные ограничения.";
        tips.SetToolTip(restore,J.B(catalog,"online")?"Сначала выключи VM.":"Открыть подтверждение восстановления выбранной точки.");
        foreach(ToolStripItem item in menu.Items)if(new[]{"Подробности","Название и описание…","Защита снимка…","Параметры VM · сравнить"}.Contains(item.Text))item.Enabled=selected!=null;
        foreach(ToolStripItem item in menu.Items)if(item.Text=="Независимая копия…")item.Enabled=usable&&service.Supports("export")&&SnapshotUiText.IsStopped(catalog)&&!J.B(catalog,"recovery_required");
        foreach(ToolStripItem item in menu.Items)if(item.Text=="Принять старый снимок…")item.Enabled=selected!=null&&selected.Id.StartsWith("legacy-",StringComparison.Ordinal);
    }
    internal async Task Reload(){
        if(busy)return;busy=loading=true;note.ForeColor=Theme.Muted;note.Text="Читаю каталог снимков…";UpdateActions();
        try{var result=await service.Load();if(!closing&&!IsDisposed)Fill(result);}
        catch(Exception error){if(!closing&&!IsDisposed){failed=true;note.ForeColor=Theme.Red;note.Text="Не удалось прочитать снимки: "+error.Message;state.Text="Список недоступен";list.Visible=false;empty.Visible=true;empty.Text="Список снимков недоступен.\r\nНажми «Обновить», чтобы повторить чтение.";detail.Text="Ни один снимок не изменён этим запросом. Повтори чтение после проверки доступности VM и дисков.";}}
        finally{if(!IsDisposed){busy=loading=false;UpdateActions();}}
    }
    internal async Task Perform(SnapshotUiRequest request){
        if(busy)return;busy=true;operationId=null;note.ForeColor=Theme.Muted;note.Text="Ожидаю подтверждённый результат операции…";UpdateActions();
        bool confirmed=false;
        try{var result=await service.Execute(request);confirmed=true;if(IsDisposed)return;string checkpoint=J.S(result,"checkpoint");
            var updated=await service.Load();if(IsDisposed)return;Fill(updated);
            note.Text="Операция завершена."+(checkpoint.Length>0?" Страховочная точка: "+checkpoint:request.Action.StartsWith("restore",StringComparison.Ordinal)?" Имя страховочной точки не передано в ответе; проверь живые логи VM.":"")+(request.Action=="export"?"\r\nКопия сохранена: "+J.S(result,"path"):"");
        }catch(OperationCanceledException){if(!IsDisposed){note.ForeColor=Theme.Muted;note.Text="Отменено до изменения дисков.";}}
        catch(Exception error){if(!IsDisposed){failed=true;note.ForeColor=Theme.Red;note.Text=(confirmed?"Операция подтверждена, но обновить список не удалось: ":"Операция не подтверждена: ")+error.Message+(operationId==null?"":"\r\nID: "+operationId)+"\r\nОбнови список перед повтором.";}}
        finally{if(!IsDisposed){busy=false;cancel.Visible=false;UpdateActionsAfterOperation();}}
    }
    void UpdateActionsAfterOperation(){string message=note.Text;Color color=note.ForeColor;UpdateActions();note.Text=message;note.ForeColor=color;}
    async void OpenOperation(string action){
        if(busy)return;if(action!="create"&&action!="schedule"&&action!="recover"&&action!="export"&&selected==null)return;
        using(var dialog=new SnapshotOperationDialog(service,action,selected,catalog)){if(dialog.ShowDialog(this)==DialogResult.OK)await Perform(dialog.Request);}
    }
    void ShowInfo(string action){
        if(busy)return;using(var dialog=new SnapshotInformationDialog(action,selected,catalog)){dialog.ShowDialog(this);}
    }
    void Progress(SnapshotUiProgress progress){
        if(IsDisposed||closing||!IsHandleCreated)return;if(InvokeRequired){BeginInvoke(new Action(()=>Progress(progress)));return;}if(!busy)return;
        operationId=progress.OperationId;note.Text=progress.Phase??"Выполняется операция…";
        if(progress.Done.HasValue&&progress.Total.HasValue&&progress.Total.Value>0)note.Text+=" · "+Math.Min(100,Math.Max(0,progress.Done.Value*100.0/progress.Total.Value)).ToString("0")+"%";
        cancel.Visible=progress.CanCancel&&service.Supports("cancel")&&!String.IsNullOrEmpty(operationId);cancel.Enabled=cancel.Visible;
    }
    async Task Cancel(){if(!busy||!service.Supports("cancel")||String.IsNullOrEmpty(operationId))return;cancel.Enabled=false;try{await service.Execute(new SnapshotUiRequest("cancel",null,J.O("operation_id",operationId)));note.Text="Отмена запрошена. Ожидаю безопасного завершения текущего шага…";}catch(Exception error){note.Text="Не удалось подтвердить отмену: "+error.Message;}}
    protected override void Dispose(bool disposing){if(disposing){service.Progress-=Progress;tips.Dispose();menu.Dispose();}base.Dispose(disposing);}
}

sealed class SnapshotInformationDialog : Form {
    public SnapshotInformationDialog(string mode,SnapshotUiEntry entry,object catalog){
        Text=mode=="config"?"Параметры VM":mode=="history"?"История снимков":mode=="help"?"О снимках":"Подробности снимка";Theme.Form(this,920,710);
        var content=new Panel{Dock=DockStyle.Fill,Padding=new Padding(24)};var buttons=Theme.Flow();
        if(mode=="config"){
            var table=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=3};table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));table.RowStyles.Add(new RowStyle(SizeType.AutoSize));table.RowStyles.Add(new RowStyle(SizeType.AutoSize));table.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var difference=Theme.Label(SnapshotUiText.ConfigurationDiff(J.V(catalog,"context"),entry==null?null:J.V(entry.Raw,"parameters")),10,Theme.Yellow);table.Controls.Add(difference,0,0);table.SetColumnSpan(difference,2);table.SizeChanged+=(sender,args)=>difference.MaximumSize=new Size(Math.Max(1,table.Width),0);table.Controls.Add(Theme.Label("Текущие параметры",10,Theme.Muted),0,1);table.Controls.Add(Theme.Label("В выбранном снимке",10,Theme.Muted),1,1);
            foreach(int column in new[]{0,1}){object data=column==0?J.V(catalog,"context"):entry==null?null:J.V(entry.Raw,"parameters");var editor=Theme.Editor(data==null?"Параметры не переданы службой.":Pretty(data));editor.ReadOnly=true;editor.Margin=new Padding(column==0?0:8,0,column==0?8:0,0);table.Controls.Add(editor,column,2);Shown+=(sender,args)=>editor.Input.Select(0,0);}content.Controls.Add(table);
        }else{
            string text=mode=="history"?History(catalog):mode=="help"?"Снимок дисков\r\nВозвращает данные дисков. RAM, открытые программы и несохранённые данные в памяти не возвращаются.\r\n\r\nПолное состояние VM\r\nСохраняет RAM и устройства только при подтверждённой поддержке backend, QEMU и конфигурации.\r\n\r\nНезависимая резервная копия\r\nОтдельный проверенный образ, который не зависит от сохранности исходного файла. Внутренний снимок qcow2 такой копией не является.\r\n\r\nСогласованность\r\nБез подготовки через Guest Agent нельзя обещать согласованность незавершённых операций приложений.\r\n\r\nСтраховочная точка\r\nНужна перед восстановлением. Не удаляй её, если результат восстановления не подтверждён.":SnapshotUiText.Details(entry,catalog);
            var editor=Theme.Editor(text);editor.ReadOnly=true;content.Controls.Add(editor);Shown+=(sender,args)=>editor.Input.Select(0,0);buttons.Controls.Add(Theme.Button("Копировать текст",()=>Clipboard.SetText(text)));
        }
        buttons.Controls.Add(Theme.Button("Закрыть",Close));Theme.Dialog(this,content,buttons,Text,entry==null?"Информация не изменяет VM и диски":entry.Name);
    }
    static string History(object catalog){var history=J.A(J.V(catalog,"history"));if(history.Length==0)return J.D(catalog).ContainsKey("history")?"История операций пока пуста.":"История ещё не передаётся backend. Действия не подменяются демонстрационными записями.";return String.Join("\r\n\r\n",history.Select(item=>J.S(item,"date")+" · "+J.S(item,"action")+" · "+J.S(item,"state")+"\r\n"+J.S(item,"detail")+"\r\nID: "+J.S(item,"id")));}
    static string Pretty(object value){return String.Join(Environment.NewLine,J.D(value).Select(pair=>pair.Key+": "+J.Json().Serialize(pair.Value)));}
}
