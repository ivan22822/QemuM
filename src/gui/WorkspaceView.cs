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
        using(var pen=new Pen(hover||dragging?Theme.Accent:Theme.Line,4)) {
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

sealed class ScrollSurface : BufferedPanel, IMessageFilter {
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    public bool PreFilterMessage(ref Message message){
        if(message.Msg!=0x020A||!Visible||!bar.Visible)return false;
        Control target=Control.FromHandle(WindowFromPoint(Cursor.Position))??Control.FromHandle(message.HWnd);
        for(Control current=target;current!=null;current=current.Parent){
            if(current is ScrollSurface&&((ScrollSurface)current).ScrollBar.Visible){if(current!=this)return false;Wheel(this,new HandledMouseEventArgs(MouseButtons.None,0,0,0,unchecked((short)((long)message.WParam>>16))));return true;}
            if(current is TextBoxBase||current is ThemedList)return false;
        }return false;
    }
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);Application.AddMessageFilter(this);}
    protected override void OnHandleDestroyed(EventArgs e){Application.RemoveMessageFilter(this);base.OnHandleDestroyed(e);}
    readonly Panel viewport=new BufferedPanel { Dock=DockStyle.Fill };
    readonly SlimScrollBar bar=new SlimScrollBar { Dock=DockStyle.Right };
    readonly Control content;
    bool arranging;
    public int ScrollOffset { get { return bar.Value; } set { bar.Value=value; } }
    internal SlimScrollBar ScrollBar { get { return bar; } }
    internal void SetVirtualRange(int total,int offset){bar.SetRange(total,Math.Max(0,ClientSize.Height-Padding.Vertical));bar.Value=offset;bar.Visible=bar.Maximum>0;}
    public ScrollSurface(Control content) {
        this.content=content; AutoScroll=false; Dock=DockStyle.Fill;
        content.Dock=DockStyle.None; viewport.Controls.Add(content);
        Controls.Add(viewport); Controls.Add(bar);
        bar.ValueChanged+=(sender,args)=>{var virtualContent=content as IVirtualScrollContent;if(virtualContent!=null&&virtualContent.VirtualMode)virtualContent.SetViewport(content.Width,content.Height,bar.Value);else content.Top=-bar.Value;};
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
            var virtualContent=content as IVirtualScrollContent;
            if(virtualContent!=null&&virtualContent.VirtualMode){int viewportHeight=Math.Max(1,ClientSize.Height-Padding.Vertical),total=virtualContent.ContentHeight(width);content.MinimumSize=Size.Empty;content.MaximumSize=Size.Empty;content.SetBounds(0,0,width,viewportHeight);bar.SetRange(total,viewportHeight);bar.Visible=bar.Maximum>0;base.OnLayout(e);virtualContent.SetViewport(width,viewportHeight,bar.Value);return;}
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

sealed class MachineIcon : PictureBox {
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);if(Image!=null)return;e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;int size=Math.Min(Width,Height)-4;var screen=new Rectangle((Width-size)/2,(Height-size)/2,size,Math.Max(8,size*2/3));using(var pen=new Pen(Theme.Accent,1.7F)){e.Graphics.DrawRectangle(pen,screen);int middle=screen.Left+screen.Width/2;e.Graphics.DrawLine(pen,middle,screen.Bottom,middle,screen.Bottom+5);e.Graphics.DrawLine(pen,middle-6,screen.Bottom+6,middle+6,screen.Bottom+6);}}
}

