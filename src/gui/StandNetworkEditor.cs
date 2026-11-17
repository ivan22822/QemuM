using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

// One draft, one window and one final backend save. All controls below edit only
// the cloned stand definition; neither a guest nor its own JSON is touched here.
sealed class StandNetworkEditorWindow : MotionDialog {
    readonly Dictionary<string,object> draft;
    readonly List<object> links,switches,multicast;
    readonly List<string> order;
    readonly Dictionary<string,object> startup;
    readonly List<object> endpoints=new List<object>();
    readonly Func<string,object> vmRow;
    readonly Action<StandNetworkEditorWindow,object> save;
    readonly StandTopologyCanvas canvas;
    readonly Func<object> statusProvider;
    readonly System.Threading.CancellationTokenSource statusReadCancellation=new System.Threading.CancellationTokenSource();
    readonly Action<Dictionary<string,Point>> placementChanged;
    ThemedEditor diagnostics;
    string statusText = "";
    bool adaptersDirty = true;
    internal NetworkOpenTiming OpeningTiming { get; private set; }
    bool statusPending,statusResourcesDisposed;
    ScrollSurface adapterScroll;string selectedVm;
    readonly WorkspaceTabs tabs=new WorkspaceTabs();
    readonly Label notice=Theme.Label("Изменения не сохранены в стенде.",9,Theme.Muted);
    readonly Label errorNotice=Theme.Label("",9,Theme.Red);
    readonly Label snapshotStatus=Theme.Label("Состояние ещё не проверено",9,Theme.Muted);
    Label linksEmpty;
    ThemedList linksList, from, to;
    ThemedList switchList, multicastList, orderList;
    ThemedSelector fromChoice, toChoice;
    FieldInput switchName, address, localAddress;
    NumberInput port, delay, timeout, readyPort;
    FlowLayoutPanel switchMembers, multicastMembers;
    FlowLayoutPanel adapterStack;
    RadioButton process, qmp, tcp;
    readonly Button saveButton;
    Button editLink,removeLink;
    ToolTip linkTips;
    bool loading,dirty,busy,allowClose;
    int priorOrder=-1,priorMulticast=-1;

    static RadioButton Radio(string title){return new RadioButton{Text=title,AutoSize=true,ForeColor=Theme.Text,BackColor=Theme.Bg,Margin=new Padding(0,5,10,5)};}
    static string End(object end){return J.S(end,"vm")+" / "+J.S(end,"nic");}
    static bool Same(object a,object b){return J.S(a,"vm")==J.S(b,"vm")&&J.S(a,"nic")==J.S(b,"nic");}
    static string LinkText(object link){return "TCP · "+End(J.V(link,"listen"))+" → "+End(J.V(link,"connect"));}
    static string GroupText(object value,bool switched){return (switched?"КОММУТАТОР · "+J.S(value,"name"):"MCAST · "+J.S(value,"address")+":"+J.N(value,"port"))+" · "+J.A(J.V(value,"members")).Length+" ВМ";}
    static Label Caption(string title){var label=Theme.Label(title,12,null,true);label.Tag="network-group";label.Margin=new Padding(0,18,0,9);return label;}
    static FlowLayoutPanel Section(string index,string title){var flow=Theme.Flow(true);var eyebrow=Theme.Label(index+" / ПАРАМЕТРЫ СЕТИ",9,Theme.Accent);eyebrow.Tag="network-overline";flow.Controls.Add(eyebrow);var heading=Theme.Label(title,18,null,true);heading.Tag="network-title";flow.Controls.Add(heading);return flow;}
    static void SetWidth(Control control,int width=370){control.Width=width;control.Margin=new Padding(0,0,0,10);}

