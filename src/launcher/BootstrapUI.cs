using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Shared installer/updater chrome. No dependency on the application frontend.
static class BootstrapUI {
    public static readonly Color Bg=Color.FromArgb(5,14,37),Panel=Color.FromArgb(11,27,54),
        Card=Color.FromArgb(16,39,70),Text=Color.FromArgb(249,242,245),
        Muted=Color.FromArgb(156,180,207),Accent=Color.FromArgb(101,199,255);
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int key,ref int value,int size);
    public static void Style(Form form,string title,int height){
        form.Text=title;form.BackColor=Bg;form.ForeColor=Text;form.Font=new Font("Segoe UI",10);
        form.ClientSize=new Size(720,height);form.MinimumSize=new Size(680,height+39);form.StartPosition=FormStartPosition.CenterScreen;
        form.Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        form.HandleCreated+=(sender,args)=>{
            try{int dark=1,color=ColorTranslator.ToWin32(Bg),ink=ColorTranslator.ToWin32(Text);
                DwmSetWindowAttribute(form.Handle,20,ref dark,4);DwmSetWindowAttribute(form.Handle,35,ref color,4);DwmSetWindowAttribute(form.Handle,36,ref ink,4);
            }catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
        };
    }
    public static Label Label(string text,int size=10,bool bold=false){
        return new Label{Text=text,AutoSize=true,ForeColor=Text,Font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular),Margin=new Padding(0,0,0,12)};
    }
    public static Button Button(string text,bool primary=false){
        return new BootstrapButton{Text=text,BackColor=primary?Accent:Card,ForeColor=primary?Bg:Text,
            AutoSize=true,MinimumSize=new Size(140,42),Padding=new Padding(16,8,16,8),Margin=new Padding(0,0,12,0)};
    }
    public static FlowLayoutPanel Buttons(params Button[] buttons){
        var flow=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=72,Padding=new Padding(28,12,20,12),WrapContents=false};
        flow.Controls.AddRange(buttons);return flow;
    }
    public static Control Header(string text){
        var panel=new TableLayoutPanel{Dock=DockStyle.Top,Height=100,ColumnCount=1,RowCount=2};
        var brand=Label("QEMU / VM MANAGER",9);brand.ForeColor=Muted;
        panel.Controls.Add(brand);panel.Controls.Add(Label(text,22,true));return panel;
    }
}

sealed class BootstrapPathField : UserControl {
    readonly TextBox input=new TextBox{BorderStyle=BorderStyle.None,BackColor=BootstrapUI.Panel,ForeColor=BootstrapUI.Text};
    public override string Text{get{return input==null?base.Text:input.Text;}set{if(input!=null)input.Text=value;}}
    public BootstrapPathField(){Height=40;BackColor=BootstrapUI.Bg;Font=new Font("Segoe UI",10F);input.Font=Font;Controls.Add(input);DoubleBuffered=true;input.GotFocus+=(sender,args)=>Invalidate();input.LostFocus+=(sender,args)=>Invalidate();}
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);if(input!=null)input.Font=Font;PerformLayout();}
    protected override void OnLayout(LayoutEventArgs e){base.OnLayout(e);if(input!=null)input.SetBounds(12,(Height-input.PreferredHeight)/2,Math.Max(1,Width-24),input.PreferredHeight);}
    protected override void OnPaint(PaintEventArgs e){
        e.Graphics.Clear(BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        int diameter=Math.Min(16,Math.Min(Width-1,Height-1));if(diameter<1)return;
        using(var path=new GraphicsPath()){
            path.AddArc(0,0,diameter,diameter,180,90);path.AddArc(Width-1-diameter,0,diameter,diameter,270,90);
            path.AddArc(Width-1-diameter,Height-1-diameter,diameter,diameter,0,90);path.AddArc(0,Height-1-diameter,diameter,diameter,90,90);path.CloseFigure();
            using(var fill=new SolidBrush(BootstrapUI.Panel))e.Graphics.FillPath(fill,path);
            using(var pen=new Pen(ContainsFocus?BootstrapUI.Accent:BootstrapUI.Card))e.Graphics.DrawPath(pen,path);
        }
    }
}