interface IToolVisibility {void SetHostVisible(bool visible);}
interface IToolNavigationGuard {bool CanDeactivate{get;}}
sealed class EmbeddedToolPage : BufferedPanel {
    readonly Func<string,Control> create;Control child,previous;Form owner;string machine,previousMachine;public Control Tool{get{return child;}}
    public event Action<Control> ToolReady;
    public EmbeddedToolPage(Func<string,Control> create){this.create=create;VisibleChanged+=(s,e)=>{EnsureTool();UpdateVisibility(s,e);};}
    void UpdateVisibility(object sender,EventArgs args){Notify(child,Visible&&(owner==null||owner.WindowState!=FormWindowState.Minimized));}
    static void Notify(Control control,bool visible){var lifetime=control as IToolVisibility;if(lifetime!=null)lifetime.SetHostVisible(visible);}
    internal bool CanSelectMachine(string name){var guard=child as IToolNavigationGuard;return machine==name||guard==null||guard.CanDeactivate;}
    public void SelectMachine(string name){if(machine==name||!CanSelectMachine(name))return;Notify(child,false);if(child!=null)child.Visible=false;if(name==previousMachine){var old=child;child=previous;previous=old;previousMachine=machine;}else{if(previous!=null)previous.Dispose();previous=child;previousMachine=machine;child=null;}machine=name;EnsureTool();}
    void EnsureTool(){if(!Visible||!IsHandleCreated||String.IsNullOrEmpty(machine))return;if(child==null){child=create(machine);var form=child as Form;if(form!=null){form.TopLevel=false;form.FormBorderStyle=FormBorderStyle.None;}child.MinimumSize=Size.Empty;child.Dock=DockStyle.Fill;Controls.Add(child);}child.Show();child.BringToFront();UpdateVisibility(this,EventArgs.Empty);if(ToolReady!=null)ToolReady(child);}
    void ReleaseTool(){Notify(child,false);Notify(previous,false);if(child!=null)child.Dispose();if(previous!=null)previous.Dispose();child=previous=null;}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);owner=FindForm();if(owner!=null){owner.Resize+=UpdateVisibility;owner.VisibleChanged+=UpdateVisibility;}EnsureTool();}
    protected override void OnHandleDestroyed(EventArgs e){if(owner!=null){owner.Resize-=UpdateVisibility;owner.VisibleChanged-=UpdateVisibility;owner=null;}base.OnHandleDestroyed(e);}
    protected override void Dispose(bool disposing){if(disposing){if(owner!=null){owner.Resize-=UpdateVisibility;owner.VisibleChanged-=UpdateVisibility;owner=null;}ReleaseTool();}base.Dispose(disposing);}
}

sealed class InspectorWorkspace : BufferedPanel {
    float scale=1F;bool arranging;protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Width;base.ScaleControl(factor,specified);}
    readonly Control body,inspector,actions;
    public InspectorWorkspace(Control body,Control inspector,Control actions=null){this.body=body;this.inspector=inspector;this.actions=actions;BackColor=Theme.Bg;Controls.Add(body);if(inspector!=null)Controls.Add(inspector);if(actions!=null){Controls.Add(actions);actions.VisibleChanged+=(s,e)=>PerformLayout();actions.SizeChanged+=(s,e)=>PerformLayout();}}
    protected override void OnLayout(LayoutEventArgs e){
        base.OnLayout(e);if(body==null||arranging)return;arranging=true;
        try{if(inspector==null){int footer=actions!=null&&actions.Visible?actions.GetPreferredSize(new Size(Math.Max(1,Width),0)).Height:0;body.SetBounds(0,0,Math.Max(1,Width),Math.Max(1,Height-footer));if(actions!=null)actions.SetBounds(0,Math.Max(0,Height-footer),Math.Max(1,Width),footer);return;}bool wide=ClientSize.Width>=740*scale;int gap=(int)(16*scale),side=wide?(int)(220*scale):Width;
            int ah=actions!=null&&actions.Visible?actions.GetPreferredSize(new Size(side,0)).Height:0;var card=inspector as ConfigurationInspector;
            if(wide){body.SetBounds(0,0,Math.Max(1,Width-side-gap),Height);int h=Math.Min(Math.Max(1,Height-(ah>0?ah+gap:0)),card==null?Height:card.CardHeight);int y=Math.Max(0,Math.Min((Height-h)/2,Height-h-(ah>0?ah+gap:0)));inspector.SetBounds(Width-side,y,side,h);if(actions!=null)actions.SetBounds(Width-side,y+h+gap,side,ah);}
            else{int h=Math.Min((int)(180*scale),Height/3);int reserved=h+(ah>0?ah+gap:0);body.SetBounds(0,0,Width,Math.Max(1,Height-reserved-gap));inspector.SetBounds(0,Height-reserved,Width,h);if(actions!=null)actions.SetBounds(0,Height-ah,Width,ah);}
        }finally{arranging=false;}
    }
}

sealed class InspectorText : Label {
    protected override void OnPaint(PaintEventArgs e){
        TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,ForeColor,TextFormatFlags.NoPadding|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
    }
}