    public StandNetworkEditorWindow(object original,Func<string,object> vmRow,Action<StandNetworkEditorWindow,object> save,Dictionary<string,Point> positions=null,Action<Dictionary<string,Point>> placementChanged=null,Func<object> statusProvider=null,bool openStatus=false,NetworkOpenTiming timing=null){
        OpeningTiming=timing??NetworkOpenTiming.Start();
        MarkOpening("editor_constructor_start");
        this.vmRow=vmRow;this.save=save;this.placementChanged=placementChanged;this.statusProvider=statusProvider;
        draft=J.Json().DeserializeObject(J.Json().Serialize(original)) as Dictionary<string,object>;
        if(draft==null)throw new ArgumentException("Некорректное определение стенда.");
        links=J.A(J.V(draft,"links")).ToList();switches=J.A(J.V(draft,"switches")).ToList();multicast=J.A(J.V(draft,"multicast")).ToList();
        order=J.A(J.V(draft,"order")).Select(Convert.ToString).ToList();
        startup=J.V(draft,"startup") as Dictionary<string,object> ?? order.ToDictionary(n=>n,n=>(object)J.O("delay",0,"timeout",60,"ready","qmp"));
        foreach(string name in order)foreach(object nic in J.A(J.V(J.V(vmRow(name),"data"),"network_adapters")))endpoints.Add(J.O("vm",name,"nic",J.S(nic,"id")));
        MarkOpening("draft_and_endpoints");
        Text="Сеть стенда · "+J.S(original,"name");Theme.Form(this,1240,865);MinimumSize=new Size(990,650);
        canvas=new StandTopologyCanvas(draft,vmRow,positions){AllowMove=true};
        canvas.PlacementChanged+=value=>{if(this.placementChanged==null)return;try{this.placementChanged(value);notice.Text=dirty?"Расположение сохранено · изменения сети не сохранены":"Расположение сохранено локально";notice.ForeColor=dirty?Theme.Yellow:Theme.Accent;}catch(Exception error){Reject("Расположение не сохранено: "+error.Message);}};
        canvas.ConnectionSelected+=(kind,index)=>{if(kind=="switch"){tabs.SelectedIndex=1;switchList.SelectedIndex=index;}else if(kind=="mcast"){tabs.SelectedIndex=2;multicastList.SelectedIndex=index;}else{tabs.SelectedIndex=0;linksList.SelectedIndex=index;}};
        canvas.VmSelected+=name=>{tabs.SelectedIndex=3;ShowAdaptersFor(name);};
        var graph=new ScrollSurface(canvas){BackColor=Theme.Bg};
        var split=new SplitContainer{Dock=DockStyle.Fill,Size=new Size(1200,620),SplitterWidth=1,BackColor=Theme.Line};
        split.Panel1MinSize=390;split.Panel2MinSize=390;split.SplitterDistance=540;split.AccessibleName="Ширина схемы и настроек";split.TabStop=true;split.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Left||e.KeyCode==Keys.Right){split.SplitterDistance=Math.Max(split.Panel1MinSize,Math.Min(split.Width-split.Panel2MinSize-split.SplitterWidth,split.SplitterDistance+(e.KeyCode==Keys.Right?16:-16)));e.Handled=true;}};
        split.Panel1.BackColor=split.Panel2.BackColor=Theme.Bg;tabs.Dock=DockStyle.Fill;
        split.Panel1.Controls.Add(graph);split.Panel2.Controls.Add(tabs);snapshotStatus.Dock=DockStyle.Top;snapshotStatus.AutoSize=false;snapshotStatus.AutoEllipsis=true;snapshotStatus.Height=32;snapshotStatus.Padding=new Padding(18,6,8,0);snapshotStatus.TextChanged+=(s,e)=>snapshotStatus.AccessibleName=snapshotStatus.Text;split.Panel1.Controls.Add(snapshotStatus);
        var legend=Theme.Label("Схему можно перемещать мышью или Ctrl+стрелками. Расположение сохраняется отдельно; TCP, коммутатор и MCAST — после «Сохранить стенд».",9,Theme.Muted);
        legend.Dock=DockStyle.Top;legend.Padding=new Padding(18,8,10,3);legend.Height=42;legend.AutoSize=false;split.Panel1.Controls.Add(legend);
        tabs.AddLazy("TCP",()=>PreparePage("tcp",BuildTcp));
        tabs.AddLazy("Коммутаторы",()=>PreparePage("switches",()=>BuildGroups(true)));
        tabs.AddLazy("MCAST",()=>PreparePage("multicast",()=>BuildGroups(false)));
        tabs.AddLazy("Адаптеры",()=>PreparePage("adapters",BuildAdapters));
        tabs.AddLazy("Запуск",()=>PreparePage("startup",BuildOrder));
        tabs.AddLazy("Состояние",()=>PreparePage("status",BuildStatus));
        tabs.SelectionChanged+=(s,e)=>{if(tabs.SelectedIndex==3&&adaptersDirty)RefreshAdapters();};
        var zoomButtons=Theme.Flow();Button zoomValue=null;
        var zoomOut=Theme.Button("−",()=>{canvas.ZoomPercent-=25;zoomValue.Text=canvas.ZoomPercent+"%";});zoomOut.AccessibleName="Уменьшить схему";zoomButtons.Controls.Add(zoomOut);
        zoomValue=Theme.Button("100%",()=>{canvas.ZoomPercent=100;zoomValue.Text="100%";});zoomValue.AccessibleName="Сбросить масштаб схемы";zoomButtons.Controls.Add(zoomValue);
        var zoomIn=Theme.Button("+",()=>{canvas.ZoomPercent+=25;zoomValue.Text=canvas.ZoomPercent+"%";});zoomIn.AccessibleName="Увеличить схему";zoomButtons.Controls.Add(zoomIn);
        foreach(Button button in zoomButtons.Controls){button.AutoSize=false;button.MinimumSize=Size.Empty;button.Size=new Size(56,40);button.Padding=Padding.Empty;}
        zoomButtons.Dock=DockStyle.Bottom;split.Panel1.Controls.Add(zoomButtons);
        var buttons=new TableLayoutPanel{AutoSize=true,ColumnCount=2,RowCount=2};buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));notice.AutoSize=true;notice.TextAlign=ContentAlignment.MiddleLeft;notice.Dock=DockStyle.Fill;buttons.Controls.Add(notice,0,0);
        var footerActions=new FlowLayoutPanel{AutoSize=true,WrapContents=false,FlowDirection=FlowDirection.LeftToRight};footerActions.Controls.Add(Theme.Button("Отмена",Close));saveButton=Theme.Button("Сохранить стенд",Save,true);footerActions.Controls.Add(saveButton);buttons.Controls.Add(footerActions,1,0);
        var errorRow=new TableLayoutPanel{AutoSize=true,Dock=DockStyle.Top,ColumnCount=2,RowCount=1,Margin=Padding.Empty};errorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));errorRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));errorNotice.AccessibleName="Ошибка сети стенда";errorNotice.Margin=new Padding(0,8,12,0);errorRow.Controls.Add(errorNotice,0,0);var dismissError=Theme.Button("Скрыть ошибку",()=>{errorNotice.Text="";errorRow.Visible=false;});dismissError.Margin=Padding.Empty;errorRow.Controls.Add(dismissError,1,0);errorRow.Visible=false;errorNotice.TextChanged+=(s,e)=>errorRow.Visible=!String.IsNullOrEmpty(errorNotice.Text);buttons.Controls.Add(errorRow,0,1);buttons.SetColumnSpan(errorRow,2);
        SelectionMotion.AccentOnShow(errorRow,Theme.Red);
        buttons.SizeChanged+=(s,e)=>{notice.MaximumSize=new Size(Math.Max(120,buttons.ClientSize.Width-footerActions.Width-buttons.Padding.Horizontal-16),0);errorNotice.MaximumSize=new Size(Math.Max(120,buttons.ClientSize.Width-dismissError.Width-buttons.Padding.Horizontal-40),0);};
        MarkOpening("shell_controls");
        Theme.AuxiliaryDialog(this,split,buttons,"Сеть и соединения · "+J.S(original,"name"),"",compact:true);
        ApplyNetworkReference(split,graph,legend,zoomButtons,buttons);
        FormClosing+=(sender,args)=>{if(busy){args.Cancel=true;return;}if(dirty&&!allowClose&&!Program.TestMode&&MessageBox.Show(this,"Закрыть редактор без сохранения изменений?","Несохранённый черновик",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)args.Cancel=true;};
        MarkOpening("shell_styles_and_fonts");
        tabs.SelectedIndex=openStatus?5:0;
        Presentation.Presented+=RefreshStatus;
        MarkOpening("editor_constructor_end");
    }

    void MarkOpening(string phase){if(OpeningTiming!=null)OpeningTiming.Mark(phase);}
    Control PreparePage(string name,Func<Control> build){
        MarkOpening("page_"+name+"_start");
        var page=build();
        Theme.StyleAuxiliary(page);
        foreach(var label in AllNetworkLabels(page)){
            string tag=Convert.ToString(label.Tag);
            if(tag=="network-group")label.Font=UiTypography.Make(12,600);
            if(tag=="network-title")label.Font=UiTypography.Make(18,700);
            if(tag=="network-overline")label.Font=UiTypography.Make(9);
        }
        MarkOpening("page_"+name+"_end");
        return page;
    }

    void ApplyNetworkReference(SplitContainer split,Control graph,Label legend,Control zoom,TableLayoutPanel footer){
        var header=Controls.OfType<ReferenceHeader>().Single();header.Present("Сеть и соединения · "+J.S(draft,"name"),"Изменения применяются после проверки и сохранения. Настройки отдельных VM не переписываются.","МЕНЕДЖЕР СЕТЕЙ");header.NetworkPresentation();
        split.Padding=Padding.Empty;split.SplitterDistance=548;split.Panel1.Padding=new Padding(27,21,24,14);split.Panel1.BackColor=MotionPaint.Hex("#F3EFE7");split.Panel2.Padding=new Padding(26,13,26,15);tabs.NetworkPresentation();
        split.Panel1.Controls.Clear();var left=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,BackColor=split.Panel1.BackColor,Margin=Padding.Empty};left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));foreach(var height in new[]{48,28,34})left.RowStyles.Add(new RowStyle(SizeType.Absolute,height));left.RowStyles.Add(new RowStyle(SizeType.Percent,100));left.RowStyles.Add(new RowStyle(SizeType.Absolute,28));left.RowStyles.Add(new RowStyle(SizeType.Absolute,49));
        legend.Dock=DockStyle.Fill;legend.Padding=Padding.Empty;legend.Margin=Padding.Empty;legend.Font=UiTypography.Make(10);snapshotStatus.Dock=DockStyle.Fill;snapshotStatus.Padding=Padding.Empty;snapshotStatus.Margin=Padding.Empty;snapshotStatus.Font=UiTypography.Make(9);left.Controls.Add(legend,0,0);left.Controls.Add(snapshotStatus,0,1);
        var labels=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=Padding.Empty};labels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));labels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));var a=Theme.Label("ВИРТУАЛЬНЫЕ МАШИНЫ",9,Theme.Accent);var b=Theme.Label("СЕТИ И СОЕДИНЕНИЯ",9,Theme.Muted);a.Dock=b.Dock=DockStyle.Fill;b.TextAlign=ContentAlignment.MiddleRight;labels.Controls.Add(a);labels.Controls.Add(b,1,0);left.Controls.Add(labels,0,2);
        canvas.ReferenceNetworkMode=true;graph.Dock=DockStyle.Fill;graph.Padding=Padding.Empty;graph.Margin=Padding.Empty;graph.BackColor=split.Panel1.BackColor;graph.SizeChanged+=(s,e)=>{canvas.ReferenceViewportHeight=graph.ClientSize.Height;graph.PerformLayout();};left.Controls.Add(graph,0,3);var guest=Theme.Label("Связь внутри гостевых ОС: не проверена",9,Theme.Muted);guest.Dock=DockStyle.Fill;guest.Margin=new Padding(0,9,0,0);left.Controls.Add(guest,0,4);
        var localFooter=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=Padding.Empty};localFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,146));localFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));zoom.Dock=DockStyle.Fill;zoom.Padding=Padding.Empty;zoom.Margin=Padding.Empty;foreach(Control button in zoom.Controls){button.Size=new Size(button.Text=="100%"?54:36,35);button.Margin=new Padding(0,0,7,0);button.Padding=Padding.Empty;button.Font=UiTypography.Make(10);}if(zoom.Controls.Count>0)zoom.Controls[zoom.Controls.Count-1].Margin=Padding.Empty;notice.Parent=null;notice.Dock=DockStyle.Fill;notice.Font=UiTypography.Make(9);notice.AutoSize=false;notice.TextAlign=ContentAlignment.MiddleRight;localFooter.Controls.Add(zoom);localFooter.Controls.Add(notice,1,0);left.Controls.Add(localFooter,0,5);split.Panel1.Controls.Add(left);
        var meta=Theme.Label("ПАРАМЕТРЫ СЕТИ",9,Theme.Muted);meta.Dock=DockStyle.Fill;meta.AutoSize=false;meta.TextAlign=ContentAlignment.MiddleLeft;footer.Controls.Add(meta,0,0);footer.MinimumSize=new Size(0,76);footer.Padding=new Padding(28,15,28,15);saveButton.MinimumSize=new Size(159,37);
    }
    static IEnumerable<Label> AllNetworkLabels(Control root){foreach(Control child in root.Controls){var label=child as Label;if(label!=null)yield return label;foreach(var nested in AllNetworkLabels(child))yield return nested;}}

    Control BuildStatus(){
        diagnostics=Theme.Editor(statusText);
        diagnostics.ReadOnly=true;
        diagnostics.AccessibleName="Состояние сетей стенда, только чтение";
        var page=new Panel{Dock=DockStyle.Fill,BackColor=Theme.Bg,Padding=new Padding(14)};
        var refresh=Theme.Button("Обновить состояние",RefreshStatus);refresh.Dock=DockStyle.Top;
        var explanation=Theme.Label("Это срез сохранённого стенда. Связь внутри гостевых ОС не проверяется; черновик сети может отличаться.",9,Theme.Muted);
        explanation.Dock=DockStyle.Top;explanation.Height=54;explanation.AutoSize=false;
        diagnostics.Dock=DockStyle.Fill;page.Controls.Add(diagnostics);page.Controls.Add(explanation);page.Controls.Add(refresh);
        return page;
    }
    void SetDiagnostics(string text){
        statusText=text;
        if(diagnostics!=null)diagnostics.Text=text;
    }
    async void RefreshStatus(){
        if(statusPending||IsDisposed||ClosingWithMotion)return;
        if(statusProvider==null){SetDiagnostics("Диагностика доступна после открытия сохранённого стенда.");return;}
        statusPending=true;
        SetDiagnostics("Читаю QEMU и коммутаторы…");
        var cancellation=statusReadCancellation.Token;
        MarkOpening("diagnostics_start");
        try{
            var result=(Tuple<object,string,string>)await System.Threading.Tasks.Task.Run(()=>Backend.WithReadCancellation(()=>{
                var value=statusProvider();
                cancellation.ThrowIfCancellationRequested();
                string timestamp=DateTime.Now.ToString("HH:mm:ss");
                var lines=new List<string>{"Срез состояния · "+timestamp,"Связь внутри гостевых ОС: НЕ ПРОВЕРЕНА", "", "ВИРТУАЛЬНЫЕ МАШИНЫ"};
                foreach(var item in J.D(J.V(value,"members")))lines.Add(item.Key+" · "+Theme.State(Convert.ToString(item.Value)));
                var networks=J.A(J.V(value,"network_diagnostics"));lines.Add("");lines.Add("КОММУТАТОРЫ · "+networks.Length);
                foreach(var network in networks){lines.Add("");lines.Add(StandNetworksWindow.Describe(network));}
                return Tuple.Create(value,timestamp,String.Join("\r\n",lines));
            },cancellation));
            if(IsDisposed||ClosingWithMotion||!Visible)return;
            canvas.ApplyStatus(result.Item1);
            snapshotStatus.ForeColor=Theme.Muted;
            snapshotStatus.Text="Срез сохранённого стенда · "+result.Item2+" · схема может содержать черновик";
            SetDiagnostics(result.Item3);
        }catch(Exception error){
            if(!cancellation.IsCancellationRequested&&!IsDisposed&&!ClosingWithMotion&&Visible){
                canvas.ApplyStatus(null);
                snapshotStatus.ForeColor=Theme.Yellow;
                snapshotStatus.Text="Состояние не подтверждено · обновление не удалось";
                SetDiagnostics("Состояние не получено; прежний срез устарел: "+error.Message+"\r\nСохранённая схема не подтверждает наличие связи внутри гостевых ОС.");
                Reject("Не удалось обновить состояние сетей: "+error.Message);
            }
        }finally{statusPending=false;MarkOpening("diagnostics_end");}
    }

    public object Result(){
        StoreOrder();if(priorMulticast>=0&&priorMulticast<multicast.Count)StoreGroup(false,priorMulticast);ValidateConnections();draft["schema_version"]=4;draft["links"]=links.ToArray();draft["switches"]=switches.ToArray();draft["multicast"]=multicast.ToArray();draft["order"]=order.ToArray();draft["startup"]=startup;
        return draft;
    }
    protected override void Dispose(bool disposing){if(disposing&&!statusResourcesDisposed){statusResourcesDisposed=true;statusReadCancellation.Cancel();statusReadCancellation.Dispose();if(linkTips!=null)linkTips.Dispose();}base.Dispose(disposing);}
    public void OpenStatus(){tabs.SelectedIndex=5;}
    void Changed(){if(loading)return;dirty=true;notice.Text="● Несохранённые изменения";notice.ForeColor=Theme.Yellow;RefreshGraph();}
    void RefreshGraph(){draft["links"]=links.ToArray();draft["switches"]=switches.ToArray();draft["multicast"]=multicast.ToArray();canvas.Load(draft);adaptersDirty=true;if(tabs.SelectedIndex==3)RefreshAdapters();}
    public void SaveFailed(Exception error){busy=false;saveButton.Enabled=true;notice.ForeColor=Theme.Yellow;notice.Text="Изменения не сохранены";Reject("Не сохранено: "+error.Message);}
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
    void Reject(string text){errorNotice.Text=text;}
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

    static FlowLayoutPanel Page(){var stack=Theme.Flow(true);stack.Padding=new Padding(0,0,3,20);stack.BackColor=Theme.Bg;stack.SizeChanged+=(sender,args)=>{int width=Math.Max(180,stack.ClientSize.Width-stack.Padding.Horizontal-8);foreach(Control child in stack.Controls){if(child is Label){child.MaximumSize=new Size(width,0);}else if(child.Width!=width)child.Width=width;}};return stack;}
    static void Add(FlowLayoutPanel stack,Control control,int width=370){SetWidth(control,width);stack.Controls.Add(control);}
    Control BuildTcp(){
        linksEmpty=Theme.Label("TCP-соединений пока нет. Выбери два адаптера и нажми «Добавить TCP».",9,Theme.Muted);
        linksList=Theme.ListBox(10);from=Theme.ListBox(10);to=Theme.ListBox(10);
        fromChoice=Theme.Combo();toChoice=Theme.Combo();
        if(linkTips==null)linkTips=new ToolTip();
        var stack=Page();Add(stack,Section("01","Прямые TCP-соединения"));
        Add(stack,Theme.Label("Слушатель запускается первым. Выбери по одному адаптеру двух VM; другие сети этих VM останутся отдельными.",9,Theme.Muted));
        Add(stack,Caption("Соединения"));Add(stack,linksEmpty);linksList.Height=64;linksList.AccessibleName="TCP-соединения";Add(stack,linksList);linksList.SelectedIndexChanged+=(s,e)=>{SelectLink();UpdateLinkActions();};
        from.Height=96;from.AccessibleName="Адаптер слушателя";
        to.Height=96;to.AccessibleName="Адаптер подключаемой VM";
        var nicFields=Theme.Fields();Theme.Row(nicFields,"Слушатель / NIC",fromChoice);Theme.Row(nicFields,"Подключаемая VM / NIC",toChoice);nicFields.AutoSize=true;Add(stack,nicFields);
        from.SelectedIndexChanged+=(s,e)=>{if(!loading&&fromChoice.SelectedIndex!=from.SelectedIndex)fromChoice.SelectedIndex=from.SelectedIndex;};to.SelectedIndexChanged+=(s,e)=>{if(!loading&&toChoice.SelectedIndex!=to.SelectedIndex)toChoice.SelectedIndex=to.SelectedIndex;};
        fromChoice.SelectedIndexChanged+=(s,e)=>{if(!loading&&from.SelectedIndex!=fromChoice.SelectedIndex)from.SelectedIndex=fromChoice.SelectedIndex;};toChoice.SelectedIndexChanged+=(s,e)=>{if(!loading&&to.SelectedIndex!=toChoice.SelectedIndex)to.SelectedIndex=toChoice.SelectedIndex;};
        Disposed+=(s,e)=>{from.Dispose();to.Dispose();};
        foreach(var list in new[]{linksList,from,to})list.SelectedIndexChanged+=(s,e)=>{string selectedText=Convert.ToString(list.SelectedItem);list.AccessibleDescription=selectedText;linkTips.SetToolTip(list,selectedText);};
        var actions=Theme.Flow();actions.Controls.Add(Theme.Button("Добавить TCP",()=>ApplyLink(false),true));editLink=Theme.Button("Изменить",()=>ApplyLink(true));removeLink=Theme.Button("Убрать",RemoveLink,false,true);actions.Controls.Add(editLink);actions.Controls.Add(removeLink);Add(stack,actions);UpdateLinkActions();
        Add(stack,Theme.Label("TCP здесь означает внутренний сетевой канал QEMU между двумя VM, а не внешний порт гостевой ОС. Слушатель должен стоять раньше подключаемой VM в порядке запуска. IP-адреса настраиваются в гостях.",9,Theme.Muted));
        RefreshLinks();
        return new ScrollSurface(stack){BackColor=Theme.Bg};
    }
    void UpdateLinkActions(){if(editLink==null||removeLink==null)return;bool selected=linksList.SelectedIndex>=0;editLink.Enabled=removeLink.Enabled=selected;string reason=selected?"Действие для выбранного соединения.":"Выбери TCP-соединение в списке.";editLink.AccessibleDescription=removeLink.AccessibleDescription=reason;linkTips.SetToolTip(editLink,reason);linkTips.SetToolTip(removeLink,reason);}
    void RefreshLinks(){
        int selected=linksList.SelectedIndex, oldFrom=from.SelectedIndex, oldTo=to.SelectedIndex;
        var owners=EndpointOwners();
        var endpointLabels=endpoints.Select(end=>{
            string owner;
            return End(end)+(owners.TryGetValue(EndpointKey(end),out owner)?" · занят: "+owner:"");
        }).Cast<object>().ToArray();
        loading=true;
        try{
            ReplaceList(linksList,links.Select(LinkText).Cast<object>().ToArray(),Math.Min(selected,links.Count-1));
            linksEmpty.Visible=links.Count==0;linksList.Visible=links.Count>0;
            fromChoice.Items.ReplaceAll(endpointLabels);
            toChoice.Items.ReplaceAll(endpointLabels);
            ReplaceList(from,endpointLabels,oldFrom);
            ReplaceList(to,endpointLabels,oldTo);
            fromChoice.SelectedIndex=oldFrom;toChoice.SelectedIndex=oldTo;
        }finally{loading=false;}
        SelectLink();UpdateLinkActions();
    }
    static void ReplaceList(ThemedList list,object[] items,int selection){
        list.BeginUpdate();
        try{list.Items.Clear();list.Items.AddRange(items);list.SelectedIndex=selection;}
        finally{list.EndUpdate();}
    }
    static Tuple<string,string> EndpointKey(object end){return Tuple.Create(J.S(end,"vm"),J.S(end,"nic"));}
    Dictionary<Tuple<string,string>,string> EndpointOwners(){
        var owners=new Dictionary<Tuple<string,string>,string>();
        foreach(var link in links){
            owners[EndpointKey(J.V(link,"listen"))]="TCP-связь";
            owners[EndpointKey(J.V(link,"connect"))]="TCP-связь";
        }
        foreach(bool switched in new[]{true,false})foreach(var group in Groups(switched)){
            foreach(var end in J.A(J.V(group,"members"))){
                var key=EndpointKey(end);
                if(!owners.ContainsKey(key))owners.Add(key,switched?"коммутатор":"MCAST");
            }
        }
        return owners;
    }
    void SelectLink(){if(loading||linksList.SelectedIndex<0)return;object link=links[linksList.SelectedIndex];from.SelectedIndex=endpoints.FindIndex(end=>Same(end,J.V(link,"listen")));to.SelectedIndex=endpoints.FindIndex(end=>Same(end,J.V(link,"connect")));}
    void ApplyLink(bool replace){
        if(from.SelectedIndex<0||to.SelectedIndex<0||J.S(endpoints[from.SelectedIndex],"vm")==J.S(endpoints[to.SelectedIndex],"vm")){Reject("Выбери адаптеры двух разных VM.");if(from.SelectedIndex<0)fromChoice.Focus();else toChoice.Focus();return;}
        int index=linksList.SelectedIndex;if(replace&&index<0)return;
        foreach(object end in new[]{endpoints[from.SelectedIndex],endpoints[to.SelectedIndex]}){string owner=EndpointOwner(end,replace?"tcp":"",index);if(owner.Length>0){Reject(End(end)+" уже использует "+owner+". Выбери другой NIC.");return;}}
        var listener=endpoints[from.SelectedIndex];var connector=endpoints[to.SelectedIndex];if(order.IndexOf(J.S(listener,"vm"))>=order.IndexOf(J.S(connector,"vm"))){Reject("Слушатель должен запускаться раньше подключаемой VM. Измени порядок во вкладке «Запуск».");toChoice.Focus();return;}try{ValidateMacs(new[]{listener,connector},"TCP-связи");}catch(InvalidOperationException error){Reject(error.Message);fromChoice.Focus();return;}
        object value=J.O("id",replace?J.S(links[index],"id"):Guid.NewGuid().ToString("N"),"listen",endpoints[from.SelectedIndex],"connect",endpoints[to.SelectedIndex]);
        if(replace)links[index]=value;else{links.Add(value);index=links.Count-1;}
        RefreshLinks();linksList.SelectedIndex=index;Changed();
    }
    void RemoveLink(){int i=linksList.SelectedIndex;if(i<0)return;links.RemoveAt(i);RefreshLinks();Changed();}

    Control BuildGroups(bool switched){
        if(linkTips==null)linkTips=new ToolTip();
        if(switched){switchList=Theme.ListBox(10);switchName=Theme.Input();switchMembers=Theme.Flow(true);}
        else{multicastList=Theme.ListBox(10);address=Theme.Input();localAddress=Theme.Input();port=Theme.Number(20000,1024,65535);multicastMembers=Theme.Flow(true);}
        var stack=Page();var list=switched?switchList:multicastList;var members=switched?switchMembers:multicastMembers;
        Add(stack,Section(switched?"02":"03",switched?"Локальные коммутаторы":"Общие сети MCAST"));
        Add(stack,Theme.Label(switched?"Один коммутатор объединяет выбранные NIC в отдельную локальную L2-сеть.":"MCAST соединяет VM через multicast-группу; доступность зависит от сети Windows и firewall.",9,Theme.Muted));
        list.Height=64;list.AccessibleName=switched?"Локальные коммутаторы":"Сети MCAST";Add(stack,list);list.SelectedIndexChanged+=(s,e)=>{if(loading)return;if(!switched&&priorMulticast>=0&&priorMulticast<multicast.Count){try{StoreGroup(false,priorMulticast);}catch(InvalidOperationException error){Reject(error.Message);loading=true;list.SelectedIndex=priorMulticast;loading=false;return;}}if(!switched)priorMulticast=list.SelectedIndex;string selectedText=Convert.ToString(list.SelectedItem);list.AccessibleDescription=selectedText;linkTips.SetToolTip(list,selectedText);LoadGroup(switched);};
        var actions=Theme.Flow();actions.Controls.Add(Theme.Button(switched?"＋ Коммутатор":"＋ MCAST",()=>AddGroup(switched),true));actions.Controls.Add(Theme.Button("Убрать",()=>RemoveGroup(switched),false,true));Add(stack,actions);
        Add(stack,Caption(switched?"Название сети":"Адрес и порт MCAST"));
        if(switched){Add(stack,switchName);switchName.TextChanged+=(s,e)=>ChangeGroup(true);}
        else{Add(stack,address);Add(stack,port);Add(stack,Theme.Label("IPv4 интерфейса Windows (необязательно)",9,Theme.Muted));Add(stack,localAddress);foreach(Control field in new Control[]{address,port,localAddress})field.TextChanged+=(s,e)=>{try{ChangeGroup(false);}catch(InvalidOperationException error){Reject(error.Message);}};}
        Add(stack,Caption("Подключённые адаптеры"));members.WrapContents=false;Add(stack,members);
        Add(stack,Theme.Label("Выбери минимум две разные VM. Подключённый NIC будет занят этой сетью при запуске стенда; для NAT оставь отдельный адаптер.",9,Theme.Muted));
        RefreshGroups(switched);
        return new ScrollSurface(stack){BackColor=Theme.Bg};
    }
    List<object> Groups(bool switched){return switched?switches:multicast;}
    ThemedList GroupList(bool switched){return switched?switchList:multicastList;}
    void RefreshGroups(bool switched){
        var groups=Groups(switched);var list=GroupList(switched);int selected=list.SelectedIndex;loading=true;ReplaceList(list,groups.Select(group=>(object)GroupText(group,switched)).ToArray(),Math.Min(selected,groups.Count-1));if(!switched)priorMulticast=list.SelectedIndex;loading=false;LoadGroup(switched);
    }
    void AddGroup(bool switched){
        var group=switched?J.O("id",Guid.NewGuid().ToString("N"),"name","Новая общая сеть","members",new object[0]):J.O("id",Guid.NewGuid().ToString("N"),"address","239.192.0.1","port",20000,"localaddr","","members",new object[0]);
        Groups(switched).Add(group);RefreshGroups(switched);GroupList(switched).SelectedIndex=Groups(switched).Count-1;Changed();
    }
    void RemoveGroup(bool switched){int i=GroupList(switched).SelectedIndex;if(i<0)return;Groups(switched).RemoveAt(i);RefreshGroups(switched);Changed();}
    void LoadGroup(bool switched){
        int index=GroupList(switched).SelectedIndex;var members=switched?switchMembers:multicastMembers;foreach(Control control in members.Controls.Cast<Control>().ToArray())control.Dispose();members.Controls.Clear();
        bool active=index>=0;if(switched)switchName.Enabled=active;else address.Enabled=port.Enabled=localAddress.Enabled=active;
        if(!active){loading=true;if(switched)switchName.Text="";else{address.Text="";localAddress.Text="";}loading=false;return;}
        object group=Groups(switched)[index];loading=true;
        if(switched)switchName.Text=J.S(group,"name");else{address.Text=J.S(group,"address");port.Value=J.N(group,"port",20000);localAddress.Text=J.S(group,"localaddr");}
        var selected=J.A(J.V(group,"members"));
        string groupVm=null;foreach(object end in endpoints.Concat(selected.Where(m=>!endpoints.Any(e=>Same(e,m))))){
            string vmName=J.S(end,"vm");if(groupVm!=vmName){groupVm=vmName;var memberHeading=Theme.Label(vmName,10,null,true);memberHeading.MaximumSize=new Size(Math.Max(180,members.Width-20),0);memberHeading.Margin=new Padding(0,8,0,2);members.Controls.Add(memberHeading);}
            bool available=endpoints.Any(e=>Same(e,end));string occupied=EndpointOwner(end,switched?"switch":"mcast",index);string suffix=!available?" · VM/NIC отсутствует; сними отметку, чтобы исключить":occupied.Length>0?" · занят: "+occupied:"";var check=new CheckBox{Text=End(end)+suffix,Checked=selected.Any(e=>Same(e,end)),Enabled=true,AutoSize=true,ForeColor=available?Theme.Text:Theme.Red,BackColor=Theme.Bg,Margin=new Padding(0,5,0,5)};
            object captured=end;check.CheckedChanged+=(s,e)=>{if(loading)return;int current=GroupList(switched).SelectedIndex;if(current<0)return;if(check.Checked){if(!available){loading=true;check.Checked=false;loading=false;Reject("Адаптер отсутствует. Восстанови его в библиотеке VM.");return;}string owner=EndpointOwner(captured,switched?"switch":"mcast",current);if(owner.Length>0){loading=true;check.Checked=false;loading=false;Reject(End(captured)+" уже использует "+owner+". Выбери другой NIC.");return;}}var data=J.D(Groups(switched)[current]);var list=J.A(J.V(data,"members")).Where(m=>!Same(m,captured)).ToList();if(check.Checked){list.Add(captured);try{ValidateMacs(list,switched?"коммутаторе":"MCAST");}catch(InvalidOperationException error){loading=true;check.Checked=false;loading=false;Reject(error.Message);return;}}data["members"]=list.ToArray();Changed();};members.Controls.Add(check);
        }
        loading=false;
    }
    void ChangeGroup(bool switched){
        if(loading)return;int i=GroupList(switched).SelectedIndex;if(i<0)return;StoreGroup(switched,i);
    }
    void StoreGroup(bool switched,int i){var data=J.D(Groups(switched)[i]);int value=switched?0:(int)port.Value;string before=J.Json().Serialize(data);if(switched)data["name"]=switchName.Text;else{data["address"]=address.Text;data["port"]=value;data["localaddr"]=localAddress.Text;}if(J.Json().Serialize(data)==before)return;GroupList(switched).Items[i]=GroupText(data,switched);Changed();}

    Control BuildOrder(){
        orderList=Theme.ListBox(10);delay=Theme.Number(0,0,300);timeout=Theme.Number(60,1,600);readyPort=Theme.Number(2222,1024,65535);
        process=Radio("Процесс и QMP");qmp=Radio("QMP готов");tcp=Radio("TCP-сервис на хосте");
        var stack=Page();Add(stack,Section("05","Очередность запуска"));Add(stack,Theme.Label("Порядок влияет на подключение TCP и готовность участников. Сеть сама не запускает VM.",9,Theme.Muted));
        orderList.Height=160;Add(stack,orderList);orderList.SelectedIndexChanged+=(s,e)=>{if(loading)return;try{StoreOrder();LoadOrder();}catch(InvalidOperationException error){Reject(error.Message);loading=true;orderList.SelectedIndex=priorOrder;loading=false;}};
        var actions=Theme.Flow();actions.Controls.Add(Theme.Button("Выше",()=>MoveOrder(-1)));actions.Controls.Add(Theme.Button("Ниже",()=>MoveOrder(1)));Add(stack,actions);
        Add(stack,Caption("Условие готовности"));Add(stack,process);Add(stack,qmp);Add(stack,tcp);
        var readyCaption=Theme.Label("Локальный порт TCP-сервиса на хосте",9,Theme.Muted);Add(stack,readyCaption);Add(stack,readyPort);
        foreach(var radio in new[]{process,qmp,tcp})radio.CheckedChanged+=(s,e)=>{readyPort.Enabled=readyPort.Visible=readyCaption.Visible=tcp.Checked;if(!loading)try{StoreOrder();}catch(InvalidOperationException error){Reject(error.Message);}};
        readyPort.Enabled=readyPort.Visible=readyCaption.Visible=tcp.Checked;
        Add(stack,Theme.Label("Задержка перед запуском, с",9,Theme.Muted));Add(stack,delay);
        Add(stack,Theme.Label("Тайм-аут готовности, с",9,Theme.Muted));Add(stack,timeout);
        foreach(var number in new[]{readyPort,delay,timeout})number.TextChanged+=(s,e)=>{if(!loading)try{StoreOrder();}catch(InvalidOperationException){}};
        RefreshOrder();
        return new ScrollSurface(stack){BackColor=Theme.Bg};
    }
    Control BuildAdapters(){
        adapterStack=Page();
        adapterScroll=new ScrollSurface(adapterStack){BackColor=Theme.Bg};
        RefreshAdapters();
        return adapterScroll;
    }
    void RefreshAdapters(){
        var owners=EndpointOwners();
        int offset=adapterScroll.ScrollOffset;
        var stack=adapterStack;stack.SuspendLayout();foreach(Control child in stack.Controls.Cast<Control>().ToArray())child.Dispose();stack.Controls.Clear();Add(stack,Section("04","Адаптеры участников"));
        Add(stack,Theme.Label("Все адаптеры VM показаны здесь, включая NAT, TAP и прямые сокеты. Редактор стенда назначает только связи выбранным NIC; независимые адаптеры сохраняют настройки своей VM.",9,Theme.Muted));
        foreach(string name in order){
            var heading=Caption(name);heading.Tag="network-vm";heading.AccessibleDescription=name;heading.ForeColor=name==selectedVm?Theme.Accent:Theme.Text;Add(stack,heading);object data=J.V(vmRow(name),"data");
            if(data==null){Add(stack,Theme.Label("VM отсутствует в библиотеке.",9,Theme.Red));continue;}
            var nics=J.A(J.V(data,"network_adapters"));
            if(nics.Length==0){Add(stack,Theme.Label("Сетевых адаптеров нет. Добавь NIC в параметрах VM, затем заново открой сеть стенда.",9,Theme.Yellow));continue;}
            foreach(object nic in nics){string owner;owners.TryGetValue(Tuple.Create(name,J.S(nic,"id")),out owner);owner=owner??"";string type=J.S(nic,"type","user");string mode=J.S(nic,"mode");
                string title=J.S(nic,"id")+" · "+(owner.Length>0?"занят: "+owner:type=="user"?"NAT":type=="tap"?"TAP":type=="socket"?"Сокет "+mode:type);
                Add(stack,new NetworkAdapterReferenceCard(title,J.S(nic,"id"),J.S(nic,"model"),J.S(nic,"mac","не задан"),owner));
            }
        }
        Add(stack,Theme.Label("Для изменения параметров самого адаптера выбери VM в библиотеке и открой «Параметры». Сохранённый стенд не переписывает конфигурацию отдельных машин.",9,Theme.Muted));
        stack.ResumeLayout(true);
        adapterScroll.ScrollOffset=offset;
        adaptersDirty=false;
    }
    void ShowAdaptersFor(string name){
        selectedVm=name;
        Label heading=null;
        foreach(var label in adapterStack.Controls.OfType<Label>().Where(c=>Equals(c.Tag,"network-vm"))){
            bool selected=label.AccessibleDescription==name;
            label.ForeColor=selected?Theme.Accent:Theme.Text;
            if(selected)heading=label;
        }
        if(heading!=null){
            adapterScroll.PerformLayout();
            adapterScroll.ScrollOffset=Math.Min(adapterScroll.ScrollBar.Maximum,Math.Max(0,heading.Top-60));
        }
    }
    void RefreshOrder(){loading=true;int selected=orderList.SelectedIndex;ReplaceList(orderList,order.Cast<object>().ToArray(),selected>=0&&selected<order.Count?selected:order.Count>0?0:-1);loading=false;LoadOrder();}
    void LoadOrder(){if(loading)return;priorOrder=orderList.SelectedIndex;if(priorOrder<0)return;object step;startup.TryGetValue(order[priorOrder],out step);loading=true;delay.Value=J.N(step,"delay");timeout.Value=J.N(step,"timeout",60);readyPort.Value=J.N(step,"tcp_port",2222);string ready=J.S(step,"ready","qmp");process.Checked=ready=="process";qmp.Checked=ready=="qmp";tcp.Checked=ready=="tcp";loading=false;}
    void StoreOrder(){if(loading||priorOrder<0||priorOrder>=order.Count)return;string ready=tcp.Checked?"tcp":process.Checked?"process":"qmp";int d=(int)delay.Value,t=(int)timeout.Value,p=ready=="tcp"?(int)readyPort.Value:2222;object old=J.V(startup,order[priorOrder]);if(J.N(old,"delay")==d&&J.N(old,"timeout",60)==t&&J.S(old,"ready","qmp")==ready&&(ready!="tcp"||J.N(old,"tcp_port",2222)==p))return;var value=J.O("delay",d,"timeout",t,"ready",ready);if(ready=="tcp")value["tcp_port"]=p;startup[order[priorOrder]]=value;Changed();}
    void MoveOrder(int direction){int i=orderList.SelectedIndex,j=i+direction;if(i<0||j<0||j>=order.Count)return;StoreOrder();string name=order[i];order.RemoveAt(i);order.Insert(j,name);priorOrder=-1;RefreshOrder();orderList.SelectedIndex=j;Changed();}
}

