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
    public static readonly Color Bg=Hex("#F4F1EA"), Panel=Hex("#ece8df"), Card=Hex("#e4dfd6"), Field=Hex("#f7f5ef"),
        Text=Hex("#141210"), Muted=Hex("#777065"), Accent=Hex("#FF3B00"), Line=Hex("#c6c0b4"),
        Green=Hex("#5f7245"), Red=Hex("#ae4a24"), Yellow=Hex("#b36b00");
    static Color Hex(string value){return ColorTranslator.FromHtml(value);}
    public static Color VmColor(object row){try{return ColorTranslator.FromHtml(J.S(J.V(row,"data"),"ui_color","#65C7FF"));}catch(ArgumentException){return Accent;}}
    public static string State(string value){switch(value){case "running":return "Работает";case "paused":return "На паузе";case "stopped":return "Выключена";case "prelaunch":return "Запускается";case "shutdown":return "Выключается";case "saved":return "Сохранена";case "error":return "Ошибка запуска";case "starting":return "Запускается";case "stopping":return "Выключается";case "mixed":return "Смешанное";case "empty":return "Пустой стенд";case "checking":return "Проверяется…";case "recovery-required":return "Нужна проверка снимков";case "no-qmp":return "Запущена · нет QMP";default:return "Состояние неизвестно";}}
    public static Color StateColor(string value){return value=="running"?Green:value=="stopped"?Muted:new[]{"paused","prelaunch","shutdown","checking"}.Contains(value)?Yellow:Red;}
    public static void Form(Form form,int width,int height,bool topLevel=true,Color? iconColor=null){
        form.BackColor=Bg;form.ForeColor=Text;form.Font=MotionPaint.Font("Segoe UI",12);form.AutoScaleMode=AutoScaleMode.Dpi;
        form.ClientSize=new Size(width,height);form.MinimumSize=new Size(Math.Min(width,740),Math.Min(height,520));form.StartPosition=FormStartPosition.CenterScreen;
        form.HandleCreated+=(sender,args)=>Caption(form);
        if(topLevel){
            var icon=Icon(iconColor??Accent);
            form.Icon=icon;
            form.Disposed+=(sender,args)=>icon.Dispose();
            new DialogEntrance(form,form is MotionDialog);
        }
        if(Program.TestMode){form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-2400,-1600);}
    }
    public static void Caption(Form form){
        if(!form.IsHandleCreated)return;
        try{int dark=0,caption=ColorTranslator.ToWin32(Bg),text=ColorTranslator.ToWin32(Text),border=ColorTranslator.ToWin32(Line);
            DwmSetWindowAttribute(form.Handle,20,ref dark,4);DwmSetWindowAttribute(form.Handle,35,ref caption,4);DwmSetWindowAttribute(form.Handle,36,ref text,4);DwmSetWindowAttribute(form.Handle,34,ref border,4);
        }catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
    }
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    [DllImport("user32.dll")]static extern bool DestroyIcon(IntPtr icon);
    public static Icon Icon(Color color){using(var bitmap=new Bitmap(32,32)){using(var graphics=Graphics.FromImage(bitmap))using(var fill=new SolidBrush(color))using(var ink=new SolidBrush(Bg))using(var font=UiTypography.Points(17F,FontStyle.Bold)){graphics.Clear(Color.Transparent);graphics.FillEllipse(fill,2,2,28,28);graphics.DrawString("Q",font,ink,4,1);}IntPtr handle=bitmap.GetHicon();try{return (Icon)System.Drawing.Icon.FromHandle(handle).Clone();}finally{DestroyIcon(handle);}}}
    public static Label Label(string text,int size=10,Color? color=null,bool bold=false){return new Label{Text=text,AutoSize=true,ForeColor=color??Text,Font=MotionPaint.Font("Arial",size,bold?FontStyle.Bold:FontStyle.Regular),Margin=new Padding(0,0,0,8),BackColor=Color.Transparent};}
    public static Button Button(string text,Action action,bool primary=false,bool danger=false){var button=new PolarButton{Text=text,Primary=primary,Danger=danger,AutoSize=true,MinimumSize=new Size(0,36),Padding=new Padding(15,10,15,10),BackColor=primary?Accent:MotionPaint.Hex("#e9e2d7"),ForeColor=primary?Color.White:danger?MotionPaint.Hex("#c3340a"):MotionPaint.Hex("#332d25"),Font=MotionPaint.Font("Segoe UI",11),Margin=new Padding(0,0,8,0)};button.Click+=(sender,args)=>action();return button;}
    public static FieldInput Input(string value=""){return new FieldInput{Text=value};}
    public static ThemedSelector Combo(params string[] items){var box=new ThemedSelector();box.Items.AddRange(items);if(items.Length>0)box.SelectedIndex=0;return box;}
    public static NumberInput Number(int value,int min,int max){return new NumberInput(value,min,max);}
    public static FlowLayoutPanel Flow(bool vertical=false){return new BufferedFlowLayoutPanel{AutoSize=true,WrapContents=!vertical,FlowDirection=vertical?FlowDirection.TopDown:FlowDirection.LeftToRight,Dock=DockStyle.Top,Margin=new Padding(0),Padding=new Padding(0)};}
    public static void Title(Control parent,string title,string subtitle){parent.Controls.Add(Label(subtitle,10,Muted));parent.Controls.Add(Label(title,22,null,true));}
    public static Control Header(string title,string subtitle,string eyebrow=""){
        return new ReferenceHeader(title,subtitle,eyebrow);
    }
    public static void Dialog(Control host,Control content,Control buttons,string title,string subtitle,string eyebrow="",bool compact=false){
        if(content is TableLayoutPanel&&content.Tag is int)content=FieldsView((TableLayoutPanel)content);
        content.Dock=DockStyle.Fill;buttons.Dock=DockStyle.Bottom;buttons.BackColor=MotionPaint.Hex("#f1ece3");buttons.Padding=new Padding(30,16,22,16);content.Padding=new Padding(30,26,30,26);
        var actions=buttons as FlowLayoutPanel;if(actions!=null){var ordered=actions.Controls.Cast<Control>().ToArray();actions.FlowDirection=FlowDirection.RightToLeft;for(int i=0;i<ordered.Length;i++)actions.Controls.SetChildIndex(ordered[i],ordered.Length-1-i);}
        Control header;
        if(compact){var layout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=2,Padding=new Padding(24,16,24,8),BackColor=Bg};var heading=Label(title,18,null,true);var detail=Label(subtitle,9,Muted);layout.Controls.Add(heading,0,0);layout.Controls.Add(detail,0,1);layout.SizeChanged+=(sender,args)=>{int width=Math.Max(1,layout.ClientSize.Width-layout.Padding.Horizontal);heading.MaximumSize=detail.MaximumSize=new Size(width,0);};header=layout;}
        else header=Header(title,subtitle,eyebrow);
        host.Controls.Add(content);host.Controls.Add(buttons);host.Controls.Add(header);var form=host as Form;if(form!=null){new ReferenceDialogChrome(form,header);new ReferenceDialogBackdrop(form);
            var dialog=form as MotionDialog;if(dialog!=null&&dialog.Presentation!=null)dialog.Presentation.Header=header as ReferenceHeader;
        }
    }
    public static void AuxiliaryDialog(Form form,Control content,Control buttons,string title,string subtitle,string eyebrow="ПАРАМЕТРЫ",bool compact=false){
        Dialog(form,content,buttons,title,subtitle,eyebrow);
        var header=form.Controls.OfType<ReferenceHeader>().Single();Action fit=()=>{float available=Math.Max(100,header.Width-85),size=38,width;using(var path=MotionPaint.TextPath(title,size,-size*.078F,out width,800))if(width>available)size=Math.Max(24,size*available/width);header.Compact(title.Split(' ')[0].ToUpperInvariant(),size);};header.SizeChanged+=(s,e)=>fit();fit();
        StyleAuxiliary(content);StyleAuxiliary(buttons);content.Padding=new Padding(24);buttons.Padding=new Padding(24,16,24,16);
        var actions=buttons as FlowLayoutPanel;if(actions!=null){actions.AutoSize=true;actions.AutoSizeMode=AutoSizeMode.GrowAndShrink;actions.WrapContents=true;}
    }
    public static void StyleAuxiliary(Control root){
        foreach(var control in new[]{root}.Concat(Descendants(root)).ToArray()){
            var label=control as Label;if(label!=null){label.Font=UiTypography.Make(label.Font.Size>=17?17.5F:10,label.Font.Bold?700:400);if(label.AutoSize)label.Margin=new Padding(label.Margin.Left,label.Margin.Top,label.Margin.Right,Math.Max(8,label.Margin.Bottom));}
            var button=control as Button;if(button!=null){button.Font=UiTypography.Make(9.5F,500);if(button.AutoSize&&!(button.Parent!=null&&button.Parent.Parent is WorkspaceTabs))button.Padding=new Padding(14,9,14,9);}
            if(control is FieldInput||control is ThemedSelector)control.Font=UiTypography.Make(11);
            if(control is CheckBox||control is RadioButton)control.Font=UiTypography.Make(10);
            if(control is ThemedEditor)control.Font=UiTypography.Make(11);
            var table=control as TableLayoutPanel;if(table!=null&&table.Tag is int)foreach(Control block in table.Controls){var cell=table.GetPositionFromControl(block);bool full=table.GetColumnSpan(block)>1;block.Margin=new Padding(full||cell.Column==0?0:8,0,full||cell.Column==1?0:8,16);}
        }
        foreach(var tabs in new[]{root}.Concat(Descendants(root)).OfType<WorkspaceTabs>())tabs.FitAuxiliaryNavigation();
    }
    static IEnumerable<Control> Descendants(Control root){foreach(Control child in root.Controls){yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    public static void SettingsDialog(Form form,PageDeck tabs,Control buttons,string title,string subtitle,bool create){
        var batch=new[]{(Control)form,tabs}.Concat(Descendants(tabs)).Distinct().ToArray();foreach(var control in batch)control.SuspendLayout();
        try{
        var flow=buttons as FlowLayoutPanel;if(flow!=null){var order=flow.Controls.Cast<Control>().ToArray();foreach(var control in order)flow.Controls.SetChildIndex(control,0);flow.Dock=DockStyle.Right;flow.AutoSize=false;flow.Padding=Padding.Empty;flow.FlowDirection=FlowDirection.LeftToRight;flow.WrapContents=false;flow.Width=218;flow.Height=39;foreach(Button button in flow.Controls){button.Font=UiTypography.Make(9,500);button.AutoSize=false;button.MinimumSize=Size.Empty;button.Size=new Size(105,37);button.Margin=new Padding(0,0,8,0);button.Padding=new Padding(10,8,10,8);}if(flow.Controls.Count>0)flow.Controls[flow.Controls.Count-1].Margin=Padding.Empty;}
        var footer=new BufferedPanel{Height=71};footer.Controls.Add(buttons);
        Dialog(form,tabs,footer,title,subtitle,create?"СОЗДАНИЕ МАШИНЫ":"ПАРАМЕТРЫ МАШИНЫ");footer.Padding=new Padding(30,15,30,15);tabs.Padding=Padding.Empty;tabs.SettingsPresentation();
        var header=form.Controls.OfType<ReferenceHeader>().Single();header.Compact(create?"CREATE":"SETTINGS");
        Action pageTitle=()=>{int index=tabs.SelectedIndex;header.Present(title,subtitle,create?"СОЗДАНИЕ МАШИНЫ":(index+1).ToString("00")+" / ПАРАМЕТРЫ МАШИНЫ");};tabs.SelectedPageChanged+=(s,e)=>pageTitle();pageTitle();
        foreach(var control in Descendants(tabs).ToArray()){
            var label=control as Label;if(label!=null){label.Font=UiTypography.Make(label.Font.Size>=17?17.5F:10F,label.Font.Bold?700:400);}
            if(control is Button)control.Font=UiTypography.Make(control.Text=="Настроить…"?9:9.5F,500);
            if(control is FieldInput||control is ThemedSelector)control.Font=UiTypography.Make(11);
            if(control is ThemedEditor)control.Font=UiTypography.Make(((ThemedEditor)control).ReadOnly?10:12);
            if(control is AppearanceView||control is ExtraHelpView)control.Padding=Padding.Empty;
            var table=control as TableLayoutPanel;if(table!=null&&table.Tag is int){foreach(Control block in table.Controls){var position=table.GetPositionFromControl(block);bool full=table.GetColumnSpan(block)>1;block.Margin=new Padding(full||position.Column==0?0:8,0,full||position.Column==1?0:8,16);foreach(var fieldLabel in block.Controls.OfType<Label>())fieldLabel.Font=UiTypography.Make(9.5F,500);}}
        }
        foreach(var appearance in Descendants(tabs).OfType<AppearanceView>().ToArray())appearance.ReferenceLayout();
        int n=0;foreach(var page in tabs.Pages){page.BackColor=Theme.Bg;string text=page.Text=="Параметры VM"?(create?"Новая машина":"Параметры VM"):page.Text=="Сеть"?"Сетевые адаптеры":page.Text=="Аргументы QEMU"?"Дополнительные аргументы":page.Text=="Оформление"?"Значок системы":page.Text;string[] tags={"ПАРАМЕТРЫ","СЕТЬ","АРГУМЕНТЫ","СПРАВКА","ЗНАЧОК","JSON"};
            foreach(var existing in Descendants(page).OfType<Label>().Where(l=>l.Text==text&&l.Font.Size>=17).ToArray()){existing.Parent.Controls.Remove(existing);existing.Dispose();}
            foreach(var layout in Descendants(page).OfType<TableLayoutPanel>().Where(t=>t.Controls.OfType<ThemedEditor>().Any()).ToArray())layout.Padding=Padding.Empty;
            if(page.Text=="Аргументы QEMU")foreach(var editorLayout in Descendants(page).OfType<TableLayoutPanel>().Where(t=>t.RowCount==3&&t.Controls.OfType<ThemedEditor>().Any()).ToArray()){var editorHeading=editorLayout.GetControlFromPosition(0,0) as FlowLayoutPanel;if(editorHeading!=null){editorHeading.AutoSize=false;Action fitHeading=()=>{int width=Math.Max(1,editorLayout.ClientSize.Width);int height=editorHeading.Controls.Cast<Control>().Sum(c=>c.GetPreferredSize(new Size(width,0)).Height+c.Margin.Vertical);editorHeading.Height=height;editorLayout.RowStyles[0].SizeType=SizeType.Absolute;editorLayout.RowStyles[0].Height=height+editorHeading.Margin.Vertical;};editorLayout.SizeChanged+=(s,e)=>fitHeading();fitHeading();}}
            page.Controls.Add(new SettingsHeading(text,n<tags.Length?tags[n]:""));if(page.Text=="Конфигурация JSON")foreach(var editor in Descendants(page).OfType<ThemedEditor>())editor.Font=UiTypography.Make(11);n++;
        }
        }finally{for(int index=batch.Length-1;index>=0;index--)if(!batch[index].IsDisposed)batch[index].ResumeLayout(true);}
    }
    public static TableLayoutPanel Fields(){var table=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=0,Padding=Padding.Empty,AutoScroll=false,BackColor=Bg,Tag=0};table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));return table;}
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
            var label=Label(title,11,MotionPaint.Hex("#736854"));
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
        var heading=Flow(true);heading.AutoSizeMode=AutoSizeMode.GrowAndShrink;heading.Dock=DockStyle.Top;heading.Margin=new Padding(0,0,0,18);heading.Controls.Add(Label(title,20,null,true));
        var note=Label(description,10,Muted);heading.Controls.Add(note);layout.Controls.Add(heading,0,0);
        heading.Resize+=(sender,args)=>note.MaximumSize=new Size(Math.Max(1,heading.ClientSize.Width),0);
        editor.Margin=Padding.Empty;editor.Dock=DockStyle.Fill;layout.Controls.Add(editor,0,1);
        if(buttons!=null){buttons.Dock=DockStyle.Fill;buttons.Margin=new Padding(0,18,0,0);buttons.Padding=Padding.Empty;layout.Controls.Add(buttons,0,2);}return layout;
    }
    public static ThemedList ListBox(int size=11) { return new ThemedList { Font=UiTypography.Points(size),ItemHeight=48 }; }
    public static ThemedEditor Editor(string value="") { return new ThemedEditor(value) { ForeColor=Text }; }

}