sealed class ConfigurationInspector : RoundedZone {
    float scale=1F;readonly bool hasEdit;
    protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Height;base.ScaleControl(factor,specified);foreach(Control slot in Controls)if(slot.Dock==DockStyle.Bottom)slot.Padding=new Padding(0,(int)(8*scale),0,0);}
    public int CardHeight{get{return (int)((38+((values.Count+1)/2)*64+24+Controls.Cast<Control>().Where(c=>c.Dock==DockStyle.Bottom).Sum(c=>c.Height)/scale)*scale);}}

    readonly TableLayoutPanel rows=new TableLayoutPanel{AutoSize=true,ColumnCount=2,BackColor=Theme.Field,Margin=Padding.Empty};
    readonly Dictionary<string,Label> values=new Dictionary<string,Label>();readonly ToolTip tips=new ToolTip();
    public ConfigurationInspector(string title,Action edit=null){
        hasEdit=edit!=null;BackColor=Theme.Field;Padding=new Padding(14,10,14,14);
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        rows.RowCount=1;rows.RowStyles.Add(new RowStyle(SizeType.Absolute,38));var heading=Theme.Label(title,11,null,true);heading.Dock=DockStyle.Fill;heading.TextAlign=ContentAlignment.MiddleLeft;rows.Controls.Add(heading,0,0);rows.SetColumnSpan(heading,2);
        Controls.Add(new ScrollSurface(rows){Dock=DockStyle.Fill,BackColor=Theme.Field});
        if(edit!=null){var button=Theme.Button("Изменить параметры",edit);AddAction(button);}
    }
    public void AddAction(Control action){
        action.Dock=DockStyle.Fill;action.Margin=Padding.Empty;action.BackColor=Theme.Field;
        var flow=action as FlowLayoutPanel;
        if(flow!=null){
            flow.AutoSize=false;flow.FlowDirection=FlowDirection.TopDown;flow.WrapContents=false;flow.Padding=Padding.Empty;
            int height=0;foreach(Control child in flow.Controls){child.AutoSize=false;child.MinimumSize=Size.Empty;child.Margin=new Padding(0,0,0,8);height+=child.Height+8;}
            action.Height=Math.Max(36,height-8);
            flow.SizeChanged+=(s,e)=>{foreach(Control child in flow.Controls)child.Width=flow.ClientSize.Width;};
        }
        var slot=new BufferedPanel{Dock=DockStyle.Bottom,BackColor=Theme.Field,Padding=new Padding(0,8,0,0),Height=Math.Max(36,action.Height)+8};
        slot.Controls.Add(action);Controls.Add(slot);
    }
    public void Value(string key,string value){
        Label label;if(!values.TryGetValue(key,out label)){int index=values.Count,row=1+index/2;if(rows.RowCount<=row){rows.RowCount=row+1;rows.RowStyles.Add(new RowStyle(SizeType.Absolute,64*scale));}
            var cell=new RoundedZone{Dock=DockStyle.Fill,Margin=new Padding(3),Padding=new Padding(8),BackColor=MotionPaint.Hex("#fffaf0")};
            var caption=new InspectorText{Text=key,ForeColor=Theme.Muted,BackColor=Color.Transparent};caption.Font=MotionPaint.Font("Courier New",9*scale);caption.AutoSize=false;caption.Dock=DockStyle.Top;caption.Height=(int)(18*scale);caption.Margin=Padding.Empty;cell.Controls.Add(caption);
            label=new InspectorText{ForeColor=Theme.Text,BackColor=Color.Transparent};label.Font=MotionPaint.Font("Arial",12*scale);label.AutoSize=false;label.AutoEllipsis=true;label.Dock=DockStyle.Bottom;label.Height=(int)(24*scale);label.Margin=Padding.Empty;label.TextAlign=ContentAlignment.MiddleLeft;cell.Controls.Add(label);rows.Controls.Add(cell,index%2,row);values[key]=label;

        }
        string shown=(key=="ISO"||key=="Диск")&&!String.IsNullOrEmpty(value)&&value.IndexOfAny(new[]{'\\','/'})>=0?System.IO.Path.GetFileName(value):value;
        if(key=="Ускорение")shown=value=="tcg"?"TCG":value=="whpx"?"WHPX":value=="auto"?"Авто":value;
        if(label.Text==shown)return;label.Text=shown;label.AccessibleName=key+": "+shown;tips.SetToolTip(label,shown);if(Parent!=null)Parent.PerformLayout();
    }
    protected override void Dispose(bool disposing){if(disposing)tips.Dispose();base.Dispose(disposing);}
}

