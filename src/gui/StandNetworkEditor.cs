using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

// One draft, one window and one final backend save. All controls below edit only
// the cloned stand definition; neither a guest nor its own JSON is touched here.
sealed class StandNetworkEditorWindow : Form {
    readonly Dictionary<string,object> draft;
    readonly List<object> links,switches,multicast;
    readonly List<string> order;
    readonly Dictionary<string,object> startup;
    readonly List<object> endpoints=new List<object>();
    readonly Func<string,object> vmRow;
    readonly Action<StandNetworkEditorWindow,object> save;
    readonly StandTopologyCanvas canvas;
    readonly Func<object> statusProvider;
    readonly Action<Dictionary<string,Point>> placementChanged;
    readonly ThemedEditor diagnostics=Theme.Editor();
    bool statusPending;
    ScrollSurface adapterScroll;string selectedVm;
    readonly WorkspaceTabs tabs=new WorkspaceTabs();
    readonly Label notice=Theme.Label("Изменения не сохранены в стенде.",9,Theme.Muted);
    readonly ThemedList linksList=Theme.ListBox(10),from=Theme.ListBox(10),to=Theme.ListBox(10);
    readonly ThemedList switchList=Theme.ListBox(10),multicastList=Theme.ListBox(10),orderList=Theme.ListBox(10);
    readonly FieldInput switchName=Theme.Input(),address=Theme.Input(),localAddress=Theme.Input();
    readonly NumberInput port=Theme.Number(20000,1024,65535),delay=Theme.Number(0,0,300),timeout=Theme.Number(60,1,600),readyPort=Theme.Number(2222,1024,65535);
    readonly FlowLayoutPanel switchMembers=Theme.Flow(true),multicastMembers=Theme.Flow(true);
    readonly FlowLayoutPanel adapterStack=Page();
    readonly RadioButton process=Radio("Процесс и QMP"),qmp=Radio("QMP готов"),tcp=Radio("TCP-сервис на хосте");
    readonly Button saveButton;
    bool loading,dirty,busy,allowClose;
    int priorOrder=-1;

    static RadioButton Radio(string title){return new RadioButton{Text=title,AutoSize=true,ForeColor=Theme.Text,BackColor=Theme.Bg,Margin=new Padding(0,5,10,5)};}
    static string End(object end){return J.S(end,"vm")+" / "+J.S(end,"nic");}
    static bool Same(object a,object b){return J.S(a,"vm")==J.S(b,"vm")&&J.S(a,"nic")==J.S(b,"nic");}
    static string LinkText(object link){return "TCP · "+End(J.V(link,"listen"))+" → "+End(J.V(link,"connect"));}
    static string GroupText(object value,bool switched){return (switched?"КОММУТАТОР · "+J.S(value,"name"):"MCAST · "+J.S(value,"address")+":"+J.N(value,"port"))+" · "+J.A(J.V(value,"members")).Length+" VM";}
    static Label Caption(string title){var label=Theme.Label(title,12,null,true);label.Margin=new Padding(0,16,0,8);return label;}
    static void SetWidth(Control control,int width=370){control.Width=width;control.Margin=new Padding(0,0,0,10);}

