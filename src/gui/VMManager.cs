using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

static class J {
    public static Dictionary<string,object> D(object value) { return value as Dictionary<string,object> ?? new Dictionary<string,object>(); }
    public static object V(object value, string key) { object result; return D(value).TryGetValue(key,out result) ? result : null; }
    public static string S(object value, string key, string fallback="") { var result=V(value,key); return result==null ? fallback : Convert.ToString(result); }
    public static int N(object value, string key, int fallback=0) { int result; return Int32.TryParse(S(value,key),out result) ? result : fallback; }
    public static long L(object value, string key, long fallback=0) { long result; return Int64.TryParse(S(value,key),out result) ? result : fallback; }
    public static bool B(object value, string key) { return V(value,key) is bool && (bool)V(value,key); }
    public static object[] A(object value) { var sequence=value as IEnumerable; return sequence==null || value is string ? new object[0] : sequence.Cast<object>().ToArray(); }
    public static Dictionary<string,object> O(params object[] pairs) { var result=new Dictionary<string,object>(); for(int i=0;i<pairs.Length;i+=2) result[(string)pairs[i]]=pairs[i+1]; return result; }
    public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength=16*1024*1024, RecursionLimit=80 }; }
    public static string Quote(string value) {
        var result=new StringBuilder("\""); int slashes=0;
        foreach(char item in value) { if(item=='\\') { slashes++; continue; } if(item=='"') { result.Append('\\',slashes*2+1).Append('"'); slashes=0; continue; } result.Append('\\',slashes).Append(item); slashes=0; }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
}

sealed class Backend : IDisposable {
    readonly object gate=new object(); Process process; StreamWriter input; int next; readonly string root,python;
    readonly BackendRequestQueue requests=new BackendRequestQueue();
    [ThreadStatic]static CancellationToken readCancellation;
    internal static object WithReadCancellation(Func<object> work,CancellationToken cancellation){var previous=readCancellation;readCancellation=cancellation;try{return work();}finally{readCancellation=previous;}}
    public int Pid { get { return process==null ? 0 : process.Id; } }
    public Backend(string root,string python=null) { this.root=root; this.python=python; }
    IEnumerable<string[]> Candidates() {
        if(!String.IsNullOrWhiteSpace(python)) { yield return new[]{python}; yield break; }
        string bundled=Path.Combine(root,"runtime","python.exe"); if(File.Exists(bundled)) yield return new[]{bundled};
        foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine}) {
            using(var core=hive.OpenSubKey(@"Software\Python\PythonCore")) {
                if(core==null) continue;
                foreach(string name in core.GetSubKeyNames().OrderByDescending(item=>item)) {
                    using(var install=core.OpenSubKey(name+@"\InstallPath")) {
                        if(install==null) continue;
                        string path=install.GetValue("ExecutablePath") as string;
                        if(String.IsNullOrWhiteSpace(path)) path=Path.Combine(install.GetValue("") as string ?? "", "python.exe");
                        if(File.Exists(path)) yield return new[]{path};
                    }
                }
            }
        }
        foreach(string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) {
            string folder=Environment.ExpandEnvironmentVariables(directory.Trim('"'));
            if(folder.IndexOf("WindowsApps",StringComparison.OrdinalIgnoreCase)>=0) continue;
            string path; try { path=Path.Combine(folder,"python.exe"); } catch(ArgumentException) { continue; }
            if(File.Exists(path)) yield return new[]{path};
        }
        yield return new[]{"py.exe","-3"};
    }
    public void Open() {
        var errors=new List<string>();
        foreach(var candidate in Candidates()) {
            try {
                string script=Path.Combine(AlphaIntegration.AppRoot(root),"vm_backend.py");
                var args=candidate.Skip(1).Concat(new[]{"-B","-X","utf8",script,"--root",root});
                var start=new ProcessStartInfo(candidate[0],String.Join(" ",args.Select(J.Quote))) {
                    UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                    RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
                    StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false),WorkingDirectory=root
                };
                process=Process.Start(start);
                input=new StreamWriter(process.StandardInput.BaseStream,new UTF8Encoding(false));input.AutoFlush=true;
                process.ErrorDataReceived+=(sender,eventArgs)=>{}; process.BeginErrorReadLine();
                input.WriteLine(J.Json().Serialize(J.O("id",++next,"command","ping","args",J.O())));
                var ready=process.StandardOutput.ReadLineAsync();
                if(!ready.Wait(10000) || ready.Result==null || !J.B(J.Json().DeserializeObject(ready.Result),"ok")) throw new IOException("Python не запустил фоновую службу.");
                return;
            } catch(Exception error) {
                errors.Add(error.Message);
                // Startup only: no guest operation has been submitted to this child.
                if(process!=null) { try { if(!process.HasExited) process.Kill(); process.WaitForExit(2000); } catch(InvalidOperationException) {} process.Dispose(); process=null; }
            }
        }
        throw new IOException("Не удалось запустить Python для менеджера.\r\n"+String.Join("\r\n",errors.Distinct()));
    }
    internal static int ReadTimeoutMilliseconds=15000,WriteTimeoutMilliseconds=120000,QueueTimeoutMilliseconds=15000;
    Task<string> pendingReply;int pendingId;bool pendingWrite;
    static bool ReadOnlyCommand(string command){return new[]{"ping","list","states","logs","snapshots","stand_list","stand_activity","stand_overview","stand_status","snapshot_job_status","doctor","snapshot_history","get","template_list","network_validate","stand_report_preview","delete_plan"}.Contains(command);}
    object Receive(int timeout){
        if(!pendingReply.Wait(timeout))throw new TimeoutException(pendingWrite?"Служба не подтвердила результат операции. Перед повтором проверь журнал и обнови состояние; операция может продолжаться.":"Служба не ответила за отведённое время. Повтори чтение; работающие машины продолжают работу.");
        string line=pendingReply.GetAwaiter().GetResult();pendingReply=null;
        if(line==null)throw new IOException("Нет ответа фоновой службы. Работающие машины продолжают работу.");
        object reply=J.Json().DeserializeObject(line);if(J.N(reply,"id")!=pendingId)throw new IOException("Нарушен порядок ответов службы.");return reply;
    }
    public object Call(string command,object arguments=null) {return CallAsync(command,arguments).GetAwaiter().GetResult();}
    internal Task<object> CallAsync(string command,object arguments=null,bool background=false,CancellationToken cancellation=default(CancellationToken)){
        bool readOnly=ReadOnlyCommand(command);
        if(readOnly&&!cancellation.CanBeCanceled)cancellation=readCancellation;
        if(!readOnly&&cancellation.CanBeCanceled)throw new InvalidOperationException("Отмена чтения не применяется к операциям записи.");
        string serialized=J.Json().Serialize(arguments??J.O());
        return requests.Enqueue(command+"|"+serialized,readOnly,background,()=>CallTransport(command,J.Json().DeserializeObject(serialized)),QueueTimeoutMilliseconds,cancellation);
    }
    object CallTransport(string command,object arguments) {
        if(!System.Threading.Monitor.TryEnter(gate,QueueTimeoutMilliseconds))throw new TimeoutException("Фоновая служба занята другим запросом. Повтори чтение через несколько секунд.");
        try{
            if(process==null||process.HasExited)throw new IOException("Фоновая служба завершилась. Открой менеджер заново; работающие машины продолжат работу.");
            // Drain a late reply before submitting another request. Never resend a write.
            if(pendingReply!=null)Receive(ReadTimeoutMilliseconds);
            pendingId=++next;pendingWrite=!ReadOnlyCommand(command);
            input.WriteLine(J.Json().Serialize(J.O("id",pendingId,"command",command,"args",arguments??J.O())));
            pendingReply=process.StandardOutput.ReadLineAsync();
            object reply=Receive(pendingWrite?WriteTimeoutMilliseconds:ReadTimeoutMilliseconds);
            if(!J.B(reply,"ok"))throw new InvalidOperationException(J.S(reply,"error","Неизвестная ошибка."));return J.V(reply,"result");
        }finally{System.Threading.Monitor.Exit(gate);}
    }
    public void Dispose() {
        requests.Stop(CloseTransport);
    }
    void CloseTransport(){
        // EOF requests orderly shutdown. Never kill a backend doing a disk operation.
        if(input!=null) { try { input.Close(); } catch(IOException) {} catch(InvalidOperationException) {} }
        if(process!=null) { try { process.WaitForExit(2000); } catch(InvalidOperationException) {} }
    }
}

class BufferedPanel : Panel {
    public BufferedPanel(){DoubleBuffered=true;ResizeRedraw=true;}
}
sealed class BufferedFlowLayoutPanel : FlowLayoutPanel {
    public BufferedFlowLayoutPanel(){DoubleBuffered=true;ResizeRedraw=true;}
}