sealed class AdapterCards : BufferedPanel {
    readonly FlowLayoutPanel items=Theme.Flow(true);string signature;
    public AdapterCards(Action edit){BackColor=Theme.Bg;Padding=Padding.Empty;Controls.Add(new ScrollSurface(items){Dock=DockStyle.Fill,BackColor=Theme.Bg});items.SizeChanged+=(s,e)=>{foreach(Control card in items.Controls)if(!(card is Button))card.Width=Math.Max(180,items.Width-8);};}
    public void ShowAdapters(object[] adapters){
        string next=J.Json().Serialize(adapters);if(signature==next)return;signature=next;foreach(Control c in items.Controls.Cast<Control>().ToArray())c.Dispose();items.Controls.Clear();
        if(adapters.Length==0){items.Controls.Add(Theme.Label("Сетевых адаптеров нет",10,Theme.Muted));return;}
        foreach(var nic in adapters){string type=J.S(nic,"type"),mode=J.S(nic,"mode");string kind=type=="user"?"NAT":type=="tap"?"TAP":type=="socket"?(mode=="mcast"?"MCAST":"TCP"):type;
            var card=new ConfigurationInspector("Адаптер "+J.S(nic,"id")){Height=200,Width=Math.Max(180,items.Width-8),Margin=new Padding(0,0,0,12)};
            card.Value("Подключение",kind);card.Value("Модель",J.S(nic,"model"));card.Value("MAC",J.S(nic,"mac","Автоматически"));items.Controls.Add(card);
        }
    }
}

sealed class LibraryCard : BufferedPanel {
    Image logo;readonly string imageOwner=GraphicsResources.Owner("library-logo");
    public Image Logo {get{return logo;}set{if(Object.ReferenceEquals(logo,value))return;var previous=logo;logo=value;GraphicsResources.Set(imageOwner,"visible-logos",GraphicsResources.ImageBytes(value));if(previous!=null)previous.Dispose();Invalidate();}}public string ReferenceIcon;
    readonly Label title=Theme.Label("",10,null,true), subtitle=Theme.Label("",9,Theme.Muted);
    readonly ToolTip tooltip=new ToolTip();
    readonly Color identity;
    public string Status { get;private set; }
    public LibraryCard(string name,string label,Color identity) {
        Tag=name;this.identity=identity;Height=52;Width=222;Margin=new Padding(0,0,0,4);
        BackColor=Theme.Panel;Cursor=Cursors.Hand;AccessibleRole=AccessibleRole.ListItem;TabStop=true;
        AccessibleName=label+" / "+name;
        title.Text=label;subtitle.Text=name;
        foreach(var text in new[]{title,subtitle}) { text.AutoSize=false;text.AutoEllipsis=true;text.BackColor=Color.Transparent;Controls.Add(text); }
        title.Font=MotionPaint.Font("Arial",11);subtitle.Font=MotionPaint.Font("Courier New",10);title.TextAlign=subtitle.TextAlign=ContentAlignment.MiddleLeft;title.SetBounds(36,8,150,19);subtitle.SetBounds(36,30,150,18);ArrangeText();
    }
    public void SetStatus(string value) {
        if(value==Status) return;
        Status=value;AccessibleDescription=Theme.State(value);subtitle.Text=AccessibleDescription;
        string details=AccessibleName+" · "+AccessibleDescription;tooltip.SetToolTip(this,details);tooltip.SetToolTip(title,details);tooltip.SetToolTip(subtitle,details);
        Invalidate(new Rectangle(Math.Max(0,Width-19),0,19,Height));
    }
    protected override void OnSizeChanged(EventArgs e) {
        base.OnSizeChanged(e);if(title==null)return;ArrangeText();
    }
    void ArrangeText(){int width=Math.Max(10,Width-58);title.Width=subtitle.Width=width;int measured=TextRenderer.MeasureText(title.Text,title.Font,new Size(width,0),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix).Height;title.Height=Math.Max(21,measured);subtitle.Top=title.Bottom+1;int desired=subtitle.Bottom+9;if(Height!=desired)Height=desired;}
    protected override bool IsInputKey(Keys keyData){return new[]{Keys.Up,Keys.Down,Keys.Enter,Keys.Space}.Contains(keyData&Keys.KeyCode)||base.IsInputKey(keyData);}
    protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
    protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
    protected override void OnPaintBackground(PaintEventArgs e) {
        e.Graphics.Clear(Theme.Panel);if(BackColor==Theme.Card){using(var fill=new SolidBrush(MotionPaint.Hex("#ded8ce")))e.Graphics.FillRectangle(fill,ClientRectangle);using(var pen=new Pen(MotionPaint.Hex("#aaa090")))e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1);using(var accent=new SolidBrush(Theme.Accent))e.Graphics.FillRectangle(accent,0,0,2,Height);}
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        var icon=new Rectangle(8,(Height-20)/2,20,20);if(Logo!=null){float fit=Math.Min((float)icon.Width/Logo.Width,(float)icon.Height/Logo.Height);int w=Math.Max(1,(int)(Logo.Width*fit)),h=Math.Max(1,(int)(Logo.Height*fit));e.Graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;e.Graphics.DrawImage(Logo,new Rectangle(icon.X+(icon.Width-w)/2,icon.Y+(icon.Height-h)/2,w,h));}else if(ReferenceIcon!=null)ReferenceIcons.Draw(e.Graphics,ReferenceIcon,icon,MotionPaint.Hex("#6f675b"));
        using(var brush=new SolidBrush(Theme.StateColor(Status)))e.Graphics.FillEllipse(brush,Width-17,12,4,4);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,ClientRectangle);
    }
    protected override void Dispose(bool disposing) { if(disposing){tooltip.Dispose();Logo=null;}base.Dispose(disposing); }
}