    public StandNetworkEditorWindow(object original,Func<string,object> vmRow,Action<StandNetworkEditorWindow,object> save,Dictionary<string,Point> positions=null,Action<Dictionary<string,Point>> placementChanged=null,Func<object> statusProvider=null){
        this.vmRow=vmRow;this.save=save;this.placementChanged=placementChanged;this.statusProvider=statusProvider;
        draft=J.Json().DeserializeObject(J.Json().Serialize(original)) as Dictionary<string,object>;
        if(draft==null)throw new ArgumentException("Некорректное определение стенда.");
        links=J.A(J.V(draft,"links")).ToList();switches=J.A(J.V(draft,"switches")).ToList();multicast=J.A(J.V(draft,"multicast")).ToList();
        order=J.A(J.V(draft,"order")).Select(Convert.ToString).ToList();
        startup=J.V(draft,"startup") as Dictionary<string,object> ?? order.ToDictionary(n=>n,n=>(object)J.O("delay",0,"timeout",60,"ready","qmp"));
        foreach(string name in order)foreach(object nic in J.A(J.V(J.V(vmRow(name),"data"),"network_adapters")))endpoints.Add(J.O("vm",name,"nic",J.S(nic,"id")));
        Text="Сеть стенда · "+J.S(original,"name");Theme.Form(this,1240,820);MinimumSize=new Size(990,650);
        canvas=new StandTopologyCanvas(draft,vmRow,positions){AllowMove=true};
        canvas.PlacementChanged+=value=>{if(this.placementChanged==null)return;try{this.placementChanged(value);notice.Text="Расположение сохранено локально · связи ещё в черновике";notice.ForeColor=Theme.Accent;}catch(Exception error){notice.Text="Расположение не сохранено: "+error.Message;notice.ForeColor=Theme.Red;}};
        canvas.ConnectionSelected+=(kind,index)=>{if(kind=="switch"){tabs.SelectedIndex=1;switchList.SelectedIndex=index;}else if(kind=="mcast"){tabs.SelectedIndex=2;multicastList.SelectedIndex=index;}else{tabs.SelectedIndex=0;linksList.SelectedIndex=index;}};
        canvas.VmSelected+=name=>{selectedVm=name;tabs.SelectedIndex=3;RefreshAdapters();};
        var graph=new ScrollSurface(canvas){BackColor=Theme.Bg};
        var split=new SplitContainer{Dock=DockStyle.Fill,Size=new Size(1200,620),SplitterWidth=7,BackColor=Theme.Line};
        split.Panel1MinSize=370;split.Panel2MinSize=390;split.SplitterDistance=600;
        split.Panel1.BackColor=split.Panel2.BackColor=Theme.Bg;tabs.Dock=DockStyle.Fill;
        split.Panel1.Controls.Add(graph);split.Panel2.Controls.Add(tabs);
        var legend=Theme.Label("Схему можно перемещать мышью или Ctrl+стрелками. Расположение сохраняется отдельно; TCP, коммутатор и MCAST — после «Сохранить стенд».",9,Theme.Muted);
        legend.Dock=DockStyle.Top;legend.Padding=new Padding(18,8,10,3);legend.Height=42;legend.AutoSize=false;split.Panel1.Controls.Add(legend);
        BuildTcp();BuildGroups(true);BuildGroups(false);BuildAdapters();BuildOrder();BuildStatus();
        var zoomButtons=Theme.Flow();Button zoomValue=null;
        zoomButtons.Controls.Add(Theme.Button("−",()=>{canvas.ZoomPercent-=25;zoomValue.Text=canvas.ZoomPercent+"%";}));
        zoomValue=Theme.Button("100%",()=>{canvas.ZoomPercent=100;zoomValue.Text="100%";});zoomButtons.Controls.Add(zoomValue);
        zoomButtons.Controls.Add(Theme.Button("+",()=>{canvas.ZoomPercent+=25;zoomValue.Text=canvas.ZoomPercent+"%";}));
        zoomButtons.Dock=DockStyle.Bottom;split.Panel1.Controls.Add(zoomButtons);
        var buttons=Theme.Flow();notice.AutoSize=false;notice.Width=360;notice.Height=38;notice.TextAlign=ContentAlignment.MiddleLeft;buttons.Controls.Add(notice);
        saveButton=Theme.Button("Сохранить стенд",Save,true);buttons.Controls.Add(saveButton);
        buttons.Controls.Add(Theme.Button("Отмена",Close));
        Theme.Dialog(this,split,buttons,"Сеть и соединения · "+J.S(original,"name"),"Изменения применятся после проверки и сохранения. Настройки отдельных VM не переписываются.");
        FormClosing+=(sender,args)=>{if(busy){args.Cancel=true;return;}if(dirty&&!allowClose&&!Program.TestMode&&MessageBox.Show(this,"Закрыть редактор без сохранения изменений?","Несохранённый черновик",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)args.Cancel=true;};
        RefreshAll();
        Shown+=(s,e)=>RefreshStatus();
    }

    void BuildStatus(){diagnostics.ReadOnly=true;diagnostics.AccessibleName="Состояние сетей стенда, только чтение";
        var page=new Panel{Dock=DockStyle.Fill,BackColor=Theme.Bg,Padding=new Padding(14)};var refresh=Theme.Button("Обновить состояние",RefreshStatus);refresh.Dock=DockStyle.Top;
        var explanation=Theme.Label("Это срез сохранённого стенда. Связь внутри гостевых ОС не проверяется; черновик сети может отличаться.",9,Theme.Muted);explanation.Dock=DockStyle.Top;explanation.Height=54;explanation.AutoSize=false;
        diagnostics.Dock=DockStyle.Fill;page.Controls.Add(diagnostics);page.Controls.Add(explanation);page.Controls.Add(refresh);tabs.Add("Состояние",page);
    }
    async void RefreshStatus(){if(statusPending||IsDisposed)return;if(statusProvider==null){diagnostics.Text="Диагностика доступна после открытия сохранённого стенда.";return;}statusPending=true;diagnostics.Text="Читаю QEMU и коммутаторы…";
        try{object value=await System.Threading.Tasks.Task.Run(statusProvider);if(IsDisposed)return;canvas.ApplyStatus(value);
            var lines=new List<string>{"Срез состояния · "+DateTime.Now.ToString("HH:mm:ss"),"Связь внутри гостевых ОС: НЕ ПРОВЕРЕНА", "", "ВИРТУАЛЬНЫЕ МАШИНЫ"};
            foreach(var item in J.D(J.V(value,"members")))lines.Add(item.Key+" · "+Theme.State(Convert.ToString(item.Value)));
            var networks=J.A(J.V(value,"network_diagnostics"));lines.Add("");lines.Add("КОММУТАТОРЫ · "+networks.Length);
            foreach(var network in networks){lines.Add("");lines.Add(StandNetworksWindow.Describe(network));}
            diagnostics.Text=String.Join("\r\n",lines);
        }catch(Exception error){if(!IsDisposed)diagnostics.Text="Состояние не получено: "+error.Message+"\r\nСохранённая схема не подтверждает наличие связи внутри гостевых ОС.";}
        finally{statusPending=false;}
    }

    public object Result(){
        StoreOrder();ValidateConnections();draft["schema_version"]=4;draft["links"]=links.ToArray();draft["switches"]=switches.ToArray();draft["multicast"]=multicast.ToArray();draft["order"]=order.ToArray();draft["startup"]=startup;
        return draft;
    }
    public void OpenStatus(){tabs.SelectedIndex=5;}
    void Changed(){if(loading)return;dirty=true;notice.Text="● Несохранённые изменения";notice.ForeColor=Theme.Yellow;RefreshGraph();}
    void RefreshGraph(){draft["links"]=links.ToArray();draft["switches"]=switches.ToArray();draft["multicast"]=multicast.ToArray();canvas.Load(draft);RefreshAdapters();}
    void RefreshAll(){RefreshLinks();RefreshGroups(true);RefreshGroups(false);RefreshOrder();RefreshGraph();}
    public void SaveFailed(Exception error){busy=false;saveButton.Enabled=true;notice.ForeColor=Theme.Red;notice.Text="Не сохранено: "+error.Message;}
    public void SaveSucceeded(){busy=false;allowClose=true;Close();}
    void Save(){
        if(busy)return;
        try{
            var result=Result();
            busy=true;saveButton.Enabled=false;notice.Text="Проверяю и сохраняю…";notice.ForeColor=Theme.Accent;save(this,result);
        }catch(Exception error){SaveFailed(error);}
    }
    string EndpointOwner(object end,string excludedKind="",int excludedIndex=-1){
        for(int i=0;i<links.Count;i++)if(excludedKind!="tcp"||i!=excludedIndex)if(Same(J.V(links[i],"listen"),end)||Same(J.V(links[i],"connect"),end))return "TCP-связь";
        foreach(bool switched in new[]{true,false}){
            var groups=Groups(switched);for(int i=0;i<groups.Count;i++)if((excludedKind!=(switched?"switch":"mcast")||i!=excludedIndex)&&J.A(J.V(groups[i],"members")).Any(m=>Same(m,end)))return switched?"коммутатор":"MCAST";
        }
        return "";
    }
    void Reject(string text){notice.Text=text;notice.ForeColor=Theme.Red;}
    void ValidateConnections(){
        foreach(object link in links){object a=J.V(link,"listen"),b=J.V(link,"connect");if(Same(a,b)||J.S(a,"vm")==J.S(b,"vm"))throw new InvalidOperationException("TCP-связь должна соединять две разные VM и NIC.");
            if(order.IndexOf(J.S(a,"vm"))>=order.IndexOf(J.S(b,"vm")))throw new InvalidOperationException("Для TCP-связи сначала запусти слушателя «"+J.S(a,"vm")+"», затем «"+J.S(b,"vm")+"». Исправь порядок во вкладке «Запуск».");
            ValidateMacs(new[]{a,b},"TCP-связи");
        }
        foreach(bool switched in new[]{true,false})foreach(object group in Groups(switched)){
            var members=J.A(J.V(group,"members"));if(members.Length<2||members.Select(m=>J.S(m,"vm")).Distinct().Count()!=members.Length)throw new InvalidOperationException("В каждой общей сети нужны минимум две разные VM, по одному NIC каждой.");
            ValidateMacs(members,switched?"коммутаторе":"MCAST");
        }
        var used=new HashSet<string>(StringComparer.Ordinal);
        foreach(object end in links.SelectMany(link=>new[]{J.V(link,"listen"),J.V(link,"connect")}).Concat(switches.SelectMany(group=>J.A(J.V(group,"members")))).Concat(multicast.SelectMany(group=>J.A(J.V(group,"members"))))){
            if(!endpoints.Any(available=>Same(available,end)))throw new InvalidOperationException("VM или NIC в схеме отсутствует: "+End(end));
            if(!used.Add(End(end)))throw new InvalidOperationException("Адаптер назначен нескольким связям: "+End(end));
        }
    }
    void ValidateMacs(IEnumerable<object> members,string kind){
        var macs=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(object end in members){
            object data=J.V(vmRow(J.S(end,"vm")),"data");
            object nic=J.A(J.V(data,"network_adapters")).FirstOrDefault(n=>J.S(n,"id")==J.S(end,"nic"));
            if(nic==null)continue; // Missing VM/NIC has a more specific error below.
            string mac=J.S(nic,"mac");
            if(String.IsNullOrWhiteSpace(mac))throw new InvalidOperationException("Для связи в "+kind+" нужен явный MAC: "+End(end));
            if(!macs.Add(mac))throw new InvalidOperationException("В "+kind+" повторяется MAC «"+mac+"». Исправь адаптер одной из VM.");
        }
    }

    static FlowLayoutPanel Page(){var stack=Theme.Flow(true);stack.Padding=new Padding(18,0,12,12);stack.BackColor=Theme.Bg;return stack;}
    static void Add(FlowLayoutPanel stack,Control control,int width=370){SetWidth(control,width);stack.Controls.Add(control);}
    void BuildTcp(){
        var stack=Page();Add(stack,Caption("Прямые TCP-соединения"));
        Add(stack,Theme.Label("Слушатель запускается первым. Выбери по одному адаптеру двух VM; другие сети этих VM останутся отдельными.",9,Theme.Muted));
        linksList.Height=110;Add(stack,linksList);linksList.SelectedIndexChanged+=(s,e)=>SelectLink();
        Add(stack,Caption("Слушатель / NIC"));from.Height=105;Add(stack,from);
        Add(stack,Caption("Подключаемая VM / NIC"));to.Height=105;Add(stack,to);
        var actions=Theme.Flow();actions.Controls.Add(Theme.Button("Добавить TCP",()=>ApplyLink(false),true));actions.Controls.Add(Theme.Button("Изменить",()=>ApplyLink(true)));actions.Controls.Add(Theme.Button("Убрать",RemoveLink,false,true));Add(stack,actions);
        Add(stack,Theme.Label("TCP здесь означает внутренний сетевой канал QEMU между двумя VM, а не внешний порт гостевой ОС. Слушатель должен стоять раньше подключаемой VM в порядке запуска. IP-адреса настраиваются в гостях.",9,Theme.Muted));
        tabs.Add("TCP",new ScrollSurface(stack){BackColor=Theme.Bg});
    }
    void RefreshLinks(){
        int selected=linksList.SelectedIndex;loading=true;linksList.Items.Clear();foreach(object link in links)linksList.Items.Add(LinkText(link));
        from.Items.Clear();to.Items.Clear();foreach(object end in endpoints){from.Items.Add(End(end));to.Items.Add(End(end));}
        linksList.SelectedIndex=selected<links.Count?selected:links.Count-1;loading=false;SelectLink();
    }
    void SelectLink(){if(loading||linksList.SelectedIndex<0)return;object link=links[linksList.SelectedIndex];from.SelectedIndex=endpoints.FindIndex(end=>Same(end,J.V(link,"listen")));to.SelectedIndex=endpoints.FindIndex(end=>Same(end,J.V(link,"connect")));}
    void ApplyLink(bool replace){
        if(from.SelectedIndex<0||to.SelectedIndex<0||J.S(endpoints[from.SelectedIndex],"vm")==J.S(endpoints[to.SelectedIndex],"vm")){Reject("Выбери адаптеры двух разных VM.");return;}
        int index=linksList.SelectedIndex;if(replace&&index<0)return;
        foreach(object end in new[]{endpoints[from.SelectedIndex],endpoints[to.SelectedIndex]}){string owner=EndpointOwner(end,replace?"tcp":"",index);if(owner.Length>0){Reject(End(end)+" уже использует "+owner+". Выбери другой NIC.");return;}}
        object value=J.O("id",replace?J.S(links[index],"id"):Guid.NewGuid().ToString("N"),"listen",endpoints[from.SelectedIndex],"connect",endpoints[to.SelectedIndex]);
        if(replace)links[index]=value;else{links.Add(value);index=links.Count-1;}
        RefreshLinks();linksList.SelectedIndex=index;Changed();
    }
    void RemoveLink(){int i=linksList.SelectedIndex;if(i<0)return;links.RemoveAt(i);RefreshLinks();Changed();}

    void BuildGroups(bool switched){
        var stack=Page();var list=switched?switchList:multicastList;var members=switched?switchMembers:multicastMembers;
        Add(stack,Caption(switched?"Локальные коммутаторы":"Общие сети MCAST"));
        Add(stack,Theme.Label(switched?"Один коммутатор объединяет выбранные NIC в отдельную локальную L2-сеть.":"MCAST соединяет VM через multicast-группу; доступность зависит от сети Windows и firewall.",9,Theme.Muted));
        list.Height=110;Add(stack,list);list.SelectedIndexChanged+=(s,e)=>LoadGroup(switched);
        var actions=Theme.Flow();actions.Controls.Add(Theme.Button(switched?"＋ Коммутатор":"＋ MCAST",()=>AddGroup(switched),true));actions.Controls.Add(Theme.Button("Убрать",()=>RemoveGroup(switched),false,true));Add(stack,actions);
        Add(stack,Caption(switched?"Название сети":"Адрес и порт MCAST"));
        if(switched){Add(stack,switchName);switchName.TextChanged+=(s,e)=>ChangeGroup(true);}
        else{Add(stack,address);Add(stack,port);Add(stack,Theme.Label("IPv4 интерфейса Windows (необязательно)",9,Theme.Muted));Add(stack,localAddress);address.TextChanged+=(s,e)=>ChangeGroup(false);port.TextChanged+=(s,e)=>{try{ChangeGroup(false);}catch(InvalidOperationException){}};localAddress.TextChanged+=(s,e)=>ChangeGroup(false);}
        Add(stack,Caption("Подключённые адаптеры"));members.WrapContents=false;Add(stack,members);
        Add(stack,Theme.Label("Выбери минимум две разные VM. Подключённый NIC будет занят этой сетью при запуске стенда; для NAT оставь отдельный адаптер.",9,Theme.Muted));
        tabs.Add(switched?"Коммутаторы":"MCAST",new ScrollSurface(stack){BackColor=Theme.Bg});
    }
    List<object> Groups(bool switched){return switched?switches:multicast;}
    ThemedList GroupList(bool switched){return switched?switchList:multicastList;}
    void RefreshGroups(bool switched){
        var groups=Groups(switched);var list=GroupList(switched);int selected=list.SelectedIndex;loading=true;list.Items.Clear();foreach(object group in groups)list.Items.Add(GroupText(group,switched));list.SelectedIndex=selected<groups.Count?selected:groups.Count-1;loading=false;LoadGroup(switched);
    }
    void AddGroup(bool switched){
        var group=switched?J.O("id",Guid.NewGuid().ToString("N"),"name","Новая общая сеть","members",new object[0]):J.O("id",Guid.NewGuid().ToString("N"),"address","239.192.0.1","port",20000,"localaddr","","members",new object[0]);
        Groups(switched).Add(group);RefreshGroups(switched);GroupList(switched).SelectedIndex=Groups(switched).Count-1;Changed();
    }
    void RemoveGroup(bool switched){int i=GroupList(switched).SelectedIndex;if(i<0)return;Groups(switched).RemoveAt(i);RefreshGroups(switched);Changed();}
    void LoadGroup(bool switched){
        int index=GroupList(switched).SelectedIndex;var members=switched?switchMembers:multicastMembers;members.Controls.Clear();
        bool active=index>=0;switchName.Enabled=active;address.Enabled=port.Enabled=localAddress.Enabled=active;
        if(!active){loading=true;if(switched)switchName.Text="";else{address.Text="";localAddress.Text="";}loading=false;return;}
        object group=Groups(switched)[index];loading=true;
        if(switched)switchName.Text=J.S(group,"name");else{address.Text=J.S(group,"address");port.Value=J.N(group,"port",20000);localAddress.Text=J.S(group,"localaddr");}
        var selected=J.A(J.V(group,"members"));
        foreach(object end in endpoints.Concat(selected.Where(m=>!endpoints.Any(e=>Same(e,m))))){
            bool available=endpoints.Any(e=>Same(e,end));var check=new CheckBox{Text=End(end)+(available?"":" · VM/NIC отсутствует"),Checked=selected.Any(e=>Same(e,end)),Enabled=available,AutoSize=true,ForeColor=available?Theme.Text:Theme.Red,BackColor=Theme.Bg,Margin=new Padding(0,5,0,5)};
            object captured=end;check.CheckedChanged+=(s,e)=>{if(loading)return;int current=GroupList(switched).SelectedIndex;if(current<0)return;if(check.Checked){string owner=EndpointOwner(captured,switched?"switch":"mcast",current);if(owner.Length>0){loading=true;check.Checked=false;loading=false;Reject(End(captured)+" уже использует "+owner+". Выбери другой NIC.");return;}}var data=J.D(Groups(switched)[current]);var list=J.A(J.V(data,"members")).Where(m=>!Same(m,captured)).ToList();if(check.Checked)list.Add(captured);data["members"]=list.ToArray();Changed();};members.Controls.Add(check);
        }
        loading=false;
    }
    void ChangeGroup(bool switched){
        if(loading)return;int i=GroupList(switched).SelectedIndex;if(i<0)return;var data=J.D(Groups(switched)[i]);
        if(switched)data["name"]=switchName.Text;else{data["address"]=address.Text;data["port"]=(int)port.Value;data["localaddr"]=localAddress.Text;}
        GroupList(switched).Items[i]=GroupText(data,switched);Changed();
    }

    void BuildOrder(){
        var stack=Page();Add(stack,Caption("Очередность запуска"));Add(stack,Theme.Label("Порядок влияет на подключение TCP и готовность участников. Сеть сама не запускает VM.",9,Theme.Muted));
        orderList.Height=160;Add(stack,orderList);orderList.SelectedIndexChanged+=(s,e)=>LoadOrder();
        var actions=Theme.Flow();actions.Controls.Add(Theme.Button("Выше",()=>MoveOrder(-1)));actions.Controls.Add(Theme.Button("Ниже",()=>MoveOrder(1)));Add(stack,actions);
        Add(stack,Caption("Условие готовности"));Add(stack,process);Add(stack,qmp);Add(stack,tcp);
        foreach(var radio in new[]{process,qmp,tcp})radio.CheckedChanged+=(s,e)=>{if(!loading)StoreOrder();};
        Add(stack,Theme.Label("Локальный порт TCP-сервиса",9,Theme.Muted));Add(stack,readyPort);
        Add(stack,Theme.Label("Задержка перед запуском, с",9,Theme.Muted));Add(stack,delay);
        Add(stack,Theme.Label("Тайм-аут готовности, с",9,Theme.Muted));Add(stack,timeout);
        foreach(var number in new[]{readyPort,delay,timeout})number.TextChanged+=(s,e)=>{if(!loading)try{StoreOrder();}catch(InvalidOperationException){}};
        tabs.Add("Запуск",new ScrollSurface(stack){BackColor=Theme.Bg});
    }
    void BuildAdapters(){
        adapterScroll=new ScrollSurface(adapterStack){BackColor=Theme.Bg};tabs.Add("Адаптеры",adapterScroll);
    }
    void RefreshAdapters(){
        var stack=adapterStack;stack.SuspendLayout();foreach(Control child in stack.Controls.Cast<Control>().ToArray())child.Dispose();stack.Controls.Clear();Add(stack,Caption("Адаптеры участников"));
        Add(stack,Theme.Label("Все адаптеры VM показаны здесь, включая NAT, TAP и прямые сокеты. Редактор стенда назначает только связи выбранным NIC; независимые адаптеры сохраняют настройки своей VM.",9,Theme.Muted));
        foreach(string name in order){
            var heading=Caption(name);heading.Tag=name;heading.ForeColor=name==selectedVm?Theme.Accent:Theme.Text;Add(stack,heading);object data=J.V(vmRow(name),"data");
            if(data==null){Add(stack,Theme.Label("VM отсутствует в библиотеке.",9,Theme.Red));continue;}
            var nics=J.A(J.V(data,"network_adapters"));
            if(nics.Length==0){Add(stack,Theme.Label("Сетевых адаптеров нет. Добавь NIC в параметрах VM, затем заново открой сеть стенда.",9,Theme.Yellow));continue;}
            foreach(object nic in nics){object end=J.O("vm",name,"nic",J.S(nic,"id"));string owner=EndpointOwner(end);string type=J.S(nic,"type","user");string mode=J.S(nic,"mode");
                string title=J.S(nic,"id")+" · "+(owner.Length>0?"занят: "+owner:type=="user"?"NAT":type=="tap"?"TAP":type=="socket"?"Сокет "+mode:type);
                Add(stack,Theme.Label(title+" · "+J.S(nic,"model")+" · "+J.S(nic,"mac","MAC не задан"),9,owner.Length>0?Theme.Accent:Theme.Text));
            }
        }
        Add(stack,Theme.Label("Для изменения параметров самого адаптера выбери VM в библиотеке и открой «Параметры». Сохранённый стенд не переписывает конфигурацию отдельных машин.",9,Theme.Muted));
        stack.ResumeLayout(true);
        if(selectedVm!=null&&adapterScroll!=null&&tabs.SelectedIndex==3){adapterScroll.PerformLayout();var heading=stack.Controls.OfType<Label>().FirstOrDefault(c=>Convert.ToString(c.Tag)==selectedVm);if(heading!=null)adapterScroll.ScrollOffset=Math.Min(adapterScroll.ScrollBar.Maximum,Math.Max(0,heading.Top-12));}
    }
    void RefreshOrder(){loading=true;int selected=orderList.SelectedIndex;orderList.Items.Clear();foreach(string name in order)orderList.Items.Add(name);orderList.SelectedIndex=selected>=0&&selected<order.Count?selected:order.Count>0?0:-1;loading=false;LoadOrder();}
    void LoadOrder(){if(loading)return;priorOrder=orderList.SelectedIndex;if(priorOrder<0)return;object step;startup.TryGetValue(order[priorOrder],out step);loading=true;delay.Value=J.N(step,"delay");timeout.Value=J.N(step,"timeout",60);readyPort.Value=J.N(step,"tcp_port",2222);string ready=J.S(step,"ready","qmp");process.Checked=ready=="process";qmp.Checked=ready=="qmp";tcp.Checked=ready=="tcp";loading=false;}
    void StoreOrder(){if(loading||priorOrder<0||priorOrder>=order.Count)return;string ready=tcp.Checked?"tcp":process.Checked?"process":"qmp";int d=(int)delay.Value,t=(int)timeout.Value,p=(int)readyPort.Value;object old=J.V(startup,order[priorOrder]);if(J.N(old,"delay")==d&&J.N(old,"timeout",60)==t&&J.S(old,"ready","qmp")==ready&&(ready!="tcp"||J.N(old,"tcp_port",2222)==p))return;var value=J.O("delay",d,"timeout",t,"ready",ready);if(ready=="tcp")value["tcp_port"]=p;startup[order[priorOrder]]=value;Changed();}
    void MoveOrder(int direction){int i=orderList.SelectedIndex,j=i+direction;if(i<0||j<0||j>=order.Count)return;StoreOrder();string name=order[i];order.RemoveAt(i);order.Insert(j,name);priorOrder=-1;RefreshOrder();orderList.SelectedIndex=j;Changed();}
}
