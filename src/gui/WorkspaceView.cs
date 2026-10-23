using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

// The scrollbar owns scroll position; the viewport owns measurement and clipping.
// Neither component creates native scrollbars or changes Windows window styles.
sealed class SlimScrollBar : Control {
    int contentLength, viewportLength, position, grabOffset;
    bool dragging, hover;
    public event EventHandler ValueChanged;
    public bool LineUnits { get;set; }
    public int Maximum { get { return Math.Max(0,contentLength-viewportLength); } }
    public int Value {
        get { return position; }
        set {
            int next=Math.Max(0,Math.Min(Maximum,value));
            if(next==position) return;
            position=next;
            Invalidate();
            if(ValueChanged!=null) ValueChanged(this,EventArgs.Empty);
        }
    }
    public SlimScrollBar() {
        Width=12; TabStop=true; BackColor=Theme.Panel;
        AccessibleRole=AccessibleRole.ScrollBar; AccessibleName="Прокрутка списка";
        SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
    }
    public void SetRange(int content,int viewport) {
        contentLength=Math.Max(0,content); viewportLength=Math.Max(0,viewport);
        Value=position; Invalidate();
    }
    internal Rectangle Thumb {
        get {
            int track=Math.Max(1,ClientSize.Height-8);
            int length=Maximum==0?track:Math.Min(track,Math.Max(28,(int)((long)track*viewportLength/Math.Max(1,contentLength))));
            int top=4+(Maximum==0?0:(int)((long)(track-length)*Value/Maximum));
            return new Rectangle(4,top,4,length);
        }
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        if(Maximum==0) return;
        var thumb=Thumb;
        using(var pen=new Pen(hover||dragging?Theme.Accent:ColorTranslator.FromHtml("#405F80"),4)) {
            pen.StartCap=pen.EndCap=LineCap.Round;
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            e.Graphics.DrawLine(pen,thumb.Left+2,thumb.Top+2,thumb.Left+2,thumb.Bottom-2);
        }
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(1,thumb.Top,Width-2,thumb.Height));
    }
    protected override bool IsInputKey(Keys keyData) {
        return new[]{Keys.Up,Keys.Down,Keys.PageUp,Keys.PageDown,Keys.Home,Keys.End}.Contains(keyData&Keys.KeyCode)||base.IsInputKey(keyData);
    }
    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        switch(e.KeyCode) {
            case Keys.Up:Value-=LineUnits?1:32;break;case Keys.Down:Value+=LineUnits?1:32;break;
            case Keys.PageUp:Value-=viewportLength;break;case Keys.PageDown:Value+=viewportLength;break;
            case Keys.Home:Value=0;break;case Keys.End:Value=Maximum;break;default:return;
        }
        e.Handled=true;
    }
    protected override void OnMouseEnter(EventArgs e) { hover=true;Invalidate();base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover=false;Invalidate();base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        if(e.Button!=MouseButtons.Left) return;
        Focus(); var thumb=Thumb;
        if(e.Y>=thumb.Top&&e.Y<=thumb.Bottom) { dragging=true;grabOffset=e.Y-thumb.Top;Capture=true; }
        else Value+=e.Y<thumb.Top?-viewportLength:viewportLength;
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        if(dragging) {
            int travel=ClientSize.Height-8-Thumb.Height;
            if(travel>0) Value=(int)((long)(e.Y-grabOffset-4)*Maximum/travel);
        }
    }
    protected override void OnMouseUp(MouseEventArgs e) { dragging=false;Capture=false;Invalidate();base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e) { dragging=false;Invalidate();base.OnMouseCaptureChanged(e); }
    protected override void OnMouseWheel(MouseEventArgs e) {
        base.OnMouseWheel(e);ScrollWheel(e.Delta);
    }
    internal void ScrollWheel(int delta) {
        int lines=SystemInformation.MouseWheelScrollLines;
        int pixels=lines<0?viewportLength:lines*(LineUnits?1:Font.Height);
        Value-=delta*pixels/SystemInformation.MouseWheelScrollDelta;
    }
}