class RoundedZone : BufferedPanel {
    protected override void OnPaintBackground(PaintEventArgs e){if(Parent==null)e.Graphics.Clear(Theme.Bg);else{var saved=e.Graphics.Save();e.Graphics.TranslateTransform(-Left,-Top);using(var parentPaint=new PaintEventArgs(e.Graphics,new Rectangle(Left,Top,Width,Height))){InvokePaintBackground(Parent,parentPaint);InvokePaint(Parent,parentPaint);}e.Graphics.Restore(saved);}e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(var path=Drawing.Rounded(new Rectangle(0,0,Width-1,Height-1),12))using(var fill=new SolidBrush(BackColor))using(var pen=new Pen(Theme.Line)){e.Graphics.FillPath(fill,path);e.Graphics.DrawPath(pen,path);}}
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);if(Region!=null){var previous=Region;Region=null;previous.Dispose();}}

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
    readonly string imageOwner=GraphicsResources.Owner("emblem-logo");
    public string ReferenceIcon="cpu";Image logo;public Image Logo {get{return logo;}set{if(ReferenceEquals(logo,value))return;var previous=logo;logo=value;GraphicsResources.Set(imageOwner,"visible-logos",GraphicsResources.ImageBytes(value));if(previous!=null)previous.Dispose();Invalidate();}}
    protected override void Dispose(bool disposing){if(disposing)Logo=null;base.Dispose(disposing);}
    public MonitorEmblem(){Size=new Size(66,66);SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer,true);AccessibleName="Виртуальная машина";}
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(var fill=new SolidBrush(MotionPaint.Hex("#ebe5dc")))using(var pen=new Pen(MotionPaint.Hex("#b9aea0"))){e.Graphics.FillEllipse(fill,0,0,Width-1,Height-1);e.Graphics.DrawEllipse(pen,0,0,Width-1,Height-1);}
        if(logo==null&&ReferenceIcon!=null){ReferenceIcons.Draw(e.Graphics,ReferenceIcon,new RectangleF((Width-22)/2F,(Height-22)/2F,22,22),Theme.Text);return;}
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
    float scale=1F;protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Width;base.ScaleControl(factor,specified);UpdateNavigationHeight();}
    float navigationLineHeight=36;
    readonly FlowLayoutPanel navigation=Theme.Flow();
    readonly Panel body=new BufferedPanel { Dock=DockStyle.Fill,Padding=new Padding(0,20,0,0) };
    sealed class Page {
        internal Control Content;
        internal Func<Control> Build;
    }
    readonly List<Page> pages=new List<Page>();
    readonly List<Button> buttons=new List<Button>();
    readonly SelectionMotion selectionMotion=new SelectionMotion();
    readonly PageFocus pageFocus=new PageFocus();
    int selected=-1;
    public event EventHandler SelectionChanged;
    public int SelectedIndex {
        get { return selected; }
        set {
            if(value==selected)return;
            if(value<0||value>=pages.Count)throw new ArgumentOutOfRangeException("value");
            bool switching=selected>=0;
            bool restoreFocus=switching&&pageFocus.Remember(pages[selected].Content);
            var next=pages[value];
            body.SuspendLayout();
            try {
                if(next.Content==null){
                    next.Content=next.Build();
                    next.Build=null;
                    if(Math.Abs(scale-1F)>.001F)next.Content.Scale(new SizeF(scale,scale));
                    AttachPage(next.Content);
                }
                if(switching)pages[selected].Content.Visible=false;
                selected=value;
                next.Content.Visible=true;
                next.Content.BringToFront();
            } finally { body.ResumeLayout(true); }
            selectionMotion.Start(navigation, buttons[value], switching);
            for(int index=0;index<buttons.Count;index++) { buttons[index].ForeColor=index==value?Theme.Accent:Theme.Muted;buttons[index].Invalidate(); }
            if(SelectionChanged!=null)SelectionChanged(this,EventArgs.Empty);
            if(restoreFocus&&selected==value)pageFocus.Restore(next.Content);
        }
    }
    public WorkspaceTabs() {
        Height=260;BackColor=Theme.Bg;Font=MotionPaint.Font("Arial",10);navigation.Height=36;navigation.AutoSize=false;navigation.Dock=DockStyle.Top;
        Controls.Add(body);Controls.Add(navigation);Resize+=(sender,args)=>UpdateNavigationHeight();
    }
    void UpdateNavigationHeight(){int width=Math.Max(150,ClientSize.Width),line=0,rows=1;
        foreach(Control item in navigation.Controls){int needed=item.PreferredSize.Width+item.Margin.Horizontal;if(line>0&&line+needed>width){rows++;line=0;}line+=needed;}
        int height=(int)(rows*navigationLineHeight*scale);if(navigation.Height!=height)navigation.Height=height;
    }
    public void NetworkPresentation(){navigationLineHeight=46;body.Padding=new Padding(0,17,0,0);foreach(var button in buttons){button.Font=UiTypography.Make(10*scale,500);button.Margin=new Padding(0,0,(int)(12*scale),0);button.Padding=new Padding((int)(3*scale),0,(int)(3*scale),(int)(4*scale));}UpdateNavigationHeight();}
    public void FitAuxiliaryNavigation(){foreach(var button in buttons){button.Font=UiTypography.Make(9.5F*scale,500);button.Margin=new Padding(0,0,(int)(16*scale),0);button.Padding=new Padding(0,0,0,(int)(11*scale));}UpdateNavigationHeight();}
    public void Add(string title,Control page) {
        Register(title,new Page{Content=page});
        AttachPage(page);
        if(selected<0)SelectedIndex=0;
    }
    public void AddLazy(string title,Func<Control> build) {
        Register(title,new Page{Build=build});
    }
    void AttachPage(Control page) {
        page.Dock=DockStyle.Fill;
        page.Visible=false;
        body.Controls.Add(page);
    }
    void Register(string title,Page page) {
        int index=pages.Count;pages.Add(page);
        var button=new Button { Text=title,AutoSize=true,MinimumSize=new Size(0,32),FlatStyle=FlatStyle.Flat,BackColor=Theme.Bg,ForeColor=Theme.Muted,Padding=new Padding(0,0,0,11),Margin=new Padding(0,0,24,0),Cursor=Cursors.Hand };
        button.FlatAppearance.BorderSize=0;button.FlatAppearance.MouseOverBackColor=Theme.Bg;button.FlatAppearance.MouseDownBackColor=Theme.Bg;
        button.Paint+=(sender,args)=>{if(selected==index)using(var pen=new Pen(MotionPaint.Mix(Theme.Bg,Theme.Accent,selectionMotion.Amount(button)),2))args.Graphics.DrawLine(pen,0,button.Height-2,button.Width,button.Height-2);};
        button.Click+=(sender,args)=>SelectedIndex=index;buttons.Add(button);navigation.Controls.Add(button);
        UpdateNavigationHeight();
    }
    // A command tab opens the existing catalog without inventing an embedded snapshot view.
    public void AddAction(string title,Action action,int position){
        var button=new Button{Text=title,AutoSize=true,MinimumSize=new Size(0,32),FlatStyle=FlatStyle.Flat,BackColor=Theme.Bg,ForeColor=Theme.Muted,Padding=new Padding(0,0,0,11),Margin=new Padding(0,0,24,0),Cursor=Cursors.Hand};
        button.FlatAppearance.BorderSize=0;button.Click+=(s,e)=>action();navigation.Controls.Add(button);navigation.Controls.SetChildIndex(button,position);UpdateNavigationHeight();
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
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e){e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(var path=Drawing.Rounded(new Rectangle(0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1),10))using(var pen=new Pen(Theme.Line))e.Graphics.DrawPath(pen,path);}
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e){}
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e){
        var bounds=new Rectangle(16,0,e.Item.Width-30,e.Item.Height);
        TextRenderer.DrawText(e.Graphics,e.Text,e.TextFont,bounds,e.Item.Enabled?Theme.Text:Theme.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
    }
    public static ContextMenuStrip Create(System.ComponentModel.IContainer owner=null){
        var menu=owner==null?new ContextMenuStrip():new ContextMenuStrip(owner);
        menu.BackColor=Theme.Panel;menu.ForeColor=Theme.Text;menu.Font=UiTypography.Points(10F);menu.ShowImageMargin=false;menu.ShowCheckMargin=false;menu.Padding=new Padding(6);menu.Renderer=new NightMenuRenderer();Action round=()=>{if(menu.Width<1||menu.Height<1)return;using(var path=Drawing.Rounded(menu.ClientRectangle,10)){var old=menu.Region;menu.Region=new Region(path);if(old!=null)old.Dispose();}};menu.SizeChanged+=(s,e)=>round();menu.Opened+=(s,e)=>{round();if(ReferenceDialogChrome.SmoothCorners(menu)){var old=menu.Region;menu.Region=null;if(old!=null)old.Dispose();}};return menu;
    }
    public static ToolStripItem Add(ContextMenuStrip menu,string text,EventHandler click){var item=menu.Items.Add(text,null,click);item.Padding=new Padding(16,10,16,10);return item;}
}

