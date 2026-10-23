using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Windows.Forms;
using System.Threading.Tasks;

sealed partial class MainWindow {
    bool standMode;object selectedStand;List<object> stands=new List<object>();
    readonly HashSet<string> expandedStands=new HashSet<string>();
    readonly Panel standDetail=new Panel{Dock=DockStyle.Fill,BackColor=Theme.Bg};
    Label standLibraryTitle;Button standAdd;bool standLoading;string standError="";
    string normalSelectedVm,standSelectedVm,standSelectedParent;int machineScroll,standScroll;string restoreStand;object libraryPrefs;
    object standActivity;bool standActivityPending;
    readonly Dictionary<string,Button> standMemberButtons=new Dictionary<string,Button>();Label standPhase;
    int StandJobs;
    void LibraryPathSafe(string path){for(string p=Path.GetFullPath(path);!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Настройки библиотеки через ссылки запрещены.");}
    void InitStands(SplitContainer split,Panel header,Label title,Button add,Button more){
        standLibraryTitle=title;standAdd=add;
        var toggle=new LibraryModeSwitch{Text="Режим стендов",Location=new Point(9,79),AccessibleName="Переключить машины и стенды"};
        header.Height=175;search.Top=115;
        toggle.CheckedChanged+=(s,e)=>{if(standMode)standScroll=libraryScroll.ScrollOffset;else machineScroll=libraryScroll.ScrollOffset;restoreStand=J.S(selectedStand,"id",restoreStand);if(standMode){standSelectedVm=selectedStand==null?selected:null;}else normalSelectedVm=selected;standMode=toggle.Checked;if(!standMode)selected=normalSelectedVm;selectedStand=null;standDetail.Visible=false;title.Text=standMode?"Мои стенды":"Мои машины";add.Text=standMode?"＋  Создать стенд":"＋  Новая машина";search.Placeholder=standMode?"Найти стенд или VM…":"Найти машину…";more.Enabled=!standMode;if(standMode){LoadStands();libraryScroll.ScrollOffset=standScroll;}else{RenderCards();SelectVm(selected);libraryScroll.ScrollOffset=machineScroll;}};
        header.Controls.Add(toggle);detail.Controls.Add(standDetail);standDetail.BringToFront();standDetail.Visible=false;
        var refresh=Theme.Button("Обновить стенды",LoadStands);refresh.Visible=false;add.Parent.Controls.Add(refresh);toggle.CheckedChanged+=(s,e)=>refresh.Visible=standMode;
        add.Click+=(s,e)=>{if(standMode)EditStand(null);};
        split.Panel1.Resize+=(s,e)=>{int width=Math.Max(170,split.Panel1.ClientSize.Width-48);search.Width=width;more.Left=Math.Max(150,width-16);add.Width=width;};
        split.AccessibleName="Изменяемая ширина библиотеки";
        split.TabStop=true;split.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Left||e.KeyCode==Keys.Right){split.SplitterDistance=Math.Max(220,Math.Min(split.SplitterDistance+(e.KeyCode==Keys.Right?16:-16),Math.Max(220,split.Width-427)));e.Handled=true;}};
        string prefs=Path.Combine(RootPath,"stand-library-ui.json");
        try{LibraryPathSafe(prefs);if(File.Exists(prefs)&&new FileInfo(prefs).Length<=65536)libraryPrefs=J.Json().DeserializeObject(File.ReadAllText(prefs));}catch(Exception error){standError="Не удалось прочитать настройки библиотеки: "+error.Message;}
        foreach(string id in J.A(J.V(libraryPrefs,"expanded")).Select(Convert.ToString))expandedStands.Add(id);
        restoreStand=J.S(libraryPrefs,"stand");
        selected=J.S(libraryPrefs,"machine",null);
        Shown+=(s,e)=>{split.SplitterDistance=Math.Max(220,Math.Min(J.N(libraryPrefs,"width",256),Math.Max(220,split.Width-427)));toggle.Checked=J.B(libraryPrefs,"stands");};
        FormClosed+=(s,e)=>{if(demo)return;try{LibraryPathSafe(prefs);Directory.CreateDirectory(RootPath);string temp=prefs+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllText(temp,J.Json().Serialize(J.O("machine",selected,"width",split.SplitterDistance,"stands",standMode,"stand",J.S(selectedStand,"id",restoreStand),"expanded",expandedStands.ToArray())));if(File.Exists(prefs))File.Replace(temp,prefs,null);else File.Move(temp,prefs);}finally{if(File.Exists(temp))File.Delete(temp);}}catch(Exception error){MessageBox.Show("Не удалось сохранить ширину и режим библиотеки: "+error.Message,"QemuM");}};
    }
    void LoadStands(){
        if(Backend==null){standError="Для стендов нужна фоновая служба.";RenderStands();return;}
        if(standLoading||Busy)return;standLoading=true;
        Run("Читаю стенды…",()=>Backend.Call("stand_list"),value=>{standLoading=false;stands=J.A(J.V(value,"rows")).ToList();standError=String.Join("\n",J.A(J.V(value,"errors")).Select(x=>J.S(x,"file")+": "+J.S(x,"error")));selectedStand=stands.FirstOrDefault(x=>J.S(x,"id")==J.S(selectedStand,"id",restoreStand));if(standSelectedVm!=null&&Row(standSelectedVm)!=null){selectedStand=null;standDetail.Visible=false;SelectVm(standSelectedVm);}else if(selectedStand!=null)ShowStand();else standDetail.Visible=false;RenderStands();libraryScroll.ScrollOffset=standScroll;},error=>{standLoading=false;standError=error.Message;RenderStands();});
    }
    void RenderStands(){
        if(!standMode)return;int offset=libraryScroll.ScrollOffset;
        libraryScroll.SuspendLayout();cards.SuspendLayout();foreach(Control child in cards.Controls.Cast<Control>().ToArray())child.Dispose();cards.Controls.Clear();libraryCount.Text="Всего стендов: "+stands.Count;
        foreach(var stand in stands){
            var members=J.A(J.V(stand,"members")).Select(Convert.ToString).ToArray();string id=J.S(stand,"id"),title=J.S(stand,"name");
            bool match=(title+" "+String.Join(" ",members.Select(n=>J.S(J.V(Row(n),"data"),"label",n)))).IndexOf(search.Text,StringComparison.OrdinalIgnoreCase)>=0;if(!match)continue;
            Button folder=null;folder=new StandFolderButton((expandedStands.Contains(id)?"▾  ":"▸  ")+"Стенд · "+title+" · "+members.Length+" VM",()=>ToggleStandFolder(stand,folder));
            folder.Width=Math.Max(118,cards.Width-folder.Margin.Horizontal);folder.MaximumSize=new Size(folder.Width,0);folder.TextAlign=ContentAlignment.MiddleLeft;folder.AccessibleName="Стенд: "+title;cards.Controls.Add(folder);
            folder.Tag=stand;
            if(expandedStands.Contains(id))for(int index=0;index<members.Length;index++)cards.Controls.Add(StandChild(members[index],stand,index==members.Length-1));
        }
        if(cards.Controls.Count==0)cards.Controls.Add(Theme.Label(stands.Count==0?"Стендов пока нет.\nСоздай свой первый стенд.":"Ничего не найдено.",10,Theme.Muted));
        if(!String.IsNullOrEmpty(standError))cards.Controls.Add(Theme.Label(standError,9,Theme.Muted));
        cards.ResumeLayout();libraryScroll.ResumeLayout(true);libraryScroll.PerformLayout();libraryScroll.ScrollOffset=offset;
        PaintStandStatus();
    }
    Button StandChild(string name,object stand,bool last){
        string title=J.S(stand,"name"),id=J.S(stand,"id");var row=Row(name);
        var child=new StandMemberButton(id,name,last){Text=J.S(J.V(row,"data"),"label",name)+(row==null?" · отсутствует":""),AccessibleName="Машина "+J.S(J.V(row,"data"),"label",name)+", стенд "+title};
        child.Click+=(s,e)=>{standSelectedVm=name;standSelectedParent=restoreStand=id;selectedStand=null;standDetail.Visible=false;SelectVm(name);breadcrumb.Text="Стенды / "+title+" / "+name;PaintStandStatus();};
        child.Width=Math.Max(112,cards.Width-child.Margin.Horizontal);child.MaximumSize=new Size(child.Width,0);return child;
    }
    void ToggleStandFolder(object stand,Button folder){
        string id=J.S(stand,"id");int offset=libraryScroll.ScrollOffset;
        bool changed=!Object.ReferenceEquals(selectedStand,stand);standSelectedVm=null;standSelectedParent=null;selectedStand=stand;if(changed)ShowStand();
        libraryScroll.SuspendLayout();cards.SuspendLayout();
        try{
            int position=cards.Controls.GetChildIndex(folder)+1;
            if(expandedStands.Remove(id)){
                while(position<cards.Controls.Count&&cards.Controls[position].AccessibleDescription=="stand-member"){var child=cards.Controls[position];cards.Controls.Remove(child);child.Dispose();}
            }else{
                expandedStands.Add(id);
                var members=J.A(J.V(stand,"members")).Select(Convert.ToString).ToArray();
                for(int index=0;index<members.Length;index++){var child=StandChild(members[index],stand,index==members.Length-1);cards.Controls.Add(child);cards.Controls.SetChildIndex(child,position++);}
            }
        }finally{cards.ResumeLayout();libraryScroll.ResumeLayout(true);libraryScroll.PerformLayout();libraryScroll.ScrollOffset=offset;}
        PaintStandStatus();
    }
    void UpdateStandStatus(){
        PaintStandStatus();
        if(Backend==null||standActivityPending)return;standActivityPending=true;
        Task.Run(()=>{try{object value=Backend.Call("stand_activity");Post(()=>{standActivity=value;StandJobs=J.D(value).Values.Count(v=>new[]{"starting","stopping"}.Contains(J.S(v,"state")));PaintStandStatus();});}catch{}finally{Post(()=>standActivityPending=false);}});
    }
    void PaintStandStatus(){
        if(!standMode)return;libraryCount.Text="Всего стендов: "+stands.Count;
        libraryScroll.SuspendLayout();cards.SuspendLayout();
        try{
        foreach(var button in cards.Controls.OfType<Button>()){
            var stand=button.Tag;if(stand==null)continue;var members=J.A(J.V(stand,"members")).Select(Convert.ToString).ToArray();
            int running=members.Count(n=>new[]{"running","paused","prelaunch"}.Contains(State(n)));
            string aggregate=members.Any(n=>new[]{"error","recovery-required"}.Contains(State(n)))?"error":members.Any(n=>new[]{"unavailable","no-qmp","unknown"}.Contains(State(n)))?"unknown":running==members.Length?"running":running>0?"partial":"stopped";
            var activity=J.V(standActivity,J.S(stand,"id"));if(!String.IsNullOrEmpty(J.S(activity,"state")))aggregate=J.S(activity,"state");
            string id=J.S(stand,"id");button.Text=(expandedStands.Contains(id)?"▾  ":"▸  ")+"Стенд · "+J.S(stand,"name")+" · "+StandOperationWindow.StateName(aggregate)+" · "+running+"/"+members.Length;
            button.ForeColor=J.S(selectedStand,"id")==id||standSelectedParent==id?Theme.Accent:Theme.Text;
        }
        foreach(var member in cards.Controls.OfType<StandMemberButton>())member.Selected=member.StandId==standSelectedParent&&member.VmName==standSelectedVm;
        }finally{cards.ResumeLayout();libraryScroll.ResumeLayout(true);}
        foreach(var entry in standMemberButtons)if(!entry.Value.IsDisposed){var row=Row(entry.Key);entry.Value.Text=J.S(J.V(row,"data"),"label",entry.Key)+(row==null?" · VM отсутствует":" · "+Theme.State(State(entry.Key)));}
        if(standPhase!=null&&!standPhase.IsDisposed&&selectedStand!=null){var activity=J.V(standActivity,J.S(selectedStand,"id"));standPhase.Text=String.IsNullOrEmpty(J.S(activity,"phase"))?"Состояния участников обновляются ниже.":J.S(activity,"phase")+" · "+J.S(activity,"current");}
    }
    void ShowStand(){
        standMemberButtons.Clear();
        foreach(Control child in standDetail.Controls.Cast<Control>().ToArray())child.Dispose();standDetail.Controls.Clear();
        var stack=Theme.Flow(true);stack.Dock=DockStyle.Top;
        stack.Controls.Add(Theme.Label("СТЕНД / "+J.S(selectedStand,"name"),10,Theme.Muted));
        var title=Theme.Label(J.S(selectedStand,"name"),24,null,true);title.MaximumSize=new Size(Math.Max(260,detail.Width-80),0);stack.Controls.Add(title);
        stack.Controls.Add(Theme.Label(J.S(selectedStand,"description"),10,Theme.Muted));
        stack.Controls.Add(Theme.Button("Изменить состав и название",()=>EditStand(selectedStand)));
        stack.Controls.Add(Theme.Button("Соединения и порядок запуска",()=>EditStandTopology(selectedStand)));
        string standId=J.S(selectedStand,"id");
        var operations=Theme.Flow();operations.MaximumSize=new Size(Math.Max(260,detail.ClientSize.Width-80),0);operations.Controls.Add(Theme.Button("Запустить / продолжить",()=>PlanStand(standId,"start"),true));
        operations.Controls.Add(Theme.Button("Выключить стенд",()=>PlanStand(standId,"stop")));
        operations.Controls.Add(Theme.Button("Остановить QEMU стенда",()=>PlanStand(standId,"stop",true),false,true));
        operations.Controls.Add(Theme.Button("Состояние операции",()=>Run("Читаю состояние стенда…",()=>Backend.Call("stand_overview",J.O("id",standId)),result=>{
            string op=J.S(result,"operation_id");if(!String.IsNullOrEmpty(op))new StandOperationWindow(Backend,op).Show(this);
            else MessageBox.Show(this,"Состояние стенда: "+StandOperationWindow.StateName(J.S(result,"state"))+". В этом сеансе групповых операций ещё нет.");
        })));
        stack.Controls.Add(operations);
        var maintenance=Theme.Flow();maintenance.MaximumSize=operations.MaximumSize;
        maintenance.Controls.Add(Theme.Button("Проверить после сбоя",()=>{
            if(MessageBox.Show(this,"Сверить QMP, процессы и журнал? Автоматического запуска и выключения не будет.","Сверка стенда",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
            Run("Сверяю стенд…",()=>Backend.Call("stand_reconcile",J.O("id",standId,"revision",J.N(selectedStand,"revision"),"confirm",standId)),result=>{
                string issues=String.Join("\n",J.A(J.V(result,"issues")).Select(Convert.ToString));MessageBox.Show(this,String.IsNullOrEmpty(issues)?"Сверка выполнена. Можно сформировать новый план запуска/продолжения.":issues,"Сверка стенда");
            });
        }));
        maintenance.Controls.Add(Theme.Button("Дублировать стенд",()=>DuplicateStand(selectedStand)));
        maintenance.Controls.Add(Theme.Button("Удалить папку стенда",()=>RemoveStand(selectedStand),false,true));
        maintenance.Controls.Add(Theme.Button("Экспорт отчёта",()=>ExportStand(standId)));
        maintenance.Controls.Add(Theme.Button("Справка о стендах",()=>{using(var faq=new FaqWindow())faq.ShowDialog(this);}));stack.Controls.Add(maintenance);
        standPhase=Theme.Label("Состояния участников обновляются ниже.",10,Theme.Muted);standPhase.MaximumSize=new Size(Math.Max(260,detail.ClientSize.Width-80),0);stack.Controls.Add(standPhase);
        stack.Controls.Add(Theme.Label("Состав стенда",14,null,true));
        foreach(string name in J.A(J.V(selectedStand,"members")).Select(Convert.ToString)){string n=name;var row=Row(n);var member=Theme.Button(J.S(J.V(row,"data"),"label",n)+(row==null?" · VM отсутствует":" · "+Theme.State(State(n))),()=>{standSelectedVm=n;standSelectedParent=restoreStand=standId;selectedStand=null;standDetail.Visible=false;SelectVm(n);PaintStandStatus();});stack.Controls.Add(member);standMemberButtons[n]=member;}
        stack.Controls.Add(Theme.Label("Стенд хранит ссылки на VM; образы не перемещаются.\nНастройка связей не назначает IP и DHCP внутри гостевых ОС.",10,Theme.Muted));
        var tabs=new WorkspaceTabs{Width=Math.Max(260,detail.ClientSize.Width-80),Height=180};
        tabs.Add("Соединения",Theme.Label(String.Join("\n",J.A(J.V(selectedStand,"links")).Select(link=>J.S(J.V(link,"listen"),"vm")+" / "+J.S(J.V(link,"listen"),"nic")+" → "+J.S(J.V(link,"connect"),"vm")+" / "+J.S(J.V(link,"connect"),"nic"))),10,Theme.Muted));
        tabs.Add("Запуск",Theme.Label("Порядок запуска:\n"+String.Join(" → ",J.A(J.V(selectedStand,"order")).Select(Convert.ToString))+"\nОтмена прекращает будущие этапы; запущенные VM остаются работать.",10,Theme.Muted));
        tabs.Add("Журнал",Theme.Label("Кнопка «Состояние операции» открывает этапы и журнал последней операции этого стенда.\nЛоги отдельных VM доступны в их карточках.",10,Theme.Muted));
        stack.Controls.Add(tabs);
        standDetail.Controls.Add(new ScrollSurface(stack){Dock=DockStyle.Fill,BackColor=Theme.Bg});foreach(Control sibling in detail.Controls)sibling.Visible=Object.ReferenceEquals(sibling,standDetail);standDetail.BringToFront();
    }
    void EditStandTopology(object existing){
        using(var form=new Form()){
            Theme.Form(form,850,800);form.Text="Соединения и запуск стенда";
            Dictionary<string,object> draft;
            // Use an independent JSON clone: cancellation never changes the selected stand.
            draft=J.Json().DeserializeObject(J.Json().Serialize(existing)) as Dictionary<string,object>;
            var order=J.A(J.V(existing,"order")).Select(Convert.ToString).ToList();
            var links=J.A(J.V(existing,"links")).ToList();
            var startup=J.V(existing,"startup") as Dictionary<string,object> ?? order.ToDictionary(n=>n,n=>(object)J.O("delay",0,"timeout",60,"ready","qmp"));
            var fields=Theme.Fields();fields.AutoSize=true;var list=new ThemedList{Height=150,MinimumSize=new Size(0,150)};foreach(var n in order)list.Items.Add(n);list.SelectedIndex=0;
            Theme.Row(fields,"Порядок запуска",list,true);
            var delay=Theme.Number(0,0,300);var timeout=Theme.Number(60,1,600);var ready=Theme.Combo("Процесс создан + проверка QMP","QMP готов (не ОС)","TCP-сервис на этом компьютере");var tcpPort=Theme.Number(2222,1024,65535);
            int previous=-1;
            Action store=()=>{if(previous>=0){var value=J.O("delay",(int)delay.Value,"timeout",(int)timeout.Value,"ready",ready.SelectedIndex==0?"process":ready.SelectedIndex==1?"qmp":"tcp");if(ready.SelectedIndex==2)value["tcp_port"]=(int)tcpPort.Value;startup[order[previous]]=value;}};
            Action load=()=>{previous=list.SelectedIndex;if(previous<0)return;var step=startup[order[previous]];delay.Value=J.N(step,"delay");timeout.Value=J.N(step,"timeout",60);ready.SelectedIndex=J.S(step,"ready")=="process"?0:J.S(step,"ready")=="tcp"?2:1;tcpPort.Value=J.N(step,"tcp_port",2222);};
            list.SelectedIndexChanged+=(s,e)=>{try{store();load();}catch(Exception error){MessageBox.Show(form,error.Message);}};load();
            var moves=Theme.Flow();foreach(int delta in new[]{-1,1}){int d=delta;moves.Controls.Add(Theme.Button(d<0?"Выше":"Ниже",()=>{store();int i=list.SelectedIndex,j=i+d;if(i<0||j<0||j>=order.Count)return;var n=order[i];order.RemoveAt(i);order.Insert(j,n);previous=-1;list.Items.Clear();foreach(var value in order)list.Items.Add(value);list.SelectedIndex=j;load();}));}Theme.Row(fields,"",moves,true);
            Theme.Row(fields,"Задержка перед запуском, секунд",delay);Theme.Row(fields,"Тайм-аут готовности, секунд",timeout);Theme.Row(fields,"Условие готовности",ready,true);
            Theme.Row(fields,"Локальный TCP-порт сервиса (только для TCP-проверки)",tcpPort,true);
            var endpoints=new List<object>();
            foreach(var n in order)foreach(var nic in J.A(J.V(J.V(Row(n),"data"),"network_adapters")))endpoints.Add(J.O("vm",n,"nic",J.S(nic,"id")));
            var listen=Theme.Combo();var connect=Theme.Combo();foreach(var end in endpoints){string text=J.S(end,"vm")+" / "+J.S(end,"nic");listen.Items.Add(text);connect.Items.Add(text);}if(endpoints.Count>0)listen.SelectedIndex=connect.SelectedIndex=0;
            Theme.Row(fields,"Слушатель (запускается первым)",listen);Theme.Row(fields,"Подключаемый участник",connect);
            var linkList=new ThemedList{Height=120};Action refresh=()=>{linkList.Items.Clear();foreach(var link in links)linkList.Items.Add(J.S(J.V(link,"listen"),"vm")+" / "+J.S(J.V(link,"listen"),"nic")+" → "+J.S(J.V(link,"connect"),"vm")+" / "+J.S(J.V(link,"connect"),"nic"));};refresh();Theme.Row(fields,"Внутренние соединения",linkList,true);
            linkList.SelectedIndexChanged+=(s,e)=>{int i=linkList.SelectedIndex;if(i<0)return;var a=J.V(links[i],"listen");var b=J.V(links[i],"connect");listen.SelectedIndex=endpoints.FindIndex(end=>J.S(end,"vm")==J.S(a,"vm")&&J.S(end,"nic")==J.S(a,"nic"));connect.SelectedIndex=endpoints.FindIndex(end=>J.S(end,"vm")==J.S(b,"vm")&&J.S(end,"nic")==J.S(b,"nic"));};
            var linkButtons=Theme.Flow();linkButtons.Controls.Add(Theme.Button("Добавить связь",()=>{if(listen.SelectedIndex<0||connect.SelectedIndex<0)return;links.Add(J.O("id",Guid.NewGuid().ToString("N"),"listen",endpoints[listen.SelectedIndex],"connect",endpoints[connect.SelectedIndex]));refresh();}));linkButtons.Controls.Add(Theme.Button("Удалить связь",()=>{int i=linkList.SelectedIndex;if(i>=0&&MessageBox.Show(form,"Удалить выбранную связь? Изменение вступит в силу после сохранения.","Соединение",MessageBoxButtons.YesNo)==DialogResult.Yes){links.RemoveAt(i);refresh();}}));Theme.Row(fields,"",linkButtons,true);
            linkButtons.Controls.Add(Theme.Button("Изменить связь",()=>{int i=linkList.SelectedIndex;if(i<0||listen.SelectedIndex<0||connect.SelectedIndex<0)return;links[i]=J.O("id",J.S(links[i],"id"),"listen",endpoints[listen.SelectedIndex],"connect",endpoints[connect.SelectedIndex]);refresh();}));
            Theme.Row(fields,"",Theme.Label("Выбранный адаптер на время запуска переводится во внутреннюю связь вместо прежней сети.\nДля внешнего NAT и проброса портов оставь отдельный адаптер. JSON одиночной VM не меняется.\nIP/DHCP внутри гостей настраиваются вручную. QMP готов подтверждает QEMU, а не загрузку ОС.\nTCP проверяет 127.0.0.1: нужен доступный сервис или явный проброс порта. Guest Agent не нужен.\nПроверка не выполняет команды внутри гостя и не хранит пароли.",10,Theme.Muted),true);
            var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Сохранить",()=>{try{store();draft["schema_version"]=2;draft["order"]=order.ToArray();draft["links"]=links.ToArray();draft["startup"]=startup;if(MessageBox.Show(form,"Сохранить порядок и "+links.Count+" соединений? Настройки отдельных VM не меняются.","План изменений",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;Run("Сохраняю топологию…",()=>Backend.Call("stand_save",J.O("stand",draft,"revision",J.N(existing,"revision"))),result=>{form.Close();selectedStand=result;ShowStand();LoadStands();});}catch(Exception error){MessageBox.Show(form,error.Message);}},true));buttons.Controls.Add(Theme.Button("Отмена",form.Close));Theme.Dialog(form,new ScrollSurface(fields),buttons,"Соединения и порядок","Изменения применяются только после сохранения.");form.ShowDialog(this);
        }
    }
    void EditStand(object existing){
        if(Backend==null)return;
        using(var form=new Form()){
            Theme.Form(form,650,640);form.Text=existing==null?"Новый стенд":"Параметры стенда";
            var stack=Theme.Flow(true);stack.Dock=DockStyle.Top;stack.Padding=new Padding(24);
            var name=Theme.Input(J.S(existing,"name"));var description=Theme.Input(J.S(existing,"description"));
            stack.Controls.Add(Theme.Label("Название стенда",10,Theme.Muted));stack.Controls.Add(name);name.Width=540;
            stack.Controls.Add(Theme.Label("Описание",10,Theme.Muted));stack.Controls.Add(description);description.Width=540;
            var checks=new Dictionary<string,CheckBox>();var members=J.A(J.V(existing,"members")).Select(Convert.ToString).ToArray();
            foreach(var row in rows){string key=J.S(row,"name");var c=new CheckBox{Text=J.S(J.V(row,"data"),"label",key),Checked=members.Contains(key),AutoSize=true,ForeColor=Theme.Text,MaximumSize=new Size(540,0)};checks[key]=c;stack.Controls.Add(c);}
            var warning=Theme.Label("Одна VM может входить в несколько стендов. Её диски не копируются.\nИсключение участника не удаляет VM.",10,Theme.Muted);warning.MaximumSize=new Size(540,0);stack.Controls.Add(warning);
            var buttons=Theme.Flow();buttons.Dock=DockStyle.Bottom;buttons.Padding=new Padding(24);
            var save=Theme.Button("Сохранить",()=>{},true);buttons.Controls.Add(save);buttons.Controls.Add(Theme.Button("Отмена",()=>form.Close()));
            bool saving=false;form.FormClosing+=(s,e)=>{if(saving)e.Cancel=true;};
            save.Click+=(s,e)=>{
                var chosen=members.Where(n=>checks.ContainsKey(n)&&checks[n].Checked).Concat(checks.Where(p=>p.Value.Checked&&!members.Contains(p.Key)).Select(p=>p.Key)).ToArray();
                if(String.IsNullOrWhiteSpace(name.Text)||chosen.Length==0){MessageBox.Show(form,"Введи название и выбери хотя бы одну VM.");return;}
                var links=J.A(J.V(existing,"links"));
                var keptLinks=links.Where(link=>chosen.Contains(J.S(J.V(link,"listen"),"vm"))&&chosen.Contains(J.S(J.V(link,"connect"),"vm"))).ToArray();
                int removedLinks=links.Length-keptLinks.Length;
                if(removedLinks>0&&MessageBox.Show(form,"Исключение участников удалит связанных соединений: "+removedLinks+". Продолжить?","План изменения связей",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
                var removed=members.Where(n=>!chosen.Contains(n)).ToArray();
                if(removed.Length>0&&MessageBox.Show(form,"Убрать из стенда: "+String.Join(", ",removed)+"? VM и её файлы сохранятся.","Состав стенда",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
                var order=J.A(J.V(existing,"order")).Select(Convert.ToString).Where(chosen.Contains).Concat(chosen.Where(n=>!J.A(J.V(existing,"order")).Select(Convert.ToString).Contains(n))).ToArray();
                var draft=J.O("schema_version",1,"id",existing==null?Guid.NewGuid().ToString("N"):J.S(existing,"id"),"revision",J.N(existing,"revision"),"name",name.Text,"description",description.Text,"members",chosen,"order",order,"links",new object[0]);
                if(J.N(existing,"schema_version")==2){draft["schema_version"]=2;draft["links"]=keptLinks;draft["startup"]=chosen.ToDictionary(n=>n,n=>J.V(J.V(existing,"startup"),n)??J.O("delay",0,"timeout",60,"ready","qmp"));}
                if(Busy)return;saving=true;save.Enabled=false;
                Run("Сохраняю стенд…",()=>Backend.Call("stand_save",J.O("stand",draft,"revision",J.N(existing,"revision"))),result=>{saving=false;form.Close();selectedStand=result;ShowStand();LoadStands();},error=>{saving=false;save.Enabled=true;});
            };
            form.Controls.Add(new ScrollSurface(stack){Dock=DockStyle.Fill});form.Controls.Add(buttons);form.ShowDialog(this);
        }
    }
    void PlanStand(string id,string action,bool force=false){
        if(Backend==null||Busy)return;
        bool include=false;
        if(action=="stop"){
            using(var options=new Form()){
                Theme.Form(options,650,380);options.Text=force?"Принудительная остановка стенда":"Штатное выключение стенда";
                var fields=Theme.Flow(true);fields.Padding=new Padding(24);
                var earlier=new CheckBox{Text="Включить ранее работавшие VM, не запущенные стендом",AutoSize=true,ForeColor=Theme.Text};fields.Controls.Add(earlier);
                var explanation=Theme.Label(force?"QEMU завершится без сохранения работы гостевых программ. Несохранённые данные могут быть потеряны.":"Будет запрошено штатное выключение в обратном порядке. Если гость не ответит, операция завершится по тайм-ауту; принудительного завершения не будет.",10,Theme.Muted);explanation.MaximumSize=new Size(560,0);fields.Controls.Add(explanation);
                var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Проверить план",()=>{include=earlier.Checked;options.DialogResult=DialogResult.OK;options.Close();},true));buttons.Controls.Add(Theme.Button("Отмена",options.Close));Theme.Dialog(options,fields,buttons,"Выключение стенда","VM и их файлы не удаляются.");if(options.ShowDialog(this)!=DialogResult.OK)return;
            }
        }
        Run("Проверяю план стенда…",()=>Backend.Call("stand_plan",J.O("id",id,"action",action,"force",force,"include_existing",include)),plan=>{
            using(var form=new Form()){
                Theme.Form(form,850,720);form.Text="План операции стенда";
                var text=Theme.Editor();text.ReadOnly=true;
                var order=J.A(J.V(plan,"order")).Select(Convert.ToString).ToArray();var running=J.A(J.V(plan,"already_running")).Select(Convert.ToString).ToArray();
                text.Text=(action=="start"?"Запуск / продолжение":"Выключение")+"\r\nПорядок: "+String.Join(" → ",order)+"\r\nУже работают: "+String.Join(", ",running)+"\r\nДополнительно запрашивается: "+J.N(plan,"requested_ram_mb")+" МБ RAM, "+J.N(plan,"requested_cores")+" vCPU.\r\nСейчас работают: "+J.N(J.V(plan,"resources"),"running_ram_mb")+" МБ RAM, "+J.N(J.V(plan,"resources"),"running_cores")+" vCPU.\r\n\r\n"+J.S(plan,"warning")+"\r\n\r\nЭффективная сеть участников:\r\n"+J.Json().Serialize(J.V(plan,"networks"));
                var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button(force?"Подтвердить остановку QEMU":"Подтвердить план",()=>{form.DialogResult=DialogResult.OK;form.Close();},true,force));buttons.Controls.Add(Theme.Button("Отмена",form.Close));Theme.Dialog(form,text,buttons,"Подтверждение плана","План действует две минуты. Изменение параметров потребует новой проверки.");
                if(form.ShowDialog(this)!=DialogResult.OK){string discarded=J.S(plan,"id");Run("Отменяю план…",()=>Backend.Call("stand_discard",J.O("plan_id",discarded)));return;}
            }
            string token=J.S(plan,"id");Run("Начинаю операцию стенда…",()=>Backend.Call("stand_execute",J.O("plan_id",token,"confirm",token)),result=>{StandJobs++;new StandOperationWindow(Backend,J.S(result,"id")).Show(this);});
        });
    }
    void RemoveStand(object row){
        if(row==null||Busy)return;string id=J.S(row,"id");int revision=J.N(row,"revision");
        if(MessageBox.Show(this,"Удалить только папку «"+J.S(row,"name")+"» из библиотеки?\n\nVM, диски, снимки и работающие процессы останутся. Журнал будет сохранён для диагностики. Выполняющаяся операция блокирует удаление.","Удаление стенда",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        Run("Удаляю определение стенда…",()=>Backend.Call("stand_remove",J.O("id",id,"revision",revision,"confirm",id)),result=>{selectedStand=null;restoreStand=null;standDetail.Visible=false;SelectVm(selected);LoadStands();});
    }
    void DuplicateStand(object row){
        if(row==null||Busy)return;using(var form=new Form()){
            Theme.Form(form,650,380);form.Text="Копия стенда";var fields=Theme.Fields();string label=J.S(row,"name");var name=Theme.Input(label.Substring(0,Math.Min(110,label.Length))+" · копия");Theme.Row(fields,"Название копии",name,true);Theme.Row(fields,"",Theme.Label("Копируются ссылки на те же VM, связи и порядок. Диски не клонируются.\nОдновременная работа стендов с одной VM может быть несовместима.",10,Theme.Muted),true);
            var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Создать копию",()=>{string title=name.Text;Run("Дублирую определение…",()=>Backend.Call("stand_duplicate",J.O("id",J.S(row,"id"),"revision",J.N(row,"revision"),"name",title)),result=>{form.Close();selectedStand=result;ShowStand();LoadStands();});},true));buttons.Controls.Add(Theme.Button("Отмена",form.Close));Theme.Dialog(form,fields,buttons,"Копия стенда","VM и образы остаются общими.");form.ShowDialog(this);
        }
    }
    void ExportStand(string id){
        if(Busy)return;Run("Готовлю отчёт стенда…",()=>Backend.Call("stand_report_preview",J.O("id",id)),preview=>{
            using(var form=new Form()){
                Theme.Form(form,850,750);form.Text="Предпросмотр отчёта стенда";var text=Theme.Editor(J.S(preview,"text"));text.ReadOnly=true;
                var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Сохранить JSON…",()=>{string path=Theme.File(true,"Отчёт JSON|*.json");if(path==null)return;Run("Сохраняю проверенный отчёт…",()=>Backend.Call("stand_report_save",J.O("id",J.S(preview,"id"),"path",path,"sha256",J.S(preview,"sha256"))),result=>form.Close());},true));buttons.Controls.Add(Theme.Button("Отмена",form.Close));Theme.Dialog(form,text,buttons,"Проверь отчёт перед передачей","Без дисков и RAM. Имена заменены; автоматической отправки нет.");form.ShowDialog(this);
            }
        });
    }
}

class StandTreeButton : PolarButton {
    string measuredText;Font measuredFont;int measuredWidth;Padding measuredPadding;Size measuredMinimum,measuredSize;
    public override Size GetPreferredSize(Size proposedSize){
        int width=MaximumSize.Width>0?MaximumSize.Width:proposedSize.Width>0?proposedSize.Width:Width;
        width=Math.Max(MinimumSize.Width,width);
        if(measuredText==Text&&Object.ReferenceEquals(measuredFont,Font)&&measuredWidth==width&&measuredPadding==Padding&&measuredMinimum==MinimumSize)return measuredSize;
        int height=TextRenderer.MeasureText(Text,Font,new Size(Math.Max(1,width-Padding.Horizontal-1),Int32.MaxValue),TextFormatFlags.WordBreak).Height+Padding.Vertical+2;
        measuredText=Text;measuredFont=Font;measuredWidth=width;measuredPadding=Padding;measuredMinimum=MinimumSize;measuredSize=new Size(width,Math.Max(MinimumSize.Height,height));return measuredSize;
    }
}

sealed class StandFolderButton : StandTreeButton {
    public StandFolderButton(string text,Action action){
        Text=text;AutoSize=true;MinimumSize=new Size(118,40);Padding=new Padding(15,8,15,8);Margin=new Padding(0,0,10,10);
        BackColor=Theme.Card;ForeColor=Theme.Text;TextAlign=ContentAlignment.MiddleLeft;Click+=(s,e)=>action();
    }
}

sealed class StandMemberButton : StandTreeButton {
    public readonly string StandId,VmName;readonly bool last;bool selected;
    public bool Selected {get{return selected;}set{if(selected==value)return;selected=value;BackColor=value?Theme.Card:Theme.Panel;ForeColor=value?Theme.Accent:Theme.Text;Invalidate();}}
    public StandMemberButton(string stand,string vm,bool last){
        StandId=stand;VmName=vm;this.last=last;AutoSize=true;MinimumSize=new Size(112,40);Padding=new Padding(36,8,10,8);
        Margin=new Padding(24,0,10,6);TextAlign=ContentAlignment.MiddleLeft;BackColor=Theme.Panel;ForeColor=Theme.Text;AccessibleDescription="stand-member";
    }
    protected override void OnPaint(PaintEventArgs e){
        base.OnPaint(e);int middle=Height/2;
        using(var pen=new Pen(selected?Theme.Accent:Theme.Line,2)){
            e.Graphics.DrawLine(pen,11,0,11,last?middle:Height);e.Graphics.DrawLine(pen,11,middle,24,middle);
        }
        using(var brush=new SolidBrush(selected?Theme.Accent:Theme.Muted))e.Graphics.FillRectangle(brush,24,middle-3,6,6);
    }
}

sealed class StandOperationWindow : Form {
    internal Func<bool,Task<object>> StatusProvider {get;set;}
    readonly Backend backend;readonly string operation;readonly ThemedEditor log=Theme.Editor();readonly Label status=Theme.Label("Читаю состояние…",12);
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=1000};bool pending;
    readonly Button cancel;
    public static string StateName(string value){switch(value){case "queued":return "В очереди";case "running":return "Работает";case "partial":return "Работает частично";case "starting":return "Запускается";case "stopping":return "Выключается";case "stopped":return "Выключен";case "completed":return "Операция завершена";case "cancelled":return "Отменено";case "interrupted":return "Прервано; нужна сверка";case "error":case "failed":return "Ошибка";default:return "Состояние неизвестно";}}
    public StandOperationWindow(Backend backend,string operation){
        this.backend=backend;this.operation=operation;Theme.Form(this,850,700);Text="Операция стенда";log.ReadOnly=true;
        var body=new Panel{Dock=DockStyle.Fill,Padding=new Padding(24)};status.Dock=DockStyle.Top;log.Dock=DockStyle.Fill;body.Controls.Add(log);body.Controls.Add(status);
        cancel=Theme.Button("Отменить будущие этапы",()=>{if(MessageBox.Show(this,"Остановить будущие этапы? Запущенные VM останутся работать.","Отмена",MessageBoxButtons.YesNo)==DialogResult.Yes)Request(true);});
        var buttons=Theme.Flow();buttons.Controls.Add(cancel);buttons.Controls.Add(Theme.Button("Закрыть",Close));Theme.Dialog(this,body,buttons,"Ход операции","Этапы QEMU; процент загрузки гостевой ОС не измеряется.");
        timer.Tick+=(s,e)=>Request(false);Shown+=(s,e)=>{timer.Start();Request(false);};FormClosed+=(s,e)=>{timer.Stop();timer.Dispose();};
    }
    async void Request(bool stop){
        if(pending||IsDisposed)return;pending=true;
        try{
            object result=await (StatusProvider!=null?StatusProvider(stop):Task.Run(()=>backend.Call(stop?"stand_cancel":"stand_status",J.O("operation_id",operation))));if(IsDisposed)return;
            string state=J.S(result,"state");status.Text=StateName(state)+" · "+J.S(result,"phase")+" · "+J.S(result,"current");
            log.Text=String.Join("\r\n",J.A(J.V(result,"events")).Select(item=>J.S(item,"vm")+" · "+J.S(item,"phase")))+"\r\n\r\n"+J.S(result,"error");cancel.Enabled=state=="running"||state=="queued";
            if(!cancel.Enabled)timer.Stop();
        }catch(Exception error){if(!IsDisposed)status.Text="Не удалось прочитать состояние: "+error.Message;}finally{pending=false;}
    }
}

sealed class LibraryModeSwitch : CheckBox {
    public LibraryModeSwitch(){AutoSize=false;Size=new Size(208,30);BackColor=Theme.Panel;ForeColor=Theme.Text;SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
    protected override void OnCheckedChanged(EventArgs e){base.OnCheckedChanged(e);Invalidate();}
    protected override void OnPaint(PaintEventArgs e){
        e.Graphics.Clear(BackColor);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var track=new Rectangle(79,5,43,21);using(var path=Drawing.Rounded(track,10))using(var brush=new SolidBrush(Checked?Theme.Accent:Theme.Card))e.Graphics.FillPath(brush,path);
        using(var brush=new SolidBrush(Checked?Theme.Bg:Theme.Text))e.Graphics.FillEllipse(brush,Checked?102:82,8,15,15);
        TextRenderer.DrawText(e.Graphics,"Машины",Font,new Rectangle(0,4,76,24),Checked?Theme.Muted:Theme.Accent,TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics,"Стенды",Font,new Rectangle(132,4,76,24),Checked?Theme.Accent:Theme.Muted,TextFormatFlags.VerticalCenter);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,ClientRectangle);
    }
}
