using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Shared installer/updater chrome. No dependency on the application frontend.
static class BootstrapUI {
    public static readonly Color Bg=ColorTranslator.FromHtml("#F4F1EA"),Panel=ColorTranslator.FromHtml("#ece8df"),
        Card=ColorTranslator.FromHtml("#e9e2d7"),Text=ColorTranslator.FromHtml("#141210"),
        Muted=ColorTranslator.FromHtml("#777065"),Accent=ColorTranslator.FromHtml("#FF3B00"),Line=ColorTranslator.FromHtml("#c6bba9"),Field=ColorTranslator.FromHtml("#fcf9f3");
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int key,ref int value,int size);
    public static void Style(Form form,string title,int height){
        form.Text=title;form.BackColor=Bg;form.ForeColor=Text;form.Font=UiTypography.Make(11);
        form.AutoScaleMode=AutoScaleMode.Dpi;
        form.ClientSize=new Size(720,height);form.MinimumSize=new Size(680,height+39);form.StartPosition=FormStartPosition.CenterScreen;
        form.Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        form.HandleCreated+=(sender,args)=>{
            try{int dark=0,color=ColorTranslator.ToWin32(Bg),ink=ColorTranslator.ToWin32(Text);
                DwmSetWindowAttribute(form.Handle,20,ref dark,4);DwmSetWindowAttribute(form.Handle,35,ref color,4);DwmSetWindowAttribute(form.Handle,36,ref ink,4);
            }catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
        };
    }
    public static Label Label(string text,int size=10,bool bold=false){
        return new Label{Text=text,AutoSize=true,ForeColor=Text,Font=UiTypography.Make(size,bold?700:400),Margin=new Padding(0,0,0,12)};
    }
    public static Button Button(string text,bool primary=false){
        return new BootstrapButton{Text=text,BackColor=primary?Accent:Card,ForeColor=primary?Color.White:Text,
            AutoSize=true,MinimumSize=new Size(140,38),Font=UiTypography.Make(9.5F,500),Padding=new Padding(16,8,16,8),Margin=new Padding(0,0,12,0)};
    }
    public static FlowLayoutPanel Buttons(params Button[] buttons){
        var flow=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=72,Padding=new Padding(28,16,20,16),WrapContents=false,BackColor=ColorTranslator.FromHtml("#f1ece3")};
        flow.Controls.AddRange(buttons);return flow;
    }
    public static Control Header(string text,bool compact=false){return new BootstrapHeader(text,compact);}

}

sealed class BootstrapPathField : UserControl {
    readonly TextBox input=new TextBox{BorderStyle=BorderStyle.None,BackColor=BootstrapUI.Field,ForeColor=BootstrapUI.Text};
    public override string Text{get{return input==null?base.Text:input.Text;}set{if(input!=null)input.Text=value;}}
    public BootstrapPathField(){Height=40;BackColor=BootstrapUI.Bg;Font=UiTypography.Make(10);input.Font=Font;Controls.Add(input);DoubleBuffered=true;input.GotFocus+=(sender,args)=>Invalidate();input.LostFocus+=(sender,args)=>Invalidate();}
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);if(input!=null)input.Font=Font;PerformLayout();}
    protected override void OnLayout(LayoutEventArgs e){base.OnLayout(e);if(input!=null)input.SetBounds(12,(Height-input.PreferredHeight)/2,Math.Max(1,Width-24),input.PreferredHeight);}
    protected override void OnPaint(PaintEventArgs e){
        e.Graphics.Clear(BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        int diameter=Math.Min(16,Math.Min(Width-1,Height-1));if(diameter<1)return;
        using(var path=new GraphicsPath()){
            path.AddArc(0,0,diameter,diameter,180,90);path.AddArc(Width-1-diameter,0,diameter,diameter,270,90);
            path.AddArc(Width-1-diameter,Height-1-diameter,diameter,diameter,0,90);path.AddArc(0,Height-1-diameter,diameter,diameter,90,90);path.CloseFigure();
            using(var fill=new SolidBrush(BootstrapUI.Field))e.Graphics.FillPath(fill,path);
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
            int d=Math.Max(2,Height-1);path.AddArc(bounds.Left,bounds.Top,d,d,180,90);path.AddArc(bounds.Right-d,bounds.Top,d,d,270,90);
            path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90);path.AddArc(bounds.Left,bounds.Bottom-d,d,d,90,90);path.CloseFigure();
            using(var fill=new SolidBrush(Enabled?BackColor:BootstrapUI.Panel))e.Graphics.FillPath(fill,path);
            using(var pen=new Pen(Focused?BootstrapUI.Accent:BootstrapUI.Line))e.Graphics.DrawPath(pen,path);
        }
        TextRenderer.DrawText(e.Graphics,Text,Font,bounds,Enabled?ForeColor:BootstrapUI.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
    }
}