sealed class LaunchModeSelector : PolarButton {
    int selected=-1;
    public event EventHandler SelectedIndexChanged;
    public int SelectedIndex {
        get{return selected;}
        set{
            if(value<0||value>1)throw new ArgumentOutOfRangeException("value");
            if(value==selected)return;selected=value;
            Text=value==1?"Запуск из снимка":"Обычный запуск";
            Primary=value==1;BackColor=Theme.Card;ForeColor=value==1?Theme.Bg:Theme.Text;
            AccessibleDescription=Text+". Нажми, чтобы переключить режим.";Invalidate();
            if(SelectedIndexChanged!=null)SelectedIndexChanged(this,EventArgs.Empty);
        }
    }
    public LaunchModeSelector(){AutoSize=false;Size=new Size(164,36);MinimumSize=new Size(148,34);Font=UiTypography.Make(10,500);Padding=new Padding(12,7,12,7);Margin=Padding.Empty;AccessibleName="Режим запуска машины";SelectedIndex=0;}
    protected override void OnClick(EventArgs e){SelectedIndex=selected==1?0:1;base.OnClick(e);}
}

// Common fixed choices are visible at once. The optional bound selector keeps
// existing form logic and keyboard/programmatic selection behavior intact.
sealed class UniformActionGrid : TableLayoutPanel {
    bool arranging;
    public UniformActionGrid(){Dock=DockStyle.Top;AutoSize=false;Margin=Padding.Empty;Padding=Padding.Empty;BackColor=Theme.Bg;}
    protected override void OnControlAdded(ControlEventArgs e){base.OnControlAdded(e);var button=e.Control as Button;if(button!=null){button.AutoSize=false;button.MinimumSize=Size.Empty;button.Dock=DockStyle.Fill;button.Margin=new Padding(0,0,8,6);button.Padding=new Padding(12,0,12,0);button.Font=UiTypography.Make(10,500);}Arrange();}
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);Arrange();}
    void Arrange(){if(arranging||Controls.Count==0)return;arranging=true;SuspendLayout();try{
        int cell=Controls.Cast<Control>().Max(c=>TextRenderer.MeasureText(c.Text,c.Font).Width+c.Padding.Horizontal+c.Margin.Horizontal+8);
        int columns=Math.Max(1,Math.Min(3,ClientSize.Width/Math.Max(1,cell))),rows=(Controls.Count+columns-1)/columns;
        ColumnCount=columns;RowCount=rows;ColumnStyles.Clear();RowStyles.Clear();
        for(int i=0;i<columns;i++)ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100F/columns));
        int rowHeight=(int)Math.Round(46*DeviceDpi/96F);for(int i=0;i<rows;i++)RowStyles.Add(new RowStyle(SizeType.Absolute,rowHeight));
        int index=0;foreach(Control control in Controls){control.MaximumSize=new Size(Math.Max(1,ClientSize.Width/columns-control.Margin.Horizontal),Math.Max(1,rowHeight-control.Margin.Vertical));SetCellPosition(control,new TableLayoutPanelCellPosition(index%columns,index/columns));index++;}
        int height=rows*rowHeight;MinimumSize=new Size(0,height);Height=height;
    }finally{ResumeLayout(true);arranging=false;}}
}