sealed class ScrollSurface : BufferedPanel {
    readonly Panel viewport=new BufferedPanel { Dock=DockStyle.Fill };
    readonly SlimScrollBar bar=new SlimScrollBar { Dock=DockStyle.Right };
    readonly Control content;
    bool arranging;
    public int ScrollOffset { get { return bar.Value; } set { bar.Value=value; } }
    internal SlimScrollBar ScrollBar { get { return bar; } }
    public ScrollSurface(Control content) {
        this.content=content; AutoScroll=false; Dock=DockStyle.Fill;
        content.Dock=DockStyle.None; viewport.Controls.Add(content);
        Controls.Add(viewport); Controls.Add(bar);
        bar.ValueChanged+=(sender,args)=>content.Top=-bar.Value;
        content.SizeChanged+=(sender,args)=>PerformLayout();
        content.ControlAdded+=(sender,args)=>PerformLayout();
        content.ControlRemoved+=(sender,args)=>PerformLayout();
        viewport.MouseWheel+=Wheel; content.MouseWheel+=Wheel;
    }
    void Wheel(object sender,MouseEventArgs e) {
        bar.ScrollWheel(e.Delta);
        var handled=e as HandledMouseEventArgs;if(handled!=null)handled.Handled=true;
    }
    protected override void OnMouseWheel(MouseEventArgs e) { Wheel(this,e);base.OnMouseWheel(e); }
    protected override void OnBackColorChanged(EventArgs e) {
        base.OnBackColorChanged(e);
        if(viewport!=null)viewport.BackColor=BackColor;
        if(bar!=null)bar.BackColor=BackColor;
    }
    protected override void OnLayout(LayoutEventArgs e) {
        if(content==null) { base.OnLayout(e);return; }
        if(arranging) return;
        arranging=true;
        try {
            int width=Math.Max(1,ClientSize.Width-Padding.Horizontal-bar.Width);
            content.MinimumSize=new Size(width,0); content.MaximumSize=new Size(width,0);
            content.Width=width;
            // Measure from an unconstrained height; a previously stretched table
            // must not hide rows below its old viewport height.
            content.Height=1;content.PerformLayout();
            int children=content.Controls.Cast<Control>().Where(c=>c.Visible).Select(c=>c.Bottom+c.Margin.Bottom).DefaultIfEmpty(0).Max()+content.Padding.Bottom;
            int height=Math.Max(children,content.GetPreferredSize(new Size(width,0)).Height);
            content.Height=height;
            bar.SetRange(height,Math.Max(0,ClientSize.Height-Padding.Vertical));
            bar.Visible=bar.Maximum>0;
            base.OnLayout(e);
            content.Location=new Point(0,-bar.Value);
        } finally { arranging=false; }
    }
}

sealed class LibraryCard : BufferedPanel {
    readonly Label title=Theme.Label("",10,null,true), subtitle=Theme.Label("",9,Theme.Muted);
    readonly ToolTip tooltip=new ToolTip();
    readonly Color identity;
    public string Status { get;private set; }
    public LibraryCard(string name,string label,Color identity) {
        Tag=name;this.identity=identity;Height=72;Width=222;Margin=new Padding(0,0,0,6);
        BackColor=Theme.Panel;Cursor=Cursors.Hand;AccessibleRole=AccessibleRole.ListItem;
        AccessibleName=label+" / "+name;
        title.Text=label;subtitle.Text=name;
        foreach(var text in new[]{title,subtitle}) { text.AutoSize=false;text.AutoEllipsis=true;text.BackColor=Color.Transparent;Controls.Add(text); }
        title.SetBounds(35,13,160,22);subtitle.SetBounds(35,37,160,19);
    }
    public void SetStatus(string value) {
        if(value==Status) return;
        Status=value;AccessibleDescription=Theme.State(value);
        tooltip.SetToolTip(this,AccessibleDescription);tooltip.SetToolTip(title,AccessibleDescription);
        Invalidate(new Rectangle(Math.Max(0,Width-19),0,19,Height));
    }
    protected override void OnSizeChanged(EventArgs e) {
        base.OnSizeChanged(e);if(title==null)return;title.Width=subtitle.Width=Math.Max(10,Width-62);
    }
    protected override void OnPaintBackground(PaintEventArgs e) {
        e.Graphics.Clear(Theme.Panel);
        if(BackColor==Theme.Card) {
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=Drawing.Rounded(new Rectangle(0,0,Width-1,Height-1),9))using(var brush=new SolidBrush(Theme.Card))e.Graphics.FillPath(brush,path);
        }
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(var pen=new Pen(identity,4)){pen.StartCap=pen.EndCap=LineCap.Round;e.Graphics.DrawLine(pen,17,25,17,46);}
        using(var brush=new SolidBrush(Theme.StateColor(Status)))e.Graphics.FillEllipse(brush,Width-17,20,4,4);
    }
    protected override void Dispose(bool disposing) { if(disposing)tooltip.Dispose();base.Dispose(disposing); }
}

