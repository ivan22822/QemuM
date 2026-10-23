using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Presentation only. No VM operations, persistence, process discovery or timers.
static class Theme {
    public static readonly Color Bg=Hex("#050E25"), Panel=Hex("#0B1B36"), Card=Hex("#102746"), Field=Hex("#091A33"),
        Text=Hex("#F9F2F5"), Muted=Hex("#9CB4CF"), Accent=Hex("#65C7FF"), Line=Hex("#213650"),
        Green=Hex("#A3D9C4"), Red=Hex("#EE97B0"), Yellow=Hex("#EBC8A4");
    static Color Hex(string value){return ColorTranslator.FromHtml(value);}
    public static Color VmColor(object row){try{return ColorTranslator.FromHtml(J.S(J.V(row,"data"),"ui_color","#65C7FF"));}catch(ArgumentException){return Accent;}}
    public static string State(string value){switch(value){case "recovery-required":return "Нужна проверка снимков";case "running":return "Работает";case "paused":return "Пауза";case "prelaunch":return "Ожидает запуска";case "stopped":return "Выключена";case "no-qmp":return "Запущена · нет QMP";case "shutdown":return "Завершается";case "checking":return "Проверка…";case "error":return "Ошибка QEMU · открой логи";default:return "Состояние неизвестно";}}
    public static Color StateColor(string value){return value=="running"?Green:value=="stopped"?Muted:new[]{"paused","prelaunch","shutdown"}.Contains(value)?Yellow:Red;}
    public static void Form(Form form,int width,int height){
        form.BackColor=Bg;form.ForeColor=Text;form.Font=new Font("Segoe UI",10F);form.AutoScaleMode=AutoScaleMode.Dpi;
        form.ClientSize=new Size(width,height);form.MinimumSize=new Size(Math.Min(width,740),Math.Min(height,520));form.StartPosition=FormStartPosition.CenterScreen;form.Icon=Icon(Accent);
        form.HandleCreated+=(sender,args)=>Caption(form);
        if(Program.TestMode){form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-2400,-1600);}
    }
    public static void Caption(Form form){
        if(!form.IsHandleCreated)return;
        try{int dark=1,caption=ColorTranslator.ToWin32(Bg),text=ColorTranslator.ToWin32(Text),border=ColorTranslator.ToWin32(Line);
            DwmSetWindowAttribute(form.Handle,20,ref dark,4);DwmSetWindowAttribute(form.Handle,35,ref caption,4);DwmSetWindowAttribute(form.Handle,36,ref text,4);DwmSetWindowAttribute(form.Handle,34,ref border,4);
        }catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
    }
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    [DllImport("user32.dll")]static extern bool DestroyIcon(IntPtr icon);
    public static Icon Icon(Color color){using(var bitmap=new Bitmap(32,32)){using(var graphics=Graphics.FromImage(bitmap))using(var fill=new SolidBrush(color))using(var ink=new SolidBrush(Bg))using(var font=new Font("Segoe UI",17F,FontStyle.Bold)){graphics.Clear(Color.Transparent);graphics.FillEllipse(fill,2,2,28,28);graphics.DrawString("Q",font,ink,4,1);}IntPtr handle=bitmap.GetHicon();try{return (Icon)System.Drawing.Icon.FromHandle(handle).Clone();}finally{DestroyIcon(handle);}}}
    public static Label Label(string text,int size=10,Color? color=null,bool bold=false){return new Label{Text=text,AutoSize=true,ForeColor=color??Text,Font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular),Margin=new Padding(0,0,0,8),BackColor=Color.Transparent};}
    public static Button Button(string text,Action action,bool primary=false,bool danger=false){var button=new PolarButton{Text=text,AutoSize=true,MinimumSize=new Size(118,40),Padding=new Padding(15,8,15,8),BackColor=primary?Accent:Card,ForeColor=primary?Bg:danger?Red:Text,Margin=new Padding(0,0,10,10)};button.Click+=(sender,args)=>action();return button;}
    public static FieldInput Input(string value=""){return new FieldInput{Text=value};}
    public static ThemedSelector Combo(params string[] items){var box=new ThemedSelector();box.Items.AddRange(items);if(items.Length>0)box.SelectedIndex=0;return box;}
    public static NumberInput Number(int value,int min,int max){return new NumberInput(value,min,max);}
    public static FlowLayoutPanel Flow(bool vertical=false){return new BufferedFlowLayoutPanel{AutoSize=true,WrapContents=!vertical,FlowDirection=vertical?FlowDirection.TopDown:FlowDirection.LeftToRight,Dock=DockStyle.Top,Margin=new Padding(0),Padding=new Padding(0)};}
    public static void Title(Control parent,string title,string subtitle){parent.Controls.Add(Label(subtitle,10,Muted));parent.Controls.Add(Label(title,22,null,true));}
    public static Control Header(string title,string subtitle,string eyebrow="QEMU / VM MANAGER"){
        var panel=new BufferedPanel{Dock=DockStyle.Top,Height=112,Padding=new Padding(26,18,24,12),BackColor=Bg};
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3};
        var category=Label(eyebrow,8,Muted);category.Margin=new Padding(0,0,0,7);layout.Controls.Add(category);layout.Controls.Add(Label(title,22,null,true));layout.Controls.Add(Label(subtitle,9,Muted));panel.Controls.Add(layout);return panel;
    }
    public static void Dialog(Form form,Control content,Control buttons,string title,string subtitle,string eyebrow="QEMU / VM MANAGER"){
        if(content is TableLayoutPanel&&content.Tag is int)content=FieldsView((TableLayoutPanel)content);
        content.Dock=DockStyle.Fill;buttons.Dock=DockStyle.Bottom;buttons.BackColor=Bg;buttons.Padding=new Padding(24,14,14,6);
        form.Controls.Add(content);form.Controls.Add(buttons);form.Controls.Add(Header(title,subtitle,eyebrow));
    }
    public static TableLayoutPanel Fields(){var table=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=0,Padding=new Padding(24),AutoScroll=false,BackColor=Bg,Tag=0};table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));return table;}
    public static Control FieldsView(TableLayoutPanel fields){return new ScrollSurface(fields);}
    public static Control Row(TableLayoutPanel table, string title, Control value, bool fullWidth=false) {
        int index=(int)table.Tag;
        bool full=fullWidth || String.IsNullOrEmpty(title);
        if(full && index%2!=0) index++;
        int row=index/2;
        while(table.RowCount<=row) {
            table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        var block=new TableLayoutPanel {
            ColumnCount=1, AutoSize=true, Dock=DockStyle.Top,
            Margin=new Padding(0,0,18,18), Padding=new Padding(0)
        };
        block.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        bool hasTitle=!String.IsNullOrEmpty(title);
        if(hasTitle) {
            var label=Label(title,9,Muted);
            label.Margin=new Padding(0,0,0,7);
            block.Controls.Add(label,0,0);
        }
        value.Dock=DockStyle.Top;
        value.Margin=new Padding(0);
        if(value is TextBox && !((TextBox)value).Multiline) value.Height=32;
        if(value is FieldInput){value.Height=40;value.AccessibleName=title;}
        block.Controls.Add(value,0,hasTitle?1:0);
        block.SizeChanged+=(sender,args)=>{foreach(Label label in block.Controls.OfType<Label>())label.MaximumSize=new Size(Math.Max(1,block.ClientSize.Width),0);};
        table.Controls.Add(block,index%2,row);
        if(full) table.SetColumnSpan(block,2);
        table.Tag=index+(full?2:1);
        return block;
    }
    public static string File(bool save,string filter,string initial=""){using(FileDialog dialog=save?(FileDialog)new SaveFileDialog():(FileDialog)new OpenFileDialog()){dialog.Filter=filter;dialog.FileName=initial;if(save)((SaveFileDialog)dialog).OverwritePrompt=true;return dialog.ShowDialog()==DialogResult.OK?dialog.FileName:null;}}
    public static string NewDiskPath(string folder,string name){
        name=String.IsNullOrWhiteSpace(name)?"disk":name.Trim();
        if(name=="."||name==".."||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)throw new InvalidOperationException("Укажи корректное короткое имя VM перед выбором папки.");
        return Path.Combine(folder,name+".qcow2");
    }
    public static string DiskPickerInitialFolder(string value){
        if(String.IsNullOrWhiteSpace(value))return null;
        try{
            string path=Path.GetFullPath(value.Trim());
            string folder=Directory.Exists(path)?path:Path.GetDirectoryName(path);
            return Directory.Exists(folder)?folder:null;
        }catch(ArgumentException){return null;}
        catch(NotSupportedException){return null;}
        catch(PathTooLongException){return null;}
    }
    public static Control BrowseNewDisk(FieldInput text,Func<bool> create,Func<string> name){
        return Browse(text,false,"Диски qcow2 / VDI|*.qcow2;*.vdi|Все файлы|*.*",()=>{
            if(!create())return File(false,"Диски qcow2 / VDI|*.qcow2;*.vdi|Все файлы|*.*",text.Text);
            using(var dialog=new FolderBrowserDialog{Description="Выбери папку для нового виртуального диска. Имя файла будет взято из короткого имени VM.",ShowNewFolderButton=true}){
                string initial=DiskPickerInitialFolder(text.Text);
                if(initial!=null)dialog.SelectedPath=initial;
                if(dialog.ShowDialog()!=DialogResult.OK)return null;
                return NewDiskPath(dialog.SelectedPath,name());
            }
        });
    }
    public static Control Browse(FieldInput text,bool save,string filter,Func<string> picker=null){
        var panel=new TableLayoutPanel{Dock=DockStyle.Top,ColumnCount=2,Height=40,Margin=Padding.Empty,Padding=Padding.Empty};
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));
        text.Dock=DockStyle.Fill;text.Margin=Padding.Empty;panel.Controls.Add(text,0,0);
        var button=Button("Обзор…",()=>{try{string path=picker==null?File(save,filter,text.Text):picker();if(path!=null)text.Text=path;}catch(Exception error){MessageBox.Show(panel.FindForm(),error.Message,"Выбор пути",MessageBoxButtons.OK,MessageBoxIcon.Warning);}});
        button.MinimumSize=new Size(88,40);button.AutoSize=false;button.Size=new Size(88,40);button.Padding=new Padding(8);button.Margin=new Padding(12,0,0,0);panel.Controls.Add(button,1,0);return panel;
    }
    public static Control EditorLayout(ThemedEditor editor,string title,string description,Control buttons){
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(24),Margin=Padding.Empty};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading=Flow(true);heading.Dock=DockStyle.Fill;heading.Margin=new Padding(0,0,0,18);heading.Controls.Add(Label(title,20,null,true));
        var note=Label(description,10,Muted);heading.Controls.Add(note);layout.Controls.Add(heading,0,0);
        heading.Resize+=(sender,args)=>note.MaximumSize=new Size(Math.Max(1,heading.ClientSize.Width),0);
        editor.Margin=Padding.Empty;editor.Dock=DockStyle.Fill;layout.Controls.Add(editor,0,1);
        if(buttons!=null){buttons.Dock=DockStyle.Fill;buttons.Margin=new Padding(0,18,0,0);buttons.Padding=Padding.Empty;layout.Controls.Add(buttons,0,2);}return layout;
    }
    public static ThemedList ListBox(int size=11) { return new ThemedList { Font=new Font("Segoe UI",size),ItemHeight=48 }; }
    public static ThemedEditor Editor(string value="") { return new ThemedEditor(value) { ForeColor=Text }; }

}