sealed class ChoiceStrip : UserControl {
    readonly Button[] choices;int selected=-1;
    public event EventHandler SelectedIndexChanged;
    public int SelectedIndex {
        get{return selected;}
        set{if(value<0||value>=choices.Length)throw new ArgumentOutOfRangeException("value");if(value==selected)return;selected=value;
            for(int i=0;i<choices.Length;i++){choices[i].BackColor=i==value?Theme.Text:Theme.Card;choices[i].ForeColor=i==value?Theme.Bg:Theme.Text;choices[i].AccessibleDescription=i==value?"Выбрано":"Не выбрано";}
            if(SelectedIndexChanged!=null)SelectedIndexChanged(this,EventArgs.Empty);
        }
    }
    public ChoiceStrip(int columns,params string[] labels){
        int count=Math.Max(1,Math.Min(columns,labels.Length)),rows=(labels.Length+count-1)/count;
        Height=rows*46;Width=count*245;BackColor=Theme.Bg;AccessibleName="Варианты выбора";
        var table=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=count,RowCount=rows,BackColor=Theme.Bg};
        for(int i=0;i<count;i++)table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100F/count));
        for(int i=0;i<rows;i++)table.RowStyles.Add(new RowStyle(SizeType.Percent,100F/rows));
        choices=new Button[labels.Length];
        for(int i=0;i<labels.Length;i++){int index=i;var button=Theme.Button(labels[i],()=>SelectedIndex=index);button.AutoSize=false;button.MinimumSize=System.Drawing.Size.Empty;button.Dock=DockStyle.Fill;button.Margin=new Padding(0,0,6,6);button.Font=UiTypography.Make(9,500);((PolarButton)button).CornerRadius=5;button.AccessibleName=labels[i];choices[i]=button;table.Controls.Add(button,i%count,i/count);}
        Controls.Add(table);if(labels.Length>0)SelectedIndex=0;
    }
    public void Bind(ThemedSelector selector){
        selector.Dock=DockStyle.None;selector.Size=new Size(1,1);selector.Visible=false;Controls.Add(selector);
        selector.SelectedIndexChanged+=(s,e)=>{if(selector.SelectedIndex>=0)SelectedIndex=selector.SelectedIndex;};
        SelectedIndexChanged+=(s,e)=>{if(selector.SelectedIndex!=SelectedIndex)selector.SelectedIndex=SelectedIndex;};
        if(selector.SelectedIndex>=0)SelectedIndex=selector.SelectedIndex;
    }
}

// Restore editor focus after a programmatic switch from a page. Keyboard
// navigation through the tab buttons keeps focus on those buttons.
sealed class PageFocus {
    readonly Dictionary<Control, WeakReference<Control>> last = new Dictionary<Control, WeakReference<Control>>();

    internal bool Remember(Control page) {
        if (page == null || !page.ContainsFocus) return false;
        var focused = page;
        while (!focused.Focused) {
            var child = focused.Controls.Cast<Control>().FirstOrDefault(control => control.ContainsFocus);
            if (child == null) break;
            focused = child;
        }
        last[page] = new WeakReference<Control>(focused);
        return true;
    }

    internal void Restore(Control page) {
        WeakReference<Control> previous;
        Control focused;
        if (last.TryGetValue(page, out previous) && previous.TryGetTarget(out focused) &&
            !focused.IsDisposed && focused.CanFocus && (focused == page || page.Contains(focused))) {
            focused.Focus();
            return;
        }
        page.SelectNextControl(null, true, true, true, false);
    }
}