sealed class ToggleButton : CheckBox {
    public ToggleButton(){Appearance=Appearance.Button;AutoSize=true;MinimumSize=new Size(130,40);Padding=new Padding(12,8,12,8);Margin=new Padding(0,0,10,8);FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Cursor=Cursors.Hand;SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
    protected override void OnCheckedChanged(EventArgs e){base.OnCheckedChanged(e);Invalidate();}
    protected override void OnPaint(PaintEventArgs e){e.Graphics.Clear(Parent==null?Theme.Bg:Parent.BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;Color fill=Checked?Theme.Accent:Theme.Card,ink=Checked?Theme.Bg:Theme.Text;if(!Enabled){fill=Theme.Panel;ink=Theme.Muted;}using(var path=Drawing.Rounded(new Rectangle(0,0,Width-1,Height-1),7))using(var brush=new SolidBrush(fill))e.Graphics.FillPath(brush,path);var bounds=new Rectangle(Padding.Left,Padding.Top,Math.Max(1,Width-Padding.Horizontal),Math.Max(1,Height-Padding.Vertical));TextRenderer.DrawText(e.Graphics,Text,Font,bounds,ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak);if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-5,-5),ink,fill);}
}

class PolarButton : Button {
    float scale=1;protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Width;base.ScaleControl(factor,specified);UpdateShape();}
    bool hover,pressed;float hoverFrom,hoverAmount,hoverTarget;readonly System.Diagnostics.Stopwatch hoverTime=new System.Diagnostics.Stopwatch();public bool Primary,Danger,Quiet,NavigationSelected,NavigationMode;public string SideLabel;int cornerRadius=-1;public int CornerRadius{get{return cornerRadius;}set{if(cornerRadius==value)return;cornerRadius=value;UpdateShape();Invalidate();}}public string IconName,TrailingIcon;
    internal Func<float> NavigationAmount;
    int EffectiveRadius{get{return CornerRadius<0?Height/2:(int)(CornerRadius*scale);}}
    void UpdateShape(){if(Region!=null){var previous=Region;Region=null;previous.Dispose();}}

    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);UpdateShape();}
    public PolarButton(){FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;Cursor=Cursors.Hand;SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
    void Transition(){hoverFrom=hoverAmount;hoverTarget=hover?1:0;hoverTime.Restart();AnimationClock.Run(this,motion=>{hoverAmount=motion?hoverFrom+(hoverTarget-hoverFrom)*MotionPaint.Ease((float)hoverTime.Elapsed.TotalMilliseconds/110):hoverTarget;Invalidate();return motion&&hoverTime.ElapsedMilliseconds<110;});Invalidate();}
    protected override void Dispose(bool disposing){if(disposing)AnimationClock.Cancel(this);base.Dispose(disposing);}
    public override Size GetPreferredSize(Size proposed){var measured=TextRenderer.MeasureText(Text,Font,new Size(proposed.Width>Padding.Horizontal?proposed.Width-Padding.Horizontal:Int32.MaxValue,Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding);return new Size(Math.Max(MinimumSize.Width,measured.Width+Padding.Horizontal+2+(String.IsNullOrEmpty(IconName)?0:(int)(21*scale))+(String.IsNullOrEmpty(TrailingIcon)?0:(int)(18*scale))),Math.Max(MinimumSize.Height,measured.Height+Padding.Vertical+2));}
    protected override void OnMouseEnter(EventArgs e){hover=true;Transition();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=pressed=false;Transition();base.OnMouseLeave(e);}
    protected override void OnMouseDown(MouseEventArgs e){pressed=true;Invalidate();base.OnMouseDown(e);}
    protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
    protected override void OnClick(EventArgs e){
        pressed=hover=false;hoverAmount=hoverFrom=hoverTarget=0;AnimationClock.Cancel(this);
        Invalidate();Update();base.OnClick(e);
    }
    protected override void OnPaint(PaintEventArgs e){
        Color background=Parent==null?Theme.Bg:Parent.BackColor;
        if(Parent==null)e.Graphics.Clear(background);else{var saved=e.Graphics.Save();e.Graphics.TranslateTransform(-Left,-Top);using(var parentPaint=new PaintEventArgs(e.Graphics,new Rectangle(Left,Top,Width,Height))){InvokePaintBackground(Parent,parentPaint);InvokePaint(Parent,parentPaint);}e.Graphics.Restore(saved);}
        if(background.A==0)background=Theme.Bg;e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        var bounds=new Rectangle(0,0,Math.Max(1,Width-1),Math.Max(1,Height-1));Color fill=Primary?Theme.Accent:Danger?MotionPaint.Mix(background,Theme.Accent,.043F):BackColor;
        Color border=Primary?Theme.Accent:Danger?MotionPaint.Mix(background,Theme.Accent,.314F):MotionPaint.Hex("#c5baaa"),ink=ForeColor;
        if(Enabled&&hoverAmount>0){Color destination=Primary?MotionPaint.Hex("#df3400"):MotionPaint.Hex("#ded4c5");fill=MotionPaint.Mix(fill,destination,hoverAmount);border=MotionPaint.Mix(border,Primary?destination:MotionPaint.Hex("#aa9982"),hoverAmount);}
        if(!Enabled){fill=MotionPaint.Mix(background,fill,.35F);ink=MotionPaint.Mix(background,ink,.35F);border=MotionPaint.Mix(background,border,.35F);}
        if(Quiet){fill=background;border=background;}if(NavigationSelected){fill=MotionPaint.Hex("#e7dac8");border=MotionPaint.Hex("#c8a67b");}else if(NavigationMode){fill=hover?MotionPaint.Hex("#e9e2d7"):background;border=hover?MotionPaint.Hex("#d2c5b2"):background;}
        int radius=EffectiveRadius;using(var path=Drawing.Rounded(bounds,radius))using(var brush=new SolidBrush(fill))using(var pen=new Pen(border)){e.Graphics.FillPath(brush,path);e.Graphics.DrawPath(pen,path);}
        int offset=Enabled&&pressed?1:Enabled&&hover?-1:0;var textBounds=new Rectangle(Padding.Left,Padding.Top+offset,Math.Max(1,Width-Padding.Horizontal),Math.Max(1,Height-Padding.Vertical));
        if(!String.IsNullOrEmpty(IconName)){int icon=(int)((String.IsNullOrEmpty(Text)?16:14)*scale);int left=String.IsNullOrEmpty(Text)?(Width-icon)/2:textBounds.Left;ReferenceIcons.Draw(e.Graphics,IconName,new RectangleF(left,(Height-icon)/2+offset,icon,icon),ink);textBounds.X+=icon+(int)(7*scale);textBounds.Width=Math.Max(1,textBounds.Width-icon-(int)(7*scale));}
        if(!String.IsNullOrEmpty(TrailingIcon)){int icon=(int)(14*scale);ReferenceIcons.Draw(e.Graphics,TrailingIcon,new RectangleF(Width-Padding.Right-icon,(Height-icon)/2+offset,icon,icon),ink);textBounds.Width=Math.Max(1,textBounds.Width-icon-(int)(4*scale));}
        if(NavigationSelected)using(var brush=new SolidBrush(MotionPaint.Mix(fill,Theme.Accent,NavigationAmount==null?1:NavigationAmount())))e.Graphics.FillRectangle(brush,1,6,2,Math.Max(1,Height-12));if(!String.IsNullOrEmpty(SideLabel)){using(var font=MotionPaint.Font("Courier New",9))TextRenderer.DrawText(e.Graphics,SideLabel,font,new Rectangle(NavigationMode?8:Width-32,0,20,Height),Theme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.Right);if(NavigationMode)textBounds.X+=24;textBounds.Width=Math.Max(1,textBounds.Width-24);}
        var flags=TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.VerticalCenter;if(TextAlign==ContentAlignment.MiddleLeft||TextAlign==ContentAlignment.TopLeft)flags|=TextFormatFlags.Left;else flags|=TextFormatFlags.HorizontalCenter;
        if(NavigationMode){string[] lines=Text=="Конфигурация JSON"?new[]{"Конфигурация","JSON"}:new[]{Text};float y=(Height-lines.Length*14.5F*scale)/2;foreach(string line in lines){float length;using(var path=MotionPaint.TextPath(line,9.5F*scale,-.045F*9.5F*scale,out length,500))using(var brush=new SolidBrush(ink)){var boundsGlyph=path.GetBounds();using(var transform=new Matrix()){transform.Translate(textBounds.Left,y-boundsGlyph.Top+2*scale);path.Transform(transform);}e.Graphics.FillPath(brush,path);}y+=14.5F*scale;}}else TextRenderer.DrawText(e.Graphics,Text,Font,textBounds,ink,flags);if(Focused&&ShowFocusCues)using(var pen=new Pen(Theme.Accent,2))using(var focus=Drawing.Rounded(Rectangle.Inflate(bounds,-3,-3),Math.Max(2,radius-3)))e.Graphics.DrawPath(pen,focus);
    }
}

sealed class PageView : BufferedPanel {
    public PageView(string text){Text=text;Dock=DockStyle.Fill;BackColor=Theme.Bg;}
}

sealed class ResourceSummary : UserControl {
    readonly Label memory=Theme.Label("—",14),cores=Theme.Label("—",14),profile=Theme.Label("—",14);
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
        public void Add(PageView page){if(page==null)throw new ArgumentNullException("page");if(page.Parent!=null||pages.Contains(page))throw new ArgumentException("Вкладка уже добавлена в контейнер.");pages.Add(page);owner.AddPage(page);}
        public void Move(PageView page,int index){int previous=pages.IndexOf(page);if(previous<0||index<0||index>=pages.Count)throw new ArgumentOutOfRangeException("index");pages.RemoveAt(previous);pages.Insert(index,page);var button=owner.navigation.Controls.Cast<Control>().First(control=>Object.ReferenceEquals(control.Tag,page));owner.navigation.Controls.SetChildIndex(button,index);}
        public IEnumerator<PageView> GetEnumerator(){return pages.GetEnumerator();}
        IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
    }
    readonly FlowLayoutPanel navigation;ScrollSurface navigationScroll;
    readonly SelectionMotion selectionMotion=new SelectionMotion();
    readonly PageFocus pageFocus=new PageFocus();
    public bool NavigationVisible{get{return navigationScroll.Visible;}set{navigationScroll.Visible=value;}}
    readonly Panel content=new BufferedPanel{Dock=DockStyle.Fill};PageView selected;
    public PageCollection Pages{get;private set;}
    public event EventHandler<PageSelectingEventArgs> Selecting;
    public event EventHandler SelectedPageChanged;
    public PageView SelectedPage {
        get { return selected; }
        set {
            if(value==selected) return;
            if(value==null || !Pages.Contains(value)) throw new ArgumentException("Вкладка не найдена.");
            var args=new PageSelectingEventArgs(value);
            if(Selecting!=null) Selecting(this,args);
            if(args.Cancel) return;
            bool switching=selected!=null, restoreFocus=pageFocus.Remember(selected);
            SuspendLayout();
            if(selected!=null) selected.Visible=false;
            selected=value;
            selected.Visible=true;
            selected.BringToFront();
            foreach(Button button in navigation.Controls.OfType<Button>()) {
                bool active=Object.ReferenceEquals(button.Tag,value);
                button.BackColor=active?MotionPaint.Hex("#e7dac8"):MotionPaint.Hex("#e9e2d7");((PolarButton)button).NavigationSelected=active;
                button.ForeColor=active?Theme.Text:Theme.Muted;if(active)selectionMotion.Start(navigation,button,switching);button.Invalidate();
            }
            ResumeLayout();
            if(restoreFocus)pageFocus.Restore(value);
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
        navigation.BackColor=Theme.Bg;
        navigation.Padding=Padding.Empty;
        navigation.AutoScroll=false;
        navigationScroll=new ScrollSurface(navigation){Dock=DockStyle.Left,Width=220,BackColor=Theme.Bg};
        Controls.Add(content);
        Controls.Add(navigationScroll);
    }
    public void SettingsPresentation(){
        content.BackColor=Theme.Bg;content.Padding=new Padding(23,22,23,26);navigationScroll.Width=179;navigationScroll.Padding=new Padding(10,20,0,20);navigationScroll.BackColor=navigation.BackColor=MotionPaint.Hex("#f1ece3");
        var kicker=Theme.Label("РАЗДЕЛЫ\nПАРАМЕТРОВ",8,Theme.Muted);kicker.AutoSize=false;kicker.Size=new Size(155,58);kicker.Margin=new Padding(0,0,0,13);kicker.Paint+=(s,e)=>{using(var pen=new Pen(MotionPaint.Hex("#c9c0b3")))e.Graphics.DrawLine(pen,0,kicker.Height-1,kicker.Width,kicker.Height-1);};navigation.Controls.Add(kicker);navigation.Controls.SetChildIndex(kicker,0);
        foreach(Button button in navigation.Controls.OfType<Button>()){var page=(PageView)button.Tag;var polar=(PolarButton)button;polar.NavigationMode=true;polar.SideLabel=(Pages.IndexOf(page)+1).ToString("00");button.Size=new Size(155,44);button.Padding=new Padding(7,9,7,9);button.Font=UiTypography.Make(9.5F,500);button.Margin=new Padding(0,0,0,7);button.BackColor=navigation.BackColor;}
        BackColor=Theme.Bg;
    }
    void AddPage(PageView page) {
        page.Visible=false;
        content.Controls.Add(page);
        var button=Theme.Button(page.Text,()=>SelectedPage=page);
        button.Tag=page;
        ((PolarButton)button).NavigationAmount=()=>selectionMotion.Amount(button);
        button.AutoSize=false;
        button.Size=new Size(190,40);button.Padding=new Padding(12);button.Font=MotionPaint.Font("Segoe UI",11);button.TextAlign=ContentAlignment.MiddleLeft;((PolarButton)button).CornerRadius=6;
        var textSize=TextRenderer.MeasureText(page.Text,button.Font,new Size(146,0),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix);
        button.Height=Math.Max(40,textSize.Height+button.Padding.Vertical+2);button.SizeChanged+=(s,e)=>{if(((PolarButton)button).NavigationMode)return;int h=TextRenderer.MeasureText(button.Text,button.Font,new Size(Math.Max(1,button.Width-button.Padding.Horizontal),Int32.MaxValue),TextFormatFlags.WordBreak).Height+button.Padding.Vertical+2;if(button.Height!=Math.Max(40,h))button.Height=Math.Max(40,h);};
        button.MinimumSize=Size.Empty;
        button.Margin=new Padding(0,0,0,8);
        button.BackColor=MotionPaint.Hex("#e9e2d7");
        button.ForeColor=Theme.Muted;
        navigation.Controls.Add(button);
        if(new[]{"Resources","Storage","Display","Appearance"}.Contains(page.Text))((PolarButton)button).SideLabel=page.Text=="Resources"?"01":page.Text=="Storage"?"02":page.Text=="Display"?"03":"04";
        if(selected==null) SelectedPage=page;
    }
}