sealed partial class MainWindow : SmoothForm {
    public Backend Backend; public bool Busy; internal int SnapshotJobs; public string RootPath; internal Exception TestError; readonly bool demo;
    List<object> rows=new List<object>(); Dictionary<string,string> states=new Dictionary<string,string>();
    DateTime lastStateUtc=DateTime.MinValue;bool stateReadFailed;int stateGeneration;
    string selected,mode="normal"; bool updatingMode,polling,closing; readonly string python;
    readonly FlowLayoutPanel cards=new VirtualLibraryPanel(), actions=Theme.Flow(); readonly Panel detail=new Panel();
    readonly Label heading=Theme.Label("Выбери виртуальную машину",18,null,true), description=Theme.Label("",10,Theme.Muted), status=Theme.Label("",10), footer=Theme.Label("Загрузка…",9,Theme.Muted);
    readonly ResourceSummary resources=new ResourceSummary();
    float layoutScale=1F;
    protected override void ScaleControl(SizeF factor,BoundsSpecified specified){layoutScale*=factor.Width;base.ScaleControl(factor,specified);}
    EmbeddedToolPage liveLogPage,snapshotPage;AdapterCards adapterCards;SnapshotsWindow bootSnapshots;string bootSnapshotsName;
    StateStage machineStage;LibraryModeSwitch libraryMode;
    ConfigurationInspector machineInspector;readonly Label machineStateText=Theme.Label("",12,Theme.Muted);
    readonly Label mediaSummary=Theme.Label("",10,Theme.Muted);
    readonly Button startButton,pauseButton,shutdownButton,logsButton,consoleButton;
    internal readonly AppearanceStore Visuals;readonly MonitorEmblem emblem=new MonitorEmblem();string visualKey,emblemFileVersion;Distro emblemDistro;int visualRequest,appearanceEpoch;
    readonly FieldInput search=new FieldInput{SearchIcon=true,Placeholder="Поиск машин…"}; readonly LaunchModeSelector launchMode=new LaunchModeSelector();
    Panel libraryHeaderPanel;Button searchButton;bool searchOpen;
    readonly ScrollSurface libraryScroll;
    readonly WorkspaceTabs workspace=new WorkspaceTabs();
    readonly Label libraryCount=Theme.Label("0",9,Theme.Muted),breadcrumb=Theme.Label("Виртуальные машины",9,Theme.Muted);
    readonly ThemedEditor networkPreview=Theme.Editor();
    readonly ProgressBar progress=new ProgressBar { Style=ProgressBarStyle.Marquee,Dock=DockStyle.Bottom,Height=3,Visible=false };
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer { Interval=2500 };
    readonly System.Windows.Forms.Timer searchDebounce;
    readonly Dictionary<string,LogsWindow> logs=new Dictionary<string,LogsWindow>();
    readonly System.ComponentModel.IContainer uiComponents=new System.ComponentModel.Container();
    ToolTip actionTips;
    readonly ContextMenuStrip machineMenu;
    public List<object> Rows { get { return rows; } }
    public MainWindow(string root,string python=null,bool demo=false,bool autoStart=true) {
        searchDebounce=new System.Windows.Forms.Timer(uiComponents){Interval=120};searchDebounce.Tick+=(s,e)=>{searchDebounce.Stop();RenderCards();};
        RootPath=root;this.python=python;this.demo=demo;Visuals=new AppearanceStore(root);
        Visuals.Changed+=(sender,args)=>{appearanceEpoch++;visualKey=null;RefreshEmblem(selected,J.V(Row(selected),"data"));Post(RenderCards);};
        machineMenu=BuildMenu("Диагностика VM",(Action)(()=>RunDoctor(selected)),"Перезагрузка",(Action)(()=>ControlVm(selected,"reset")),"Остановить QEMU",(Action)(()=>ControlVm(selected,"quit")),"Удалить VM",(Action)(()=>DeleteVm(selected)));
        Text="QEMU VM Manager";Theme.Form(this,1280,800);MinimumSize=new Size(920,680);
        var working=Screen.FromPoint(Cursor.Position).WorkingArea;MinimumSize=new Size(Math.Min(920,working.Width),Math.Min(680,working.Height));Size=new Size(Math.Min(Width,working.Width),Math.Min(Height,working.Height));if(!Program.TestMode){StartPosition=FormStartPosition.Manual;Location=new Point(working.Left+(working.Width-Width)/2,working.Top+(working.Height-Height)/2);}
        var top=new BufferedPanel { Dock=DockStyle.Top,Height=78,Padding=new Padding(24,0,24,0) };
        var section=Theme.Label("",8,Theme.Muted);section.Dock=DockStyle.Top;section.Height=34;section.AutoSize=false;top.Controls.Add(section);
        var support=Theme.Flow();support.Dock=DockStyle.Right;support.AutoSize=false;support.Width=160;support.WrapContents=false;support.FlowDirection=FlowDirection.RightToLeft;
        var serviceButton=Theme.Button("Сервис",()=>ShowService());serviceButton.Margin=Padding.Empty;serviceButton.Padding=Padding.Empty;serviceButton.MinimumSize=Size.Empty;serviceButton.AutoSize=false;serviceButton.Size=new Size(118,30);support.Controls.Add(serviceButton);
        support.Dock=DockStyle.Bottom;support.Height=36;top.Controls.Add(support);breadcrumb.Dock=DockStyle.Bottom;breadcrumb.Height=36;breadcrumb.AutoSize=false;breadcrumb.TextAlign=ContentAlignment.MiddleLeft;top.Controls.Add(breadcrumb);
        var bottom=new Panel { Dock=DockStyle.Bottom,Height=32,Padding=new Padding(20,7,16,0) };
        footer.Dock=DockStyle.Fill;bottom.Controls.Add(footer);bottom.Controls.Add(progress);
        SelectionMotion.AccentOnShow(bottom,Theme.Line);
        // Status text changes inside a permanently reserved strip. Reads and
        // writes must never resize the workspace while a dialog is opening.
        bottom.Visible=true;
        var content=new SplitContainer { Dock=DockStyle.Fill,Size=new Size(1000,600),SplitterWidth=1,IsSplitterFixed=true,BackColor=Theme.Line,Panel1MinSize=195,Panel2MinSize=420,SplitterDistance=238 };
        var left=new BufferedPanel { Dock=DockStyle.Fill,Padding=new Padding(14,25,14,20),BackColor=Theme.Panel };
        var libraryHeader=new Panel { Dock=DockStyle.Top,Height=175,BackColor=Theme.Panel };libraryHeaderPanel=libraryHeader;
        var category=Theme.Label("",8,Theme.Muted);category.Location=new Point(9,0);category.Visible=false;
        var library=Theme.Label("Мои машины",20,null,true);library.Location=new Point(9,22);
        libraryCount.Location=new Point(9,56);libraryCount.ForeColor=Theme.Muted;
        search.Dock=DockStyle.None;search.SetBounds(9,80,208,40);search.AccessibleName="Поиск машин";
        search.TextChanged+=(sender,args)=>{searchDebounce.Stop();if(rows.Count<100&&stands.Count<100)RenderCards();else searchDebounce.Start();};
        search.Visible=true;searchOpen=true;searchButton=Theme.Button("Поиск",()=>SetSearchVisible(!searchOpen));searchButton.AccessibleName="Открыть или закрыть поиск";searchButton.AutoSize=false;searchButton.MinimumSize=Size.Empty;searchButton.SetBounds(132,0,90,32);searchButton.Padding=Padding.Empty;searchButton.BackColor=Theme.Panel;
        searchButton.Visible=false;KeyPreview=true;KeyDown+=(sender,args)=>{if(args.Control&&args.KeyCode==Keys.F){SetSearchVisible(true);args.Handled=true;args.SuppressKeyPress=true;}else if(args.KeyCode==Keys.Escape&&searchOpen){if(search.Text.Length>0)search.Text="";else SetSearchVisible(false);args.Handled=true;args.SuppressKeyPress=true;}};
        var libraryTips=new ToolTip(uiComponents);libraryTips.SetToolTip(searchButton,"Поиск · Ctrl+F; закрытие очищает фильтр");
        libraryHeader.Controls.Add(category);libraryHeader.Controls.Add(library);libraryHeader.Controls.Add(libraryCount);libraryHeader.Controls.Add(search);
        cards.WrapContents=false;cards.BackColor=Theme.Panel;
        libraryScroll=new ScrollSurface(cards) { BackColor=Theme.Panel };
        cards.SizeChanged+=(sender,args)=>{foreach(Control card in cards.Controls){int width=Math.Max(1,cards.Width-card.Margin.Horizontal);if(standMode&&card is Button&&card.MaximumSize.Width!=width)card.MaximumSize=new Size(width,0);if(card.Width!=width)card.Width=width;}};
        var tools=Theme.Flow(true);tools.Dock=DockStyle.Bottom;tools.BackColor=Theme.Panel;tools.Padding=new Padding(9,20,0,0);
        var add=Theme.Button("Новая машина",()=>{if(!standMode)CreateVm();});add.AutoSize=false;add.Width=208;tools.Controls.Add(add);
        left.Controls.Add(libraryScroll);left.Controls.Add(libraryHeader);left.Controls.Add(tools);
        detail.Dock=DockStyle.Fill;detail.Padding=new Padding(24,0,24,28);detail.BackColor=Theme.Bg;

        breadcrumb.Visible=false;breadcrumb.Margin=Padding.Empty;
        var hero=new TableLayoutPanel {AutoSize=true,Dock=DockStyle.Top,ColumnCount=2,RowCount=2,Padding=new Padding(0,0,0,18),Margin=Padding.Empty};
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,54));hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        hero.RowStyles.Add(new RowStyle(SizeType.AutoSize));hero.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        heading.Font=MotionPaint.Font("Arial",22,FontStyle.Bold);emblem.Size=new Size(40,40);emblem.Margin=new Padding(0,0,11,0);hero.Controls.Add(emblem,0,0);hero.SetRowSpan(emblem,2);
        heading.Margin=new Padding(0,0,0,5);hero.Controls.Add(heading,1,0);
        status.AutoSize=true;status.Margin=Padding.Empty;hero.Controls.Add(status,1,1);
        description.Visible=false;

        actions.FlowDirection=FlowDirection.TopDown;actions.WrapContents=false;actions.Width=680;
        var primary=Theme.Flow();
        startButton=Theme.Button("▶ Запустить",()=>StartVm(selected),true);startButton.MinimumSize=new Size(0,33);primary.Controls.Add(startButton);
        actionTips=new ToolTip(uiComponents);
        consoleButton=Theme.Button("Открыть консоль",()=>{},true);consoleButton.Enabled=false;((PolarButton)consoleButton).IconName="monitor";consoleButton.AccessibleDescription="Открытие гостевой консоли пока не поддерживается приложением.";actionTips.SetToolTip(consoleButton,consoleButton.AccessibleDescription);primary.Controls.Add(consoleButton);
        pauseButton=Theme.Button("Пауза / продолжить",()=>ControlVm(selected,"toggle"));primary.Controls.Add(pauseButton);
        shutdownButton=Theme.Button("Выключить",()=>{var menu=BuildMenu("Выключить",(Action)(()=>ControlVm(selected,"shutdown")),"Перезагрузить",(Action)(()=>ControlVm(selected,"reset")),"Принудительно выключить",(Action)(()=>ControlVm(selected,"quit")));menu.Closed+=(s,e)=>menu.Dispose();menu.Show(shutdownButton,new Point(0,shutdownButton.Height));});primary.Controls.Add(shutdownButton);((PolarButton)shutdownButton).IconName="power";((PolarButton)shutdownButton).TrailingIcon="chevron-down";


        logsButton=Theme.Button("Журналы",()=>{workspace.SelectedIndex=2;});
        Button moreActions=null;moreActions=Theme.Button("Ещё…",()=>ShowMachineMenu(moreActions));((PolarButton)moreActions).Quiet=true;primary.Controls.Add(moreActions);
        foreach(Button button in primary.Controls.OfType<Button>()){button.Font=MotionPaint.Font("Arial",10);button.Padding=new Padding(13,8,13,8);button.MinimumSize=new Size(0,33);}
        actions.Controls.Add(primary);
        var launchLabel=Theme.Label("",9,Theme.Muted);launchLabel.Visible=false;launchLabel.Margin=new Padding(0,0,0,4);actions.Controls.Add(launchLabel);actionTips.SetToolTip(launchLabel,"Применяется ко всем виртуальным машинам.");
        launchMode.AutoSize=false;launchMode.Margin=new Padding(8,0,0,0);primary.Controls.Add(launchMode);
        machineStateText.AutoSize=false;machineStateText.TextAlign=ContentAlignment.MiddleCenter;
        machineStage=new StateStage(machineStateText,actions);
        workspace.Add("Консоль",machineStage);
        adapterCards=new AdapterCards(()=>EditVm(selected,"network"));workspace.Add("Сеть",adapterCards);
        liveLogPage=new EmbeddedToolPage(name=>new LiveLogsView(this,name,false,true));
        workspace.Add("События",liveLogPage);
        snapshotPage=new EmbeddedToolPage(name=>new SnapshotCatalogView(new LegacySnapshotUiService(this,name),name,CachedSnapshots(name),false,true));workspace.Add("Снимки",snapshotPage);
        machineInspector=new ConfigurationInspector("Параметры",()=>EditVm(selected));

        var contextActions=Theme.Flow();contextActions.Dock=DockStyle.None;contextActions.WrapContents=true;contextActions.Visible=false;
        var networkSettings=Theme.Button("Настроить адаптеры",()=>EditVm(selected,"network"));networkSettings.AutoSize=false;networkSettings.MinimumSize=Size.Empty;networkSettings.Size=new Size(220,42);networkSettings.Margin=Padding.Empty;networkSettings.Padding=new Padding(6);
        var followAction=new ToggleButton{Text="Прокрутка",Checked=true,AutoSize=false,MinimumSize=Size.Empty,Size=new Size(106,40),Padding=new Padding(4),Margin=new Padding(0,0,8,0),Font=UiTypography.Points(9F)};
        var pauseAction=new ToggleButton{Text="Пауза",AutoSize=false,MinimumSize=Size.Empty,Size=new Size(106,40),Padding=new Padding(4),Margin=Padding.Empty,Font=UiTypography.Points(9F)};
        actionTips.SetToolTip(followAction,"Автопрокрутка журнала");actionTips.SetToolTip(pauseAction,"Пауза просмотра журнала");
        followAction.CheckedChanged+=(s,e)=>{var log=liveLogPage.Tool as LiveLogsView;if(log!=null)log.Follow=followAction.Checked;};
        pauseAction.CheckedChanged+=(s,e)=>{var log=liveLogPage.Tool as LiveLogsView;if(log!=null)log.Paused=pauseAction.Checked;};
        liveLogPage.ToolReady+=form=>{var log=(LiveLogsView)form;followAction.Checked=log.Follow;pauseAction.Checked=log.Paused;};
        contextActions.Controls.Add(networkSettings);contextActions.Controls.Add(followAction);contextActions.Controls.Add(pauseAction);
        Action updateContext=()=>{networkSettings.Visible=workspace.SelectedIndex==1;followAction.Visible=pauseAction.Visible=workspace.SelectedIndex==2;contextActions.Visible=workspace.SelectedIndex==1||workspace.SelectedIndex==2;contextActions.PerformLayout();if(contextActions.Parent!=null)contextActions.Parent.PerformLayout();};
        workspace.SelectionChanged+=(s,e)=>updateContext();
        var work=new InspectorWorkspace(workspace,null,contextActions){Dock=DockStyle.Fill};
        updateContext();
        var machineLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty,BackColor=Theme.Bg};
        machineLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        machineLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,110));machineLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        hero.Dock=DockStyle.Fill;workspace.Dock=DockStyle.None;
        machineLayout.Controls.Add(new SelectionHeader(hero,machineInspector){Dock=DockStyle.Fill},0,0);machineLayout.Controls.Add(work,0,1);
        top.Controls.Clear();top.Visible=false;top.Height=0;serviceButton.Parent=null;section.Dispose();support.Dispose();
        detail.Controls.Add(machineLayout);content.Panel1.Controls.Add(left);var right=new BufferedPanel{Dock=DockStyle.Fill,BackColor=Theme.Bg,Padding=Padding.Empty};top.Parent=null;right.Controls.Add(detail);right.Controls.Add(top);content.Panel2.Controls.Add(right);
        InitStands(content,libraryHeader,library,add);
        var chrome=new BufferedPanel{Dock=DockStyle.Top,Height=91,Padding=new Padding(27,20,27,20),BackColor=Theme.Bg};
        var brand=new ReferenceBrand();brand.Dock=DockStyle.Left;brand.AutoSize=false;brand.Width=173;
        var serviceHost=new BufferedPanel{Dock=DockStyle.Left,Width=142,BackColor=Theme.Bg};serviceHost.Controls.Add(serviceButton);Action placeService=()=>serviceButton.Location=new Point((int)(12*layoutScale),Math.Max(0,(int)Math.Round(brand.VisualCenterY-serviceButton.Height/2F)));serviceHost.Resize+=(s,e)=>placeService();brand.SizeChanged+=(s,e)=>placeService();chrome.Layout+=(s,e)=>placeService();placeService();chrome.Controls.Add(serviceHost);chrome.Controls.Add(brand);
        libraryMode.Dock=DockStyle.Right;libraryMode.Width=153;chrome.Controls.Add(libraryMode);
        BackColor=Theme.Bg;Padding=Padding.Empty;Controls.Add(content);Controls.Add(bottom);Controls.Add(chrome);
        launchMode.SelectedIndexChanged+=(sender,args)=>{if(updatingMode||Backend==null)return;modeEpoch++;requestedMode=launchMode.SelectedIndex==1?"snapshot":"normal";SaveLaunchMode();};
        Action arrange=()=>{
            int width=Math.Max(270,detail.ClientSize.Width-detail.Padding.Horizontal);
            machineLayout.RowStyles[0].Height=(width<550*layoutScale?110:80)*layoutScale;
            heading.MaximumSize=status.MaximumSize=new Size(Math.Max(180,width-80),0);
            actions.MinimumSize=new Size(0,0);actions.MaximumSize=new Size(width,0);actions.Width=width;primary.MaximumSize=new Size(width,0);
        };
        detail.Resize+=(sender,args)=>arrange();
        Shown+=(sender,args)=>arrange();
        timer.Tick+=(sender,args)=>{if(!demo&&lastStateUtc!=DateTime.MinValue&&(DateTime.UtcNow-lastStateUtc).TotalSeconds>10){SelectVm(selected);PaintStandStatus();}Poll();};FormClosing+=OnClosing;
        Shown+=(sender,args)=>{if(!demo&&autoStart)Run("Открываю библиотеку…",()=>{Backend=new Backend(root,python);Backend.Open();return Backend.Call("list");},result=>{Fill(result);timer.Start();Poll();AlphaIntegration.Ready();});};
        SelectVm(null);
    }
    void SetSearchVisible(bool value){searchOpen=true;search.Visible=true;if(!value)search.Text="";search.Focus();}
    void ShowService(){
        if(DialogLaunch.Defer(this,ShowService))return;
        Action selectedAction=null;
        using(var service=new MotionDialog()){
            service.Text="Service · QemuM";Theme.Form(service,1000,590);
            var body=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=3,Margin=Padding.Empty};
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute,133));body.RowStyles.Add(new RowStyle(SizeType.Absolute,133));body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Action<Action> choose=action=>{selectedAction=action;service.Close();};
            string vmName=standMode&&selectedStand!=null?null:selected;
            var settings=new ReferenceServiceTile("dialog:settings","Настройки приложения","Тема, плотность и анимация",()=>{});
            settings.Enabled=false;settings.AccessibleDescription="Настройки темы, плотности и анимации пока отсутствуют в приложении.";
            var tips=new ToolTip();tips.SetToolTip(settings,settings.AccessibleDescription);service.Disposed+=(s,e)=>tips.Dispose();
            body.Controls.Add(settings,0,0);
            body.Controls.Add(new ReferenceServiceTile("dialog:shield","Диагностика","QEMU, среда и возможности",()=>choose(()=>RunDoctor(null))),1,0);
            body.Controls.Add(new ReferenceServiceTile("dialog:folder","Копии параметров","Сохранённые параметры машин",()=>choose(()=>CreateVm(true))),0,1);
            body.Controls.Add(new ReferenceServiceTile("dialog:layers","Обновления","Версия и канал обновлений",()=>choose(()=>AlphaIntegration.Updates(this))),1,1);
            var extras=Theme.Flow();extras.Margin=new Padding(0,12,0,0);
            extras.Controls.Add(Theme.Button("Обновить список",()=>choose(()=>{if(standMode)LoadStands();else Reload();})));
            var doctorVm=Theme.Button("VM Doctor · "+(vmName??"выбери VM"),()=>choose(()=>RunDoctor(vmName)));doctorVm.Enabled=vmName!=null;extras.Controls.Add(doctorVm);
            extras.Controls.Add(Theme.Button("Помощь / отчёт",()=>choose(()=>{using(var window=new SupportWindow(this,vmName,true))window.ShowDialog(this);})));
            if(selectedStand!=null){string id=J.S(selectedStand,"id");extras.Controls.Add(Theme.Button("Отчёт стенда · "+J.S(selectedStand,"name"),()=>choose(()=>ExportStand(id))));}
            body.Controls.Add(extras,0,2);body.SetColumnSpan(extras,2);
            var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Закрыть",service.Close));Theme.Dialog(service,new ScrollSurface(body),buttons,"Сервис","Инструменты приложения","СЕРВИС");service.ShowDialog(this);
        }
        if(selectedAction!=null)selectedAction();
    }
    Panel ToolPage(string title,string actionTitle,Action action){var page=new BufferedPanel{Padding=new Padding(20),BackColor=Theme.Field};var caption=Theme.Label(title,12,null,true);caption.Dock=DockStyle.Top;var open=Theme.Button(actionTitle,action);open.Dock=DockStyle.Top;page.Controls.Add(open);page.Controls.Add(caption);return page;}
    Panel PreviewPage(ThemedEditor preview,string buttonTitle,Action action) {
        var page=new Panel { Padding=new Padding(0,20,0,0),BackColor=Theme.Bg };
        preview.ReadOnly=true;preview.BorderVisible=false;preview.BackColor=Theme.Bg;
        preview.Font=UiTypography.Points(10F);
        var buttons=Theme.Flow();buttons.Dock=DockStyle.Bottom;buttons.Controls.Add(Theme.Button(buttonTitle,action));
        page.Controls.Add(preview);page.Controls.Add(buttons);return page;
    }
    ContextMenuStrip BuildMenu(params object[] entries) {
        var menu=NightMenuRenderer.Create(uiComponents);
        for(int index=0;index<entries.Length;index+=2){var action=(Action)entries[index+1];NightMenuRenderer.Add(menu,(string)entries[index],(sender,args)=>action());}
        return menu;
    }
    void ShowMachineMenu(Control anchor) {
        bool fresh=demo||(!stateReadFailed&&lastStateUtc!=DateTime.MinValue&&(DateTime.UtcNow-lastStateUtc).TotalSeconds<=10);bool canControl=!Busy&&fresh&&new[]{"running","paused"}.Contains(State(selected));
        foreach(ToolStripItem item in machineMenu.Items)if(item.Text=="Перезагрузка"||item.Text=="Остановить QEMU")item.Enabled=canControl;
        machineMenu.Show(anchor,new Point(0,anchor.Height));
    }
    public void Post(Action action) { if(!IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch(InvalidOperationException) {} } }
    readonly Dictionary<string,Task<object>> reads=new Dictionary<string,Task<object>>();
    readonly Dictionary<string,CancellationTokenSource> readCancellations=new Dictionary<string,CancellationTokenSource>();
    readonly Dictionary<string,int> readTickets=new Dictionary<string,int>();int nextReadTicket;
    int readFooterTicket;
    readonly Dictionary<string,object> snapshotCatalogs=new Dictionary<string,object>();
    internal bool Reading{get{return readTickets.Count>0;}}
    internal bool ModeSaving{get{return modeSaving;}}bool modeSaving,modeUnconfirmed;string requestedMode;int modeEpoch;
    internal Task<object> ReadAsync(string key,Func<object> operation){
        lock(reads){
            Task<object> task;CancellationTokenSource prior;if(reads.TryGetValue(key,out task)&&!task.IsCompleted&&readCancellations.TryGetValue(key,out prior)&&!prior.IsCancellationRequested)return task;
            var source=new CancellationTokenSource();readCancellations[key]=source;
            task=Task.Run(()=>global::Backend.WithReadCancellation(operation,source.Token));reads[key]=task;
            task.ContinueWith(finished=>{lock(reads){Task<object> current;if(reads.TryGetValue(key,out current)&&Object.ReferenceEquals(current,finished)){reads.Remove(key);readCancellations.Remove(key);}}source.Dispose();},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            return task;
        }
    }
    internal void CancelRead(string key){readTickets.Remove(key);lock(reads){CancellationTokenSource source;if(readCancellations.TryGetValue(key,out source))source.Cancel();}}
    void CancelReads(){readTickets.Clear();lock(reads){foreach(var source in readCancellations.Values.ToArray())source.Cancel();}}
    public async void Read(string message,Func<object> operation,Action<object> done,Action<Exception> failed=null,string key=null){
        key=key??message;int ticket=++nextReadTicket;readTickets[key]=ticket;
        try{var task=ReadAsync(key,operation);if(await Task.WhenAny(task,Task.Delay(180))!=task){int active;if(!IsDisposed&&!Busy&&readTickets.TryGetValue(key,out active)&&active==ticket){readFooterTicket=ticket;footer.Text=message;}}object result=await task;int current;if(IsDisposed||!readTickets.TryGetValue(key,out current)||current!=ticket)return;readTickets.Remove(key);if(!Busy&&readFooterTicket==ticket)footer.Text="";if(done!=null)done(result);}
        catch(OperationCanceledException){int current;if(!IsDisposed&&readTickets.TryGetValue(key,out current)&&current==ticket){readTickets.Remove(key);if(!Busy&&readFooterTicket==ticket)footer.Text="";}}
        catch(Exception error){int current;if(IsDisposed||!readTickets.TryGetValue(key,out current)||current!=ticket)return;readTickets.Remove(key);footer.Text=message.TrimEnd('…')+": "+error.Message;if(failed!=null)failed(error);}
    }
    internal object CachedSnapshots(string name){object value;return snapshotCatalogs.TryGetValue(name,out value)?value:null;}
    readonly LinkedList<string> snapshotCatalogOrder=new LinkedList<string>();
    internal void RememberSnapshots(string name,object value){snapshotCatalogs[name]=value;snapshotCatalogOrder.Remove(name);snapshotCatalogOrder.AddFirst(name);while(snapshotCatalogOrder.Count>2){string old=snapshotCatalogOrder.Last.Value;snapshotCatalogOrder.RemoveLast();snapshotCatalogs.Remove(old);}}
    async void SaveLaunchMode(){
        if(modeSaving)return;modeSaving=true;modeUnconfirmed=false;SelectVm(selected);
        try{
            while(requestedMode!=null){string value=requestedMode;requestedMode=null;if(value==mode)continue;await Task.Run(()=>Backend.Call("mode",J.O("value",value)));mode=value;}
            footer.Text="";
        }catch(Exception error){requestedMode=null;modeUnconfirmed=true;footer.Text="Режим запуска не подтверждён. Обнови библиотеку: "+error.Message;updatingMode=true;launchMode.SelectedIndex=mode=="snapshot"?1:0;updatingMode=false;}
        finally{modeSaving=false;if(!IsDisposed){if(!standMode||selectedStand==null)SelectVm(selected);launchMode.Invalidate();}}
    }
    public void Run(string message,Func<object> operation,Action<object> done=null,Action<Exception> failed=null,bool notifyError=true) {
        if(demo) return;
        if(Busy) { footer.Text="Дождись завершения текущей операции.";return; }
        Busy=true;progress.Visible=true;footer.Text=message;actions.Enabled=false;launchMode.Enabled=false;
        Task.Run(()=>{try {object result=operation();Post(()=>{Busy=false;progress.Visible=false;actions.Enabled=selected!=null;launchMode.Enabled=true;footer.Text="Готово";if(!standMode||selectedStand==null)SelectVm(selected);if(done!=null)done(result);});}
            catch(Exception error){Post(()=>{Busy=false;progress.Visible=false;actions.Enabled=selected!=null;launchMode.Enabled=true;footer.Text="Действие не выполнено";if(!standMode||selectedStand==null)SelectVm(selected);if(notifyError){if(Program.TestMode)TestError=error;else MessageBox.Show(this,error.Message,"VM Manager",MessageBoxButtons.OK,MessageBoxIcon.Warning);}if(failed!=null)failed(error);});}});
    }
    void Fill(object result) { rows=J.A(J.V(result,"rows")).ToList();if(!modeSaving){mode=J.S(result,"mode","normal");updatingMode=true;launchMode.SelectedIndex=mode=="snapshot"?1:0;updatingMode=false;}if(selected==null || !rows.Any(row=>J.S(row,"name")==selected)){string next=rows.Count>0?J.S(rows[0],"name"):null;if(snapshotPage.CanSelectMachine(next))selected=next;}RenderCards();SelectVm(selected);if(standMode)LoadStands();footer.Text=""; }
    public void Reload() { if(Backend==null)return;int epoch=modeEpoch;Read("Обновляю список…",()=>Backend.Call("list"),result=>{if(epoch==modeEpoch&&!modeSaving)modeUnconfirmed=false;else if(epoch!=modeEpoch){var current=new Dictionary<string,object>(J.D(result));current["mode"]=mode;result=current;}Fill(result);},null,"library"); }
    List<object> indexedRows;readonly Dictionary<string,object> rowIndex=new Dictionary<string,object>(StringComparer.Ordinal);
    public object Row(string name) {
        if(String.IsNullOrEmpty(name))return null;
        if(!Object.ReferenceEquals(indexedRows,rows)||rowIndex.Count!=rows.Count){
            rowIndex.Clear();foreach(var row in rows){string key=J.S(row,"name");if(!rowIndex.ContainsKey(key))rowIndex[key]=row;}indexedRows=rows;
        }
        object value;return rowIndex.TryGetValue(name,out value)?value:null;
    }
    public string State(string name) { string result;return name!=null && states.TryGetValue(name,out result)?result:"checking"; }
    void RenderCards() {
        searchDebounce.Stop();
        if(standMode){RenderStands();return;}
        var virtualCards=(VirtualLibraryPanel)cards;
        if(rows.Count>=64){
            libraryCount.Text="Всего машин: "+rows.Count;
            var models=new List<VirtualLibraryRow>();foreach(var row in rows){string name=J.S(row,"name"),label=J.S(J.V(row,"data"),"label",name);if((name+" "+label).IndexOf(search.Text,StringComparison.OrdinalIgnoreCase)<0)continue;
                var captured=row;string key="vm/"+name;models.Add(new VirtualLibraryRow{Key=key,Version=label+"|"+Theme.VmColor(row).ToArgb()+"|"+appearanceEpoch,Tag=name,AccessibleName=label+" / "+name,Margin=new Padding(0,0,0,4),Measure=width=>virtualCards.MachineHeight(label,width),Create=()=>MachineCard(captured),Bind=control=>{var card=(LibraryCard)control;card.BackColor=name==selected?Theme.Card:Theme.Panel;card.SetStatus(State(name));},Activate=()=>SelectVm(name)});
            }
            if(models.Count==0)models.Add(new VirtualLibraryRow{Key="empty",Version=search.Text,AccessibleName="По запросу ничего не найдено",Margin=Padding.Empty,Measure=width=>80,Create=()=>Theme.Label("По запросу ничего не найдено. Измени поиск или очисти поле.",10,Theme.Muted)});
            virtualCards.ViewportChanged=null;virtualCards.SelectedKey="vm/"+selected;virtualCards.SetRows(models);return;
        }
        virtualCards.DisableVirtual();
        int scroll=libraryScroll.ScrollOffset;
        libraryScroll.SuspendLayout();cards.SuspendLayout();foreach(Control child in cards.Controls.Cast<Control>().ToArray())child.Dispose();cards.Controls.Clear();
        foreach(object row in rows) {
            string name=J.S(row,"name");object data=J.V(row,"data");string label=J.S(data,"label",name);
            if((name+" "+label).IndexOf(search.Text,StringComparison.OrdinalIgnoreCase)<0)continue;
            var card=MachineCard(row);
            cards.Controls.Add(card);
        }
        if(cards.Controls.Count==0)cards.Controls.Add(Theme.Label(rows.Count==0?"Виртуальных машин пока нет.\r\nНажми «Новая машина».":"По запросу ничего не найдено. Измени поиск или очисти поле.",10,Theme.Muted));
        cards.ResumeLayout();libraryScroll.ResumeLayout(true);libraryScroll.PerformLayout();libraryScroll.ScrollOffset=scroll;
    }
    LibraryCard MachineCard(object row){
        string name=J.S(row,"name"),label=J.S(J.V(row,"data"),"label",name);var card=new LibraryCard(name,label,Theme.VmColor(row)){Width=Math.Max(170,cards.Width),BackColor=name==selected?Theme.Card:Theme.Panel};
        try{string choice=Visuals.Choice(name);var distro=Visuals.Resolve(choice,J.V(row,"data"),name,false);card.Logo=Visuals.AcquireLogo(distro,Math.Max(24,24*DeviceDpi/96));if(choice=="auto")card.ReferenceIcon=distro!=null&&distro.Id=="kali"?"layers":distro!=null&&distro.Id=="tails"?"shield":"cpu";}catch{card.ReferenceIcon="cpu";}card.SetStatus(State(name));
        EventHandler select=(sender,args)=>SelectVm(name);card.Click+=select;card.DoubleClick+=select;
        card.KeyDown+=(sender,args)=>{if(args.KeyCode==Keys.Enter||args.KeyCode==Keys.Space){SelectVm(name);args.Handled=true;}else if(args.KeyCode==Keys.Up||args.KeyCode==Keys.Down){var panel=(VirtualLibraryPanel)cards;if(panel.VirtualMode)panel.Navigate("vm/"+name,args.KeyCode==Keys.Up?-1:1);else{var items=cards.Controls.OfType<LibraryCard>().ToArray();int index=Array.IndexOf(items,card)+(args.KeyCode==Keys.Up?-1:1);if(index>=0&&index<items.Length){items[index].Focus();SelectVm(Convert.ToString(items[index].Tag));}}args.Handled=true;}};
        foreach(Control control in card.Controls){control.Click+=select;control.DoubleClick+=select;}return card;
    }
    internal void SaveAppearanceAfterOperation(string name,string choice){
        try{Visuals.SaveChoice(name,choice);}catch(Exception error){
            if(Program.TestMode)TestError=error;else MessageBox.Show(this,"Операция с VM выполнена, но значок сохранить не удалось.\r\n"+error.Message,"Оформление",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
    }
    void RefreshEmblem(string name,object data){
        string choice;try{choice=Visuals.Choice(name);}catch(Exception){choice="auto";}
        string key=J.Json().Serialize(J.O("name",name,"choice",choice,"pixels",emblem.Width,"dpi",DeviceDpi,"iso",J.S(data,"iso_path",J.S(data,"kind")=="iso"?J.S(data,"path"):""),"label",J.S(data,"label"),"disk",J.S(data,"disk_path")));
        if(visualKey==key){string version=AppearanceStore.FileVersion(emblemDistro);if(version!=emblemFileVersion){emblemFileVersion=version;emblem.Logo=Visuals.AcquireLogo(emblemDistro,Math.Max(32,emblem.Width));}return;}visualKey=key;int request=++visualRequest;emblem.Logo=null;if(name==null)return;
        Task.Run(()=>{Distro distro=null;try{distro=Visuals.Resolve(choice,data,name);}catch(Exception){}
            Post(()=>{if(request==visualRequest&&!IsDisposed){emblemDistro=distro;emblemFileVersion=AppearanceStore.FileVersion(distro);emblem.ReferenceIcon=choice=="auto"?(distro!=null&&distro.Id=="kali"?"layers":distro!=null&&distro.Id=="tails"?"shield":"cpu"):null;emblem.Logo=Visuals.AcquireLogo(distro,Math.Max(32,emblem.Width));emblem.Invalidate();}});
        });
    }
    void SelectVm(string name) {
        if(selected!=name&&(Busy||SnapshotJobs>0))return;
        if(!snapshotPage.CanSelectMachine(name))return;
        if(standMode && selectedStand!=null)return;
        foreach(Control sibling in detail.Controls)sibling.Visible=!Object.ReferenceEquals(sibling,standDetail);
        bool selectionChanged=selected!=name;selected=name;var virtualCards=(VirtualLibraryPanel)cards;if(virtualCards.VirtualMode&&!standMode){virtualCards.SelectedKey="vm/"+name;if(selectionChanged)virtualCards.EnsureKeyVisible("vm/"+name,false);}var row=Row(name);object data=J.V(row,"data");
        if(Backend!=null){liveLogPage.SelectMachine(name);snapshotPage.SelectMachine(name);}
        RefreshEmblem(name,data);
        heading.Text=row==null?"Выбери виртуальную машину":J.S(data,"label",name);heading.ForeColor=Theme.Text;
        description.Text=row==null?"Добавь машину или выбери её в списке.":"Твоя лаборатория. Всё под контролем.";resources.ShowData(data,row!=null);
        breadcrumb.Text=row==null?"Машины":"Machines / "+J.S(data,"label",name);
        var standOwner=standMode&&standSelectedVm==name?stands.FirstOrDefault(item=>J.S(item,"id")==standSelectedParent):null;
        if(standOwner!=null)breadcrumb.Text="Стенды / "+J.S(standOwner,"name")+" / "+name;
        libraryCount.Text="Всего машин: "+rows.Count;
        var adapters=J.A(J.V(J.V(row,"network_view"),"adapters"));
        if(adapters.Length==0&&J.D(data).ContainsKey("network_adapters"))adapters=J.A(J.V(data,"network_adapters"));
        adapterCards.ShowAdapters(adapters);
        string disk=J.S(data,"disk_path"),iso=J.S(data,"iso_path");
        mediaSummary.Text=row==null?"":"Диск: "+(disk.Length==0?"не задан":System.IO.Path.GetFileName(disk)+" · "+J.S(data,"disk_format","авто"))+"\nISO: "+(iso.Length==0?"не подключён":System.IO.Path.GetFileName(iso))+" · Сеть: "+adapters.Length+" адапт.";
        mediaSummary.AccessibleDescription=row==null?"":("Диск: "+Path.GetFileName(disk)+"; ISO: "+Path.GetFileName(iso)+"; сетевых адаптеров: "+adapters.Length);
        networkPreview.Text=adapters.Length==0?"Нет распознанных сетевых адаптеров. Открой настройки сети для подробностей.":String.Join(Environment.NewLine,adapters.Select(item=>{string type=J.S(item,"type"),modeName=J.S(item,"mode"),kind=type=="user"?"NAT":type=="tap"?"TAP":type=="socket"?(modeName=="mcast"?"MCAST":new[]{"listen","connect"}.Contains(modeName)?"TCP":"Сокет"):type;return "Адаптер: "+J.S(item,"id")+" · Подключение: "+kind+" · Модель: "+J.S(item,"model");}));
        machineInspector.Value("Память",row==null?"—":J.N(data,"ram_mb")+" МБ");
        machineInspector.Value("Процессор",row==null?"—":J.N(data,"cores")+" CPU");
        machineInspector.Value("Ускорение",J.S(data,"launch_profile","Авто"));
        machineInspector.Value("Диск",String.IsNullOrEmpty(disk)?"Не задан":disk);
        machineInspector.Value("ISO",String.IsNullOrEmpty(iso)?"Не подключён":iso);
        machineInspector.Value("Сеть",adapters.Length+" адапт.");
        string vmState=State(name);
        bool fresh=demo||(!stateReadFailed&&lastStateUtc!=DateTime.MinValue&&(DateTime.UtcNow-lastStateUtc).TotalSeconds<=10);
        status.Text=row==null?"":fresh?Theme.State(vmState):lastStateUtc==DateTime.MinValue?"Проверка состояния…":"Последний статус: "+Theme.State(vmState)+" · "+lastStateUtc.ToLocalTime().ToString("HH:mm:ss")+" · не подтверждён";status.ForeColor=fresh?Theme.StateColor(vmState):Theme.Yellow;actions.Enabled=row!=null && !Busy;
        status.AccessibleName=status.Text;
        machineStage.Context(name??"",row==null?"—":J.N(data,"ram_mb")+" МБ",row==null?"—":J.N(data,"cores").ToString());
        machineStateText.Text=row==null?"Выбери машину в библиотеке":!fresh?"Проверка состояния QEMU…":vmState=="running"?"Машина работает":vmState=="paused"?"Выполнение приостановлено":vmState=="stopped"?"Машина выключена":status.Text;
        machineStage.Present(fresh?vmState:"checking");machineStage.Acceleration(J.S(J.V(data,"launch_profile_settings"),"accelerator",J.S(data,"launch_profile","auto")).ToUpperInvariant());
        consoleButton.Visible=fresh&&vmState=="running";
        startButton.Visible=!fresh||!new[]{"running","paused"}.Contains(vmState);
        pauseButton.Visible=fresh&&new[]{"running","paused"}.Contains(vmState);
        shutdownButton.Visible=fresh&&new[]{"running","paused"}.Contains(vmState);
        startButton.Text=vmState=="error"?"Повторить запуск":"Запустить";((PolarButton)startButton).IconName="play";

        logsButton.Text=vmState=="error"?"Журналы · ошибка":"Журналы";logsButton.ForeColor=vmState=="error"?Theme.Red:Theme.Text;
        bool running=vmState=="running"||vmState=="paused";
        startButton.Enabled=row!=null&&!Busy&&!modeSaving&&!modeUnconfirmed&&fresh&&new[]{"stopped","error"}.Contains(vmState);
        pauseButton.Enabled=shutdownButton.Enabled=row!=null&&!Busy&&fresh&&running;
        string unavailable=row==null?"Выбери виртуальную машину.":Busy?"Дождись завершения операции.":!fresh?"Дождись подтверждения текущего состояния.":vmState=="recovery-required"?"Сначала проверь незавершённые операции снимков.":running?"Машина уже запущена.":"Запуск недоступен в текущем состоянии.";
        startButton.AccessibleDescription=startButton.Enabled?(vmState=="error"?"Повторный запуск после предупреждения и проверки.":"Запустить виртуальную машину."):unavailable;actionTips.SetToolTip(startButton,startButton.AccessibleDescription);
        ((PolarButton)pauseButton).Primary=vmState=="paused";pauseButton.ForeColor=vmState=="paused"?Color.White:Theme.Text;pauseButton.Text=vmState=="paused"?"Продолжить":"Приостановить";((PolarButton)pauseButton).IconName=vmState=="paused"?"play":"pause";
        if(!standMode)foreach(Control card in cards.Controls) {
            Color color=Object.Equals(card.Tag,name)?Theme.Card:Theme.Panel;
            if(card.BackColor!=color)card.BackColor=color;
            var libraryCard=card as LibraryCard;if(libraryCard!=null)libraryCard.SetStatus(fresh?State(card.Tag as string):"checking");
        }
        else PaintStandStatus();
        // Nested command strips do not resize their fixed-size parent when a
        // state change reveals another button. Measure after all updates above.
        machineStage.PerformLayout();
    }
    async void Poll() {
        if(Backend==null || Busy || polling || closing)return;polling=true;int generation=stateGeneration;
        try{object result=await Backend.CallAsync("states",null,true);if(!IsDisposed&&!closing&&generation==stateGeneration)ApplyStates(result);}
        catch(Exception error){if(!IsDisposed&&!closing&&generation==stateGeneration){stateReadFailed=true;footer.Text="Не удалось обновить состояние: "+error.Message;SelectVm(selected);PaintStandStatus();}}finally{polling=false;}
    }
    internal void ApplyStates(object result) {
        lastStateUtc=DateTime.UtcNow;stateReadFailed=false;
        if(footer.Text.StartsWith("Не удалось обновить состояние:",StringComparison.Ordinal))footer.Text="Готово";
        var next=J.D(result).ToDictionary(pair=>pair.Key,pair=>Convert.ToString(pair.Value));
        foreach(Control card in cards.Controls) {
            string name=card.Tag as string;if(name==null)continue;string value;if(!next.TryGetValue(name,out value))value="checking";
            if(value==State(name))continue;
            var libraryCard=card as LibraryCard;if(libraryCard!=null)libraryCard.SetStatus(value);
        }
        states=next;SelectVm(selected);UpdateStandStatus();
    }
    public void OpenPanel(string name) {
        if(name==null)return;
        if(standMode){
            var owner=stands.FirstOrDefault(stand=>J.A(J.V(stand,"members")).Any(member=>Convert.ToString(member)==name));
            standSelectedParent=J.S(owner,"id");standSelectedVm=name;selectedStand=null;standDetail.Visible=false;
        }
        SelectVm(name);
        if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;
        Activate();
    }
    public void OpenLogs(string name) { if(name==null)return;LogsWindow window;if(logs.TryGetValue(name,out window)&&!window.IsDisposed){window.Activate();return;}window=new LogsWindow(this,name);logs[name]=window;window.FormClosed+=(sender,args)=>logs.Remove(name);window.Show(); }
    public void StartVm(string name,object snapshot=null) {
        if(name==null||Backend==null||Busy||modeSaving||modeUnconfirmed)return;
        if(snapshot==null && mode=="snapshot"){Snapshots(name,true);return;}
        if(State(name)=="error"&&MessageBox.Show(this,"Последний запуск QEMU завершился с ошибкой. Проверь журнал перед повтором. Продолжить?","Повторный запуск · "+name,MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        object data=J.V(Row(name),"data");string boot=String.IsNullOrEmpty(J.S(data,"disk_path",J.S(data,"kind")=="disk"?J.S(data,"path"):""))?"iso":"disk";
        bool iso=!String.IsNullOrEmpty(J.S(data,"iso_path",J.S(data,"kind")=="iso"?J.S(data,"path"):""));
        if(snapshot==null && boot=="disk" && iso) { using(var choice=new ChoiceDialog("Источник загрузки",new[]{"Виртуальный диск","Установочный / Live ISO"})) {if(choice.ShowDialog(this)!=DialogResult.OK)return;boot=choice.Selected==0?"disk":"iso";} }
        if(snapshot!=null)boot="disk";
        stateGeneration++;stateReadFailed=true;Run("Подготавливаю запуск…",()=>Backend.Call("start",J.O("name",name,"boot",boot,"snapshot",snapshot)),result=>{stateReadFailed=true;footer.Text=J.B(result,"attached")?"VM уже работает · управление справа":"QEMU запущен · "+J.S(result,"accelerator").ToUpperInvariant();OpenPanel(name);Poll();},error=>{stateReadFailed=true;SelectVm(selected);Poll();});
    }
    public void ControlVm(string name,string action) {
        if(name==null||Backend==null||Busy)return;
        if(new[]{"shutdown","reset","quit"}.Contains(action)) {
            string message=action=="shutdown"?"Запросить штатное выключение гостевой ОС?":action=="reset"?"Жёстко перезагрузить VM? Несохранённые данные могут быть потеряны.":"Принудительно остановить QEMU? Несохранённые данные будут потеряны.";
            if(MessageBox.Show(this,name+"\r\n\r\n"+message,"Подтверждение",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        }
        stateGeneration++;stateReadFailed=true;Run("Выполняю действие…",()=>Backend.Call("control",J.O("name",name,"action",action,"confirm",name)),result=>{stateReadFailed=true;SelectVm(selected);footer.Text=action=="snapshot"?"Снимок дисков создан":"Запрос отправлен VM";Poll();},error=>{stateReadFailed=true;SelectVm(selected);Poll();});
    }
    public void Snapshots(string name,bool boot=false) {
        if(name==null||Backend==null)return;
        if(!boot){SelectVm(name);if(selected!=name)return;snapshotPage.SelectMachine(name);workspace.SelectedIndex=3;return;}
        if(bootSnapshots!=null&&!bootSnapshots.IsDisposed){if(bootSnapshotsName==name){bootSnapshots.Activate();return;}if(!bootSnapshots.View.CanClose())return;bootSnapshots.CloseImmediately();if(!bootSnapshots.IsDisposed)return;}
        var cached=CachedSnapshots(name);var dialog=new SnapshotsWindow(this,name,cached,true);bootSnapshotsName=name;bootSnapshots=dialog;if(cached!=null)dialog.Shown+=async(sender,args)=>await dialog.Reload();dialog.Show(this);
    }
    void EditVm(string name,string page=null) {if(name==null||Backend==null)return;DialogLaunch.Open(this,async active=>{var result=await ReadAsync("machine/"+name,()=>Backend.Call("get",J.O("name",name)));if(selected!=name||!active())return;var dialog=new EditDialog(this,name,result);dialog.OpenPage(page);dialog.Show(this);});}
    void CreateVm() {CreateVm(false);}
    void CreateVm(bool fromBackup) {
        if(Backend==null)return;
        DialogLaunch.Open(this,async active=>{var result=await ReadAsync("create/templates",()=>Backend.Call("template_list"));if(!active())return;
            if(fromBackup){
                var library=new TemplatesWindow(result,id=>new CreateDialog(this,result,false,id).Show(this));
                library.Show(this);
            }else new CreateDialog(this,result).Show(this);
        });
    }
    void DeleteVm(string name) {if(name==null||Backend==null)return;DialogLaunch.Open(this,async active=>{var result=await ReadAsync("delete-plan/"+name,()=>Backend.Call("delete_plan",J.O("name",name,"remove_files",false)));if(selected!=name||!active())return;var dialog=new DeleteDialog(this,result);dialog.Show(this);});}
    public void RunDoctor(string name) {if(Backend==null)return;DialogLaunch.Open(this,async active=>{var result=await ReadAsync("doctor/"+name,()=>Backend.Call("doctor",name==null?J.O():J.O("name",name)));if(!active())return;var dialog=new DoctorWindow(this,result);dialog.Show(this);});}
    void OnClosing(object sender,FormClosingEventArgs args) {
        if(demo)return;
        if(modeSaving){args.Cancel=true;footer.Text="Сохраняем режим запуска…";return;}
        var snapshots=snapshotPage.Tool as SnapshotCatalogView;
        if(Busy||SnapshotJobs>0||StandJobs>0||snapshots!=null&&snapshots.WriteBusy||bootSnapshots!=null&&!bootSnapshots.IsDisposed&&bootSnapshots.View.WriteBusy){args.Cancel=true;if(!Program.TestMode)MessageBox.Show(this,"Дождись завершения операции. Для стенда можно отменить будущие этапы в окне его состояния, затем закрыть менеджер.","VM Manager");return;}
        bool running=rows.Any(row=>State(J.S(row,"name"))!="stopped"&&State(J.S(row,"name"))!="error");
        if(!Program.TestMode && !closing && running && MessageBox.Show(this,"Закрыть менеджер? Работающие VM продолжат работу. Если статус неизвестен, VM также может быть запущена.","Закрытие менеджера",MessageBoxButtons.YesNo,MessageBoxIcon.Information)!=DialogResult.Yes){args.Cancel=true;return;}
        closing=true;timer.Stop();CancelReads();foreach(var window in logs.Values.ToArray())window.CloseImmediately();if(Backend!=null)Backend.Dispose();
    }
    protected override void Dispose(bool disposing) {
        if(disposing){CancelReads();timer.Dispose();uiComponents.Dispose();Visuals.Dispose();}
        base.Dispose(disposing);
    }
    public void Demo() {rows=new List<object>{J.O("name","ubuntu-desktop","data",J.O("label","Ubuntu Desktop","ram_mb",8192,"cores",4,"launch_profile","auto","ui_color","#79B8FF")),J.O("name","ubuntu-server","data",J.O("label","Ubuntu Server","ram_mb",4096,"cores",2,"ui_color","#F4BD62")),J.O("name","kali","data",J.O("label","Kali Linux","ram_mb",4096,"cores",4,"ui_color","#C4A2FF")),J.O("name","tails","data",J.O("label","Tails","ram_mb",4000,"cores",4,"ui_color","#61D6C3"))};states=new Dictionary<string,string>{{"ubuntu-desktop","running"},{"ubuntu-server","stopped"},{"kali","paused"},{"tails","stopped"}};selected="ubuntu-desktop";RenderCards();SelectVm(selected);if(standMode)LoadStands();footer.Text="";}
}

sealed class ChoiceDialog : MotionDialog {
    public int Selected { get { return choices.SelectedIndex; } }
    readonly ThemedList choices=Theme.ListBox(11);
    public ChoiceDialog(string title,string[] values) {
        Text=title;Theme.Form(this,600,440);MinimumSize=new Size(520,400);var panel=new Panel { Dock=DockStyle.Fill,Padding=new Padding(22) };
        choices.Items.AddRange(values);if(values.Length>0)choices.SelectedIndex=0;panel.Controls.Add(choices);
        var buttons=Theme.Flow();buttons.Dock=DockStyle.Bottom;buttons.Padding=new Padding(22,10,10,8);
        var accept=Theme.Button("Выбрать",()=>{if(Selected>=0){DialogResult=DialogResult.OK;Close();}},true);var cancel=Theme.Button("Отмена",()=>{DialogResult=DialogResult.Cancel;Close();});buttons.Controls.Add(accept);buttons.Controls.Add(cancel);AcceptButton=accept;CancelButton=cancel;Theme.AuxiliaryDialog(this,panel,buttons,title,"Выбери один из доступных вариантов.");
    }
}

sealed class LogsWindow : MotionDialog,IToolVisibility {
    internal readonly LiveLogsView View;
    public bool Follow{get{return View.Follow;}set{View.Follow=value;}}
    public bool Paused{get{return View.Paused;}set{View.Paused=value;}}
    public LogsWindow(MainWindow main,string name,bool demo=false,bool embedded=false){
        Text="Живые логи | "+name;Theme.Form(this,1000,660,!embedded,Theme.VmColor(main.Row(name)));
        View=new LiveLogsView(main,name,demo,embedded);
        if(embedded)Controls.Add(View);else{var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Закрыть",Close));Theme.AuxiliaryDialog(this,View,buttons,"Живые логи",name+" · события менеджера и вывод QEMU","ЖУРНАЛ СОБЫТИЙ");View.Padding=Padding.Empty;}
        Shown+=(s,e)=>SetHostVisible(true);VisibleChanged+=(s,e)=>SetHostVisible(Visible);Resize+=(s,e)=>SetHostVisible(Visible);
    }
    public void SetHostVisible(bool visible){View.SetHostVisible(visible&&WindowState!=FormWindowState.Minimized);}
}

sealed class LiveLogsView : UserControl,IToolVisibility {
    public bool Follow{get{return follow.Checked;}set{follow.Checked=value;}}
    public bool Paused{get{return pause.Checked;}set{pause.Checked=value;}}
    readonly MainWindow main;readonly string name;readonly ThemedEditor qemu=Theme.Editor(),events=Theme.Editor();
    readonly ToggleButton follow=new ToggleButton { Text="Автопрокрутка",Checked=true,AutoSize=true,ForeColor=Theme.Text },pause=new ToggleButton { Text="Пауза просмотра",AutoSize=true,ForeColor=Theme.Text };
    readonly Label info=Theme.Label("",9,Theme.Muted);readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer { Interval=1000 };
    bool pending,active,hostVisible;readonly bool live,embedded;int readGeneration;CancellationTokenSource lifetime;
    string file="";long offset,eventId;Decoder decoder=Encoding.UTF8.GetDecoder();
    public LiveLogsView(MainWindow main,string name,bool demo=false,bool embedded=false) {
        this.main=main;this.name=name;live=!demo;this.embedded=embedded;Dock=DockStyle.Fill;BackColor=Theme.Bg;
        var top=Theme.Flow();top.Padding=new Padding(22,8,18,8);top.Dock=DockStyle.Top;foreach(var box in new[]{follow,pause}){box.AutoSize=false;box.Size=new Size(190,40);box.Margin=new Padding(0,0,12,0);box.Padding=new Padding(10,6,10,6);box.TextAlign=ContentAlignment.MiddleCenter;}top.Controls.Add(follow);top.Controls.Add(pause);top.Visible=!embedded;top.Padding=new Padding(0,0,0,16);
        var tabs=new WorkspaceTabs { Dock=DockStyle.Fill };var qemuTab=new PageView("Вывод QEMU") { BackColor=Theme.Panel };var eventsTab=new PageView("События менеджера") { BackColor=Theme.Panel };
        foreach(var text in new[]{qemu,events}){text.ReadOnly=true;text.Font=UiTypography.Points(10F);}
        qemuTab.Controls.Add(qemu);eventsTab.Controls.Add(events);tabs.Add("События менеджера",eventsTab);tabs.Add("Вывод QEMU",qemuTab);
        foreach(var text in new[]{qemu,events}){text.BackColor=Theme.Field;text.BorderStyle=BorderStyle.None;}
        qemuTab.BackColor=eventsTab.BackColor=Theme.Bg;qemuTab.Padding=eventsTab.Padding=new Padding(0,4,0,0);
        foreach(var pair in new[]{new{Page=qemuTab,Editor=qemu,Hint="Вывод QEMU появится после запуска машины."},new{Page=eventsTab,Editor=events,Hint="Событий пока нет. Здесь появятся сообщения менеджера."}}){
            var hint=Theme.Label(pair.Hint,10,Theme.Muted);hint.AutoSize=false;hint.Dock=DockStyle.Fill;hint.TextAlign=ContentAlignment.MiddleCenter;hint.BackColor=Theme.Field;hint.Padding=new Padding(24);pair.Page.Controls.Add(hint);hint.BringToFront();var editor=pair.Editor;editor.TextChanged+=(sender,eventArgs)=>hint.Visible=editor.TextLength==0;hint.Visible=editor.TextLength==0;
        }
        var bottom=new Panel { Dock=DockStyle.Bottom,Height=36,Padding=new Padding(16,8,16,0) };info.AutoEllipsis=true;info.AutoSize=false;info.Dock=DockStyle.Fill;bottom.Controls.Add(info);var journalCard=new RoundedZone{Dock=DockStyle.Fill,BackColor=Theme.Bg,Padding=new Padding(16)};journalCard.Controls.Add(tabs);Controls.Add(journalCard);Controls.Add(top);Controls.Add(bottom);
        timer.Tick+=(sender,args)=>Read();VisibleChanged+=VisibilityChanged;Resize+=VisibilityChanged;main.Resize+=VisibilityChanged;main.VisibleChanged+=VisibilityChanged;pause.CheckedChanged+=VisibilityChanged;
        if(demo){qemu.Text="QEMU запущен с WHPX\r\nЖурнал обновляется во время работы VM.\r\n";events.Text="12:00:01  Выбрано ускорение WHPX\r\n12:00:02  Запущен QEMU\r\n";info.Text="Демонстрация окна логов";}

    }
    public void SetHostVisible(bool visible){hostVisible=visible;VisibilityChanged(this,EventArgs.Empty);}
    void VisibilityChanged(object sender,EventArgs args){
        bool next=live&&!IsDisposed&&Visible&&hostVisible&&!pause.Checked&&(!embedded||main.Visible&&main.WindowState!=FormWindowState.Minimized);
        if(next==active)return;active=next;readGeneration++;
        if(lifetime!=null){lifetime.Cancel();lifetime.Dispose();lifetime=null;}pending=false;
        if(!active){timer.Stop();return;}lifetime=new CancellationTokenSource();timer.Start();Read();
    }
    protected override void Dispose(bool disposing){
        if(disposing){active=false;readGeneration++;main.Resize-=VisibilityChanged;main.VisibleChanged-=VisibilityChanged;timer.Dispose();if(lifetime!=null){lifetime.Cancel();lifetime.Dispose();lifetime=null;}}
        base.Dispose(disposing);
    }
    void Append(ThemedEditor box,string text) {
        if(String.IsNullOrEmpty(text))return;box.AppendText(text);if(box.TextLength>2000000)box.Text=box.Text.Substring(box.TextLength-1500000);
        if(follow.Checked){box.SelectionStart=box.TextLength;box.ScrollToCaret();}
    }
    async void Read() {
        if(!active||pending||main.Busy||main.Backend==null||lifetime==null)return;pending=true;int generation=readGeneration;
        try{object value=await main.Backend.CallAsync("logs",J.O("name",name,"file",file,"offset",offset,"event",eventId),true,lifetime.Token);
            if(IsDisposed||!active||generation!=readGeneration)return;string next=J.S(value,"file");if(next!=file){file=next;decoder.Reset();qemu.Clear();}
            offset=J.L(value,"offset");byte[] bytes=Convert.FromBase64String(J.S(value,"content"));char[] chars=new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];int count=decoder.GetChars(bytes,0,bytes.Length,chars,0,false);Append(qemu,new string(chars,0,count));
            var batch=new StringBuilder();foreach(object item in J.A(J.V(value,"events"))){eventId=J.L(item,"id");batch.Append(J.S(item,"text")).Append(Environment.NewLine);if(batch.Length>2000000)batch.Remove(0,batch.Length-1500000);}Append(events,batch.ToString());
            info.Text=String.IsNullOrEmpty(file)?"":Path.GetFileName(file)+"  ·  "+DateTime.Now.ToString("HH:mm:ss");
        }catch(OperationCanceledException){}
        catch(Exception error){if(!IsDisposed&&active&&generation==readGeneration)info.Text=error.Message;}
        finally{if(generation==readGeneration)pending=false;}
    }
}

sealed class EditDialog : MotionDialog {
    readonly MainWindow main;readonly string name;readonly object original;readonly FieldInput label,disk,iso;readonly ThemedEditor raw;
    readonly NumberInput ram,cores,width,height;readonly ThemedSelector vga,profile;readonly ChoiceStrip vgaChoices;readonly PageDeck tabs;
    internal readonly LaunchSettingsUi launchSettings;internal readonly AppearanceView appearance;readonly string initialFields,initialRaw,initialAppearance;bool jsonCurrent,syncingJson,allowClose;
    Dictionary<string,object> draft;
    public EditDialog(MainWindow main,string name,object original) {
        this.main=main;this.name=name;this.original=original;object data=J.V(original,"data");draft=new Dictionary<string,object>(J.D(data));Text="Параметры | "+name;Theme.Form(this,960,790,iconColor:Theme.VmColor(main.Row(name)));
        tabs=new PageDeck { Dock=DockStyle.Fill,Padding=new Padding(0) };var common=new PageView("Параметры VM") { BackColor=Theme.Panel };var advanced=new PageView("Конфигурация JSON") { BackColor=Theme.Panel };
        var fields=Theme.Fields();label=Theme.Input(J.S(data,"label",name));ram=Theme.Number(J.N(data,"ram_mb",4096),1,1048576);cores=Theme.Number(J.N(data,"cores",2),1,256);
        width=Theme.Number(J.N(data,"screen_width",1920),320,4095);height=Theme.Number(J.N(data,"screen_height",1080),200,4095);
        vga=Theme.Combo("std","virtio","qxl","cirrus","none");vga.SelectedItem=J.S(data,"vga","std");vgaChoices=new ChoiceStrip(3,"std","virtio","qxl","cirrus","none");vgaChoices.Bind(vga);vgaChoices.Width=430;profile=LaunchProfiles.Selector();var profileField=LaunchProfiles.Field(profile,main.Backend,this);LaunchProfiles.Load(profile,data);
        disk=Theme.Input(J.S(data,"disk_path"));iso=Theme.Input(J.S(data,"iso_path"));
        Theme.Row(fields,"Название",label,true);Theme.Row(fields,"Память, МБ",ram);Theme.Row(fields,"Ядра CPU",cores);Theme.Row(fields,"Ширина экрана",width);Theme.Row(fields,"Высота экрана",height);
        Theme.Row(fields,"Видеоадаптер",vgaChoices);Theme.Row(fields,"Профиль запуска",profileField);Theme.Row(fields,"",new SettingsSeparator());Theme.Row(fields,"Диск",Theme.Browse(disk,false,"Диски qcow2 / VDI|*.qcow2;*.vdi|Все файлы|*.*"),true);Theme.Row(fields,"ISO",Theme.Browse(iso,false,"ISO|*.iso|Все файлы|*.*"),true);
        var note=Theme.Label("Применится при следующем запуске.\r\nПеред сохранением создаётся копия параметров.",10,Theme.Muted);Theme.Row(fields,"",note);common.Controls.Add(Theme.FieldsView(fields));
        raw=Theme.Editor(J.S(original,"raw"));raw.Font=UiTypography.Points(10F);advanced.Padding=new Padding(24);advanced.Controls.Add(raw);tabs.Pages.Add(common);
        launchSettings=new LaunchSettingsUi(main,this,tabs,FieldsData,EnsureVisual);launchSettings.Load(data,J.V(original,"network_view"));
        appearance=new AppearanceView(main.Visuals,()=>J.O("iso_path",iso.Text,"label",label.Text,"disk_path",disk.Text),()=>name,main.Visuals.Choice(name));
        var appearancePage=new PageView("Оформление");appearancePage.Controls.Add(appearance);tabs.Pages.Add(appearancePage);tabs.Pages.Add(advanced);
        try{initialFields=J.Json().Serialize(FieldsData());}catch(Exception){initialFields=null;}
        initialRaw=raw.Text;initialAppearance=appearance.Choice;
        FormClosing+=(sender,args)=>{if(allowClose||Program.TestMode)return;bool changed=appearance.Choice!=initialAppearance;try{changed|=jsonCurrent?raw.Text!=initialRaw&&raw.Text!=initialFields:initialFields==null||J.Json().Serialize(FieldsData())!=initialFields;}catch(Exception){changed=true;}if(changed&&MessageBox.Show(this,"Закрыть параметры без сохранения изменений?","Несохранённые изменения",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)args.Cancel=true;};
        iso.Leave+=(sender,args)=>appearance.RefreshPreview();label.Leave+=(sender,args)=>appearance.RefreshPreview();
        jsonCurrent=!String.IsNullOrEmpty(J.S(original,"error"));if(jsonCurrent)tabs.SelectedPage=advanced;
        raw.TextChanged+=(sender,args)=>{if(!syncingJson)jsonCurrent=true;};
        tabs.Selecting+=(sender,args)=>{
            try {
                if(args.Page==launchSettings.HelpPage||args.Page==appearancePage)return;
                if(args.Page==advanced){if(!jsonCurrent){syncingJson=true;try{raw.Text=J.Json().Serialize(FieldsData());}finally{syncingJson=false;}jsonCurrent=true;}}
                else EnsureVisual();
            }catch(Exception error){args.Cancel=true;MessageBox.Show(this,error.Message,"Конфигурация JSON",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        };
        var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Сохранить",Save,true));buttons.Controls.Add(Theme.Button("Отмена",Close));Theme.SettingsDialog(this,tabs,buttons,"Параметры машины",name+" · изменения применятся при следующем запуске",false);
    }
    void EnsureVisual(){
        if(!jsonCurrent)return;object parsed=J.Json().DeserializeObject(raw.Text);
        if(!(parsed is Dictionary<string,object>))throw new InvalidOperationException("Конфигурация должна быть объектом JSON.");
        draft=J.D(parsed);label.Text=J.S(draft,"label",name);disk.Text=J.S(draft,"disk_path",J.S(draft,"kind")=="disk"?J.S(draft,"path"):"");iso.Text=J.S(draft,"iso_path",J.S(draft,"kind")=="iso"?J.S(draft,"path"):"");
        ram.Value=Math.Max(ram.Minimum,Math.Min(ram.Maximum,J.N(draft,"ram_mb",4096)));cores.Value=Math.Max(cores.Minimum,Math.Min(cores.Maximum,J.N(draft,"cores",2)));
        width.Value=Math.Max(width.Minimum,Math.Min(width.Maximum,J.N(draft,"screen_width",1920)));height.Value=Math.Max(height.Minimum,Math.Min(height.Maximum,J.N(draft,"screen_height",1080)));
        vga.SelectedItem=J.S(draft,"vga","std");LaunchProfiles.Load(profile,draft);
        launchSettings.Load(draft);jsonCurrent=false;
    }
    public void OpenPage(string page) {
        if(page=="network")tabs.SelectedPage=launchSettings.NetworkPage;
        else if(page=="extra")tabs.SelectedPage=launchSettings.ExtraPage;
    }
    Dictionary<string,object> FieldsData() {
        var updates=J.O("label",label.Text,"ram_mb",(int)ram.Value,"cores",(int)cores.Value,
            "screen_width",(int)width.Value,"screen_height",(int)height.Value,"vga",Convert.ToString(vga.SelectedItem),
            "launch_profile",LaunchProfiles.Value(profile),"disk_format",Path.GetExtension(disk.Text.Trim()).Equals(".vdi",StringComparison.OrdinalIgnoreCase)?"vdi":"qcow2","disk_path",disk.Text.Trim(),"iso_path",iso.Text.Trim());
        var data=new Dictionary<string,object>(draft);foreach(var pair in updates)data[pair.Key]=pair.Value;LaunchProfiles.Apply(profile,data);return launchSettings.Merge(data);
    }
    void Save() {
        try{
            string content=jsonCurrent?raw.Text:J.Json().Serialize(FieldsData());string choice=appearance.Choice;
            if(initialFields!=null&&content==initialFields){main.Visuals.SaveChoice(name,choice);allowClose=true;Close();main.Reload();return;}
            var args=J.O("name",name,"hash",J.S(original,"hash"),"raw",content);
            RunWrite(main,"Сохраняю параметры и резервную копию…",()=>main.Backend.Call("save",args),result=>{main.SaveAppearanceAfterOperation(name,choice);allowClose=true;Close();main.Reload();});
        }catch(Exception error){if(Program.TestMode)main.TestError=error;else MessageBox.Show(this,error.Message,"Параметры VM");}
    }
}

sealed class CreateDialog : MotionDialog {
    readonly MainWindow main;readonly object[] templates;readonly FieldInput name=Theme.Input(),label=Theme.Input(),disk=Theme.Input(),iso=Theme.Input();
    readonly ThemedSelector template=Theme.Combo("Новые настройки"),mode=Theme.Combo("Установка ISO на новый диск","Существующий диск","Только Live ISO"),profile=LaunchProfiles.Selector();
    readonly NumberInput ram=Theme.Number(4096,1,1048576),cores=Theme.Number(2,1,256),size=Theme.Number(60,1,65536);
    readonly Dictionary<string,string> replacements=new Dictionary<string,string>();
    string initialDraft;bool allowClose,jsonCurrent,syncingJson;readonly ThemedEditor raw=Theme.Editor();Dictionary<string,object> jsonDraft;
    internal readonly LaunchSettingsUi launchSettings;internal readonly AppearanceView appearance;readonly PageDeck tabs;
    public CreateDialog(MainWindow main,object saved,bool fromBackup=false,string selectedTemplate=null) {
        this.main=main;var profileField=LaunchProfiles.Field(profile,main.Backend,this);templates=J.A(saved);Text="Добавление виртуальной машины";Theme.Form(this,960,790);
        foreach(object item in templates)template.Items.Add(J.S(item,"backup_name",J.S(item,"id")));
        var sourceChoices=new ChoiceStrip(3,"Новый диск и ISO","Существующий диск","Загрузка с ISO");sourceChoices.Bind(mode);
        var fields=Theme.Fields();Theme.Row(fields,"Параметры из копии",template,true);Theme.Row(fields,"Короткое имя VM",name);Theme.Row(fields,"Название",label);Theme.Row(fields,"Источник",sourceChoices,true);
        var diskPicker=Theme.BrowseNewDisk(disk,()=>mode.SelectedIndex==0,()=>name.Text);var isoPicker=Theme.Browse(iso,false,"ISO|*.iso|Все файлы|*.*");
        Theme.Row(fields,"Виртуальный диск",diskPicker);Theme.Row(fields,"ISO",isoPicker);Theme.Row(fields,"Размер диска, ГБ",size);
        Theme.Row(fields,"Память, МБ",ram);Theme.Row(fields,"Ядра CPU",cores);Theme.Row(fields,"Профиль запуска",profileField);
        var missing=Theme.Button("Заменить доп. файлы…",MissingFiles);Theme.Row(fields,"Файлы из копии",missing);
        Theme.Row(fields,"",Theme.Label("Копия переносит параметры, включая сеть.\r\nДиск и ISO выбираются отдельно.\r\nПосле создания VM появится в списке.",10,Theme.Muted));
        tabs=new PageDeck{Dock=DockStyle.Fill,Padding=new Padding(0)};var common=new PageView("Параметры VM"){BackColor=Theme.Panel};common.Controls.Add(Theme.FieldsView(fields));tabs.Pages.Add(common);
        launchSettings=new LaunchSettingsUi(main,this,tabs,CreateData);launchSettings.Load(LaunchSettingsUi.Defaults());
        appearance=new AppearanceView(main.Visuals,()=>J.O("iso_path",iso.Text,"label",label.Text,"disk_path",disk.Text),()=>name.Text);
        var appearancePage=new PageView("Оформление");appearancePage.Controls.Add(appearance);tabs.Pages.Add(appearancePage);
        iso.Leave+=(sender,args)=>appearance.RefreshPreview();label.Leave+=(sender,args)=>appearance.RefreshPreview();
        var jsonPage=new PageView("Конфигурация JSON");raw.Font=UiTypography.Make(11);jsonPage.Controls.Add(raw);tabs.Pages.Add(jsonPage);
        raw.TextChanged+=(s,e)=>{if(!syncingJson)jsonCurrent=true;};
        tabs.Selecting+=(s,e)=>{try{if(e.Page==jsonPage){if(!jsonCurrent){syncingJson=true;try{raw.Text=J.Json().Serialize(CreateData());}finally{syncingJson=false;}jsonCurrent=true;}}else EnsureCreationVisual();}catch(Exception error){e.Cancel=true;if(Program.TestMode)main.TestError=error;else MessageBox.Show(this,error.Message,"Конфигурация JSON",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
        var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Создать VM",Create,true));buttons.Controls.Add(Theme.Button("Отмена",Close));Theme.SettingsDialog(this,tabs,buttons,"Новая машина","От образа системы до первого запуска.",true);
        template.SelectedIndexChanged+=(sender,args)=>LoadTemplate();mode.SelectedIndexChanged+=(sender,args)=>{diskPicker.Enabled=mode.SelectedIndex!=2;isoPicker.Enabled=mode.SelectedIndex!=1;size.Enabled=mode.SelectedIndex==0;};
        if(selectedTemplate!=null){int index=Array.FindIndex(templates,item=>J.S(item,"id")==selectedTemplate);if(index>=0)template.SelectedIndex=index+1;}
        else if(fromBackup && templates.Length>0)template.SelectedIndex=1;
        try{initialDraft=DraftFingerprint();}catch(Exception){initialDraft=null;}
        FormClosing+=(sender,args)=>{if(allowClose||Program.TestMode)return;bool changed;try{changed=initialDraft==null||DraftFingerprint()!=initialDraft;}catch(Exception){changed=true;}if(changed&&MessageBox.Show(this,"Закрыть создание VM без сохранения введённых параметров?","Несохранённые изменения",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)args.Cancel=true;};
    }
    string DraftFingerprint(){return J.Json().Serialize(J.O("name",name.Text,"data",CreateData(),"mode",mode.SelectedIndex,"size",size.Text,"template",template.SelectedIndex,"appearance",appearance.Choice,"replacements",replacements));}
    void LoadTemplate() {
        replacements.Clear();if(template.SelectedIndex<1){launchSettings.Load(LaunchSettingsUi.Defaults());return;}object saved=templates[template.SelectedIndex-1];object data=J.V(saved,"parameters");
        name.Text=J.S(saved,"original_vm_name");label.Text=J.S(data,"label",name.Text);ram.Value=Math.Max(ram.Minimum,Math.Min(ram.Maximum,J.N(data,"ram_mb",4096)));cores.Value=Math.Max(cores.Minimum,Math.Min(cores.Maximum,J.N(data,"cores",2)));
        LaunchProfiles.Load(profile,data);
        launchSettings.Load(data,J.V(saved,"network_view"));
    }
    void MissingFiles() {
        if(template.SelectedIndex<1){MessageBox.Show(this,"Сначала выбери копию параметров.");return;}
        object saved=templates[template.SelectedIndex-1];object data=J.V(saved,"parameters");string mainDisk=J.S(data,"disk_path",J.S(data,"kind")=="disk"?J.S(data,"path"):"");string mainIso=J.S(data,"iso_path",J.S(data,"kind")=="iso"?J.S(data,"path"):"");
        foreach(object item in J.A(J.V(saved,"missing_files")).Concat(J.A(J.V(saved,"deleted_files"))).Distinct()) {
            string old=Convert.ToString(item);if(old==mainDisk||old==mainIso||File.Exists(old))continue;
            if(!J.A(J.V(data,"extra_args")).Any(arg=>Convert.ToString(arg).Contains(old)||Convert.ToString(arg).Contains(old.Replace(",",",,"))))continue;
            using(var dialog=new OpenFileDialog { Title="Замена: "+Path.GetFileName(old),Filter="Все файлы|*.*" }){if(dialog.ShowDialog(this)==DialogResult.OK)replacements[old]=dialog.FileName;}
        }
        MessageBox.Show(this,"Выбрано замен: "+replacements.Count,"Дополнительные файлы");
    }
    void EnsureCreationVisual(){
        if(!jsonCurrent)return;object value=J.Json().DeserializeObject(raw.Text);if(!(value is Dictionary<string,object>))throw new InvalidOperationException("Конфигурация должна быть объектом JSON.");var data=J.D(value);
        int memory=J.N(data,"ram_mb",(int)ram.Value),cpu=J.N(data,"cores",(int)cores.Value);if(memory<ram.Minimum||memory>ram.Maximum||cpu<cores.Minimum||cpu>cores.Maximum)throw new InvalidOperationException("Проверь диапазоны памяти и числа ядер CPU в JSON.");
        jsonCurrent=false;label.Text=J.S(data,"label",name.Text);disk.Text=J.S(data,"disk_path");iso.Text=J.S(data,"iso_path");ram.Value=memory;cores.Value=cpu;LaunchProfiles.Load(profile,data);launchSettings.Load(data);jsonDraft=new Dictionary<string,object>(data);
    }
    Dictionary<string,object> CreateData(){
        EnsureCreationVisual();
        string diskValue=mode.SelectedIndex==2?"":disk.Text.Trim(),isoValue=mode.SelectedIndex==1?"":iso.Text.Trim();
        var data=J.O("label",String.IsNullOrWhiteSpace(label.Text)?name.Text:label.Text,"disk_format",Path.GetExtension(diskValue).Equals(".vdi",StringComparison.OrdinalIgnoreCase)?"vdi":"qcow2","disk_path",diskValue,"iso_path",isoValue,"ram_mb",(int)ram.Value,"cores",(int)cores.Value,
            "launch_profile",LaunchProfiles.Value(profile));
        if(jsonDraft!=null){var merged=new Dictionary<string,object>(jsonDraft);foreach(var pair in data)merged[pair.Key]=pair.Value;data=merged;}LaunchProfiles.Apply(profile,data);return launchSettings.Merge(data);
    }
    void Create() {
        try{var data=CreateData();
        var args=J.O("name",name.Text.Trim(),"data",data,"new_disk",mode.SelectedIndex==0,"size_gb",(int)size.Value,"replacements",replacements);
        args["network_managed"]=true;
        if(template.SelectedIndex>0)args["template"]=J.S(templates[template.SelectedIndex-1],"id");
        string createdName=name.Text.Trim(),choice=appearance.Choice;
        RunWrite(main,"Создаю VM…",()=>main.Backend.Call("create",args),result=>{main.SaveAppearanceAfterOperation(createdName,choice);allowClose=true;Close();main.Reload();});
        }catch(Exception error){if(Program.TestMode)main.TestError=error;else MessageBox.Show(this,error.Message,"Создание VM");}
    }
}

sealed class TemplatesWindow : MotionDialog {
    readonly object[] templates;
    readonly ThemedList list=Theme.ListBox(10);
    readonly Label heading=Theme.Label("Сохранённых копий пока нет",18,null,true);
    readonly ThemedEditor description=Theme.Editor();
    readonly ResourceSummary resources=new ResourceSummary();
    readonly Action<string> use;
    readonly Button create;

    public TemplatesWindow(object saved,Action<string> use) {
        this.use=use;
        templates=J.A(saved);
        Text="Копии параметров";
        Theme.Form(this,980,680);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,300));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        var library=new Panel{Dock=DockStyle.Fill,Padding=new Padding(18),BackColor=Theme.Panel};
        foreach(object item in templates)list.Items.Add(J.S(item,"backup_name",J.S(item,"id")));
        list.BackColor=Theme.Panel;
        library.Controls.Add(list);
        var details=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(24)};
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute,100));
        details.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        heading.MaximumSize=new Size(530,0);
        description.ReadOnly=true;description.BorderStyle=BorderStyle.None;description.BackColor=Theme.Bg;
        resources.Dock=DockStyle.Fill;
        details.Controls.Add(heading,0,0);details.Controls.Add(resources,0,1);details.Controls.Add(description,0,2);
        layout.Controls.Add(library,0,0);layout.Controls.Add(details,1,0);
        var buttons=Theme.Flow();
        create=Theme.Button("Создать VM из копии",Choose,true);create.Enabled=templates.Length>0;
        buttons.Controls.Add(create);buttons.Controls.Add(Theme.Button("Закрыть",Close));
        Theme.AuxiliaryDialog(this,layout,buttons,"Копии параметров","Знакомые настройки для новой установки системы.");
        list.SelectedIndexChanged+=(sender,args)=>SelectTemplate();
        list.DoubleClick+=(sender,args)=>Choose();
        if(templates.Length>0)list.SelectedIndex=0;
        else description.Text="Именные копии появятся здесь после удаления VM с сохранением параметров.\r\n\r\nКопия хранит настройки. Диск с системой и ISO выбираются отдельно.";
    }
    void SelectTemplate() {
        if(list.SelectedIndex<0)return;
        object item=templates[list.SelectedIndex],data=J.V(item,"parameters");
        heading.Text=J.S(item,"backup_name",J.S(item,"id"));
        resources.ShowData(data,true);
        description.Text="Исходная VM: "+J.S(item,"original_vm_name")+"\r\nСоздана: "+J.S(item,"created_utc")+"\r\n\r\nЧто переносится:\r\n• Ресурсы и профиль запуска\r\n• Настройки сети\r\n• Дополнительные аргументы\r\n\r\nДиск и ISO выбираются отдельно. Перед созданием настройки можно изменить.";
    }
    void Choose() {
        if(list.SelectedIndex<0)return;
        string id=J.S(templates[list.SelectedIndex],"id");
        Close();use(id);
    }
}

sealed class DeleteDialog : MotionDialog {
    readonly MainWindow main;readonly string vmName;object plan;readonly FieldInput backup=Theme.Input(),confirm=Theme.Input();
    readonly ThemedSelector mode=Theme.Combo("Убрать из списка · файлы сохранить","Удалить VM и её личные файлы");
    readonly CheckBox saveBackup=new CheckBox{Text="Сохранить копию параметров",Checked=true,AutoSize=true,ForeColor=Theme.Text};
    readonly ThemedEditor files=Theme.Editor();readonly Label note=Theme.Label("",10,Theme.Muted);readonly Button delete;bool planning;
    public DeleteDialog(MainWindow main,object plan) {
        this.main=main;this.plan=plan;vmName=J.S(plan,"name");Text="Удаление | "+vmName;Theme.Form(this,850,800);
        mode.SelectedIndex=J.D(plan).ContainsKey("remove_files")&&!J.B(plan,"remove_files")?0:1;
        var fields=Theme.Fields();files.ReadOnly=true;files.Height=250;files.Font=UiTypography.Points(9F);
        var deleteChoices=new ChoiceStrip(2,"Только убрать из списка","Удалить личные файлы");deleteChoices.Bind(mode);
        var machine=new ReferenceDataCard();object data=J.V(main.Row(vmName),"data");machine.Present(new[]{"Machine","State","Диск","ISO"},new[]{J.S(data,"label",vmName),Theme.State(main.State(vmName)),Path.GetFileName(J.S(data,"disk_path")),String.IsNullOrEmpty(J.S(data,"iso_path"))?"Не подключён":Path.GetFileName(J.S(data,"iso_path"))});Theme.Row(fields,"",machine,true);Theme.Row(fields,"Действие",deleteChoices,true);Theme.Row(fields,"",saveBackup,true);
        Theme.Row(fields,"Имя копии параметров",backup);Theme.Row(fields,"Введи имя VM",confirm);Theme.Row(fields,"",files);Theme.Row(fields,"",note);
        var buttons=Theme.Flow();delete=Theme.Button("Удалить VM",Delete,false,true);delete.Enabled=false;
        backup.TextChanged+=(sender,args)=>ValidateChoice();confirm.TextChanged+=(sender,args)=>ValidateChoice();saveBackup.CheckedChanged+=(sender,args)=>{backup.Enabled=saveBackup.Checked;ValidateChoice();};
        mode.SelectedIndexChanged+=(sender,args)=>LoadPlan();
        buttons.Controls.Add(Theme.Button("Отмена",Close));buttons.Controls.Add(delete);Theme.AuxiliaryDialog(this,fields,buttons,"Удалить машину",J.S(plan,"name"),"УДАЛЕНИЕ МАШИНЫ");Render();
    }
    void ValidateChoice(){delete.Enabled=!planning&&plan!=null&&confirm.Text==J.S(plan,"name")&&(!saveBackup.Checked||!String.IsNullOrWhiteSpace(backup.Text));}
    void Render(){
        if(plan==null)return;var text=new StringBuilder("Будет удалено:\r\n");foreach(object item in J.A(J.V(plan,"files")))text.AppendLine(Convert.ToString(item));
        text.AppendLine("\r\nОстанется на диске:");foreach(object item in J.A(J.V(plan,"kept")))text.AppendLine(String.Join(" — ",J.A(item).Select(Convert.ToString)));
        if(J.A(J.V(plan,"missing")).Length>0){text.AppendLine("\r\nУже отсутствует:");foreach(object item in J.A(J.V(plan,"missing")))text.AppendLine(Convert.ToString(item));}files.Text=text.ToString();
        bool removeFiles=mode.SelectedIndex==1;delete.Text=removeFiles?"Удалить VM":"Убрать из списка";
        note.Text=removeFiles?"Будут удалены только файлы из списка выше. Общие диски и ISO сохраняются.":"Удалится только JSON записи. Диски, ISO, логи и прежние копии останутся на месте.";
        note.Text+="\r\nКопия параметров содержит настройки, без дисков и ОС.";ValidateChoice();
    }
    async void LoadPlan(){
        if(main.Backend==null)return;bool removeFiles=mode.SelectedIndex==1;plan=null;planning=true;mode.Enabled=false;ValidateChoice();
        try{var result=await Task.Run(()=>main.Backend.Call("delete_plan",J.O("name",vmName,"remove_files",removeFiles)));if(!IsDisposed&&!ClosingWithMotion){plan=result;Render();}}
        catch(Exception error){if(!IsDisposed&&!ClosingWithMotion)MessageBox.Show(this,error.Message,"Проверка удаления",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        finally{if(!IsDisposed){planning=false;mode.Enabled=true;ValidateChoice();}}
    }
    void Delete(){
        if(main.Busy)return;var args=J.O("name",vmName,"token",J.S(plan,"token"),"backup_name",backup.Text,"save_backup",saveBackup.Checked,"confirm",confirm.Text);
        mode.Enabled=saveBackup.Enabled=delete.Enabled=false;
        RunWrite(main,"Удаляю выбранные данные VM…",()=>main.Backend.Call("delete",args),result=>{main.SaveAppearanceAfterOperation(vmName,"auto");Close();main.Reload();},error=>{if(!IsDisposed){mode.Enabled=saveBackup.Enabled=true;ValidateChoice();}});
    }
}

sealed class DoctorWindow : MotionDialog {
    public DoctorWindow(MainWindow main,object report) {
        Text="VM Doctor";Theme.Form(this,960,780);
        var output=Theme.Flow(true);output.Padding=new Padding(24,8,24,8);var scroll=new ScrollSurface(output);
        foreach(object item in J.A(J.V(report,"checks")))output.Controls.Add(new DiagnosticCard(item));
        output.SizeChanged+=(sender,args)=>{int width=Math.Max(260,output.ClientSize.Width-output.Padding.Horizontal);foreach(Control card in output.Controls)card.Width=width;};
        var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Сохранить отчёт",()=>RunWrite(main,"Сохраняю обезличенный отчёт…",()=>main.Backend.Call("report_save",J.O("token",J.S(report,"token"))),result=>MessageBox.Show(this,J.S(result,"path"),"Отчёт сохранён")),true));buttons.Controls.Add(Theme.Button("Закрыть",Close));Theme.AuxiliaryDialog(this,scroll,buttons,"VM Doctor","Проверки компьютера и конфигурации виртуальных машин.","ДИАГНОСТИКА");
    }
}

static class Program {
    internal static bool TestMode=false;
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [STAThread] static void Main(string[] args) {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture=System.Globalization.CultureInfo.GetCultureInfo("ru-RU");System.Globalization.CultureInfo.DefaultThreadCurrentUICulture=System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string root=Environment.GetEnvironmentVariable("QEMUM_DATA_DIR")??AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'),python=Environment.GetEnvironmentVariable("QEMUM_PYTHON"),preview=null;
        string libraryLink=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"manager-library.json");
        if(!args.Contains("--preview")&&File.Exists(libraryLink)){
            var link=J.Json().DeserializeObject(File.ReadAllText(libraryLink));
            if(String.IsNullOrEmpty(Environment.GetEnvironmentVariable("QEMUM_APP_DIR")))Environment.SetEnvironmentVariable("QEMUM_APP_DIR",AppDomain.CurrentDomain.BaseDirectory);
            if(String.IsNullOrEmpty(Environment.GetEnvironmentVariable("QEMUM_DATA_DIR")))root=Path.GetFullPath(J.S(link,"root",root));
            if(String.IsNullOrEmpty(python))python=J.S(link,"python",python);
            string qemu=J.S(link,"qemu_dir");if(!String.IsNullOrEmpty(qemu))Environment.SetEnvironmentVariable("PATH",qemu+Path.PathSeparator+Environment.GetEnvironmentVariable("PATH"));
        }
        for(int i=0;i<args.Length;i++){if(args[i]=="--root"&&i+1<args.Length)root=Path.GetFullPath(args[++i]);else if(args[i]=="--python"&&i+1<args.Length)python=args[++i];else if(args[i]=="--preview"&&i+1<args.Length)preview=args[++i];}
        Directory.CreateDirectory(root);
        Application.ThreadException+=(sender,eventArgs)=>MessageBox.Show(eventArgs.Exception.Message,"VM Manager",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        System.Threading.Mutex mutex=null;bool acquired=false;
        try {
            if(preview==null) {
                string identity;using(var hash=System.Security.Cryptography.SHA256.Create())identity=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))).Replace("-","");
                mutex=new System.Threading.Mutex(false,"Local\\QemuVMManagerGUI-"+identity);
                try{acquired=mutex.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
                if(!acquired){foreach(var other in Process.GetProcessesByName("VMManager")){try{if(other.MainWindowHandle!=IntPtr.Zero && String.Equals(Path.GetDirectoryName(other.MainModule.FileName),root,StringComparison.OrdinalIgnoreCase)){SetForegroundWindow(other.MainWindowHandle);return;}}catch(Exception){}}MessageBox.Show("Менеджер уже открыт.","VM Manager");return;}
            }
            using(var window=new MainWindow(root,python,preview!=null)) {
                if(preview!=null) {
                    window.StartPosition=FormStartPosition.Manual;window.Location=new Point(-2400,-1600);
                    window.Shown+=(sender,eventArgs)=>{window.Demo();window.BeginInvoke(new Action(()=>{
                        using(var bitmap=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(preview,System.Drawing.Imaging.ImageFormat.Png);}
                        window.Close();
                    }));};
                }
                Application.Run(window);
            }
        } catch(Exception error) {MessageBox.Show(error.Message,"VM Manager",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        finally{if(mutex!=null){if(acquired)mutex.ReleaseMutex();mutex.Dispose();}}
    }
}