static class Drawing {
    public static GraphicsPath Rounded(Rectangle bounds,int radius) {
        int diameter=Math.Min(radius*2,Math.Min(bounds.Width,bounds.Height));
        var path=new GraphicsPath();
        if(diameter<1){path.AddRectangle(bounds);return path;}
        path.AddArc(bounds.Left,bounds.Top,diameter,diameter,180,90);
        path.AddArc(bounds.Right-diameter,bounds.Top,diameter,diameter,270,90);
        path.AddArc(bounds.Right-diameter,bounds.Bottom-diameter,diameter,diameter,0,90);
        path.AddArc(bounds.Left,bounds.Bottom-diameter,diameter,diameter,90,90);path.CloseFigure();return path;
    }
}

sealed class MonitorEmblem : Control {
    Image logo;public Image Logo {get{return logo;}set{if(ReferenceEquals(logo,value))return;logo=value;Invalidate();}}
    public MonitorEmblem(){Size=new Size(66,66);SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer,true);AccessibleName="Виртуальная машина";}
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(var path=Drawing.Rounded(new Rectangle(0,0,Width-1,Height-1),16))
        using(var fill=new LinearGradientBrush(ClientRectangle,ColorTranslator.FromHtml("#163E64"),Theme.Card,45))e.Graphics.FillPath(fill,path);
        if(logo!=null){
            e.Graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;float scale=Math.Min((Width-20F)/logo.Width,(Height-20F)/logo.Height);
            int width=Math.Max(1,(int)(logo.Width*scale)),height=Math.Max(1,(int)(logo.Height*scale));e.Graphics.DrawImage(logo,new Rectangle((Width-width)/2,(Height-height)/2,width,height));return;
        }
        float sx=Width/66F,sy=Height/66F;e.Graphics.ScaleTransform(sx,sy);
        using(var pen=new Pen(Theme.Accent,1.7F)) {
            e.Graphics.DrawRectangle(pen,18,19,30,22);e.Graphics.DrawLine(pen,33,41,33,47);e.Graphics.DrawLine(pen,25,48,41,48);
        }
    }
}

sealed class WorkspaceTabs : UserControl {
    readonly FlowLayoutPanel navigation=Theme.Flow();
    readonly Panel body=new BufferedPanel { Dock=DockStyle.Fill };
    readonly List<Control> pages=new List<Control>();
    readonly List<Button> buttons=new List<Button>();
    int selected=-1;
    public event EventHandler SelectionChanged;
    public int SelectedIndex {
        get { return selected; }
        set {
            if(value==selected)return;
            if(value<0||value>=pages.Count)throw new ArgumentOutOfRangeException("value");
            if(selected>=0)pages[selected].Visible=false;selected=value;
            pages[value].Visible=true;pages[value].BringToFront();
            for(int index=0;index<buttons.Count;index++) { buttons[index].ForeColor=index==value?Theme.Accent:Theme.Muted;buttons[index].Invalidate(); }
            if(SelectionChanged!=null)SelectionChanged(this,EventArgs.Empty);
        }
    }
    public WorkspaceTabs() {
        Height=260;BackColor=Theme.Bg;navigation.Height=48;navigation.AutoSize=false;navigation.Dock=DockStyle.Top;
        Controls.Add(body);Controls.Add(navigation);
    }
    public void Add(string title,Control page) {
        int index=pages.Count;pages.Add(page);page.Dock=DockStyle.Fill;page.Visible=false;body.Controls.Add(page);
        var button=new Button { Text=title,AutoSize=true,MinimumSize=new Size(0,44),FlatStyle=FlatStyle.Flat,BackColor=Theme.Bg,ForeColor=Theme.Muted,Padding=new Padding(0,8,0,10),Margin=new Padding(0,0,27,0),Cursor=Cursors.Hand };
        button.FlatAppearance.BorderSize=0;button.FlatAppearance.MouseOverBackColor=Theme.Bg;button.FlatAppearance.MouseDownBackColor=Theme.Bg;
        button.Paint+=(sender,args)=>{if(selected==index)using(var pen=new Pen(Theme.Accent,2))args.Graphics.DrawLine(pen,0,button.Height-2,button.Width,button.Height-2);};
        button.Click+=(sender,args)=>SelectedIndex=index;buttons.Add(button);navigation.Controls.Add(button);
        if(selected<0)SelectedIndex=0;
    }
}

