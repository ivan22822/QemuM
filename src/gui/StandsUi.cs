using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Windows.Forms;
using System.Threading.Tasks;

sealed partial class MainWindow {
    static string MulticastText(object group){return "MCAST · "+J.S(group,"address")+":"+J.N(group,"port")+" · "+String.Join(", ",J.A(J.V(group,"members")).Select(end=>J.S(end,"vm")+" / "+J.S(end,"nic")));}
    static void EditMulticast(Form owner,List<object> groups,List<object> endpoints,Action refresh,int index,bool switched=false){
        object existing=index<0?null:groups[index];
        using(var form=new Form()){
            Theme.Form(form,760,720);form.Text=switched?"Общая сеть · локальный коммутатор":"Общая multicast-сеть";
            var fields=Theme.Fields();fields.AutoSize=true;
            var bytes=Guid.NewGuid().ToByteArray();
            var address=Theme.Input(J.S(existing,"address","239.192."+bytes[0]+"."+bytes[1]));var port=Theme.Number(J.N(existing,"port",20000+bytes[2]*100+bytes[3]%100),1024,65535);var local=Theme.Input(J.S(existing,"localaddr"));
            var title=Theme.Input(J.S(existing,"name","Общая сеть"));if(switched)Theme.Row(fields,"Название сети",title,true);else{Theme.Row(fields,"Multicast-группа (239.x.x.x)",address);Theme.Row(fields,"Порт UDP",port);Theme.Row(fields,"IPv4 интерфейса хоста (пусто — выбор QEMU)",local,true);}
            var selected=new List<Tuple<object,CheckBox>>();
            var choices=endpoints.Concat(J.A(J.V(existing,"members")).Where(e=>!endpoints.Any(a=>J.S(a,"vm")==J.S(e,"vm")&&J.S(a,"nic")==J.S(e,"nic")))).ToArray();
            foreach(var end in choices){bool available=endpoints.Any(e=>J.S(e,"vm")==J.S(end,"vm")&&J.S(e,"nic")==J.S(end,"nic"));var check=new CheckBox{Text=J.S(end,"vm")+" / "+J.S(end,"nic")+(available?"":" · адаптер или VM отсутствует"),AutoSize=true,Enabled=available,ForeColor=Theme.Text,Checked=J.A(J.V(existing,"members")).Any(e=>J.S(e,"vm")==J.S(end,"vm")&&J.S(e,"nic")==J.S(end,"nic"))};selected.Add(Tuple.Create(end,check));Theme.Row(fields,"Участник",check,true);
                var main=owner as MainWindow;if(main!=null&&available){var adapters=J.A(J.V(J.V(main.Row(J.S(end,"vm")),"data"),"network_adapters"));var nic=adapters.FirstOrDefault(n=>J.S(n,"id")==J.S(end,"nic"));string nat=String.Join(", ",adapters.Where(n=>J.S(n,"type")=="user"&&J.S(n,"id")!=J.S(end,"nic")).Select(n=>J.S(n,"id")));Theme.Row(fields,"",Theme.Label("MAC: "+J.S(nic,"mac","не задан")+" · "+J.S(nic,"model")+" · "+J.S(nic,"type")+"\nДругие NAT-адаптеры: "+(nat.Length>0?nat:"нет"),10,Theme.Muted),true);}
            }
            Theme.Row(fields,"",Theme.Label(switched?"Выбери один адаптер каждой VM (минимум две машины). Остальные NAT-адаптеры сохранятся. IP/DHCP настраиваются в гостях. Коммутатор работает на loopback, отдельные сети разделены. Подключение QEMU не подтверждает связь внутри ОС.":"Выбери один адаптер каждой VM (минимум две машины). Для Интернета оставь отдельный NAT-адаптер. IP и DHCP настраиваются внутри гостей.\nГруппа и порт должны отличаться у независимых сетей. Multicast не шифруется; интерфейс хоста и firewall влияют на доступность. Проверка QMP не проверяет передачу пакетов.",10,Theme.Muted),true);
            var validation=Theme.Label("",10,Theme.Muted);Theme.Row(fields,"",validation,true);
            var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Применить",()=>{var members=selected.Where(p=>p.Item2.Checked).Select(p=>p.Item1).ToArray();if(selected.Any(p=>p.Item2.Checked&&!p.Item2.Enabled)){validation.Text="Есть отсутствующий адаптер. Исправь состав стенда перед сохранением.";return;}if(members.Length<2||members.Select(e=>J.S(e,"vm")).Distinct().Count()!=members.Length){validation.Text="Выбери минимум две VM, по одному адаптеру каждой.";return;}if(switched&&(title.Text.Trim().Length<1||title.Text.Trim().Length>120||title.Text.Any(Char.IsControl))){validation.Text="Название: от 1 до 120 символов без управляющих знаков.";return;}var value=J.O("id",existing==null?Guid.NewGuid().ToString("N"):J.S(existing,"id"),"address",address.Text.Trim(),"port",(int)port.Value,"localaddr",local.Text.Trim(),"members",members);if(switched)value=J.O("id",J.S(value,"id"),"name",title.Text.Trim(),"members",members);if(index<0)groups.Add(value);else groups[index]=value;refresh();form.Close();},true));buttons.Controls.Add(Theme.Button("Отмена",form.Close));Theme.Dialog(form,new ScrollSurface(fields),buttons,switched?"Общая сеть · локальный коммутатор":"Общая сеть MCAST","До сохранения стенда параметры VM не меняются.");form.ShowDialog(owner);
        }
    }
    bool standMode;object selectedStand;List<object> stands=new List<object>();
    readonly HashSet<string> expandedStands=new HashSet<string>();
    readonly Panel standDetail=new Panel{Dock=DockStyle.Fill,BackColor=Theme.Bg};
    Label standLibraryTitle;Button standAdd;bool standLoading;string standError="";
    string normalSelectedVm,standSelectedVm,standSelectedParent;int machineScroll,standScroll;string restoreStand;object libraryPrefs;
    object standActivity;bool standActivityPending;
    readonly Dictionary<string,Button> standMemberButtons=new Dictionary<string,Button>();Label standPhase,standNetworkStatus,standPlanNotice;StandTopologyCanvas standGraph;
    WorkspaceTabs standTabs;ThemedEditor standPlanText;CheckBox standIncludeExisting;Button standConfirmPlan,standDiscardPlan;StandOperationPanel standOperationPanel;
    object standPendingPlan;string standPendingId,standPendingAction,standActiveOperationId,standActiveOperationStandId;bool standPendingForce;
    int standPlanTabIndex,standJournalTabIndex;
    readonly HashSet<string> standOverviewPending=new HashSet<string>(StringComparer.Ordinal);
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
        if(cards.Controls.Count==0)cards.Controls.Add(Theme.Label(stands.Count==0?"Стендов пока нет.\nНажми «Создать стенд».":"По запросу ничего не найдено. Измени поиск или очисти поле.",10,Theme.Muted));
        if(!String.IsNullOrEmpty(standError))cards.Controls.Add(Theme.Label("Не удалось полностью прочитать стенды. "+(stands.Count>0?"Показаны доступные записи. ":"")+"Нажми «Обновить стенды».\n"+standError,9,Theme.Red));
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
        if(standPhase!=null&&!standPhase.IsDisposed&&selectedStand!=null){var activity=J.V(standActivity,J.S(selectedStand,"id"));standPhase.Text=String.IsNullOrEmpty(J.S(activity,"phase"))?"Состояния участников обновляются автоматически.":J.S(activity,"phase")+" · "+J.S(activity,"current");}
    }
    void ShowStand(){
        standMemberButtons.Clear();
        foreach(Control child in standDetail.Controls.Cast<Control>().ToArray())child.Dispose();standDetail.Controls.Clear();
        var stack=Theme.Flow(true);stack.Dock=DockStyle.Top;
        stack.Controls.Add(Theme.Label("СТЕНД / "+J.S(selectedStand,"name"),10,Theme.Muted));
        var title=Theme.Label(J.S(selectedStand,"name"),24,null,true);title.MaximumSize=new Size(Math.Max(260,detail.Width-80),0);stack.Controls.Add(title);
        stack.Controls.Add(Theme.Label(J.S(selectedStand,"description"),10,Theme.Muted));
        var configure=Theme.Flow();configure.MaximumSize=new Size(Math.Max(260,detail.ClientSize.Width-80),0);
        configure.Controls.Add(Theme.Button("Изменить состав и название",()=>EditStand(selectedStand)));
        configure.Controls.Add(Theme.Button("Сеть и соединения",()=>OpenNetworkWorkspace(selectedStand)));
        stack.Controls.Add(configure);
        string standId=J.S(selectedStand,"id");
        var operations=Theme.Flow();operations.MaximumSize=new Size(Math.Max(260,detail.ClientSize.Width-80),0);operations.Controls.Add(Theme.Button("Запустить / продолжить",()=>PlanStand(standId,"start"),true));
        operations.Controls.Add(Theme.Button("Выключить стенд",()=>PlanStand(standId,"stop")));
        operations.Controls.Add(Theme.Button("Остановить QEMU стенда",()=>PlanStand(standId,"stop",true),false,true));
        var operationStatusButton=Theme.Button("Состояние операции",()=>ReadStandOperation(standId));
        stack.Controls.Add(operations);
        var maintenance=Theme.Flow();maintenance.MaximumSize=operations.MaximumSize;
        maintenance.Controls.Add(operationStatusButton);
        maintenance.Controls.Add(Theme.Button("Проверить после сбоя",()=>{
            if(MessageBox.Show(this,"Сверить QMP, процессы и журнал? Автоматического запуска и выключения не будет.","Сверка стенда",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
            Run("Сверяю стенд…",()=>Backend.Call("stand_reconcile",J.O("id",standId,"revision",J.N(selectedStand,"revision"),"confirm",standId)),result=>{
                string issues=String.Join("\n",J.A(J.V(result,"issues")).Select(Convert.ToString));MessageBox.Show(this,String.IsNullOrEmpty(issues)?"Сверка выполнена. Можно сформировать новый план запуска/продолжения.":issues,"Сверка стенда");
            });
        }));
        maintenance.Controls.Add(Theme.Button("Дублировать стенд",()=>DuplicateStand(selectedStand)));
        maintenance.Controls.Add(Theme.Button("Удалить папку стенда",()=>RemoveStand(selectedStand),false,true));
        maintenance.Controls.Add(Theme.Button("Экспорт отчёта",()=>ExportStand(standId)));
        if(J.A(J.V(selectedStand,"switches")).Length>0)maintenance.Controls.Add(Theme.Button("Диагностика общих сетей",()=>OpenNetworkWorkspace(selectedStand,true)));
        maintenance.Controls.Add(Theme.Button("Справка о стендах",()=>{using(var faq=new FaqWindow())faq.ShowDialog(this);}));stack.Controls.Add(maintenance);
        standPhase=Theme.Label("Состояния участников обновляются автоматически.",10,Theme.Muted);standPhase.MaximumSize=new Size(Math.Max(260,detail.ClientSize.Width-80),0);stack.Controls.Add(standPhase);
        var tabs=new WorkspaceTabs{Width=Math.Max(260,detail.ClientSize.Width-80),Height=365};standTabs=tabs;
        var layout=new Dictionary<string,Point>();string layoutError="";Guid parsed;
        if(Guid.TryParseExact(standId,"N",out parsed))try{layout=StandDiagramLayout.Read(RootPath,standId);}catch(Exception error){layoutError="Расположение не загружено: "+error.Message;}
        var graph=new StandTopologyCanvas(selectedStand,Row,layout);standGraph=graph;
        var graphPanel=new Panel{Dock=DockStyle.Fill,BackColor=Theme.Bg};var statusBar=new Panel{Dock=DockStyle.Top,Height=42,BackColor=Theme.Bg};standNetworkStatus=Theme.Label(layoutError.Length>0?layoutError:"Нажми «Обновить» для состояния сети. Связь гостевых ОС не проверяется.",9,layoutError.Length>0?Theme.Red:Theme.Muted);standNetworkStatus.Dock=DockStyle.Fill;standNetworkStatus.AutoSize=false;standNetworkStatus.TextAlign=ContentAlignment.MiddleLeft;
        var refreshGraph=Theme.Button("Обновить",()=>RefreshStandOverview(graph,standId));refreshGraph.AutoSize=false;refreshGraph.MinimumSize=Size.Empty;refreshGraph.Width=112;refreshGraph.Dock=DockStyle.Right;statusBar.Controls.Add(standNetworkStatus);statusBar.Controls.Add(refreshGraph);
        graphPanel.Controls.Add(new ScrollSurface(graph){BackColor=Theme.Bg});graphPanel.Controls.Add(statusBar);
        tabs.Add("Схема сети",graphPanel);
        tabs.Add("Соединения",Theme.Label(String.Join("\n",J.A(J.V(selectedStand,"links")).Select(link=>J.S(J.V(link,"listen"),"vm")+" / "+J.S(J.V(link,"listen"),"nic")+" → "+J.S(J.V(link,"connect"),"vm")+" / "+J.S(J.V(link,"connect"),"nic"))),10,Theme.Muted));
        if(J.A(J.V(selectedStand,"switches")).Length>0)tabs.Add("Общие сети",Theme.Label(String.Join("\n",J.A(J.V(selectedStand,"switches")).Select(g=>J.S(g,"name")+" · "+String.Join(", ",J.A(J.V(g,"members")).Select(end=>J.S(end,"vm")+" / "+J.S(end,"nic"))))),10,Theme.Muted));
        if(J.A(J.V(selectedStand,"multicast")).Length>0)tabs.Add("MCAST",Theme.Label(String.Join("\n",J.A(J.V(selectedStand,"multicast")).Select(MulticastText)),10,Theme.Muted));
        standPlanTabIndex=2+(J.A(J.V(selectedStand,"switches")).Length>0?1:0)+(J.A(J.V(selectedStand,"multicast")).Length>0?1:0);
        tabs.Add("Запуск",BuildStandPlanPage(standId));
        standJournalTabIndex=standPlanTabIndex+1;
        standOperationPanel=new StandOperationPanel(Backend,()=>ReadStandOperation(standId)){Dock=DockStyle.Fill};tabs.Add("Журнал",standOperationPanel);
        if(standActiveOperationStandId==standId&&!String.IsNullOrEmpty(standActiveOperationId))standOperationPanel.SetOperation(standActiveOperationId);
        if(standPendingId==standId&&standPendingPlan!=null)RenderStandPlan();
        stack.Controls.Add(tabs);
        stack.Controls.SetChildIndex(standPhase,5);
        stack.Controls.SetChildIndex(tabs,6);
        stack.Controls.Add(Theme.Label("Состав стенда",14,null,true));
        foreach(string name in J.A(J.V(selectedStand,"members")).Select(Convert.ToString)){string n=name;var row=Row(n);var member=Theme.Button(J.S(J.V(row,"data"),"label",n)+(row==null?" · VM отсутствует":" · "+Theme.State(State(n))),()=>{standSelectedVm=n;standSelectedParent=restoreStand=standId;selectedStand=null;standDetail.Visible=false;SelectVm(n);PaintStandStatus();});stack.Controls.Add(member);standMemberButtons[n]=member;}
        stack.Controls.Add(Theme.Label("Стенд хранит ссылки на VM; образы не перемещаются.\nНастройка связей не назначает IP и DHCP внутри гостевых ОС.",10,Theme.Muted));
        standDetail.Controls.Add(new ScrollSurface(stack){Dock=DockStyle.Fill,BackColor=Theme.Bg});foreach(Control sibling in detail.Controls)sibling.Visible=Object.ReferenceEquals(sibling,standDetail);standDetail.BringToFront();
    }
    void RefreshStandOverview(StandTopologyCanvas graph,string id){
        if(Backend==null||graph==null||standOverviewPending.Contains(id))return;standOverviewPending.Add(id);
        Task.Run(()=>{try{object value=Backend.Call("stand_overview",J.O("id",id));Post(()=>{if(!graph.IsDisposed&&J.S(selectedStand,"id")==id){graph.ApplyStatus(value);if(standNetworkStatus!=null&&!standNetworkStatus.IsDisposed){var networks=J.A(J.V(value,"network_diagnostics"));standNetworkStatus.Text="Срез "+DateTime.Now.ToString("HH:mm:ss")+" · "+networks.Length+" общих сетей · связь внутри ОС не проверена";standNetworkStatus.ForeColor=Theme.Muted;}}});}
            catch(Exception error){Post(()=>{if(!graph.IsDisposed&&standNetworkStatus!=null&&!standNetworkStatus.IsDisposed){standNetworkStatus.Text="Состояние сети недоступно: "+error.Message;standNetworkStatus.ForeColor=Theme.Red;}});}
            finally{Post(()=>standOverviewPending.Remove(id));}});
    }
    void OpenNetworkWorkspace(object existing,bool status=false){
        if(existing==null||Backend==null)return;
        string id=J.S(existing,"id");var layout=new Dictionary<string,Point>();Guid parsed;
        if(Guid.TryParseExact(id,"N",out parsed))try{layout=StandDiagramLayout.Read(RootPath,id);}catch(Exception error){MessageBox.Show(this,"Расположение схемы не загружено: "+error.Message,"Сеть стенда",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        using(var editor=new StandNetworkEditorWindow(existing,Row,(window,draft)=>{
            Run("Проверяю и сохраняю сеть стенда…",()=>Backend.Call("stand_save",J.O("stand",draft,"revision",J.N(existing,"revision"))),result=>{
                window.SaveSucceeded();selectedStand=result;ShowStand();LoadStands();
            },error=>window.SaveFailed(error),false);
        },layout,value=>{StandDiagramLayout.Save(RootPath,id,value);if(standGraph!=null&&!standGraph.IsDisposed&&J.S(selectedStand,"id")==id)standGraph.SetPositions(value);},()=>Backend.Call("stand_overview",J.O("id",id)))){if(status)editor.OpenStatus();editor.ShowDialog(this);}
    }
    void EditStandTopology(object existing){
        using(var form=new Form()){
            Theme.Form(form,850,800);form.Text="Соединения и запуск стенда";
            Dictionary<string,object> draft;
            // Use an independent JSON clone: cancellation never changes the selected stand.
            draft=J.Json().DeserializeObject(J.Json().Serialize(existing)) as Dictionary<string,object>;
            var order=J.A(J.V(existing,"order")).Select(Convert.ToString).ToList();
            var links=J.A(J.V(existing,"links")).ToList();
            var multicast=J.A(J.V(draft,"multicast")).ToList();var switches=J.A(J.V(draft,"switches")).ToList();
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
            var switchList=new ThemedList{Height=130};Action refreshSwitches=()=>{switchList.Items.Clear();foreach(var group in switches)switchList.Items.Add(J.S(group,"name")+" · "+String.Join(", ",J.A(J.V(group,"members")).Select(end=>J.S(end,"vm")+" / "+J.S(end,"nic"))));};refreshSwitches();Theme.Row(fields,"Общие сети · локальный коммутатор",switchList,true);
            var switchButtons=Theme.Flow();switchButtons.Controls.Add(Theme.Button("Добавить общую сеть",()=>EditMulticast(form,switches,endpoints,refreshSwitches,-1,true)));switchButtons.Controls.Add(Theme.Button("Изменить общую сеть",()=>{if(switchList.SelectedIndex>=0)EditMulticast(form,switches,endpoints,refreshSwitches,switchList.SelectedIndex,true);}));switchButtons.Controls.Add(Theme.Button("Удалить общую сеть",()=>{if(switchList.SelectedIndex>=0){switches.RemoveAt(switchList.SelectedIndex);refreshSwitches();}}));Theme.Row(fields,"",switchButtons,true);
            var groupList=new ThemedList{Height=130};Action refreshGroups=()=>{groupList.Items.Clear();foreach(var group in multicast)groupList.Items.Add(MulticastText(group));};refreshGroups();Theme.Row(fields,"Общие сети MCAST (без слушателя)",groupList,true);
            var groupButtons=Theme.Flow();groupButtons.Controls.Add(Theme.Button("Добавить MCAST",()=>EditMulticast(form,multicast,endpoints,refreshGroups,-1)));groupButtons.Controls.Add(Theme.Button("Изменить MCAST",()=>{if(groupList.SelectedIndex>=0)EditMulticast(form,multicast,endpoints,refreshGroups,groupList.SelectedIndex);}));groupButtons.Controls.Add(Theme.Button("Удалить MCAST",()=>{if(groupList.SelectedIndex>=0){multicast.RemoveAt(groupList.SelectedIndex);refreshGroups();}}));Theme.Row(fields,"",groupButtons,true);
            var listen=Theme.Combo();var connect=Theme.Combo();foreach(var end in endpoints){string text=J.S(end,"vm")+" / "+J.S(end,"nic");listen.Items.Add(text);connect.Items.Add(text);}if(endpoints.Count>0)listen.SelectedIndex=connect.SelectedIndex=0;
            Theme.Row(fields,"Слушатель (запускается первым)",listen);Theme.Row(fields,"Подключаемый участник",connect);
            var linkList=new ThemedList{Height=120};Action refresh=()=>{linkList.Items.Clear();foreach(var link in links)linkList.Items.Add(J.S(J.V(link,"listen"),"vm")+" / "+J.S(J.V(link,"listen"),"nic")+" → "+J.S(J.V(link,"connect"),"vm")+" / "+J.S(J.V(link,"connect"),"nic"));};refresh();Theme.Row(fields,"Внутренние соединения",linkList,true);
            linkList.SelectedIndexChanged+=(s,e)=>{int i=linkList.SelectedIndex;if(i<0)return;var a=J.V(links[i],"listen");var b=J.V(links[i],"connect");listen.SelectedIndex=endpoints.FindIndex(end=>J.S(end,"vm")==J.S(a,"vm")&&J.S(end,"nic")==J.S(a,"nic"));connect.SelectedIndex=endpoints.FindIndex(end=>J.S(end,"vm")==J.S(b,"vm")&&J.S(end,"nic")==J.S(b,"nic"));};
            var linkButtons=Theme.Flow();linkButtons.Controls.Add(Theme.Button("Добавить связь",()=>{if(listen.SelectedIndex<0||connect.SelectedIndex<0)return;links.Add(J.O("id",Guid.NewGuid().ToString("N"),"listen",endpoints[listen.SelectedIndex],"connect",endpoints[connect.SelectedIndex]));refresh();}));linkButtons.Controls.Add(Theme.Button("Удалить связь",()=>{int i=linkList.SelectedIndex;if(i>=0&&MessageBox.Show(form,"Удалить выбранную связь? Изменение вступит в силу после сохранения.","Соединение",MessageBoxButtons.YesNo)==DialogResult.Yes){links.RemoveAt(i);refresh();}}));Theme.Row(fields,"",linkButtons,true);
            linkButtons.Controls.Add(Theme.Button("Изменить связь",()=>{int i=linkList.SelectedIndex;if(i<0||listen.SelectedIndex<0||connect.SelectedIndex<0)return;links[i]=J.O("id",J.S(links[i],"id"),"listen",endpoints[listen.SelectedIndex],"connect",endpoints[connect.SelectedIndex]);refresh();}));
            Theme.Row(fields,"",Theme.Label("Выбранный адаптер на время запуска переводится во внутреннюю связь вместо прежней сети.\nДля внешнего NAT и проброса портов оставь отдельный адаптер. JSON одиночной VM не меняется.\nIP/DHCP внутри гостей настраиваются вручную. QMP готов подтверждает QEMU, а не загрузку ОС.\nTCP проверяет 127.0.0.1: нужен доступный сервис или явный проброс порта. Guest Agent не нужен.\nПроверка не выполняет команды внутри гостя и не хранит пароли.",10,Theme.Muted),true);
            var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Сохранить",()=>{try{store();draft["schema_version"]=4;draft["multicast"]=multicast.ToArray();draft["switches"]=switches.ToArray();draft["order"]=order.ToArray();draft["links"]=links.ToArray();draft["startup"]=startup;if(MessageBox.Show(form,"Сохранить порядок и "+links.Count+" соединений? Настройки отдельных VM не меняются.","План изменений",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;Run("Сохраняю топологию…",()=>Backend.Call("stand_save",J.O("stand",draft,"revision",J.N(existing,"revision"))),result=>{form.Close();selectedStand=result;ShowStand();LoadStands();});}catch(Exception error){MessageBox.Show(form,error.Message);}},true));buttons.Controls.Add(Theme.Button("Отмена",form.Close));Theme.Dialog(form,new ScrollSurface(fields),buttons,"Соединения и порядок","Изменения применяются только после сохранения.");form.ShowDialog(this);
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
                var changedNetworks=J.A(J.V(existing,"multicast")).Concat(J.A(J.V(existing,"switches"))).Where(g=>J.A(J.V(g,"members")).Any(end=>!chosen.Contains(J.S(end,"vm")))).ToArray();int changedGroups=changedNetworks.Length;string removedEndpoints=String.Join("\n",changedNetworks.Select(g=>J.S(g,"name",J.S(g,"address","Сеть"))+": "+String.Join(", ",J.A(J.V(g,"members")).Where(end=>!chosen.Contains(J.S(end,"vm"))).Select(end=>J.S(end,"vm")+" / "+J.S(end,"nic")))));
                if(changedGroups>0&&MessageBox.Show(form,"Изменятся общие сети: "+changedGroups+".\n"+removedEndpoints+"\nСети с менее чем двумя участниками будут удалены. Продолжить?","Состав сетей",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
                if(removedLinks>0&&MessageBox.Show(form,"Исключение участников удалит связанных соединений: "+removedLinks+". Продолжить?","План изменения связей",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
                var removed=members.Where(n=>!chosen.Contains(n)).ToArray();
                if(removed.Length>0&&MessageBox.Show(form,"Убрать из стенда: "+String.Join(", ",removed)+"? VM и её файлы сохранятся.","Состав стенда",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
                var order=J.A(J.V(existing,"order")).Select(Convert.ToString).Where(chosen.Contains).Concat(chosen.Where(n=>!J.A(J.V(existing,"order")).Select(Convert.ToString).Contains(n))).ToArray();
                var draft=J.O("schema_version",1,"id",existing==null?Guid.NewGuid().ToString("N"):J.S(existing,"id"),"revision",J.N(existing,"revision"),"name",name.Text,"description",description.Text,"members",chosen,"order",order,"links",new object[0]);
                if(J.N(existing,"schema_version")>=2){draft["schema_version"]=2;draft["links"]=keptLinks;draft["startup"]=chosen.ToDictionary(n=>n,n=>J.V(J.V(existing,"startup"),n)??J.O("delay",0,"timeout",60,"ready","qmp"));}
                if(J.N(existing,"schema_version")>=3){draft["schema_version"]=3;draft["multicast"]=J.A(J.V(existing,"multicast")).Select(g=>{var copy=new Dictionary<string,object>(J.D(g));copy["members"]=J.A(J.V(g,"members")).Where(end=>chosen.Contains(J.S(end,"vm"))).ToArray();return (object)copy;}).Where(g=>J.A(J.V(g,"members")).Length>=2).ToArray();}
                if(J.N(existing,"schema_version")>=4){draft["schema_version"]=4;draft["switches"]=J.A(J.V(existing,"switches")).Select(g=>{var copy=new Dictionary<string,object>(J.D(g));copy["members"]=J.A(J.V(g,"members")).Where(end=>chosen.Contains(J.S(end,"vm"))).ToArray();return (object)copy;}).Where(g=>J.A(J.V(g,"members")).Length>=2).ToArray();}
                if(Busy)return;saving=true;save.Enabled=false;
                Run("Сохраняю стенд…",()=>Backend.Call("stand_save",J.O("stand",draft,"revision",J.N(existing,"revision"))),result=>{saving=false;form.Close();selectedStand=result;ShowStand();LoadStands();},error=>{saving=false;save.Enabled=true;});
            };
            form.Controls.Add(new ScrollSurface(stack){Dock=DockStyle.Fill});form.Controls.Add(buttons);form.ShowDialog(this);
        }
    }
    Control BuildStandPlanPage(string id){
        var page=new TableLayoutPanel{Dock=DockStyle.Fill,BackColor=Theme.Bg,ColumnCount=1,RowCount=5,Margin=Padding.Empty,Padding=Padding.Empty};
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute,54));page.RowStyles.Add(new RowStyle(SizeType.Absolute,36));page.RowStyles.Add(new RowStyle(SizeType.Absolute,54));page.RowStyles.Add(new RowStyle(SizeType.Percent,100));page.RowStyles.Add(new RowStyle(SizeType.Absolute,54));
        var heading=Theme.Label("Порядок: "+String.Join(" → ",J.A(J.V(selectedStand,"order")).Select(Convert.ToString))+"\nВыбери действие выше, затем проверь план до подтверждения.",10,Theme.Muted);
        heading.Dock=DockStyle.Fill;heading.AutoSize=false;page.Controls.Add(heading,0,0);
        standIncludeExisting=new CheckBox{Text="При выключении включить ранее работавшие VM",AutoSize=true,ForeColor=Theme.Text,Dock=DockStyle.Fill,AccessibleName="Включить ранее работавшие машины в план выключения"};page.Controls.Add(standIncludeExisting,0,1);
        standPlanNotice=Theme.Label("План действует две минуты. Настройки VM и диски не меняются при просмотре.",9,Theme.Muted);standPlanNotice.Dock=DockStyle.Fill;standPlanNotice.AutoSize=false;page.Controls.Add(standPlanNotice,0,2);
        standPlanText=Theme.Editor();standPlanText.ReadOnly=true;standPlanText.AccessibleName="Предпросмотр плана стенда";standPlanText.Dock=DockStyle.Fill;standPlanText.Text="Порядок запуска:\r\n"+String.Join(" → ",J.A(J.V(selectedStand,"order")).Select(Convert.ToString))+"\r\n\r\nОтмена операции прекращает будущие этапы; запущенные VM остаются работать.";page.Controls.Add(standPlanText,0,3);
        var buttons=Theme.Flow();buttons.Dock=DockStyle.Fill;buttons.AutoSize=false;
        standConfirmPlan=Theme.Button("Подтвердить план",()=>ConfirmStandPlan(id),true);standConfirmPlan.Enabled=false;
        standDiscardPlan=Theme.Button("Отменить план",DiscardStandPlan);standDiscardPlan.Enabled=false;
        buttons.Controls.Add(standConfirmPlan);buttons.Controls.Add(standDiscardPlan);page.Controls.Add(buttons,0,4);
        return page;
    }
    void RenderStandPlan(){
        if(standPlanText==null||standPlanText.IsDisposed||standPendingPlan==null||standPendingId!=J.S(selectedStand,"id"))return;
        object plan=standPendingPlan;var order=J.A(J.V(plan,"order")).Select(Convert.ToString).ToArray();var running=J.A(J.V(plan,"already_running")).Select(Convert.ToString).ToArray();
        standPlanText.Text=(standPendingAction=="start"?"Запуск / продолжение":standPendingForce?"Принудительная остановка QEMU":"Штатное выключение")+"\r\nПорядок: "+String.Join(" → ",order)+"\r\nУже работают: "+String.Join(", ",running)+"\r\nДополнительно запрашивается: "+J.N(plan,"requested_ram_mb")+" МБ RAM, "+J.N(plan,"requested_cores")+" vCPU.\r\nСейчас работают: "+J.N(J.V(plan,"resources"),"running_ram_mb")+" МБ RAM, "+J.N(J.V(plan,"resources"),"running_cores")+" vCPU.\r\n\r\n"+J.S(plan,"warning")+"\r\n\r\nЭффективная сеть участников:\r\n"+J.Json().Serialize(J.V(plan,"networks"));
        var shared=J.A(J.V(plan,"switches"));if(shared.Length>0)standPlanText.Text+="\r\n\r\nОбщие сети · локальный коммутатор:\r\n"+String.Join("\r\n",shared.Select(g=>J.S(g,"name")+": "+String.Join(", ",J.A(J.V(g,"members")).Select(end=>J.S(end,"vm")+" / "+J.S(end,"nic")))))+"\r\nIP/DHCP настраиваются внутри гостей.";
        standPlanNotice.Text=standPendingForce?"ВНИМАНИЕ: остановка QEMU может уничтожить несохранённые данные. Проверь план ниже.":"План действует две минуты. Изменение параметров потребует новой проверки.";
        standPlanNotice.ForeColor=standPendingForce?Theme.Red:Theme.Muted;standConfirmPlan.Text=standPendingForce?"Подтвердить остановку QEMU":"Подтвердить план";standConfirmPlan.Enabled=standDiscardPlan.Enabled=true;
        standIncludeExisting.Checked=J.V(plan,"include_existing") is bool && (bool)J.V(plan,"include_existing");standIncludeExisting.Enabled=false;
    }
    void PlanStand(string id,string action,bool force=false){
        if(Backend==null||Busy||selectedStand==null||J.S(selectedStand,"id")!=id)return;
        if(standPendingPlan!=null){
            if(standPendingId==id){standTabs.SelectedIndex=standPlanTabIndex;standPlanNotice.Text="Сначала подтверди или отмени текущий план. Затем можно выбрать другое действие.";return;}
            string old=J.S(standPendingPlan,"id");Run("Отменяю план предыдущего стенда…",()=>Backend.Call("stand_discard",J.O("plan_id",old)),value=>{standPendingPlan=null;standPendingId=null;PlanStand(id,action,force);});return;
        }
        bool include=action=="stop"&&standIncludeExisting!=null&&!standIncludeExisting.IsDisposed&&standIncludeExisting.Checked;
        standTabs.SelectedIndex=standPlanTabIndex;standPlanNotice.Text="Проверяю план…";standPlanNotice.ForeColor=Theme.Muted;
        Run("Проверяю план стенда…",()=>Backend.Call("stand_plan",J.O("id",id,"action",action,"force",force,"include_existing",include)),plan=>{
            if(J.S(selectedStand,"id")!=id){string stale=J.S(plan,"id");Task.Run(()=>{try{Backend.Call("stand_discard",J.O("plan_id",stale));}catch{}});return;}
            standPendingPlan=plan;standPendingId=id;standPendingAction=action;standPendingForce=force;RenderStandPlan();
        },error=>{if(standPlanNotice!=null&&!standPlanNotice.IsDisposed){standPlanNotice.Text="План не создан: "+error.Message;standPlanNotice.ForeColor=Theme.Red;}});
    }
    void DiscardStandPlan(){
        if(standPendingPlan==null||Busy)return;string token=J.S(standPendingPlan,"id");
        Run("Отменяю план…",()=>Backend.Call("stand_discard",J.O("plan_id",token)),value=>{
            if(J.S(standPendingPlan,"id")==token){standPendingPlan=null;standPendingId=null;standConfirmPlan.Enabled=standDiscardPlan.Enabled=false;standIncludeExisting.Enabled=true;standPlanNotice.Text="План отменён. VM и диски не изменены.";standPlanNotice.ForeColor=Theme.Muted;}
        });
    }
    void ConfirmStandPlan(string id){
        if(standPendingPlan==null||standPendingId!=id||J.S(selectedStand,"id")!=id||Busy)return;
        string token=J.S(standPendingPlan,"id");standConfirmPlan.Enabled=false;
        Run("Начинаю операцию стенда…",()=>Backend.Call("stand_execute",J.O("plan_id",token,"confirm",token)),result=>{
            standPendingPlan=null;standPendingId=null;StandJobs++;standActiveOperationId=J.S(result,"id");standActiveOperationStandId=id;
            standPlanNotice.Text="План выполнен; ход операции находится во вкладке «Журнал».";standPlanNotice.ForeColor=Theme.Muted;standDiscardPlan.Enabled=false;standIncludeExisting.Enabled=true;
            standOperationPanel.SetOperation(standActiveOperationId);standTabs.SelectedIndex=standJournalTabIndex;
        },error=>{standConfirmPlan.Enabled=false;standPlanNotice.Text="Исход запуска не подтверждён: "+error.Message+". Открой «Состояние операции» и проверь стенд перед новым планом.";standPlanNotice.ForeColor=Theme.Red;});
    }
    void ReadStandOperation(string id){
        if(Backend==null||Busy)return;standTabs.SelectedIndex=standJournalTabIndex;
        Run("Читаю состояние стенда…",()=>Backend.Call("stand_overview",J.O("id",id)),result=>{
            if(J.S(selectedStand,"id")!=id)return;string op=J.S(result,"operation_id");
            if(!String.IsNullOrEmpty(op)){standActiveOperationId=op;standActiveOperationStandId=id;standOperationPanel.SetOperation(op);}
            else {standActiveOperationId=null;standActiveOperationStandId=null;standOperationPanel.ShowEmpty("Состояние стенда: "+StandOperationWindow.StateName(J.S(result,"state"))+". Групповых операций в этом сеансе нет.");}
        });
    }
    void RemoveStand(object row){
        if(row==null||Busy)return;string id=J.S(row,"id");int revision=J.N(row,"revision");
        if(MessageBox.Show(this,"Удалить только папку «"+J.S(row,"name")+"» из библиотеки?\n\nVM, диски, снимки и работающие процессы останутся. Журнал будет сохранён для диагностики. Выполняющаяся операция блокирует удаление.","Удаление стенда",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        Run("Удаляю определение стенда…",()=>Backend.Call("stand_remove",J.O("id",id,"revision",revision,"confirm",id)),result=>{try{StandDiagramLayout.Delete(RootPath,id);}catch(Exception error){footer.Text="Стенд удалён, расположение схемы осталось: "+error.Message;}selectedStand=null;restoreStand=null;standDetail.Visible=false;SelectVm(selected);LoadStands();});
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

sealed class StandOperationPanel : Panel {
    internal Func<bool,Task<object>> StatusProvider {get;set;}
    readonly Backend backend;readonly Action loadLatest;readonly ThemedEditor log=Theme.Editor();readonly Label status=Theme.Label("Здесь появятся этапы и журнал операции стенда.",10,Theme.Muted);
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=1800};readonly Button cancel;string operation;bool pending;
    public StandOperationPanel(Backend backend,Action loadLatest=null){
        this.backend=backend;this.loadLatest=loadLatest;BackColor=Theme.Bg;Padding=new Padding(0,8,0,0);
        status.Dock=DockStyle.Top;status.AutoSize=false;status.Height=36;status.TextAlign=ContentAlignment.MiddleLeft;
        log.Dock=DockStyle.Fill;log.ReadOnly=true;log.AccessibleName="Журнал операции стенда";log.Text="Логи отдельных VM доступны в их карточках. Процент загрузки гостевой ОС не измеряется.";
        var buttons=Theme.Flow();buttons.Dock=DockStyle.Bottom;buttons.AutoSize=false;buttons.Height=52;
        cancel=Theme.Button("Отменить будущие этапы",()=>{if(MessageBox.Show(FindForm(),"Остановить будущие этапы? Запущенные VM останутся работать.","Отмена",MessageBoxButtons.YesNo)==DialogResult.Yes)Request(true);});cancel.Enabled=false;
        buttons.Controls.Add(cancel);buttons.Controls.Add(Theme.Button("Обновить журнал",()=>{if(String.IsNullOrEmpty(operation)){if(this.loadLatest!=null)this.loadLatest();}else Request(false);}));
        Controls.Add(log);Controls.Add(status);Controls.Add(buttons);
        timer.Tick+=(s,e)=>Request(false);VisibleChanged+=(s,e)=>{if(Visible&&!String.IsNullOrEmpty(operation)){timer.Start();Request(false);}else timer.Stop();};
    }
    public void SetOperation(string id){
        if(String.IsNullOrEmpty(id))return;if(operation!=id){operation=id;status.Text="Читаю состояние операции…";log.Text="";cancel.Enabled=false;}
        if(Visible)timer.Start();Request(false);
    }
    public void ShowEmpty(string message){operation=null;timer.Stop();status.Text=message;status.ForeColor=Theme.Muted;log.Text="Логи отдельных VM доступны в их карточках.";cancel.Enabled=false;}
    async void Request(bool stop){
        if(pending||IsDisposed||String.IsNullOrEmpty(operation))return;pending=true;string current=operation;
        try{
            object result=await (StatusProvider!=null?StatusProvider(stop):Task.Run(()=>backend.Call(stop?"stand_cancel":"stand_status",J.O("operation_id",current))));
            if(IsDisposed||operation!=current)return;string state=J.S(result,"state");
            status.Text=StandOperationWindow.StateName(state)+" · "+J.S(result,"phase")+" · "+J.S(result,"current");status.ForeColor=state=="failed"||state=="error"||state=="interrupted"?Theme.Red:state=="completed"?Theme.Green:Theme.Text;
            log.Text=String.Join("\r\n",J.A(J.V(result,"events")).Select(item=>J.S(item,"vm")+" · "+J.S(item,"phase")))+"\r\n\r\n"+J.S(result,"error");
            cancel.Enabled=state=="running"||state=="queued";if(!cancel.Enabled)timer.Stop();
        }catch(Exception error){if(!IsDisposed&&operation==current){status.Text="Не удалось прочитать состояние: "+error.Message;status.ForeColor=Theme.Red;}}
        finally{pending=false;}
    }
    protected override void Dispose(bool disposing){if(disposing)timer.Dispose();base.Dispose(disposing);}
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

sealed class StandTopologyCanvas : Control {
    readonly Func<string,object> vmRow;
    string[] machines;object[] switches,multicast,links;int diagramHeight,focusedNetwork=-1;
    readonly Dictionary<string,Point> positions=new Dictionary<string,Point>(StringComparer.Ordinal);
    object overview;int zoomPercent=100;string dragKey,focusedKey;Point dragStart,dragOriginal;bool moved,suppressClick;
    readonly ToolTip nodeTip=new ToolTip();
    public event Action<string,int> ConnectionSelected;
    public event Action<string> VmSelected;
    public event Action<Dictionary<string,Point>> PlacementChanged;
    public bool AllowMove {get;set;}
    public int ZoomPercent {get{return zoomPercent;}set{int next=Math.Max(75,Math.Min(150,value));if(next==zoomPercent)return;zoomPercent=next;Measure();Invalidate();}}
    HashSet<string> ActiveKeys(){var keys=new HashSet<string>(machines==null?new string[0]:machines.Select(name=>"vm:"+name),StringComparer.Ordinal);if(switches!=null)for(int i=0;i<switches.Length+multicast.Length+links.Length;i++)keys.Add(NetworkKey(i));return keys;}
    public Dictionary<string,Point> Positions(){var active=ActiveKeys();return positions.Where(item=>active.Contains(item.Key)).ToDictionary(item=>item.Key,item=>item.Value,StringComparer.Ordinal);}
    public void SetPositions(Dictionary<string,Point> values){positions.Clear();if(values!=null)foreach(var item in values)positions[item.Key]=item.Value;Measure();Invalidate();}
    public void ApplyStatus(object value){overview=value;var summaries=new List<string>{"Срез состояния QEMU и коммутаторов; связь внутри гостевых ОС не проверена."};
        foreach(string name in machines)if(vmRow!=null&&vmRow(name)==null)summaries.Add(name+": VM отсутствует.");
        foreach(var item in J.D(J.V(value,"members")))summaries.Add(item.Key+": "+Theme.State(Convert.ToString(item.Value)));
        foreach(var group in switches){object diag=SwitchDiagnostic(group);summaries.Add(J.S(group,"name")+": "+SwitchCaption(diag));}
        AccessibleDescription=String.Join(" ",summaries);Invalidate();}
    public StandTopologyCanvas(object stand,Func<string,object> vmRow=null,Dictionary<string,Point> initialPositions=null){
        this.vmRow=vmRow;
        BackColor=Theme.Bg;ForeColor=Theme.Text;AccessibleName="Схема сетей стенда";AccessibleDescription="Виртуальные машины слева, сети и соединения справа. Tab переводит фокус на схему; стрелки выбирают связь, Enter открывает её настройки.";TabStop=true;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        if(initialPositions!=null)foreach(var item in initialPositions)positions[item.Key]=item.Value;Load(stand);
    }
    public void Load(object stand){
        machines=J.A(J.V(stand,"members")).Select(Convert.ToString).ToArray();
        switches=J.A(J.V(stand,"switches"));multicast=J.A(J.V(stand,"multicast"));links=J.A(J.V(stand,"links"));
        Measure();
        focusedNetwork=Math.Min(focusedNetwork,switches.Length+multicast.Length+links.Length-1);
        Invalidate();
    }
    void Measure(){var active=ActiveKeys();int content=Math.Max(Math.Max(machines.Length,switches.Length+multicast.Length+links.Length)*90+120,positions.Where(item=>active.Contains(item.Key)).Select(item=>item.Value.Y+110).DefaultIfEmpty(0).Max());diagramHeight=Math.Max(330,(int)Math.Ceiling(content*zoomPercent/100F));Height=diagramHeight;}
    public override Size GetPreferredSize(Size proposedSize){return new Size(Math.Max(1,proposedSize.Width),diagramHeight);}
    int NodeWidth(){return Math.Max(90,Math.Min((int)(220F*zoomPercent/100),Math.Max(90,(ClientSize.Width-140)/2)));}
    int NodeHeight(){return (int)(66F*zoomPercent/100);}
    Rectangle Node(string key,int column,int index){int width=NodeWidth(),available=Math.Max(1,ClientSize.Width-32-width);Point value;bool custom=positions.TryGetValue(key,out value);if(!custom)value=new Point(column==0?0:1000,55+index*90);
        int x=16+(int)((long)available*value.X/1000),y=(int)(value.Y*zoomPercent/100F);return new Rectangle(x,y,width,NodeHeight());}
    string NetworkKey(int index){
        object row;string kind;if(index<switches.Length){row=switches[index];kind="switch";}
        else if(index<switches.Length+multicast.Length){row=multicast[index-switches.Length];kind="mcast";}
        else{row=links[index-switches.Length-multicast.Length];kind="tcp";}
        string id=J.S(row,"id");return kind+":"+(id.Length==32?id:index.ToString());
    }
    int NetworkAt(Point point){
        for(int i=switches.Length+multicast.Length+links.Length-1;i>=0;i--)if(Node(NetworkKey(i),1,i).Contains(point))return i;return -1;
    }
    string VmAt(Point point){
        for(int i=machines.Length-1;i>=0;i--)if(Node("vm:"+machines[i],0,i).Contains(point))return machines[i];return null;
    }
    string NetworkDetails(int index){
        if(index<0)return "";
        if(index<switches.Length)return "Коммутатор «"+J.S(switches[index],"name")+"»: "+String.Join(", ",J.A(J.V(switches[index],"members")).Select(end=>J.S(end,"vm")+" / "+J.S(end,"nic")));
        index-=switches.Length;
        if(index<multicast.Length)return "MCAST "+J.S(multicast[index],"address")+":"+J.N(multicast[index],"port")+": "+String.Join(", ",J.A(J.V(multicast[index],"members")).Select(end=>J.S(end,"vm")+" / "+J.S(end,"nic")));
        var link=links[index-multicast.Length];return "TCP: "+J.S(J.V(link,"listen"),"vm")+" / "+J.S(J.V(link,"listen"),"nic")+" → "+J.S(J.V(link,"connect"),"vm")+" / "+J.S(J.V(link,"connect"),"nic");
    }
    Point DefaultPosition(string key){if(key.StartsWith("vm:")){int i=Array.IndexOf(machines,key.Substring(3));return new Point(0,55+Math.Max(0,i)*90);}for(int i=0;i<switches.Length+multicast.Length+links.Length;i++)if(NetworkKey(i)==key)return new Point(1000,55+i*90);return new Point(0,55);}
    void Place(string key,Point value){positions[key]=new Point(Math.Max(0,Math.Min(1000,value.X)),Math.Max(55,Math.Min(8000,value.Y)));Measure();Invalidate();}
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(!AllowMove||e.Button!=MouseButtons.Left)return;int index=NetworkAt(e.Location);string vm=VmAt(e.Location);dragKey=vm!=null?"vm:"+vm:index>=0?NetworkKey(index):null;if(dragKey==null)return;
        focusedKey=dragKey;dragStart=e.Location;if(!positions.TryGetValue(dragKey,out dragOriginal))dragOriginal=DefaultPosition(dragKey);moved=false;Capture=true;Focus();}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragKey!=null&&AllowMove&&Capture){int dx=e.X-dragStart.X,dy=e.Y-dragStart.Y;if(Math.Abs(dx)+Math.Abs(dy)>3)moved=true;if(moved){int available=Math.Max(1,ClientSize.Width-32-NodeWidth());Place(dragKey,new Point(dragOriginal.X+(int)((long)dx*1000/available),dragOriginal.Y+(int)(dy*100F/zoomPercent)));}Cursor=Cursors.SizeAll;return;}
        int index=NetworkAt(e.Location);string vm=VmAt(e.Location);Cursor=(ConnectionSelected!=null&&index>=0)||(VmSelected!=null&&vm!=null)?Cursors.Hand:AllowMove&&(index>=0||vm!=null)?Cursors.SizeAll:Cursors.Default;
        string tip=index>=0?NetworkDetails(index):vm==null?"":vm+" · "+(vmRow!=null&&vmRow(vm)==null?"VM отсутствует":"открыть список адаптеров");if(nodeTip.GetToolTip(this)!=tip)nodeTip.SetToolTip(this,tip);}
    protected override void OnMouseUp(MouseEventArgs e){bool changed=dragKey!=null&&moved;dragKey=null;moved=false;Capture=false;if(changed){suppressClick=true;if(PlacementChanged!=null)PlacementChanged(Positions());}base.OnMouseUp(e);}
    void SelectNetwork(int index){if(index<0||ConnectionSelected==null)return;focusedNetwork=index;Invalidate();
        if(index<switches.Length)ConnectionSelected("switch",index);
        else if(index<switches.Length+multicast.Length)ConnectionSelected("mcast",index-switches.Length);
        else ConnectionSelected("tcp",index-switches.Length-multicast.Length);
    }
    protected override bool IsInputKey(Keys keyData){return keyData==Keys.Up||keyData==Keys.Down||base.IsInputKey(keyData);}
    protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);int count=switches.Length+multicast.Length+links.Length;
        if(AllowMove&&e.Control&&focusedKey!=null&&new[]{Keys.Left,Keys.Right,Keys.Up,Keys.Down}.Contains(e.KeyCode)){
            Point value;if(!positions.TryGetValue(focusedKey,out value))value=DefaultPosition(focusedKey);
            Place(focusedKey,new Point(value.X+(e.KeyCode==Keys.Left?-20:e.KeyCode==Keys.Right?20:0),value.Y+(e.KeyCode==Keys.Up?-16:e.KeyCode==Keys.Down?16:0)));
            if(PlacementChanged!=null)PlacementChanged(Positions());e.Handled=true;return;
        }
        if(count==0)return;
        if(e.KeyCode==Keys.Down||e.KeyCode==Keys.Right){focusedNetwork=Math.Min(count-1,focusedNetwork+1);e.Handled=true;Invalidate();}
        else if(e.KeyCode==Keys.Up||e.KeyCode==Keys.Left){focusedNetwork=Math.Max(0,focusedNetwork-1);e.Handled=true;Invalidate();}
        else if(e.KeyCode==Keys.Enter||e.KeyCode==Keys.Space){SelectNetwork(focusedNetwork<0?0:focusedNetwork);e.Handled=true;}
        if(Focused&&focusedNetwork>=0)AccessibleDescription=NetworkDetails(focusedNetwork)+". Enter открывает настройки.";
    }
    protected override void OnMouseClick(MouseEventArgs e){
        if(suppressClick){suppressClick=false;return;}
        base.OnMouseClick(e);Focus();string vm=VmAt(e.Location);if(vm!=null&&VmSelected!=null)VmSelected(vm);else SelectNetwork(NetworkAt(e.Location));
    }
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(dragKey!=null&&moved&&PlacementChanged!=null){suppressClick=true;PlacementChanged(Positions());}dragKey=null;moved=false;}
    protected override void Dispose(bool disposing){if(disposing)nodeTip.Dispose();base.Dispose(disposing);}
    static void Box(Graphics g,Rectangle r,string title,string subtitle,Color accent,Font font,float scale){
        using(var path=Drawing.Rounded(r,9))using(var fill=new SolidBrush(Theme.Panel))using(var border=new Pen(accent,2)){g.FillPath(fill,path);g.DrawPath(border,path);}
        int inset=(int)(12*scale),titleTop=(int)(7*scale),titleHeight=(int)(33*scale),subtitleHeight=(int)(19*scale);
        TextRenderer.DrawText(g,title,font,new Rectangle(r.X+inset,r.Y+titleTop,r.Width-2*inset,titleHeight),Theme.Text,TextFormatFlags.WordBreak|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g,subtitle,font,new Rectangle(r.X+inset,r.Bottom-subtitleHeight-(int)(4*scale),r.Width-2*inset,subtitleHeight),accent,TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
    }
    object SwitchDiagnostic(object group){string id=J.S(group,"id");return J.A(J.V(overview,"network_diagnostics")).FirstOrDefault(item=>J.S(item,"id")==id);}
    static string SwitchCaption(object diag){if(diag==null)return "КОММУТАТОР · не проверен";string state=J.S(diag,"state");int online=J.A(J.V(diag,"members")).Count(item=>J.B(item,"connected")),total=J.A(J.V(diag,"members")).Length;
        return state=="connected"?"QEMU ПОДКЛЮЧЁН · "+online+"/"+total:state=="helper-ready"?"КОММУТАТОР ГОТОВ · "+online+"/"+total:state=="stopped"?"КОММУТАТОР ВЫКЛЮЧЕН":"СОСТОЯНИЕ НЕИЗВЕСТНО";}
    protected override void OnPaint(PaintEventArgs e){
        base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;g.Clear(BackColor);
        float scale=zoomPercent/100F;int left=16,right=Math.Max(16,ClientSize.Width-16-NodeWidth());
        var nodes=new Dictionary<string,Rectangle>();
        for(int i=0;i<machines.Length;i++)nodes[machines[i]]=Node("vm:"+machines[i],0,i);
        var groups=new List<Tuple<string,string,Color,object[]>>();
        foreach(var group in switches)groups.Add(Tuple.Create(J.S(group,"name"),SwitchCaption(SwitchDiagnostic(group)),Theme.Green,J.A(J.V(group,"members"))));
        foreach(var group in multicast)groups.Add(Tuple.Create(J.S(group,"address")+":"+J.N(group,"port"),"MCAST · ОС НЕ ПРОВЕРЕНА",Theme.Yellow,J.A(J.V(group,"members"))));
        foreach(var link in links)groups.Add(Tuple.Create("TCP: "+J.S(J.V(link,"listen"),"vm")+" → "+J.S(J.V(link,"connect"),"vm"),"TCP · ОС НЕ ПРОВЕРЕНА",Theme.Accent,new[]{J.V(link,"listen"),J.V(link,"connect")}));
        var lanes=new Dictionary<string,int>();
        for(int i=0;i<groups.Count;i++){
            var group=groups[i];var box=Node(NetworkKey(i),1,i);object diag=i<switches.Length?SwitchDiagnostic(switches[i]):null;
            for(int memberIndex=0;memberIndex<group.Item4.Length;memberIndex++){var member=group.Item4[memberIndex];Rectangle vm;string vmName=J.S(member,"vm");if(!nodes.TryGetValue(vmName,out vm))continue;
                int lane;lanes.TryGetValue(vmName,out lane);lanes[vmName]=lane+1;
                int sourceY=Math.Min(vm.Bottom-8,vm.Y+(int)((18+Math.Min(lane,3)*14)*scale)),destY=Math.Min(box.Bottom-8,box.Y+(int)((18+Math.Min(memberIndex,3)*14)*scale));
                bool forward=vm.X<box.X;int sourceX=forward?vm.Right:vm.Left,destX=forward?box.Left:box.Right;
                using(var pen=new Pen(group.Item3,2))g.DrawLine(pen,sourceX,sourceY,destX,destY);
                var name=J.S(member,"nic");if(!String.IsNullOrEmpty(name)){
                    int available=Math.Max(1,Math.Abs(destX-sourceX)-12),labelWidth=Math.Min(available,Math.Max(34,TextRenderer.MeasureText(name,Font).Width+6));
                    var badge=new Rectangle(forward?sourceX+4:sourceX-labelWidth-4,sourceY-8,labelWidth,16);
                    using(var backing=new SolidBrush(Theme.Bg))g.FillRectangle(backing,badge);
                    TextRenderer.DrawText(g,name,Font,badge,group.Item3,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
                }
                if(diag!=null){object port=J.A(J.V(diag,"members")).FirstOrDefault(item=>J.S(item,"vm")==vmName&&J.S(item,"nic")==name);using(var dot=new SolidBrush(port==null?Theme.Muted:J.B(port,"connected")?Theme.Green:Theme.Red))g.FillEllipse(dot,destX-4,destY-4,8,8);}
            }
        }
        TextRenderer.DrawText(g,"ВИРТУАЛЬНЫЕ МАШИНЫ",Font,new Point(left,18),Theme.Muted);
        TextRenderer.DrawText(g,"СЕТИ И СОЕДИНЕНИЯ",Font,new Point(right,18),Theme.Muted);
        using(var nodeFont=new Font(Font.FontFamily,Math.Max(7F,Font.Size*scale),FontStyle.Regular)){
            foreach(var vm in nodes){bool missing=vmRow!=null&&vmRow(vm.Key)==null;object data=J.V(vmRow==null?null:vmRow(vm.Key),"data");int nics=J.A(J.V(data,"network_adapters")).Length;string state=J.S(J.V(overview,"members"),vm.Key);string subtitle=missing?"VM ОТСУТСТВУЕТ":state.Length>0?"VM · "+Theme.State(state):"VM · "+nics+" NIC · не проверена";Box(g,vm.Value,vm.Key,subtitle,missing?Theme.Red:state.Length>0?Theme.StateColor(state):Theme.Accent,nodeFont,scale);}
            for(int i=0;i<groups.Count;i++){var group=groups[i];var box=Node(NetworkKey(i),1,i);Box(g,box,group.Item1,group.Item2,group.Item3,nodeFont,scale);if(Focused&&i==focusedNetwork)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(box,-4,-4));}
        }
        if(groups.Count==0)TextRenderer.DrawText(g,"Соединения ещё не настроены.",Font,new Point(right,65),Theme.Muted);
        TextRenderer.DrawText(g,"Связь внутри гостевых ОС: не проверена",Font,new Point(left,diagramHeight-26),Theme.Muted);
    }
}

sealed class StandNetworksWindow : Form {
    readonly Backend backend;readonly string stand;
    readonly ThemedSelector selector=Theme.Combo();readonly ThemedEditor text=Theme.Editor();
    readonly Label status=Theme.Label("Читаю состояние…",10,Theme.Muted);readonly Button refresh;
    object[] networks=new object[0];bool pending;
    public Func<Task<object>> StatusProvider {get;set;}
    static string Value(object row,string key){return Convert.ToString(J.V(row,key))??"0";}
    public static string Category(string value){
        switch(value){
            case "helper-ready":return "Коммутатор запущен";case "connected":return "QEMU подключён";
            case "stopped":return "Сеть выключена";case "unknown":return "Состояние не подтверждено";
            case "helper-start":return "Помощник не запустился";case "helper-unavailable":return "Помощник недоступен";
            case "port-unavailable":case "port-busy":return "Порт занят или недоступен";
            case "permission":return "Нет доступа";case "identity":case "owner-rejected":return "Владелец не подтверждён";
            case "protocol":return "Ошибка протокола";case "connection":case "connection-error":return "Нет подключения QEMU";
            case "nic-conflict":return "Адаптер используется повторно";case "mac-conflict":return "Конфликт MAC";
            case "missing-vm":return "VM отсутствует";case "missing-nic":return "Адаптер отсутствует";
            case "port-connected":return "Участник подключён";case "port-disconnected":return "Участник отключён";
            case "malformed-frame":case "partial-frame":return "Некорректный или неполный кадр";
            case "read-error-or-timeout":return "Ошибка чтения или тайм-аут кадра";
            case "queue-overflow":return "Очередь получателя переполнена";case "rate-limit":return "Превышен лимит отправителя";
            default:return String.IsNullOrEmpty(value)?"Нет ошибок":value;
        }
    }
    public static string Describe(object network){
        var lines=new List<string>{J.S(network,"name"),Category(J.S(network,"state")),
            "Связь внутри гостевых ОС: не проверена.","", "Диагностика: "+Category(J.S(network,"category"))};
        if(!String.IsNullOrEmpty(J.S(network,"error")))lines.Add(J.S(network,"error"));
        lines.Add(J.S(network,"advice"));lines.Add("");lines.Add("УЧАСТНИКИ");
        foreach(var member in J.A(J.V(network,"members"))){
            lines.Add(J.S(member,"vm")+" / "+J.S(member,"nic")+" · "+(J.B(member,"connected")?"QEMU подключён":"QEMU не подключён"));
            lines.Add("MAC: "+J.S(member,"mac","не задан")+" · "+J.S(member,"model")+" · порт: "+(J.N(member,"port")>0?Value(member,"port"):"не выделен"));
            lines.Add("Другие NAT-адаптеры: "+(J.A(J.V(member,"nat")).Length>0?String.Join(", ",J.A(J.V(member,"nat")).Select(Convert.ToString)):"нет"));
            if(!String.IsNullOrEmpty(J.S(member,"problem")))lines.Add("Проблема: "+Category(J.S(member,"problem")));lines.Add("");
        }
        var counters=J.V(network,"counters");lines.Add("СЧЁТЧИКИ КАДРОВ");
        lines.Add("Принято: "+Value(counters,"received")+" · отправлено: "+Value(counters,"sent")+" · отброшено: "+Value(counters,"dropped"));
        lines.Add("Отклонено подключений: "+Value(counters,"rejected")+" · изучено MAC: "+Value(counters,"learned"));
        lines.Add("Последнее событие (UTC): "+J.S(network,"last_event_utc","нет"));lines.Add("");lines.Add("ПОСЛЕДНИЕ СОБЫТИЯ · без содержимого пакетов");
        foreach(var entry in J.A(J.V(network,"events"))){int index=J.N(entry,"port_index",-1);lines.Add(J.S(entry,"time")+" · "+Category(J.S(entry,"category"))+(index>=0?" · участник "+(index+1):""));}
        return String.Join("\r\n",lines);
    }
    public StandNetworksWindow(Backend backend,string stand){
        this.backend=backend;this.stand=stand;Theme.Form(this,940,760);Text="Общие сети · диагностика";
        selector.AccessibleName="Выбрать общую сеть";selector.Dock=DockStyle.Top;selector.SelectedIndexChanged+=(s,e)=>Render();
        text.ReadOnly=true;text.Dock=DockStyle.Fill;text.AccessibleName="Состояние и счётчики общей сети";
        var body=new Panel{Dock=DockStyle.Fill,BackColor=Theme.Bg};body.Controls.Add(text);body.Controls.Add(selector);
        status.Dock=DockStyle.Top;status.AutoSize=true;body.Controls.Add(status);
        refresh=Theme.Button("Обновить",()=>Read());var buttons=Theme.Flow();buttons.Controls.Add(refresh);buttons.Controls.Add(Theme.Button("Закрыть",Close));
        Theme.Dialog(this,body,buttons,"Общие сети стенда","Только чтение. Окно не запускает и не выключает VM или коммутатор.");
        Shown+=(s,e)=>Read();
    }
    void Render(){text.Text=selector.SelectedIndex>=0&&selector.SelectedIndex<networks.Length?Describe(networks[selector.SelectedIndex]):"Общих сетей пока нет.";}
    async void Read(){
        if(pending||IsDisposed)return;pending=true;refresh.Enabled=false;
        try{
            object value=StatusProvider!=null?await StatusProvider():await Task.Run(()=>backend.Call("stand_overview",J.O("id",stand)));
            if(IsDisposed)return;int previous=selector.SelectedIndex;networks=J.A(J.V(value,"network_diagnostics"));selector.Items.Clear();
            foreach(var network in networks)selector.Items.Add(J.S(network,"name"));if(networks.Length>0)selector.SelectedIndex=Math.Min(Math.Max(0,previous),networks.Length-1);Render();
            status.Text="Снимок состояния на "+DateTime.Now.ToString("HH:mm:ss")+". Для повторной проверки нажми «Обновить».";
        }catch(Exception error){if(!IsDisposed){status.Text="Не удалось прочитать сеть. Повтори проверку или выполни сверку стенда.";text.Text=error.Message;}}
        finally{pending=false;if(!IsDisposed)refresh.Enabled=true;}
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