class PolarButton : Button {
    bool hover,pressed;
    public PolarButton(){FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;Cursor=Cursors.Hand;SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
    protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=pressed=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnMouseDown(MouseEventArgs e){pressed=true;Invalidate();base.OnMouseDown(e);}
    protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
    protected override void OnPaint(PaintEventArgs e){
        e.Graphics.Clear(Parent==null?Theme.Bg:Parent.BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        var bounds=new Rectangle(0,0,Math.Max(1,Width-1),Math.Max(1,Height-1));Color fill=Enabled?BackColor:Theme.Panel;if(Enabled&&hover)fill=ControlPaint.Light(fill,pressed?.05F:.12F);
        using(var path=new GraphicsPath()){const int diameter=14;path.AddArc(bounds.Left,bounds.Top,diameter,diameter,180,90);path.AddArc(bounds.Right-diameter,bounds.Top,diameter,diameter,270,90);path.AddArc(bounds.Right-diameter,bounds.Bottom-diameter,diameter,diameter,0,90);path.AddArc(bounds.Left,bounds.Bottom-diameter,diameter,diameter,90,90);path.CloseFigure();using(var brush=new SolidBrush(fill))e.Graphics.FillPath(brush,path);}
        var textBounds=new Rectangle(bounds.Left+Padding.Left,bounds.Top+Padding.Top,Math.Max(1,bounds.Width-Padding.Horizontal),Math.Max(1,bounds.Height-Padding.Vertical));
        var flags=TextFormatFlags.WordBreak;
        if(TextAlign==ContentAlignment.TopCenter||TextAlign==ContentAlignment.MiddleCenter||TextAlign==ContentAlignment.BottomCenter)flags|=TextFormatFlags.HorizontalCenter;
        else if(TextAlign==ContentAlignment.TopRight||TextAlign==ContentAlignment.MiddleRight||TextAlign==ContentAlignment.BottomRight)flags|=TextFormatFlags.Right;
        if(TextAlign==ContentAlignment.MiddleLeft||TextAlign==ContentAlignment.MiddleCenter||TextAlign==ContentAlignment.MiddleRight)flags|=TextFormatFlags.VerticalCenter;
        else if(TextAlign==ContentAlignment.BottomLeft||TextAlign==ContentAlignment.BottomCenter||TextAlign==ContentAlignment.BottomRight)flags|=TextFormatFlags.Bottom;
        TextRenderer.DrawText(e.Graphics,Text,Font,textBounds,Enabled?ForeColor:Theme.Muted,flags);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(bounds,-5,-5),ForeColor,fill);
    }
}

sealed class PageView : BufferedPanel {
    public PageView(string text){Text=text;Dock=DockStyle.Fill;BackColor=Theme.Bg;}
}

sealed class ResourceSummary : UserControl {
    readonly Label memory=Theme.Label("—",17),cores=Theme.Label("—",17),profile=Theme.Label("—",17);
    readonly ToolTip profileTip=new ToolTip();
    public ResourceSummary(){Size=new Size(650,80);BackColor=Theme.Bg;Margin=new Padding(0);var table=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=2,Padding=new Padding(0,12,0,8)};
        for(int index=0;index<3;index++)table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.333F));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute,22));table.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        foreach(var value in new[]{memory,cores,profile}){value.AutoSize=false;value.AutoEllipsis=true;value.Dock=DockStyle.Fill;value.TextAlign=ContentAlignment.MiddleLeft;}
        table.Controls.Add(Theme.Label("Оперативная память",9,Theme.Muted),0,0);table.Controls.Add(Theme.Label("Процессор",9,Theme.Muted),1,0);table.Controls.Add(Theme.Label("Ускорение",9,Theme.Muted),2,0);table.Controls.Add(memory,0,1);table.Controls.Add(cores,1,1);table.Controls.Add(profile,2,1);Controls.Add(table);
    }
    public void ShowData(object data,bool present){
        int ram=J.N(data,"ram_mb");memory.Text=present?(ram>0&&ram%1024==0?ram/1024+" ГБ":ram+" МБ"):"—";cores.Text=present?J.N(data,"cores")+" CPU":"—";
        string id=J.S(data,"launch_profile","whpx"),accelerator=J.S(J.V(data,"launch_profile_settings"),"accelerator");
        // The resource value describes acceleration; the full preset name belongs in its tooltip.
        profile.Text=!present?"—":accelerator.Length>0?accelerator.ToUpperInvariant():id=="auto"?"Авто":id=="tcg"?"TCG":id=="whpx"||id=="whpx-compatible"?"WHPX":"—";
        string description=present?J.S(data,"launch_profile_label",id):"";profileTip.SetToolTip(profile,description);profile.AccessibleDescription=description;
    }
    protected override void Dispose(bool disposing){if(disposing)profileTip.Dispose();base.Dispose(disposing);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(var pen=new Pen(Theme.Line)){e.Graphics.DrawLine(pen,0,0,Width,0);e.Graphics.DrawLine(pen,0,Height-1,Width,Height-1);}}
}