sealed class NightMenuColors : ProfessionalColorTable {
    public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
    public override Color MenuItemSelected { get { return Theme.Card; } }
    public override Color MenuItemSelectedGradientBegin { get { return Theme.Card; } }
    public override Color MenuItemSelectedGradientEnd { get { return Theme.Card; } }
    public override Color MenuItemBorder { get { return Theme.Line; } }
    public override Color MenuBorder { get { return Theme.Line; } }
    public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
    public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
    public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
}

sealed class NightMenuRenderer : ToolStripProfessionalRenderer {
    public NightMenuRenderer():base(new NightMenuColors()){}
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e){
        var bounds=new Rectangle(3,2,e.Item.Width-6,e.Item.Height-4);
        if(e.Item.Selected&&e.Item.Enabled)using(var path=Drawing.Rounded(bounds,6))using(var brush=new SolidBrush(Theme.Card))e.Graphics.FillPath(brush,path);
        var item=e.Item as ToolStripMenuItem;if(item!=null&&item.Checked)using(var brush=new SolidBrush(Theme.Accent))e.Graphics.FillRectangle(brush,4,8,3,Math.Max(1,e.Item.Height-16));
    }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e){}
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e){
        var bounds=new Rectangle(16,0,e.Item.Width-30,e.Item.Height);
        TextRenderer.DrawText(e.Graphics,e.Text,e.TextFont,bounds,e.Item.Enabled?Theme.Text:Theme.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
    }
    public static ContextMenuStrip Create(System.ComponentModel.IContainer owner=null){
        var menu=owner==null?new ContextMenuStrip():new ContextMenuStrip(owner);
        menu.BackColor=Theme.Panel;menu.ForeColor=Theme.Text;menu.Font=new Font("Segoe UI",10F);menu.ShowImageMargin=false;menu.ShowCheckMargin=false;menu.Padding=new Padding(6);menu.Renderer=new NightMenuRenderer();return menu;
    }
    public static ToolStripItem Add(ContextMenuStrip menu,string text,EventHandler click){var item=menu.Items.Add(text,null,click);item.Padding=new Padding(16,10,16,10);return item;}
}

sealed class LaunchModeSelector : PolarButton {
    readonly ContextMenuStrip menu;
    int selected=-1;
    public event EventHandler SelectedIndexChanged;
    public int SelectedIndex {
        get { return selected; }
        set {
            if(value<0||value>1)throw new ArgumentOutOfRangeException("value");
            if(value==selected)return;
            selected=value;Text=(value==0?"Обычный запуск":"Выбор снимка")+"  ▾";
            for(int index=0;index<menu.Items.Count;index++)((ToolStripMenuItem)menu.Items[index]).Checked=index==value;
            if(SelectedIndexChanged!=null)SelectedIndexChanged(this,EventArgs.Empty);
        }
    }
    public LaunchModeSelector() {
        menu=NightMenuRenderer.Create();
        NightMenuRenderer.Add(menu,"Обычный запуск",(sender,args)=>SelectedIndex=0);
        NightMenuRenderer.Add(menu,"Выбор снимка",(sender,args)=>SelectedIndex=1);
        BackColor=Theme.Bg;ForeColor=Theme.Muted;Font=new Font("Segoe UI",9F);
        AutoSize=true;MinimumSize=new Size(0,34);Padding=new Padding(0,5,0,5);SelectedIndex=0;
        Click+=(sender,args)=>menu.Show(this,new Point(0,Height));
    }
    protected override void Dispose(bool disposing) { if(disposing)menu.Dispose();base.Dispose(disposing); }
}
