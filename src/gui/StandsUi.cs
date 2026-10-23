using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Windows.Forms;

sealed partial class MainWindow {
    bool standMode;object selectedStand;List<object> stands=new List<object>();
    readonly HashSet<string> expandedStands=new HashSet<string>();
    readonly Panel standDetail=new Panel{Dock=DockStyle.Fill,BackColor=Theme.Bg};
    Label standLibraryTitle;Button standAdd;bool standLoading;string standError="";
    string normalSelectedVm,standSelectedVm;int machineScroll,standScroll;string restoreStand;object libraryPrefs;
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
        cards.SuspendLayout();foreach(Control child in cards.Controls.Cast<Control>().ToArray())child.Dispose();cards.Controls.Clear();libraryCount.Text="Всего стендов: "+stands.Count;
        foreach(var stand in stands){
            var members=J.A(J.V(stand,"members")).Select(Convert.ToString).ToArray();string id=J.S(stand,"id"),title=J.S(stand,"name");
            bool match=(title+" "+String.Join(" ",members.Select(n=>J.S(J.V(Row(n),"data"),"label",n)))).IndexOf(search.Text,StringComparison.OrdinalIgnoreCase)>=0;if(!match)continue;
            var folder=Theme.Button((expandedStands.Contains(id)?"▾  ":"▸  ")+title+" · "+members.Length+" VM",()=>{if(!expandedStands.Add(id))expandedStands.Remove(id);standSelectedVm=null;selectedStand=stand;ShowStand();RenderStands();});
            folder.Width=Math.Max(170,cards.Width);folder.MaximumSize=new Size(folder.Width,0);folder.TextAlign=ContentAlignment.MiddleLeft;folder.AccessibleName="Стенд: "+title;cards.Controls.Add(folder);
            folder.Tag=stand;
            if(expandedStands.Contains(id))foreach(string name in members){string n=name;var row=Row(n);var child=Theme.Button("    "+J.S(J.V(row,"data"),"label",n)+(row==null?" · отсутствует":""),()=>{standSelectedVm=n;selectedStand=null;standDetail.Visible=false;SelectVm(n);breadcrumb.Text="Стенды / "+title+" / "+n;});child.Width=Math.Max(170,cards.Width);child.MaximumSize=new Size(child.Width,0);child.TextAlign=ContentAlignment.MiddleLeft;cards.Controls.Add(child);}
        }
        if(cards.Controls.Count==0)cards.Controls.Add(Theme.Label(stands.Count==0?"Стендов пока нет.\nСоздай свой первый стенд.":"Ничего не найдено.",10,Theme.Muted));
        if(!String.IsNullOrEmpty(standError))cards.Controls.Add(Theme.Label(standError,9,Theme.Muted));
        cards.ResumeLayout();libraryScroll.PerformLayout();libraryScroll.ScrollOffset=offset;
    }
    void UpdateStandStatus(){
        if(!standMode)return;libraryCount.Text="Всего стендов: "+stands.Count;
        foreach(var button in cards.Controls.OfType<Button>()){
            var stand=button.Tag;if(stand==null)continue;var members=J.A(J.V(stand,"members")).Select(Convert.ToString).ToArray();
            int running=members.Count(n=>State(n)=="running"||State(n)=="paused");
            button.Text=(expandedStands.Contains(J.S(stand,"id"))?"▾  ":"▸  ")+J.S(stand,"name")+" · "+running+"/"+members.Length+" запущено";
        }
    }
    void ShowStand(){
        foreach(Control child in standDetail.Controls.Cast<Control>().ToArray())child.Dispose();standDetail.Controls.Clear();
        var stack=Theme.Flow(true);stack.Dock=DockStyle.Top;
        stack.Controls.Add(Theme.Label("СТЕНД / "+J.S(selectedStand,"name"),10,Theme.Muted));
        var title=Theme.Label(J.S(selectedStand,"name"),24,null,true);title.MaximumSize=new Size(Math.Max(260,detail.Width-80),0);stack.Controls.Add(title);
        stack.Controls.Add(Theme.Label(J.S(selectedStand,"description"),10,Theme.Muted));
        stack.Controls.Add(Theme.Button("Изменить состав и название",()=>EditStand(selectedStand)));
        stack.Controls.Add(Theme.Label("Состав стенда",14,null,true));
        foreach(string name in J.A(J.V(selectedStand,"members")).Select(Convert.ToString)){string n=name;var row=Row(n);stack.Controls.Add(Theme.Button(J.S(J.V(row,"data"),"label",n)+(row==null?" · VM отсутствует":" · "+Theme.State(State(n))),()=>{standSelectedVm=n;selectedStand=null;standDetail.Visible=false;SelectVm(n);}));}
        stack.Controls.Add(Theme.Label("Соединения и запуск группы появятся на следующем этапе.\nСтенд хранит ссылки на VM; образы не перемещаются.",10,Theme.Muted));
        var tabs=new WorkspaceTabs{Width=Math.Max(260,detail.ClientSize.Width-80),Height=180};
        tabs.Add("Соединения",Theme.Label("Сетевых связей стенда пока нет. Редактор и сетевой backend — следующий этап.",10,Theme.Muted));
        tabs.Add("Запуск",Theme.Label("Запуск группы ещё недоступен. Сохранённый состав:\n"+String.Join(" → ",J.A(J.V(selectedStand,"order")).Select(Convert.ToString)),10,Theme.Muted));
        tabs.Add("Журнал",Theme.Label("Групповых операций пока нет. Логи отдельной VM доступны в её карточке.",10,Theme.Muted));
        stack.Controls.Add(tabs);
        standDetail.Controls.Add(new ScrollSurface(stack){Dock=DockStyle.Fill,BackColor=Theme.Bg});standDetail.Visible=true;standDetail.BringToFront();
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
                var links=J.A(J.V(existing,"links"));if(links.Length>0){MessageBox.Show(form,"Связи требуют отдельного плана изменения.");return;}
                var removed=members.Where(n=>!chosen.Contains(n)).ToArray();
                if(removed.Length>0&&MessageBox.Show(form,"Убрать из стенда: "+String.Join(", ",removed)+"? VM и её файлы сохранятся.","Состав стенда",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
                var order=J.A(J.V(existing,"order")).Select(Convert.ToString).Where(chosen.Contains).Concat(chosen.Where(n=>!J.A(J.V(existing,"order")).Select(Convert.ToString).Contains(n))).ToArray();
                var draft=J.O("schema_version",1,"id",existing==null?Guid.NewGuid().ToString("N"):J.S(existing,"id"),"revision",J.N(existing,"revision"),"name",name.Text,"description",description.Text,"members",chosen,"order",order,"links",new object[0]);
                if(Busy)return;saving=true;save.Enabled=false;
                Run("Сохраняю стенд…",()=>Backend.Call("stand_save",J.O("stand",draft,"revision",J.N(existing,"revision"))),result=>{saving=false;form.Close();selectedStand=result;ShowStand();LoadStands();},error=>{saving=false;save.Enabled=true;});
            };
            form.Controls.Add(new ScrollSurface(stack){Dock=DockStyle.Fill});form.Controls.Add(buttons);form.ShowDialog(this);
        }
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