sealed class DiagnosticCard : UserControl {
    readonly Label heading,detail,advice,badge;
    public DiagnosticCard(object item){
        BackColor=Theme.Panel;Padding=new Padding(18);Margin=new Padding(0,0,0,12);Width=820;
        string state=J.S(item,"state");Color color=state=="error"?Theme.Red:state=="warning"?Theme.Yellow:state=="ok"?Theme.Green:Theme.Muted;
        string scope=J.S(item,"scope");heading=Theme.Label((scope.Length==0?"":scope+" / ")+J.S(item,"title"),11,null,true);detail=Theme.Label(J.S(item,"detail"),10,Theme.Muted);advice=Theme.Label(J.S(item,"advice"),9,Theme.Muted);
        badge=Theme.Label(state=="ok"?"OK":state=="error"?"ОШИБКА":state=="warning"?"ВНИМАНИЕ":"НЕ ПРОВЕРЕНО",8,color);
        foreach(var label in new[]{heading,detail,advice,badge})Controls.Add(label);Arrange();
    }
    void Arrange(){if(heading==null)return;int width=Math.Max(160,ClientSize.Width-Padding.Horizontal);badge.Location=new Point(Math.Max(18,ClientSize.Width-Padding.Right-badge.PreferredWidth),20);heading.MaximumSize=new Size(Math.Max(100,width-badge.PreferredWidth-18),0);heading.Location=new Point(18,16);detail.MaximumSize=advice.MaximumSize=new Size(width,0);detail.Location=new Point(18,heading.Bottom+10);advice.Location=new Point(18,detail.Bottom+8);Height=(String.IsNullOrWhiteSpace(advice.Text)?detail.Bottom:advice.Bottom)+18;}
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);Arrange();}
}
sealed class PageSelectingEventArgs : CancelEventArgs {
    public PageView Page{get;private set;}
    public PageSelectingEventArgs(PageView page){Page=page;}
}
// A native control composition, rather than repainting Windows' tab-control chrome.
sealed class PageDeck : UserControl {
    public sealed class PageCollection : IEnumerable<PageView> {
        readonly PageDeck owner;readonly List<PageView> pages=new List<PageView>();
        internal PageCollection(PageDeck owner){this.owner=owner;}
        public int Count{get{return pages.Count;}}
        public PageView this[int index]{get{return pages[index];}}
        public bool Contains(PageView page){return pages.Contains(page);}
        public int IndexOf(PageView page){return pages.IndexOf(page);}
        public void Add(PageView page){if(page==null)throw new ArgumentNullException("page");if(page.Parent!=null||pages.Contains(page))throw new ArgumentException("Page already belongs to a container.");pages.Add(page);owner.AddPage(page);}
        public IEnumerator<PageView> GetEnumerator(){return pages.GetEnumerator();}
        IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
    }
    readonly FlowLayoutPanel navigation;readonly Panel content=new BufferedPanel{Dock=DockStyle.Fill};PageView selected;
    public PageCollection Pages{get;private set;}
    public event EventHandler<PageSelectingEventArgs> Selecting;
    public event EventHandler SelectedPageChanged;
    public PageView SelectedPage {
        get { return selected; }
        set {
            if(value==selected) return;
            if(value==null || !Pages.Contains(value)) throw new ArgumentException("Unknown page.");
            var args=new PageSelectingEventArgs(value);
            if(Selecting!=null) Selecting(this,args);
            if(args.Cancel) return;
            SuspendLayout();
            if(selected!=null) selected.Visible=false;
            selected=value;
            selected.Visible=true;
            selected.BringToFront();
            foreach(Button button in navigation.Controls) {
                bool active=Object.ReferenceEquals(button.Tag,value);
                button.BackColor=active?Theme.Card:Theme.Panel;
                button.ForeColor=active?Theme.Accent:Theme.Muted;
            }
            ResumeLayout();
            if(SelectedPageChanged!=null)SelectedPageChanged(this,EventArgs.Empty);
        }
    }
    public int SelectedIndex{get{return Pages.IndexOf(selected);}set{SelectedPage=Pages[value];}}
    public PageDeck() {
        Dock=DockStyle.Fill;
        BackColor=Theme.Bg;
        Pages=new PageCollection(this);
        navigation=Theme.Flow(true);
        navigation.Dock=DockStyle.None;
        navigation.AutoSize=true;
        navigation.BackColor=Theme.Panel;
        navigation.Padding=new Padding(12,20,8,12);
        navigation.AutoScroll=false;
        var navigationScroll=new ScrollSurface(navigation){Dock=DockStyle.Left,Width=186,BackColor=Theme.Panel};
        Controls.Add(content);
        Controls.Add(navigationScroll);
    }
    void AddPage(PageView page) {
        page.Visible=false;
        content.Controls.Add(page);
        var button=Theme.Button(page.Text,()=>SelectedPage=page);
        button.Tag=page;
        button.AutoSize=false;
        button.Size=new Size(148,46);
        button.MinimumSize=Size.Empty;
        button.Margin=new Padding(0,0,0,6);
        button.BackColor=Theme.Panel;
        button.ForeColor=Theme.Muted;
        navigation.Controls.Add(button);
        if(selected==null) SelectedPage=page;
    }
}