sealed class BootstrapButton : Button {
    public BootstrapButton(){FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);Cursor=Cursors.Hand;}
    protected override void OnPaint(PaintEventArgs e){
        e.Graphics.Clear(Parent==null?BootstrapUI.Bg:Parent.BackColor);
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        var bounds=new Rectangle(0,0,Width-1,Height-1);
        using(var path=new GraphicsPath()){
            int d=16;path.AddArc(bounds.Left,bounds.Top,d,d,180,90);path.AddArc(bounds.Right-d,bounds.Top,d,d,270,90);
            path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90);path.AddArc(bounds.Left,bounds.Bottom-d,d,d,90,90);path.CloseFigure();
            using(var fill=new SolidBrush(Enabled?BackColor:BootstrapUI.Panel))e.Graphics.FillPath(fill,path);
            if(Focused)using(var pen=new Pen(BootstrapUI.Accent))e.Graphics.DrawPath(pen,path);
        }
        TextRenderer.DrawText(e.Graphics,Text,Font,bounds,Enabled?ForeColor:BootstrapUI.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
    }
}

sealed class ComponentProgress : Panel {
    readonly Label title=BootstrapUI.Label("",11,true),detail=BootstrapUI.Label("",9);
    readonly BootstrapProgress bar=new BootstrapProgress{Dock=DockStyle.Bottom,Height=8};
    public ComponentProgress(string name){
        Height=92;BackColor=BootstrapUI.Panel;Padding=new Padding(14,10,14,12);Margin=new Padding(0,0,0,12);
        title.Text=name;title.Dock=DockStyle.Top;detail.Text="Ожидает";detail.ForeColor=BootstrapUI.Muted;detail.Dock=DockStyle.Fill;
        Controls.Add(detail);Controls.Add(title);Controls.Add(bar);
    }
    public void Report(string phase,long done=0,long? total=null){
        if(total.HasValue&&total.Value>0){
            bar.Style=ProgressBarStyle.Continuous;bar.Value=(int)Math.Min(1000,done*1000.0/total.Value);
            detail.Text=phase+" · "+(done*100.0/total.Value).ToString("0")+"%";
            if(phase=="Загрузка")detail.Text+=" · "+(done/1048576.0).ToString("0.0")+" / "+(total.Value/1048576.0).ToString("0.0")+" МБ";
        }else{bar.Style=ProgressBarStyle.Marquee;detail.Text=phase;}
    }
}

sealed class BootstrapProgress : Control {
    readonly Timer animation=new Timer{Interval=40};int offset,value;
    ProgressBarStyle style;
    public int Value{get{return value;}set{this.value=Math.Max(0,Math.Min(1000,value));Invalidate();}}
    public ProgressBarStyle Style{get{return style;}set{style=value;animation.Enabled=Visible&&style==ProgressBarStyle.Marquee;Invalidate();}}
    public BootstrapProgress(){DoubleBuffered=true;animation.Tick+=(sender,args)=>{offset=(offset+8)%Math.Max(1,Width+Width/3);Invalidate();};}
    protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);animation.Enabled=Visible&&style==ProgressBarStyle.Marquee;}
    protected override void OnPaint(PaintEventArgs e){
        e.Graphics.Clear(BootstrapUI.Card);
        using(var fill=new SolidBrush(BootstrapUI.Accent)){
            if(style==ProgressBarStyle.Marquee)e.Graphics.FillRectangle(fill,offset-Width/3,0,Width/3,Height);
            else e.Graphics.FillRectangle(fill,0,0,Width*value/1000,Height);
        }
    }
    protected override void Dispose(bool disposing){if(disposing)animation.Dispose();base.Dispose(disposing);}
}

sealed class InstallProgress {
    public string Component,Phase;
    public long Done;
    public long? Total;
    public InstallProgress(string component,string phase,long done=0,long? total=null){Component=component;Phase=phase;Done=done;Total=total;}
}
