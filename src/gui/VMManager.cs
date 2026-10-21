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
    public object Call(string command,object arguments=null) {
        lock(gate) {
            if(process==null || process.HasExited) throw new IOException("Фоновая служба завершилась. Открой менеджер заново; работающие VM продолжат работу.");
            int id=++next;
            input.WriteLine(J.Json().Serialize(J.O("id",id,"command",command,"args",arguments ?? J.O())));
            string line=process.StandardOutput.ReadLine();
            if(line==null) throw new IOException("Нет ответа фоновой службы. Работающие VM не остановлены.");
            object reply=J.Json().DeserializeObject(line);
            if(J.N(reply,"id")!=id) throw new IOException("Нарушен порядок ответов службы.");
            if(!J.B(reply,"ok")) throw new InvalidOperationException(J.S(reply,"error","Неизвестная ошибка."));
            return J.V(reply,"result");
        }
    }
    public void Dispose() {
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

sealed class MainWindow : Form {
    public Backend Backend; public bool Busy; public string RootPath; internal Exception TestError; readonly bool demo;
    List<object> rows=new List<object>(); Dictionary<string,string> states=new Dictionary<string,string>();
    string selected,mode="normal"; bool updatingMode,polling,closing; readonly string python;
    readonly FlowLayoutPanel cards=Theme.Flow(true), actions=Theme.Flow(); readonly Panel detail=new Panel();
    readonly Label heading=Theme.Label("Выбери виртуальную машину",25,null,true), description=Theme.Label("",10,Theme.Muted), status=Theme.Label("",10), footer=Theme.Label("Загрузка…",9,Theme.Muted);
    readonly ResourceSummary resources=new ResourceSummary();
    internal readonly AppearanceStore Visuals;readonly MonitorEmblem emblem=new MonitorEmblem();string visualKey;int visualRequest;
    readonly FieldInput search=new FieldInput{SearchIcon=true,Placeholder="Найти машину…"}; readonly LaunchModeSelector launchMode=new LaunchModeSelector();
    readonly ScrollSurface libraryScroll;
    readonly WorkspaceTabs workspace=new WorkspaceTabs();
    readonly Label libraryCount=Theme.Label("0",9,Theme.Muted),breadcrumb=Theme.Label("Виртуальные машины",9,Theme.Muted);
    readonly TextBox networkPreview=Theme.TextBox("",true),extraPreview=Theme.TextBox("",true),logPreview=Theme.TextBox("",true);
    readonly ProgressBar progress=new ProgressBar { Style=ProgressBarStyle.Marquee,Dock=DockStyle.Bottom,Height=3,Visible=false };
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer { Interval=2500 };
    readonly Dictionary<string,VmWindow> panels=new Dictionary<string,VmWindow>(); readonly Dictionary<string,LogsWindow> logs=new Dictionary<string,LogsWindow>();
    readonly System.ComponentModel.IContainer uiComponents=new System.ComponentModel.Container();
    readonly ContextMenuStrip libraryMenu,machineMenu;
    public List<object> Rows { get { return rows; } }
    public MainWindow(string root,string python=null,bool demo=false,bool autoStart=true) {
        RootPath=root;this.python=python;this.demo=demo;Visuals=new AppearanceStore(root);
        Visuals.Changed+=(sender,args)=>{visualKey=null;RefreshEmblem(selected,J.V(Row(selected),"data"));};
        libraryMenu=BuildMenu("Из копии параметров",(Action)(()=>CreateVm(true)),"Обновить список",(Action)Reload);
        machineMenu=BuildMenu("Панель VM",(Action)(()=>OpenPanel(selected)),"Диагностика VM",(Action)(()=>RunDoctor(selected)),"Перезагрузка",(Action)(()=>ControlVm(selected,"reset")),"Остановить QEMU",(Action)(()=>ControlVm(selected,"quit")),"Удалить VM",(Action)(()=>DeleteVm(selected)));
        Text="QEMU VM Manager";Theme.Form(this,1040,760);MinimumSize=new Size(920,680);
        var top=new BufferedPanel { Dock=DockStyle.Top,Height=43,Padding=new Padding(20,9,20,7) };
        var brand=Theme.Label("QEMU   /   VM Manager",11,null,true);brand.Dock=DockStyle.Left;
        var support=Theme.Flow();support.Dock=DockStyle.Right;support.AutoSize=false;support.Width=310;support.WrapContents=false;
        support.Controls.Add(Theme.Button("Помощь / отчёт",()=>{using(var window=new SupportWindow(this,selected))window.ShowDialog(this);}));
        support.Controls.Add(Theme.Button("Обновления",()=>AlphaIntegration.Updates(this)));
        top.Height=64;top.Controls.Add(brand);top.Controls.Add(support);
        var bottom=new Panel { Dock=DockStyle.Bottom,Height=32,Padding=new Padding(20,7,16,0) };
        footer.Dock=DockStyle.Fill;bottom.Controls.Add(footer);bottom.Controls.Add(progress);
        var content=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,Margin=Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,256));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        var left=new BufferedPanel { Dock=DockStyle.Fill,Padding=new Padding(14,22,12,12),BackColor=Theme.Panel };
        var libraryHeader=new Panel { Dock=DockStyle.Top,Height=137,BackColor=Theme.Panel };
        var category=Theme.Label("БИБЛИОТЕКА",8,Theme.Muted);category.Location=new Point(9,0);
        var library=Theme.Label("Мои машины",16,null,true);library.Location=new Point(9,22);
        libraryCount.Location=new Point(9,56);libraryCount.ForeColor=Theme.Muted;
        search.Dock=DockStyle.None;search.SetBounds(9,80,208,40);search.AccessibleName="Поиск машин";
        search.TextChanged+=(sender,args)=>RenderCards();
        var more=Theme.Button("···",()=>{});more.MinimumSize=Size.Empty;more.AutoSize=false;more.Size=new Size(29,25);more.Location=new Point(192,0);more.Padding=Padding.Empty;more.BackColor=Theme.Panel;more.ForeColor=Theme.Muted;more.AccessibleName="Меню библиотеки";more.Click+=(sender,args)=>ShowLibraryMenu(more);
        libraryHeader.Controls.Add(category);libraryHeader.Controls.Add(library);libraryHeader.Controls.Add(libraryCount);libraryHeader.Controls.Add(search);libraryHeader.Controls.Add(more);
        cards.WrapContents=false;cards.BackColor=Theme.Panel;
        libraryScroll=new ScrollSurface(cards) { BackColor=Theme.Panel };
        cards.SizeChanged+=(sender,args)=>{foreach(Control card in cards.Controls)card.Width=cards.Width;};
        var tools=Theme.Flow(true);tools.Dock=DockStyle.Bottom;tools.BackColor=Theme.Panel;tools.Padding=new Padding(9,20,0,0);
        var add=Theme.Button("＋  Новая машина",CreateVm);add.AutoSize=false;add.Width=208;tools.Controls.Add(add);
        var doctor=Theme.Button("VM Doctor",()=>RunDoctor(null));doctor.BackColor=Theme.Panel;doctor.ForeColor=Theme.Muted;doctor.AutoSize=false;doctor.Width=208;doctor.Height=34;doctor.MinimumSize=Size.Empty;tools.Controls.Add(doctor);
        left.Controls.Add(libraryScroll);left.Controls.Add(libraryHeader);left.Controls.Add(tools);
        detail.Dock=DockStyle.Fill;detail.Padding=new Padding(32,24,30,14);detail.BackColor=Theme.Bg;
        var detailStack=Theme.Flow(true);detailStack.Dock=DockStyle.Top;
        breadcrumb.Margin=new Padding(0,0,0,24);detailStack.Controls.Add(breadcrumb);
        var hero=new TableLayoutPanel { AutoSize=true,Width=680,ColumnCount=2,RowCount=3,MinimumSize=new Size(0,201),BackColor=Theme.Bg,Margin=Padding.Empty };
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,80));hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute,91));hero.RowStyles.Add(new RowStyle(SizeType.AutoSize));hero.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        emblem.Margin=new Padding(0,3,0,0);hero.Controls.Add(emblem,0,0);
        status.AutoSize=false;status.Dock=DockStyle.Top;status.Height=23;status.TextAlign=ContentAlignment.MiddleRight;status.Margin=new Padding(0,9,0,0);hero.Controls.Add(status,1,0);
        heading.AutoSize=true;heading.MaximumSize=new Size(680,0);heading.Margin=new Padding(0,0,0,8);hero.Controls.Add(heading,0,1);hero.SetColumnSpan(heading,2);
        description.Margin=new Padding(0,0,0,24);description.Text="Твоя лаборатория. Всё под контролем.";hero.Controls.Add(description,0,2);hero.SetColumnSpan(description,2);
        detailStack.Controls.Add(hero);
        actions.FlowDirection=FlowDirection.TopDown;actions.WrapContents=false;actions.Width=680;
        var primary=Theme.Flow();
        primary.Controls.Add(Theme.Button("▶ Запустить",()=>StartVm(selected),true));
        primary.Controls.Add(Theme.Button("Снимки дисков",()=>Snapshots(selected)));
        primary.Controls.Add(Theme.Button("Параметры",()=>EditVm(selected)));
        actions.Controls.Add(primary);
        launchMode.AutoSize=false;launchMode.Size=new Size(200,32);launchMode.Margin=new Padding(0,0,0,8);actions.Controls.Add(launchMode);
        var overview=new Panel { Padding=new Padding(0,18,0,0) };
        resources.Dock=DockStyle.Top;resources.Height=83;
        var power=Theme.Flow();power.Dock=DockStyle.Top;power.Padding=new Padding(0,20,0,0);
        power.Controls.Add(Theme.Button("Пауза / продолжить",()=>ControlVm(selected,"toggle")));
        power.Controls.Add(Theme.Button("Выключить",()=>ControlVm(selected,"shutdown")));
        power.Controls.Add(Theme.Button("Ещё действия",()=>ShowMachineMenu(power)));
        overview.Controls.Add(power);overview.Controls.Add(resources);
        workspace.Add("Обзор",overview);
        workspace.Add("Сеть",PreviewPage(networkPreview,"Настроить адаптеры",()=>EditVm(selected,"network")));
        workspace.Add("Extra args",PreviewPage(extraPreview,"Открыть редактор",()=>EditVm(selected,"extra")));
        workspace.Add("Живые логи",PreviewPage(logPreview,"Открыть живые логи",()=>OpenLogs(selected)));
        workspace.SelectionChanged+=(sender,args)=>{if(workspace.SelectedIndex==3)ReadLogPreview();};
        workspace.Margin=Padding.Empty;actions.Controls.Add(workspace);detailStack.Controls.Add(actions);
        var note=Theme.Label("Локальная библиотека",9,Theme.Muted);note.Margin=new Padding(0,22,0,0);detailStack.Controls.Add(note);
        detail.Controls.Add(new ScrollSurface(detailStack) { BackColor=Theme.Bg });content.Controls.Add(left,0,0);content.Controls.Add(detail,1,0);
        Controls.Add(content);Controls.Add(top);Controls.Add(bottom);
        launchMode.SelectedIndexChanged+=(sender,args)=>{
            if(updatingMode||Backend==null)return;
            string value=launchMode.SelectedIndex==1?"snapshot":"normal";
            Run("Сохраняю режим…",()=>Backend.Call("mode",J.O("value",value)),result=>mode=value);
        };
        Action arrange=()=>{
            int width=Math.Max(270,detail.ClientSize.Width-detail.Padding.Horizontal-12);
            hero.MinimumSize=new Size(width,201);
            detailStack.MinimumSize=actions.MinimumSize=new Size(width,0);
            detailStack.MaximumSize=actions.MaximumSize=new Size(width,0);
            heading.MaximumSize=description.MaximumSize=new Size(width,0);
            primary.MaximumSize=power.MaximumSize=new Size(width,0);
            int tabHeight=Math.Max(215,detail.ClientSize.Height-detail.Padding.Vertical-hero.Height-215);
            workspace.MinimumSize=workspace.MaximumSize=new Size(width,tabHeight);
            workspace.Size=new Size(width,tabHeight);
        };
        detail.Resize+=(sender,args)=>arrange();
        Shown+=(sender,args)=>arrange();
        timer.Tick+=(sender,args)=>Poll();FormClosing+=OnClosing;
        Shown+=(sender,args)=>{if(!demo&&autoStart)Run("Открываю библиотеку…",()=>{Backend=new Backend(root,python);Backend.Open();return Backend.Call("list");},result=>{Fill(result);timer.Start();Poll();AlphaIntegration.Ready();});};
        SelectVm(null);
    }
    Panel PreviewPage(TextBox preview,string buttonTitle,Action action) {
        var page=new Panel { Padding=new Padding(0,20,0,0),BackColor=Theme.Bg };
        preview.ReadOnly=true;preview.BorderStyle=BorderStyle.None;preview.BackColor=Theme.Bg;
        preview.Font=new Font("Consolas",10F);preview.WordWrap=true;preview.ScrollBars=ScrollBars.None;
        var buttons=Theme.Flow();buttons.Dock=DockStyle.Bottom;buttons.Controls.Add(Theme.Button(buttonTitle,action));
        page.Controls.Add(preview);page.Controls.Add(buttons);return page;
    }
    ContextMenuStrip BuildMenu(params object[] entries) {
        var menu=new ContextMenuStrip(uiComponents) { BackColor=Theme.Panel,ForeColor=Theme.Text,Font=new Font("Segoe UI",10F),ShowImageMargin=false,ShowCheckMargin=false,Padding=new Padding(6),Renderer=new ToolStripProfessionalRenderer(new NightMenuColors()) };
        for(int index=0;index<entries.Length;index+=2){var action=(Action)entries[index+1];var item=menu.Items.Add((string)entries[index],null,(sender,args)=>action());item.Padding=new Padding(12,10,12,10);}
        return menu;
    }
    void ShowLibraryMenu(Control anchor) {
        libraryMenu.Show(anchor,new Point(0,anchor.Height+6));
    }
    void ShowMachineMenu(Control anchor) {
        machineMenu.Show(anchor,new Point(0,anchor.Height));
    }
    void ReadLogPreview() {
        if(Backend==null||selected==null){logPreview.Text="Журнал появится после запуска машины.";return;}
        string name=selected;
        Run("Читаю фрагмент журнала…",()=>Backend.Call("logs",J.O("name",name)),value=>{
            if(selected!=name)return;
            var lines=J.A(J.V(value,"events")).Select(item=>J.S(item,"text")).ToArray();
            logPreview.Text=lines.Length==0?"Событий пока нет. Полный вывод QEMU доступен в окне живых логов.":String.Join(Environment.NewLine,lines.Skip(Math.Max(0,lines.Length-7)));
        });
    }
    public void Post(Action action) { if(!IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch(InvalidOperationException) {} } }
    public void Run(string message,Func<object> operation,Action<object> done=null) {
        if(demo) return;
        if(Busy) { footer.Text="Дождись завершения текущей операции.";return; }
        Busy=true;progress.Visible=true;footer.Text=message;actions.Enabled=false;launchMode.Enabled=false;
        Task.Run(()=>{try {object result=operation();Post(()=>{Busy=false;progress.Visible=false;actions.Enabled=selected!=null;launchMode.Enabled=true;footer.Text="Готово";if(done!=null)done(result);});}
            catch(Exception error){Post(()=>{Busy=false;progress.Visible=false;actions.Enabled=selected!=null;launchMode.Enabled=true;footer.Text="Действие не выполнено";if(Program.TestMode)TestError=error;else MessageBox.Show(this,error.Message,"VM Manager",MessageBoxButtons.OK,MessageBoxIcon.Warning);});}});
    }
    void Fill(object result) { rows=J.A(J.V(result,"rows")).ToList();mode=J.S(result,"mode","normal");updatingMode=true;launchMode.SelectedIndex=mode=="snapshot"?1:0;updatingMode=false;if(selected==null || !rows.Any(row=>J.S(row,"name")==selected))selected=rows.Count>0?J.S(rows[0],"name"):null;RenderCards();SelectVm(selected);footer.Text="Машин: "+rows.Count+" · все действия доступны в интерфейсе"; }
    public void Reload() { if(Backend!=null)Run("Обновляю список…",()=>Backend.Call("list"),Fill); }
    public object Row(string name) { return rows.FirstOrDefault(row=>J.S(row,"name")==name); }
    public string State(string name) { string result;return name!=null && states.TryGetValue(name,out result)?result:"checking"; }
    void RenderCards() {
        int scroll=libraryScroll.ScrollOffset;
        cards.SuspendLayout();foreach(Control child in cards.Controls.Cast<Control>().ToArray())child.Dispose();cards.Controls.Clear();
        foreach(object row in rows) {
            string name=J.S(row,"name");object data=J.V(row,"data");string label=J.S(data,"label",name);
            if((name+" "+label).IndexOf(search.Text,StringComparison.OrdinalIgnoreCase)<0)continue;
            var card=new LibraryCard(name,label,Theme.VmColor(row)) { Width=Math.Max(170,cards.Width),BackColor=name==selected?Theme.Card:Theme.Panel };
            card.SetStatus(State(name));
            EventHandler select=(sender,args)=>SelectVm(name);card.Click+=select;card.DoubleClick+=(sender,args)=>OpenPanel(name);
            foreach(Control control in card.Controls){control.Click+=select;control.DoubleClick+=(sender,args)=>OpenPanel(name);}
            cards.Controls.Add(card);
        }
        if(rows.Count==0)cards.Controls.Add(Theme.Label("Виртуальных машин пока нет.\r\nНажми «Добавить VM».",10,Theme.Muted));
        cards.ResumeLayout();libraryScroll.PerformLayout();libraryScroll.ScrollOffset=scroll;
    }
    internal void SaveAppearanceAfterOperation(string name,string choice){
        try{Visuals.SaveChoice(name,choice);}catch(Exception error){
            if(Program.TestMode)TestError=error;else MessageBox.Show(this,"Операция с VM выполнена, но значок сохранить не удалось.\r\n"+error.Message,"Оформление",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
    }
    void RefreshEmblem(string name,object data){
        string choice;try{choice=Visuals.Choice(name);}catch(Exception){choice="auto";}
        string key=J.Json().Serialize(J.O("name",name,"choice",choice,"iso",J.S(data,"iso_path",J.S(data,"kind")=="iso"?J.S(data,"path"):""),"label",J.S(data,"label"),"disk",J.S(data,"disk_path")));
        if(visualKey==key)return;visualKey=key;int request=++visualRequest;emblem.Logo=null;if(name==null)return;
        Task.Run(()=>{Distro distro=null;try{distro=Visuals.Resolve(choice,data,name);}catch(Exception){}
            Post(()=>{if(request==visualRequest&&!IsDisposed)emblem.Logo=Visuals.Logo(distro);});
        });
    }
    void SelectVm(string name) {
        bool changed=selected!=name;selected=name;var row=Row(name);object data=J.V(row,"data");
        RefreshEmblem(name,data);
        heading.Text=row==null?"Выбери виртуальную машину":J.S(data,"label",name);heading.ForeColor=Theme.Text;
        description.Text=row==null?"Добавь машину или выбери её в списке.":"Твоя лаборатория. Всё под контролем.";resources.ShowData(data,row!=null);
        breadcrumb.Text=row==null?"Виртуальные машины":"Виртуальные машины   /   "+name;
        libraryCount.Text="Всего машин: "+rows.Count;
        var adapters=J.A(J.V(J.V(row,"network_view"),"adapters"));
        if(adapters.Length==0&&J.D(data).ContainsKey("network_adapters"))adapters=J.A(J.V(data,"network_adapters"));
        networkPreview.Text=adapters.Length==0?"Нет распознанных сетевых адаптеров. Открой настройки сети для подробностей.":String.Join(Environment.NewLine,adapters.Select(item=>J.S(item,"id")+"   ·   "+J.S(item,"type")+"   ·   "+J.S(item,"model")));
        string extra=ArgText.Format(J.A(J.V(data,"extra_args")));extraPreview.Text=String.IsNullOrEmpty(extra)?"Дополнительные аргументы не заданы.":extra;
        if(row==null)logPreview.Text="Выбери виртуальную машину.";
        else if(changed){logPreview.Text="Журнал машины: "+name;if(workspace.SelectedIndex==3&&!Busy)ReadLogPreview();}
        status.Text=row==null?"":Theme.State(State(name));status.ForeColor=Theme.StateColor(State(name));actions.Enabled=row!=null && !Busy;
        foreach(Control card in cards.Controls) {
            Color color=Object.Equals(card.Tag,name)?Theme.Card:Theme.Panel;
            if(card.BackColor!=color)card.BackColor=color;
        }
    }
    void Poll() {
        if(Backend==null || Busy || polling || closing)return;polling=true;
        Task.Run(()=>{try {object result=Backend.Call("states");Post(()=>ApplyStates(result));}
            catch(Exception error){Post(()=>footer.Text=error.Message);}finally{polling=false;}});
    }
    internal void ApplyStates(object result) {
        var next=J.D(result).ToDictionary(pair=>pair.Key,pair=>Convert.ToString(pair.Value));
        foreach(Control card in cards.Controls) {
            string name=card.Tag as string;if(name==null)continue;string value;if(!next.TryGetValue(name,out value))value="checking";
            if(value==State(name))continue;
            var libraryCard=card as LibraryCard;if(libraryCard!=null)libraryCard.SetStatus(value);
        }
        states=next;SelectVm(selected);foreach(var panel in panels.Values.ToArray())panel.RefreshState();
    }
    public void OpenPanel(string name) { if(name==null)return;VmWindow panel;if(panels.TryGetValue(name,out panel)&&!panel.IsDisposed){panel.Activate();return;}panel=new VmWindow(this,name);panels[name]=panel;panel.FormClosed+=(sender,args)=>panels.Remove(name);panel.Show(); }
    public void OpenLogs(string name) { if(name==null)return;LogsWindow window;if(logs.TryGetValue(name,out window)&&!window.IsDisposed){window.Activate();return;}window=new LogsWindow(this,name);logs[name]=window;window.FormClosed+=(sender,args)=>logs.Remove(name);window.Show(); }
    public void StartVm(string name,object snapshot=null) {
        if(name==null||Backend==null)return;
        if(snapshot==null && mode=="snapshot"){Snapshots(name,true);return;}
        object data=J.V(Row(name),"data");string boot=String.IsNullOrEmpty(J.S(data,"disk_path",J.S(data,"kind")=="disk"?J.S(data,"path"):""))?"iso":"disk";
        bool iso=!String.IsNullOrEmpty(J.S(data,"iso_path",J.S(data,"kind")=="iso"?J.S(data,"path"):""));
        if(snapshot==null && boot=="disk" && iso) { using(var choice=new ChoiceDialog("Источник загрузки",new[]{"Виртуальный диск","Установочный / Live ISO"})) {if(choice.ShowDialog(this)!=DialogResult.OK)return;boot=choice.Selected==0?"disk":"iso";} }
        if(snapshot!=null)boot="disk";
        Run("Подготавливаю запуск…",()=>Backend.Call("start",J.O("name",name,"boot",boot,"snapshot",snapshot)),result=>{footer.Text=J.B(result,"attached")?"VM уже работает · панель подключена":"QEMU запущен · "+J.S(result,"accelerator").ToUpperInvariant();OpenPanel(name);Poll();});
    }
    public void ControlVm(string name,string action) {
        if(name==null||Backend==null)return;
        if(new[]{"shutdown","reset","quit"}.Contains(action)) {
            string message=action=="shutdown"?"Запросить штатное выключение гостевой ОС?":action=="reset"?"Жёстко перезагрузить VM? Несохранённые данные могут быть потеряны.":"Принудительно остановить QEMU? Несохранённые данные будут потеряны.";
            if(MessageBox.Show(this,name+"\r\n\r\n"+message,"Подтверждение",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        }
        Run("Выполняю действие…",()=>Backend.Call("control",J.O("name",name,"action",action,"confirm",name)),result=>{footer.Text=action=="snapshot"?"Снимок дисков создан":"Запрос отправлен VM";Poll();});
    }
    public void Snapshots(string name,bool boot=false) {if(name==null||Backend==null)return;Run("Читаю снимки дисков…",()=>Backend.Call("snapshots",J.O("name",name)),result=>{var dialog=new SnapshotsWindow(this,name,result,boot);dialog.Show(this);});}
    void EditVm(string name,string page=null) {if(name==null||Backend==null)return;Run("Читаю параметры…",()=>Backend.Call("get",J.O("name",name)),result=>{var dialog=new EditDialog(this,name,result);dialog.OpenPage(page);dialog.Show(this);});}
    void CreateVm() {CreateVm(false);}
    void CreateVm(bool fromBackup) {
        if(Backend==null)return;
        Run("Читаю сохранённые параметры…",()=>Backend.Call("template_list"),result=>{
            if(fromBackup){
                var library=new TemplatesWindow(result,id=>new CreateDialog(this,result,false,id).Show(this));
                library.Show(this);
            }else new CreateDialog(this,result).Show(this);
        });
    }
    void DeleteVm(string name) {if(name==null||Backend==null)return;Run("Проверяю файлы и состояние VM…",()=>Backend.Call("delete_plan",J.O("name",name)),result=>{var dialog=new DeleteDialog(this,result);dialog.Show(this);});}
    public void RunDoctor(string name) {if(Backend==null)return;Run("VM Doctor · выполняю проверки…",()=>Backend.Call("doctor",name==null?J.O():J.O("name",name)),result=>{var dialog=new DoctorWindow(this,result);dialog.Show(this);});}
    void OnClosing(object sender,FormClosingEventArgs args) {
        if(demo)return;
        if(Busy){args.Cancel=true;MessageBox.Show(this,"Дождись завершения текущей операции, чтобы не прервать работу с дисками.","VM Manager");return;}
        bool running=rows.Any(row=>State(J.S(row,"name"))!="stopped"&&State(J.S(row,"name"))!="error");
        if(!Program.TestMode && !closing && running && MessageBox.Show(this,"Закрыть менеджер? Работающие VM продолжат работу. Если статус неизвестен, VM также может быть запущена.","Закрытие менеджера",MessageBoxButtons.YesNo,MessageBoxIcon.Information)!=DialogResult.Yes){args.Cancel=true;return;}
        closing=true;timer.Stop();foreach(var window in panels.Values.Cast<Form>().Concat(logs.Values).ToArray())window.Close();if(Backend!=null)Backend.Dispose();
    }
    protected override void Dispose(bool disposing) {
        if(disposing){uiComponents.Dispose();Visuals.Dispose();}
        base.Dispose(disposing);
    }
    public void Demo() {rows=new List<object>{J.O("name","ubuntu-desktop","data",J.O("label","Ubuntu Desktop","ram_mb",8192,"cores",4,"launch_profile","auto","ui_color","#79B8FF")),J.O("name","ubuntu-server","data",J.O("label","Ubuntu Server","ram_mb",4096,"cores",2,"ui_color","#F4BD62")),J.O("name","kali","data",J.O("label","Kali Linux","ram_mb",4096,"cores",4,"ui_color","#C4A2FF")),J.O("name","tails","data",J.O("label","Tails","ram_mb",4000,"cores",4,"ui_color","#61D6C3"))};states=new Dictionary<string,string>{{"ubuntu-desktop","running"},{"ubuntu-server","stopped"},{"kali","paused"},{"tails","stopped"}};selected="ubuntu-desktop";RenderCards();SelectVm(selected);footer.Text="Машин: 4 · интерфейс приложения";}
}

sealed class ChoiceDialog : Form {
    public int Selected { get { return choices.SelectedIndex; } }
    readonly ThemedList choices=Theme.ListBox(11);
    public ChoiceDialog(string title,string[] values) {
        Text=title;Theme.Form(this,520,300);var panel=new Panel { Dock=DockStyle.Fill,Padding=new Padding(22) };
        choices.Items.AddRange(values);if(values.Length>0)choices.SelectedIndex=0;panel.Controls.Add(choices);
        var buttons=Theme.Flow();buttons.Dock=DockStyle.Bottom;buttons.Padding=new Padding(22,10,10,8);
        buttons.Controls.Add(Theme.Button("Выбрать",()=>{if(Selected>=0){DialogResult=DialogResult.OK;Close();}},true));buttons.Controls.Add(Theme.Button("Отмена",()=>{DialogResult=DialogResult.Cancel;Close();}));Theme.Dialog(this,panel,buttons,title,"Выбери один из доступных вариантов.");
    }
}

sealed class VmWindow : Form {
    readonly MainWindow main;readonly string name;readonly Label status=Theme.Label("",13);readonly FlowLayoutPanel actions=Theme.Flow();bool hasRun;
    public VmWindow(MainWindow main,string name) {
        this.main=main;this.name=name;object row=main.Row(name);object data=J.V(row,"data");
        Text=J.S(data,"label",name)+" | "+name+" | QEMU";Theme.Form(this,760,570);Icon=Theme.Icon(Theme.VmColor(row));
        var content=Theme.Flow(true);content.Dock=DockStyle.Fill;content.Padding=new Padding(26);content.BackColor=Theme.Bg;content.AutoScroll=true;
        content.Controls.Add(status);var summary=new ResourceSummary();summary.ShowData(data,true);summary.Width=670;content.Controls.Add(summary);
        actions.MaximumSize=new Size(580,0);actions.Margin=new Padding(0,16,0,0);
        actions.Controls.Add(Theme.Button("▶ Запустить",()=>main.StartVm(name),true));actions.Controls.Add(Theme.Button("Пауза / продолжить",()=>main.ControlVm(name,"toggle")));
        actions.Controls.Add(Theme.Button("Выключить",()=>main.ControlVm(name,"shutdown")));actions.Controls.Add(Theme.Button("Снимки дисков",()=>main.Snapshots(name)));
        actions.Controls.Add(Theme.Button("Живые логи",()=>main.OpenLogs(name)));actions.Controls.Add(Theme.Button("Диагностика",()=>main.RunDoctor(name)));
        actions.Controls.Add(Theme.Button("Перезагрузка",()=>main.ControlVm(name,"reset")));actions.Controls.Add(Theme.Button("Остановить QEMU",()=>main.ControlVm(name,"quit"),false,true));
        content.Controls.Add(actions);content.Controls.Add(Theme.Label("Закрытие этой панели оставляет VM работать.",9,Theme.Muted));Controls.Add(content);Controls.Add(Theme.Header(J.S(data,"label",name),name,"ПАНЕЛЬ МАШИНЫ"));RefreshState();
    }
    public void RefreshState() {
        string value=main.State(name);status.Text=Theme.State(value);status.ForeColor=Theme.StateColor(value);actions.Enabled=!main.Busy;
        bool online=new[]{"running","paused","prelaunch","shutdown"}.Contains(value);
        if(online)hasRun=true;
        Text=name+" | "+Theme.State(value)+" | QEMU";
        if(hasRun && (value=="stopped"||value=="error")){Close();if(!main.IsDisposed){main.Show();main.Activate();}}
    }
}

sealed class LogsWindow : Form {
    readonly MainWindow main;readonly string name;readonly ThemedEditor qemu=Theme.Editor(),events=Theme.Editor();
    readonly CheckBox follow=new CheckBox { Text="Автопрокрутка",Checked=true,AutoSize=true,ForeColor=Theme.Text },pause=new CheckBox { Text="Пауза просмотра",AutoSize=true,ForeColor=Theme.Text };
    readonly Label info=Theme.Label("Ожидание журнала…",9,Theme.Muted);readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer { Interval=1000 };
    bool pending;string file="";long offset,eventId;Decoder decoder=Encoding.UTF8.GetDecoder();
    public LogsWindow(MainWindow main,string name,bool demo=false) {
        this.main=main;this.name=name;Text="Живые логи | "+name;Theme.Form(this,1000,660);Icon=Theme.Icon(Theme.VmColor(main.Row(name)));
        var top=Theme.Flow();top.Padding=new Padding(22,8,18,8);top.Dock=DockStyle.Top;follow.Margin=new Padding(0,0,20,6);top.Controls.Add(follow);top.Controls.Add(pause);
        var tabs=new PageDeck { Dock=DockStyle.Fill,Padding=new Padding(0) };var qemuTab=new PageView("Вывод QEMU") { BackColor=Theme.Panel };var eventsTab=new PageView("События менеджера") { BackColor=Theme.Panel };
        foreach(var text in new[]{qemu,events}){text.ReadOnly=true;text.Font=new Font("Consolas",10F);}
        qemuTab.Controls.Add(qemu);eventsTab.Controls.Add(events);tabs.Pages.Add(eventsTab);tabs.Pages.Add(qemuTab);
        foreach(var text in new[]{qemu,events}){text.BackColor=Theme.Field;text.BorderStyle=BorderStyle.None;}
        qemuTab.Padding=new Padding(20);eventsTab.Padding=new Padding(20);
        var bottom=new Panel { Dock=DockStyle.Bottom,Height=44,Padding=new Padding(22,8,8,0) };info.AutoEllipsis=true;info.AutoSize=false;info.Dock=DockStyle.Fill;bottom.Controls.Add(info);Controls.Add(tabs);Controls.Add(top);Controls.Add(bottom);Controls.Add(Theme.Header("Живые логи",name+" · события менеджера и вывод QEMU"));
        timer.Tick+=(sender,args)=>Read();FormClosed+=(sender,args)=>timer.Stop();
        if(demo){qemu.Text="QEMU запущен с WHPX\r\nЖурнал обновляется во время работы VM.\r\n";events.Text="12:00:01  Выбрано ускорение WHPX\r\n12:00:02  Запущен QEMU\r\n";info.Text="Демонстрация окна логов";}
        else {Shown+=(sender,args)=>{timer.Start();Read();};}
    }
    void Append(ThemedEditor box,string text) {
        if(String.IsNullOrEmpty(text))return;box.AppendText(text);if(box.TextLength>2000000)box.Text=box.Text.Substring(box.TextLength-1500000);
        if(follow.Checked){box.SelectionStart=box.TextLength;box.ScrollToCaret();}
    }
    void Read() {
        if(pending||pause.Checked||main.Busy||main.Backend==null)return;pending=true;
        Task.Run(()=>{try{object value=main.Backend.Call("logs",J.O("name",name,"file",file,"offset",offset,"event",eventId));main.Post(()=>{
            if(IsDisposed)return;string next=J.S(value,"file");if(next!=file){file=next;decoder.Reset();qemu.Clear();}
            offset=J.L(value,"offset");byte[] bytes=Convert.FromBase64String(J.S(value,"content"));char[] chars=new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];int count=decoder.GetChars(bytes,0,bytes.Length,chars,0,false);Append(qemu,new string(chars,0,count));
            foreach(object item in J.A(J.V(value,"events"))){eventId=J.L(item,"id");Append(events,J.S(item,"text")+Environment.NewLine);}
            info.Text=String.IsNullOrEmpty(file)?"Журнал появится после запуска VM через менеджер.":file+"  ·  "+DateTime.Now.ToString("HH:mm:ss");
        });}catch(Exception error){main.Post(()=>{if(!IsDisposed)info.Text=error.Message;});}finally{pending=false;}});
    }
}

sealed class EditDialog : Form {
    readonly MainWindow main;readonly string name;readonly object original;readonly FieldInput label,disk,iso;readonly ThemedEditor raw;
    readonly NumberInput ram,cores,width,height;readonly ThemedSelector vga,profile;readonly PageDeck tabs;
    internal readonly LaunchSettingsUi launchSettings;internal readonly AppearanceView appearance;readonly string initialFields;bool jsonCurrent,syncingJson;
    Dictionary<string,object> draft;
    public EditDialog(MainWindow main,string name,object original) {
        this.main=main;this.name=name;this.original=original;object data=J.V(original,"data");draft=new Dictionary<string,object>(J.D(data));Text="Параметры | "+name;Theme.Form(this,1020,800);Icon=Theme.Icon(Theme.VmColor(main.Row(name)));
        tabs=new PageDeck { Dock=DockStyle.Fill,Padding=new Padding(0) };var common=new PageView("Параметры VM") { BackColor=Theme.Panel };var advanced=new PageView("Конфигурация JSON") { BackColor=Theme.Panel };
        var fields=Theme.Fields();label=Theme.Input(J.S(data,"label",name));ram=Theme.Number(J.N(data,"ram_mb",4096),1,1048576);cores=Theme.Number(J.N(data,"cores",2),1,256);
        width=Theme.Number(J.N(data,"screen_width",1920),320,4095);height=Theme.Number(J.N(data,"screen_height",1080),200,4095);
        vga=Theme.Combo("std","virtio","qxl","cirrus","none");vga.SelectedItem=J.S(data,"vga","std");profile=LaunchProfiles.Selector();LaunchProfiles.Load(profile,data);
        disk=Theme.Input(J.S(data,"disk_path"));iso=Theme.Input(J.S(data,"iso_path"));
        Theme.Row(fields,"Название",label,true);Theme.Row(fields,"Память, МБ",ram);Theme.Row(fields,"Ядра CPU",cores);Theme.Row(fields,"Ширина экрана",width);Theme.Row(fields,"Высота экрана",height);
        Theme.Row(fields,"Видеоадаптер",vga);Theme.Row(fields,"Профиль запуска",profile);Theme.Row(fields,"Диск",Theme.Browse(disk,false,"Диски qcow2 / VDI|*.qcow2;*.vdi|Все файлы|*.*"),true);Theme.Row(fields,"ISO",Theme.Browse(iso,false,"ISO|*.iso|Все файлы|*.*"),true);
        var note=Theme.Label("Применится при следующем запуске.\r\nПеред сохранением создаётся копия параметров.",10,Theme.Muted);Theme.Row(fields,"",note);common.Controls.Add(fields);
        raw=Theme.Editor(J.S(original,"raw"));raw.Font=new Font("Consolas",10F);advanced.Padding=new Padding(24);advanced.Controls.Add(raw);tabs.Pages.Add(common);
        launchSettings=new LaunchSettingsUi(main,this,tabs,FieldsData,EnsureVisual);launchSettings.Load(data,J.V(original,"network_view"));
        appearance=new AppearanceView(main.Visuals,()=>J.O("iso_path",iso.Text,"label",label.Text,"disk_path",disk.Text),()=>name,main.Visuals.Choice(name));
        var appearancePage=new PageView("Оформление");appearancePage.Controls.Add(appearance);tabs.Pages.Add(appearancePage);tabs.Pages.Add(advanced);
        try{initialFields=J.Json().Serialize(FieldsData());}catch(Exception){initialFields=null;}
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
        var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Сохранить",Save,true));buttons.Controls.Add(Theme.Button("Отмена",Close));Theme.Dialog(this,tabs,buttons,"Параметры машины",name+" · изменения применятся при следующем запуске");
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
        var data=new Dictionary<string,object>(draft);foreach(var pair in updates)data[pair.Key]=pair.Value;return launchSettings.Merge(data);
    }
    void Save() {
        try{
            string content=jsonCurrent?raw.Text:J.Json().Serialize(FieldsData());string choice=appearance.Choice;
            if(initialFields!=null&&content==initialFields){main.Visuals.SaveChoice(name,choice);Close();main.Reload();return;}
            var args=J.O("name",name,"hash",J.S(original,"hash"),"raw",content);
            main.Run("Сохраняю параметры и резервную копию…",()=>main.Backend.Call("save",args),result=>{main.SaveAppearanceAfterOperation(name,choice);Close();main.Reload();});
        }catch(Exception error){if(Program.TestMode)main.TestError=error;else MessageBox.Show(this,error.Message,"Параметры VM");}
    }
}

sealed class CreateDialog : Form {
    readonly MainWindow main;readonly object[] templates;readonly FieldInput name=Theme.Input(),label=Theme.Input(),disk=Theme.Input(),iso=Theme.Input();
    readonly ThemedSelector template=Theme.Combo("Новые настройки"),mode=Theme.Combo("Установка ISO на новый диск","Существующий диск","Только Live ISO"),profile=LaunchProfiles.Selector();
    readonly NumberInput ram=Theme.Number(4096,1,1048576),cores=Theme.Number(2,1,256),size=Theme.Number(60,1,65536);
    readonly Dictionary<string,string> replacements=new Dictionary<string,string>();
    internal readonly LaunchSettingsUi launchSettings;internal readonly AppearanceView appearance;readonly PageDeck tabs;
    public CreateDialog(MainWindow main,object saved,bool fromBackup=false,string selectedTemplate=null) {
        this.main=main;templates=J.A(saved);Text="Добавление виртуальной машины";Theme.Form(this,1020,830);
        foreach(object item in templates)template.Items.Add(J.S(item,"backup_name",J.S(item,"id")));
        var fields=Theme.Fields();Theme.Row(fields,"Параметры из копии",template,true);Theme.Row(fields,"Короткое имя VM",name);Theme.Row(fields,"Название",label);Theme.Row(fields,"Источник",mode,true);
        var diskPicker=Theme.Browse(disk,true,"Диски qcow2 / VDI|*.qcow2;*.vdi",()=>mode.SelectedIndex==0);var isoPicker=Theme.Browse(iso,false,"ISO|*.iso|Все файлы|*.*");
        Theme.Row(fields,"Виртуальный диск",diskPicker);Theme.Row(fields,"ISO",isoPicker);Theme.Row(fields,"Размер диска, ГБ",size);
        Theme.Row(fields,"Память, МБ",ram);Theme.Row(fields,"Ядра CPU",cores);Theme.Row(fields,"Профиль запуска",profile);
        var missing=Theme.Button("Заменить доп. файлы…",MissingFiles);Theme.Row(fields,"Файлы из копии",missing);
        Theme.Row(fields,"",Theme.Label("Копия переносит параметры, включая сеть.\r\nДиск и ISO выбираются отдельно.\r\nПосле создания VM появится в списке.",10,Theme.Muted));
        tabs=new PageDeck{Dock=DockStyle.Fill,Padding=new Padding(0)};var common=new PageView("Параметры VM"){BackColor=Theme.Panel};common.Controls.Add(fields);tabs.Pages.Add(common);
        launchSettings=new LaunchSettingsUi(main,this,tabs,CreateData);launchSettings.Load(LaunchSettingsUi.Defaults());
        appearance=new AppearanceView(main.Visuals,()=>J.O("iso_path",iso.Text,"label",label.Text,"disk_path",disk.Text),()=>name.Text);
        var appearancePage=new PageView("Оформление");appearancePage.Controls.Add(appearance);tabs.Pages.Add(appearancePage);
        iso.Leave+=(sender,args)=>appearance.RefreshPreview();label.Leave+=(sender,args)=>appearance.RefreshPreview();
        var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Создать VM",Create,true));buttons.Controls.Add(Theme.Button("Отмена",Close));Theme.Dialog(this,tabs,buttons,"Новая машина","От образа системы до первого запуска.");
        template.SelectedIndexChanged+=(sender,args)=>LoadTemplate();mode.SelectedIndexChanged+=(sender,args)=>{diskPicker.Enabled=mode.SelectedIndex!=2;isoPicker.Enabled=mode.SelectedIndex!=1;size.Enabled=mode.SelectedIndex==0;};
        if(selectedTemplate!=null){int index=Array.FindIndex(templates,item=>J.S(item,"id")==selectedTemplate);if(index>=0)template.SelectedIndex=index+1;}
        else if(fromBackup && templates.Length>0)template.SelectedIndex=1;
    }
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
    Dictionary<string,object> CreateData(){
        string diskValue=mode.SelectedIndex==2?"":disk.Text.Trim(),isoValue=mode.SelectedIndex==1?"":iso.Text.Trim();
        var data=J.O("label",String.IsNullOrWhiteSpace(label.Text)?name.Text:label.Text,"disk_format",Path.GetExtension(diskValue).Equals(".vdi",StringComparison.OrdinalIgnoreCase)?"vdi":"qcow2","disk_path",diskValue,"iso_path",isoValue,"ram_mb",(int)ram.Value,"cores",(int)cores.Value,
            "launch_profile",LaunchProfiles.Value(profile));
        return launchSettings.Merge(data);
    }
    void Create() {
        try{var data=CreateData();
        var args=J.O("name",name.Text.Trim(),"data",data,"new_disk",mode.SelectedIndex==0,"size_gb",(int)size.Value,"replacements",replacements);
        args["network_managed"]=true;
        if(template.SelectedIndex>0)args["template"]=J.S(templates[template.SelectedIndex-1],"id");
        string createdName=name.Text.Trim(),choice=appearance.Choice;
        main.Run("Создаю VM…",()=>main.Backend.Call("create",args),result=>{main.SaveAppearanceAfterOperation(createdName,choice);Close();main.Reload();});
        }catch(Exception error){if(Program.TestMode)main.TestError=error;else MessageBox.Show(this,error.Message,"Создание VM");}
    }
}

sealed class TemplatesWindow : Form {
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
        Theme.Dialog(this,layout,buttons,"Копии параметров","Знакомые настройки для новой установки системы.");
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

sealed class DeleteDialog : Form {
    readonly MainWindow main;readonly object plan;readonly FieldInput backup=Theme.Input(),confirm=Theme.Input();
    public DeleteDialog(MainWindow main,object plan) {
        this.main=main;this.plan=plan;Text="Удаление | "+J.S(plan,"name");Theme.Form(this,850,760);
        var fields=Theme.Fields();var files=Theme.Editor();files.ReadOnly=true;files.Height=260;files.Font=new Font("Consolas",9F);
        var text=new StringBuilder("БУДУТ УДАЛЕНЫ\r\n");foreach(object item in J.A(J.V(plan,"files")))text.AppendLine(Convert.ToString(item));
        text.AppendLine("\r\nБУДУТ СОХРАНЕНЫ");foreach(object item in J.A(J.V(plan,"kept")))text.AppendLine(String.Join(" — ",J.A(item).Select(Convert.ToString)));
        text.AppendLine("\r\nУЖЕ ОТСУТСТВУЮТ");foreach(object item in J.A(J.V(plan,"missing")))text.AppendLine(Convert.ToString(item));files.Text=text.ToString();
        Theme.Row(fields,"Имя копии параметров",backup);Theme.Row(fields,"Введи имя VM",confirm);Theme.Row(fields,"",files);
        Theme.Row(fields,"",Theme.Label("Для подтверждения: "+J.S(plan,"name")+"\r\nКопия содержит настройки. Диск и ОС в неё не входят.",10,Theme.Yellow));
        var buttons=Theme.Flow();var delete=Theme.Button("Удалить VM",Delete,false,true);delete.BackColor=Theme.Red;delete.ForeColor=Theme.Bg;delete.Enabled=false;
        EventHandler validate=(sender,args)=>delete.Enabled=!String.IsNullOrWhiteSpace(backup.Text)&&confirm.Text==J.S(plan,"name");backup.TextChanged+=validate;confirm.TextChanged+=validate;
        buttons.Controls.Add(delete);buttons.Controls.Add(Theme.Button("Отмена",Close));Theme.Dialog(this,fields,buttons,"Удаление машины",J.S(plan,"name")+" · сначала сохраним именную копию параметров");
    }
    void Delete() {var args=J.O("name",J.S(plan,"name"),"token",J.S(plan,"token"),"backup_name",backup.Text,"confirm",confirm.Text);main.Run("Сохраняю копию и удаляю личные файлы VM…",()=>main.Backend.Call("delete",args),result=>{main.SaveAppearanceAfterOperation(J.S(plan,"name"),"auto");Close();main.Reload();});}
}

sealed class SnapshotsWindow : Form {
    readonly MainWindow main;readonly string name;object[] rows;bool online;readonly ThemedList list=Theme.ListBox();readonly Button restore;
    readonly Label note=Theme.Label("",10,Theme.Muted);
    public SnapshotsWindow(MainWindow main,string name,object result,bool boot=false) {
        this.main=main;this.name=name;Text=(boot?"Загрузка со снимка | ":"Снимки дисков | ")+name;Theme.Form(this,760,470);
        var content=new Panel { Dock=DockStyle.Fill,Padding=new Padding(22) };content.Controls.Add(list);note.Dock=DockStyle.Top;note.Height=45;content.Controls.Add(note);
        var buttons=Theme.Flow();buttons.Dock=DockStyle.Bottom;buttons.Padding=new Padding(22,10,10,0);
        buttons.Controls.Add(Theme.Button("Создать снимок",()=>main.Run("Создаю снимок дисков…",()=>main.Backend.Call("control",J.O("name",name,"action","snapshot")),value=>Reload())));
        buttons.Controls.Add(Theme.Button("Обновить",Reload));restore=Theme.Button("Загрузить снимок",Restore,true);buttons.Controls.Add(restore);buttons.Controls.Add(Theme.Button("Закрыть",Close));Theme.Dialog(this,content,buttons,boot?"Загрузка со снимка":"Снимки диска",name+" · точки восстановления без оперативной памяти");Fill(result);
    }
    void Fill(object result) {rows=J.A(J.V(result,"rows"));online=J.B(result,"online");list.Items.Clear();foreach(object row in rows){DateTime date=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(J.L(row,"date")).ToLocalTime();list.Items.Add(J.S(row,"name")+"   ·   "+date.ToString("yyyy-MM-dd HH:mm"));}if(rows.Length>0)list.SelectedIndex=0;restore.Enabled=!online && rows.Length>0;note.Text=online?"VM работает. Можно сохранить снимок; для восстановления сначала выключи её.":rows.Length==0?"Снимков дисков пока нет. Создай снимок у работающей VM.":"Снимки содержат только диски, без оперативной памяти.";}
    void Reload() {main.Run("Обновляю снимки…",()=>main.Backend.Call("snapshots",J.O("name",name)),Fill);}
    void Restore() {if(online||list.SelectedIndex<0)return;object selected=rows[list.SelectedIndex];if(MessageBox.Show(this,"Вернуть диски к снимку «"+J.S(selected,"name")+"» и запустить VM?\r\n\r\nПеред откатом сохранится страховочный снимок текущего состояния. RAM не восстанавливается.","Восстановление снимка",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;Close();main.StartVm(name,selected);}
}

sealed class DoctorWindow : Form {
    public DoctorWindow(MainWindow main,object report) {
        Text="VM Doctor";Theme.Form(this,960,780);
        var scroll=new BufferedPanel{Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(24,8,24,8)};var output=Theme.Flow(true);output.Dock=DockStyle.Top;
        foreach(object item in J.A(J.V(report,"checks")))output.Controls.Add(new DiagnosticCard(item));
        scroll.Controls.Add(output);scroll.ClientSizeChanged+=(sender,args)=>{int width=Math.Max(260,scroll.ClientSize.Width-scroll.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth);foreach(Control card in output.Controls)card.Width=width;};
        var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Сохранить отчёт",()=>main.Run("Сохраняю обезличенный отчёт…",()=>main.Backend.Call("report_save",J.O("token",J.S(report,"token"))),result=>MessageBox.Show(this,J.S(result,"path"),"Отчёт сохранён")),true));buttons.Controls.Add(Theme.Button("Закрыть",Close));Theme.Dialog(this,scroll,buttons,"VM Doctor","Проверки компьютера и конфигурации виртуальных машин.","ДИАГНОСТИКА");
    }
}

static class Program {
    internal static bool TestMode=false;
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [STAThread] static void Main(string[] args) {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string root=Environment.GetEnvironmentVariable("QEMUM_DATA_DIR")??AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'),python=Environment.GetEnvironmentVariable("QEMUM_PYTHON"),preview=null;
        Directory.CreateDirectory(root);
        for(int i=0;i<args.Length;i++){if(args[i]=="--root"&&i+1<args.Length)root=Path.GetFullPath(args[++i]);else if(args[i]=="--python"&&i+1<args.Length)python=args[++i];else if(args[i]=="--preview"&&i+1<args.Length)preview=args[++i];}
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