sealed class ComponentProgress : Panel {
    readonly Label title=BootstrapUI.Label("",11,true),detail=BootstrapUI.Label("",9);
    readonly BootstrapProgress bar=new BootstrapProgress{Dock=DockStyle.Bottom,Height=8};
    public ComponentProgress(string name){
        Height=92;BackColor=BootstrapUI.Panel;Padding=new Padding(18,14,18,14);Margin=new Padding(0,0,0,12);
        title.Text=name;title.Dock=DockStyle.Top;detail.Text="Ожидает";detail.ForeColor=BootstrapUI.Muted;detail.Dock=DockStyle.Fill;
        Controls.Add(detail);Controls.Add(title);Controls.Add(bar);
    }
    protected override void OnPaintBackground(PaintEventArgs e){e.Graphics.Clear(Parent==null?BootstrapUI.Bg:Parent.BackColor);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;var r=new Rectangle(0,0,Width-1,Height-1);using(var path=new GraphicsPath()){int d=16;path.AddArc(0,0,d,d,180,90);path.AddArc(r.Right-d,0,d,d,270,90);path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(0,r.Bottom-d,d,d,90,90);path.CloseFigure();using(var fill=new SolidBrush(BootstrapUI.Panel))e.Graphics.FillPath(fill,path);using(var pen=new Pen(BootstrapUI.Line))e.Graphics.DrawPath(pen,path);}}
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

sealed class BootstrapHeader : Control {
    readonly string title;readonly bool compact;float scale=1;readonly Timer motion=new Timer{Interval=33};readonly System.Diagnostics.Stopwatch clock=System.Diagnostics.Stopwatch.StartNew();
    public BootstrapHeader(string title,bool compact=false){this.title=title;this.compact=compact;motion.Tick+=(s,e)=>Invalidate();Dock=DockStyle.Top;Height=compact?104:142;SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
    protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Width;base.ScaleControl(factor,specified);}
    [DllImport("user32.dll")]static extern bool SystemParametersInfo(uint action,uint parameter,out bool value,uint flags);
    protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);bool enabled=true;try{SystemParametersInfo(0x1042,0,out enabled,0);}catch(EntryPointNotFoundException){}motion.Enabled=Visible&&enabled&&!compact;}
    protected override void Dispose(bool disposing){if(disposing)motion.Dispose();base.Dispose(disposing);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);var g=e.Graphics;var state=g.Save();g.ScaleTransform(scale,scale);float width=Width/scale,height=Height/scale;g.SmoothingMode=SmoothingMode.AntiAlias;
        if(compact){
            using(var path=BootstrapCard.Shape(new RectangleF(0,0,width-1,height-1),16)){
                using(var fill=new LinearGradientBrush(new RectangleF(0,0,width,height),ColorTranslator.FromHtml("#eee5d8"),BootstrapUI.Bg,15F))g.FillPath(fill,path);
                using(var pen=new Pen(BootstrapUI.Line))g.DrawPath(pen,path);
            }
            using(var font=UiTypography.Make(9,600))using(var ink=new SolidBrush(BootstrapUI.Accent))g.DrawString("QEMU M  /  ALPHA",font,ink,20,15);
            using(var font=UiTypography.Make(25,800))using(var ink=new SolidBrush(BootstrapUI.Text))g.DrawString(title,font,ink,18,39);
            using(var font=UiTypography.Make(43,900))using(var ink=new SolidBrush(Color.FromArgb(10,BootstrapUI.Accent)))g.DrawString("UPDATE",font,ink,width-265,28);
            g.Restore(state);return;
        }
        using(var gradient=new LinearGradientBrush(new RectangleF(0,0,width,height),BootstrapUI.Bg,ColorTranslator.FromHtml("#eee5d8"),120F))g.FillRectangle(gradient,0,0,width,height);
        var ghost=g.Save();float drift=motion.Enabled?(float)((1-Math.Cos(clock.Elapsed.TotalSeconds*Math.PI/14))/2):0;g.TranslateTransform(width*.38F-15*drift,12+4*drift);g.RotateTransform(-4+3*drift);using(var font=UiTypography.Make(70,900))using(var ink=new SolidBrush(Color.FromArgb(14,BootstrapUI.Accent)))g.DrawString(title.ToUpperInvariant(),font,ink,0,0);g.Restore(ghost);
        float size=30;using(var font=UiTypography.Make(size,800)){var measured=g.MeasureString(title,font);if(measured.Width>width-48)size=Math.Max(22,size*(width-48)/measured.Width);}using(var font=UiTypography.Make(size,800))using(var ink=new SolidBrush(BootstrapUI.Text))g.DrawString(title,font,ink,24,43);
        using(var pen=new Pen(BootstrapUI.Line))g.DrawLine(pen,0,height-1,width,height-1);g.Restore(state);
    }
}

sealed class BootstrapCard : Panel {
    public BootstrapCard(){DoubleBuffered=true;BackColor=BootstrapUI.Panel;}
    internal static GraphicsPath Shape(RectangleF r,float radius){
        var path=new GraphicsPath();float d=Math.Max(1,Math.Min(radius*2,Math.Min(r.Width,r.Height)));
        path.AddArc(r.Left,r.Top,d,d,180,90);path.AddArc(r.Right-d,r.Top,d,d,270,90);
        path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.Left,r.Bottom-d,d,d,90,90);path.CloseFigure();return path;
    }
    protected override void OnPaintBackground(PaintEventArgs e){
        e.Graphics.Clear(Parent==null?BootstrapUI.Bg:Parent.BackColor);
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        if(Width<2||Height<2)return;
        using(var path=Shape(new RectangleF(0,0,Width-1,Height-1),16)){
            using(var brush=new SolidBrush(BackColor))e.Graphics.FillPath(brush,path);
            using(var pen=new Pen(BootstrapUI.Line))e.Graphics.DrawPath(pen,path);
        }
    }
}