sealed class NetworkAdapterReferenceCard : Control {
    readonly string title,id,model,mac,owner;
    public NetworkAdapterReferenceCard(string title,string id,string model,string mac,string owner){this.title=title;this.id=id;this.model=model;this.mac=mac;this.owner=owner;Height=owner.Length>0?110:94;Text=title+" NIC "+id+" Модель "+model+" MAC "+mac;AccessibleName=Text;AccessibleRole=AccessibleRole.StaticText;SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);var g=e.Graphics;g.Clear(Parent==null?Theme.Bg:Parent.BackColor);g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;using(var path=Drawing.Rounded(new Rectangle(0,0,Width-1,Height-1),8))using(var fill=new SolidBrush(MotionPaint.Hex("#f9f5ed")))using(var border=new Pen(MotionPaint.Hex("#c7bcac"))){g.FillPath(fill,path);g.DrawPath(border,path);}using(var font=UiTypography.Make(11,600))TextRenderer.DrawText(g,title,font,new Rectangle(14,13,Math.Max(1,Width-28),20),Theme.Text,TextFormatFlags.EndEllipsis);using(var font=UiTypography.Make(10)){TextRenderer.DrawText(g,"NIC: "+id+" · Модель: "+model,font,new Rectangle(14,38,Math.Max(1,Width-28),18),Theme.Muted);TextRenderer.DrawText(g,"MAC: "+mac,font,new Rectangle(14,57,Math.Max(1,Width-28),18),Theme.Muted);if(owner.Length>0)TextRenderer.DrawText(g,"занят: "+owner,font,new Rectangle(14,78,Math.Max(1,Width-28),18),Theme.Accent);}}
}


// Opt-in profiling used by the Windows benchmark. Values are offset from the click.
sealed class NetworkOpenTiming {
    internal static Action<string,double> Observer { get; set; }
    readonly Action<string,double> observe;
    readonly Stopwatch watch = Stopwatch.StartNew();

    NetworkOpenTiming(Action<string,double> observe){this.observe=observe;}
    internal static NetworkOpenTiming Start(){return Observer==null?null:new NetworkOpenTiming(Observer);}
    internal static NetworkOpenTiming For(Form form){var editor=form as StandNetworkEditorWindow;return editor==null?null:editor.OpeningTiming;}
    internal void Mark(string phase){observe(phase,watch.Elapsed.TotalMilliseconds);}
}
