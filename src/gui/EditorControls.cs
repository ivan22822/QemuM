using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class FieldInput : UserControl {
    protected override CreateParams CreateParams{get{var value=base.CreateParams;value.ExStyle|=0x02000000;return value;}}
    protected readonly TextBox input=new TextBox{AutoSize=false,BorderStyle=BorderStyle.None,BackColor=Theme.Field,ForeColor=Theme.Text};
    string placeholder="";
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,string text);
    public string Placeholder {get{return placeholder;}set{placeholder=value??"";if(input.IsHandleCreated)SendMessage(input.Handle,0x1501,new IntPtr(1),placeholder);}}
    public bool SearchIcon {get;set;}
    public int CornerRadius=4;
    internal TextBox Input {get{return input;}}
    public override string Text {get{return input==null?base.Text:input.Text;}set{if(input==null)base.Text=value;else input.Text=value??"";}}
    public FieldInput(){
        SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        Height=40;Dock=DockStyle.Fill;BackColor=MotionPaint.Hex("#fcf9f3");input.BackColor=BackColor;ForeColor=Theme.Text;Font=MotionPaint.Font("Segoe UI",12);TabStop=false;
        input.Font=Font;Controls.Add(input);
        input.HandleCreated+=(sender,args)=>SendMessage(input.Handle,0x1501,new IntPtr(1),placeholder);
        input.TextChanged+=(sender,args)=>{OnTextChanged(EventArgs.Empty);Invalidate();};
        input.GotFocus+=(sender,args)=>Invalidate();input.LostFocus+=(sender,args)=>Invalidate();
        MouseDown+=(sender,args)=>input.Focus();
    }
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);if(input!=null)input.Font=Font;PerformLayout();}
    protected override void OnPaint(PaintEventArgs e){
        if(CornerRadius>4)e.Graphics.Clear(Parent==null?Theme.Bg:Parent.BackColor);
        e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using(var path=Drawing.Rounded(new Rectangle(0,0,Width-1,Height-1),CornerRadius))using(var fill=new SolidBrush(MotionPaint.Hex("#fcf9f3")))using(var pen=new Pen(ContainsFocus?Theme.Accent:MotionPaint.Hex("#c6bba9"))){e.Graphics.FillPath(fill,path);e.Graphics.DrawPath(pen,path);}
        if(SearchIcon)using(var pen=new Pen(Theme.Muted,1.4F)){int y=Height/2;e.Graphics.DrawEllipse(pen,12,y-6,9,9);e.Graphics.DrawLine(pen,20,y+2,25,y+7);}
    }
    public void Clear(){input.Clear();}
    public new bool Focus(){return input.Focus();}
    protected override void OnEnter(EventArgs e){base.OnEnter(e);input.Focus();}
    protected override void OnLayout(LayoutEventArgs e){base.OnLayout(e);if(input!=null){input.AccessibleName=AccessibleName;NativeInputLayout.Arrange(input,ClientSize,SearchIcon?34:12,12,DeviceDpi);}}
}

static class NativeInputLayout {
    internal static void Arrange(TextBox input,Size client,int left,int right,int dpi){
        // Borderless single-line Edit HWNDs can clip Unbounded descenders at
        // PreferredHeight. Reserve real rasterization space instead of letting
        // AutoSize force that short height back after SetBounds.
        int inset=Math.Max(2,(int)Math.Ceiling(3*dpi/96F));
        int height=Math.Max(input.PreferredHeight,input.Font.Height)+2*inset;
        height=Math.Max(1,Math.Min(height,client.Height-2*inset));
        // Native Edit's baseline sits slightly below the padded line box center.
        int top=(client.Height-height)/2-(int)Math.Round(dpi/96F);
        top=Math.Max(inset,Math.Min(top,client.Height-height-inset));
        input.SetBounds(left,top,Math.Max(1,client.Width-left-right),height);
    }
}

sealed class NumberInput : FieldInput {
    public decimal Minimum {get;private set;}
    public decimal Maximum {get;private set;}
    public decimal Value {
        get{int value;if(!Int32.TryParse(Text,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out value)||value<Minimum||value>Maximum)throw new InvalidOperationException((AccessibleName??"Число")+": введи целое число от "+Minimum+" до "+Maximum+".");return value;}
        set{if(value<Minimum||value>Maximum||Decimal.Truncate(value)!=value)throw new ArgumentOutOfRangeException("value");Text=value.ToString(System.Globalization.CultureInfo.InvariantCulture);}
    }
    public NumberInput(int value,int minimum,int maximum){Minimum=minimum;Maximum=maximum;Value=Math.Max(minimum,Math.Min(value,maximum));input.MaxLength=10;}
}

// One selector for fixed choices and editable model names. The popup belongs to
// the control and is reused until disposal, including after native auto-close.
sealed class ThemedSelector : UserControl {
    public sealed class ItemCollection : Collection<object> {
        readonly ThemedSelector owner;
        internal ItemCollection(ThemedSelector owner){this.owner=owner;}
        public void AddRange(object[] items){
            foreach(object item in items)Items.Add(item);
            owner.ItemsChanged();
        }
        internal void ReplaceAll(object[] items){
            owner.SelectedIndex=-1;
            Items.Clear();
            AddRange(items);
        }
        protected override void InsertItem(int index,object item){base.InsertItem(index,item);owner.ItemsChanged();}
        protected override void ClearItems(){base.ClearItems();owner.SelectedIndex=-1;owner.ItemsChanged();}
        protected override void RemoveItem(int index){base.RemoveItem(index);if(owner.selected>=Count)owner.SelectedIndex=-1;owner.ItemsChanged();}
        protected override void SetItem(int index,object item){base.SetItem(index,item);owner.ItemsChanged();}
    }
    readonly TextBox input=new TextBox{AutoSize=false,BorderStyle=BorderStyle.None,BackColor=Theme.Field,ForeColor=Theme.Text,Visible=false};
    readonly ToolStripDropDown popup=new ToolStripDropDown{AutoSize=false,Padding=new Padding(1),BackColor=Theme.Line,Renderer=new ToolStripProfessionalRenderer(new NightMenuColors())};
    readonly ThemedList choices=new ThemedList{BackColor=Theme.Field,ItemHeight=38};
    readonly ToolStripControlHost host;
    int selected=-1;bool updating;
    public int CornerRadius=4;
    internal int TextOffsetY;
    ComboBoxStyle style=ComboBoxStyle.DropDownList;
    public ItemCollection Items {get;private set;}
    public event EventHandler SelectedIndexChanged;
    public int SelectedIndex {
        get{return selected;}
        set{if(value < -1 || value>=Items.Count)throw new ArgumentOutOfRangeException("value");if(value==selected)return;selected=value;updating=true;try{input.Text=value<0?"":Convert.ToString(Items[value]);}finally{updating=false;}Invalidate();OnTextChanged(EventArgs.Empty);if(SelectedIndexChanged!=null)SelectedIndexChanged(this,EventArgs.Empty);}
    }
    public object SelectedItem {get{return selected<0?null:Items[selected];}set{SelectedIndex=Items.IndexOf(value);}}
    public ComboBoxStyle DropDownStyle {get{return style;}set{style=value;input.Visible=value==ComboBoxStyle.DropDown;TabStop=!input.Visible;PerformLayout();Invalidate();}}
    public override string Text {
        get{return input==null?base.Text:input.Text;}
        set{if(input==null){base.Text=value;return;}int index=Items.IndexOf(value);if(index>=0)SelectedIndex=index;else{SelectedIndex=-1;input.Text=value??"";Invalidate();}}
    }
    internal bool PopupVisible {get{return popup.Visible;}}
    public ThemedSelector(){
        SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);
        Items=new ItemCollection(this);Height=40;Dock=DockStyle.Fill;BackColor=Theme.Field;ForeColor=Theme.Text;Font=MotionPaint.Font("Segoe UI",12);TabStop=true;Cursor=Cursors.Hand;AccessibleRole=AccessibleRole.ComboBox;
        Controls.Add(input);input.Font=Font;
        host=new ToolStripControlHost(choices){AutoSize=false,Margin=Padding.Empty,Padding=Padding.Empty};popup.Items.Add(host);
        popup.Closed+=(sender,args)=>Invalidate();
        choices.MouseClick+=(sender,args)=>{if(args.Button==MouseButtons.Left&&choices.SelectedIndex>=0)Commit();};
        choices.KeyDown+=(sender,args)=>{if(args.KeyCode==Keys.Enter){Commit();args.Handled=true;args.SuppressKeyPress=true;}else if(args.KeyCode==Keys.Escape){popup.Close();FocusInput();args.Handled=true;args.SuppressKeyPress=true;}};
        input.TextChanged+=(sender,args)=>{if(updating)return;int index=Items.IndexOf(input.Text);bool changed=selected!=index;selected=index;Invalidate();OnTextChanged(EventArgs.Empty);if(changed&&SelectedIndexChanged!=null)SelectedIndexChanged(this,EventArgs.Empty);};
        input.KeyDown+=(sender,args)=>{if(args.KeyCode==Keys.F4||(args.Alt&&args.KeyCode==Keys.Down)){ShowChoices();args.Handled=true;args.SuppressKeyPress=true;}};
        input.GotFocus+=(sender,args)=>Invalidate();input.LostFocus+=(sender,args)=>Invalidate();
    }
    void FocusInput(){if(input.Visible)input.Focus();else Focus();}
    void ItemsChanged(){if(popup.Visible)popup.Close();Invalidate();}
    void Commit(){SelectedIndex=choices.SelectedIndex;popup.Close();FocusInput();}
    internal void ShowChoices(){
        if(!Enabled||Items.Count==0)return;if(popup.Visible){popup.Close();return;}
        choices.Items.Clear();choices.Items.AddRange(Items.ToArray());
        int height=Math.Min(8,Items.Count)*choices.ItemHeight;
        int textWidth=Items.Cast<object>().Max(item=>TextRenderer.MeasureText(Convert.ToString(item),choices.Font,Size.Empty,TextFormatFlags.SingleLine).Width);
        int width=Math.Min(Screen.FromControl(this).WorkingArea.Width-16,Math.Max(Math.Max(Width,180),textWidth+48));
        choices.Size=host.Size=new Size(width-2,height);popup.Size=new Size(width,height+2);choices.SelectedIndex=selected;
        popup.Show(this,new Point(0,Height+3));choices.Focus();Invalidate();
    }
    protected override void OnLayout(LayoutEventArgs e){base.OnLayout(e);if(input!=null)NativeInputLayout.Arrange(input,ClientSize,12,40,DeviceDpi);}
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left){FocusInput();ShowChoices();}}
    protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);Invalidate();}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);Invalidate();}
    protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
    protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
    protected override bool IsInputKey(Keys key){return key==Keys.Up||key==Keys.Down||key==Keys.Home||key==Keys.End||base.IsInputKey(key);}
    protected override void OnKeyDown(KeyEventArgs e){
        base.OnKeyDown(e);if(e.KeyCode==Keys.F4||e.KeyCode==Keys.Space||(e.Alt&&e.KeyCode==Keys.Down)){ShowChoices();e.Handled=true;}
        else if(Items.Count>0&&(e.KeyCode==Keys.Up||e.KeyCode==Keys.Down||e.KeyCode==Keys.Home||e.KeyCode==Keys.End)){
            SelectedIndex=e.KeyCode==Keys.Home?0:e.KeyCode==Keys.End?Items.Count-1:Math.Max(0,Math.Min(Items.Count-1,selected+(e.KeyCode==Keys.Up?-1:1)));e.Handled=true;
        }
    }
    protected override void OnPaint(PaintEventArgs e){
        if(CornerRadius>4)e.Graphics.Clear(Parent==null?Theme.Bg:Parent.BackColor);
        e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using(var path=Drawing.Rounded(new Rectangle(0,0,Width-1,Height-1),CornerRadius))using(var fill=new SolidBrush(MotionPaint.Hex("#fcf9f3")))using(var line=new Pen(ContainsFocus||popup.Visible?Theme.Accent:MotionPaint.Hex("#c6bba9"))){
            e.Graphics.FillPath(fill,path);e.Graphics.DrawPath(line,path);
        }
        if(!input.Visible)TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(12,(int)(TextOffsetY*DeviceDpi/96F),Math.Max(1,Width-49),Height),Enabled?Theme.Text:Theme.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        using(var pen=new Pen(Enabled?Theme.Accent:Theme.Muted,1.6F)){int x=Width-23,y=Height/2;e.Graphics.DrawLines(pen,popup.Visible?new[]{new Point(x-4,y+2),new Point(x,y-2),new Point(x+4,y+2)}:new[]{new Point(x-4,y-2),new Point(x,y+2),new Point(x+4,y-2)});}
    }
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);if(input!=null)input.Font=Font;if(choices!=null)choices.Font=Font;PerformLayout();}
    protected override void Dispose(bool disposing){if(disposing)popup.Dispose();base.Dispose(disposing);}
}

// The native edit control keeps editing, undo, selection and clipboard handling.
// Only its scroll chrome is composed here. No timers or window-style rewriting.
sealed class TextViewport : TextBox {
    public event EventHandler ViewChanged;
    public event EventHandler<MouseEventArgs> WheelRequested;
    protected override void WndProc(ref Message message) {
        if(message.Msg==0x20A&&WheelRequested!=null) {
            int delta=(short)(message.WParam.ToInt64()>>16);
            WheelRequested(this,new MouseEventArgs(MouseButtons.None,0,0,0,delta));
            message.Result=IntPtr.Zero;return;
        }
        base.WndProc(ref message);
        switch(message.Msg) {
            case 0x115: case 0x20A: case 0xB6: case 0xB7: // scroll / wheel / line scroll / caret
            case 0x100: case 0x102: case 0x202:          // keyboard / typing / selection
                if(ViewChanged!=null)ViewChanged(this,EventArgs.Empty);
                break;
        }
    }
}

sealed class ThemedEditor : UserControl {
    public bool BorderVisible {get;set;}
    readonly TextViewport input=new TextViewport {
        Multiline=true,WordWrap=true,ScrollBars=ScrollBars.None,BorderStyle=BorderStyle.None,
        Dock=DockStyle.Fill,BackColor=Theme.Field,ForeColor=Theme.Text,MaxLength=0,
        AcceptsReturn=true,HideSelection=false
    };
    readonly SlimScrollBar bar=new SlimScrollBar { Dock=DockStyle.Right,LineUnits=true };
    readonly Panel track=new Panel { Dock=DockStyle.Right,Width=12 };
    bool synchronizing;
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    internal TextBox Input { get { return input; } }
    internal SlimScrollBar ScrollBar { get { return bar; } }
    public override string Text { get { return input==null?base.Text:input.Text; } set { if(input==null)base.Text=value;else input.Text=value??""; } }
    public bool ReadOnly { get { return input.ReadOnly; } set { input.ReadOnly=value; } }
    public bool AcceptsTab { get { return input.AcceptsTab; } set { input.AcceptsTab=value; } }
    public int TextLength { get { return input.TextLength; } }
    public int SelectionStart { get { return input.SelectionStart; } set { input.SelectionStart=value; } }
    public string[] Lines { get { return input.Lines; } set { input.Lines=value; } }
    public ThemedEditor(string value="") {
        SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        BorderVisible=true;Dock=DockStyle.Fill;Height=100;BackColor=MotionPaint.Hex("#fcf9f3");Padding=new Padding(12);
        Font=MotionPaint.Font("Segoe UI",12);input.Font=Font;
        bar.Dock=DockStyle.Fill;track.Controls.Add(bar);Controls.Add(input);Controls.Add(track);
        input.TextChanged+=(sender,args)=>{OnTextChanged(EventArgs.Empty);UpdateViewport();};
        input.GotFocus+=(sender,args)=>Invalidate();input.LostFocus+=(sender,args)=>Invalidate();
        input.ViewChanged+=(sender,args)=>UpdateViewport();
        input.WheelRequested+=(sender,args)=>bar.ScrollWheel(args.Delta);
        input.HandleCreated+=(sender,args)=>UpdateViewport();
        input.Resize+=(sender,args)=>UpdateViewport();
        bar.ValueChanged+=(sender,args)=>ScrollToLine(bar.Value);
        Text=value;
    }
    int FirstLine { get { return input.IsHandleCreated?SendMessage(input.Handle,0xCE,IntPtr.Zero,IntPtr.Zero).ToInt32():0; } }
    void ScrollToLine(int line) {
        if(synchronizing||!input.IsHandleCreated)return;
        SendMessage(input.Handle,0xB6,IntPtr.Zero,new IntPtr(line-FirstLine));
        UpdateViewport();
    }
    void UpdateViewport() {
        if(synchronizing||!input.IsHandleCreated)return;
        synchronizing=true;
        try {
            int count=SendMessage(input.Handle,0xBA,IntPtr.Zero,IntPtr.Zero).ToInt32();
            int visible=Math.Max(1,input.ClientSize.Height/Math.Max(1,input.Font.Height));
            bar.SetRange(count,visible);bar.Value=FirstLine;bar.Visible=count>visible;
        } finally { synchronizing=false; }
    }
    public void AppendText(string text) { input.AppendText(text);UpdateViewport(); }
    public void Clear() { input.Clear(); }
    public void ScrollToCaret() { input.ScrollToCaret();UpdateViewport(); }
    public new bool Focus() { return input.Focus(); }
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);if(!BorderVisible)return;e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;using(var path=Drawing.Rounded(new Rectangle(0,0,Width-1,Height-1),4))using(var pen=new Pen(ContainsFocus&&!ReadOnly?Theme.Accent:MotionPaint.Hex("#c6bba9")))e.Graphics.DrawPath(pen,path);}
    protected override void OnEnter(EventArgs e) { base.OnEnter(e);input.Focus(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e);if(input!=null){input.Font=Font;UpdateViewport();} }
    protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e);if(input!=null)input.BackColor=BackColor;if(bar!=null)bar.BackColor=BackColor;if(track!=null)track.BackColor=BackColor; }
    protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e);if(input!=null)input.ForeColor=ForeColor; }
}

class ThemedList : UserControl {
    public Color SelectionForeColor=Theme.Accent;
    public sealed class ItemCollection : Collection<object> {
        readonly ThemedList owner;
        internal ItemCollection(ThemedList owner) { this.owner=owner; }
        public void AddRange(object[] values) { owner.BeginUpdate();try{foreach(object value in values)Add(value);}finally{owner.EndUpdate();} }
        protected override void InsertItem(int index,object value) { base.InsertItem(index,value);owner.ItemsChanged(); }
        protected override void ClearItems() { base.ClearItems();owner.ItemsChanged(); }
        protected override void RemoveItem(int index) { base.RemoveItem(index);owner.ItemsChanged(); }
        protected override void SetItem(int index,object value) { base.SetItem(index,value);owner.ItemsChanged(); }
    }
    readonly SlimScrollBar bar=new SlimScrollBar { Dock=DockStyle.Right };
    readonly SelectionMotion selectionMotion=new SelectionMotion();
    protected float SelectionAmount{get{return selectionMotion.Amount(this);}}
    int selected=-1,itemHeight=44,updateDepth;bool rangeDirty,selectionDirty;
    public void BeginUpdate(){updateDepth++;}
    public void EndUpdate(){if(updateDepth==0)throw new InvalidOperationException("BeginUpdate is required.");if(--updateDepth!=0)return;bool changed=selectionDirty;selectionDirty=false;if(rangeDirty){rangeDirty=false;UpdateRange();}EnsureSelectionVisible();Invalidate();if(changed&&SelectedIndexChanged!=null)SelectedIndexChanged(this,EventArgs.Empty);}
    void SelectionChanged(){if(updateDepth>0){selectionDirty=true;return;}EnsureSelectionVisible();Invalidate();if(SelectedIndexChanged!=null)SelectedIndexChanged(this,EventArgs.Empty);}
    void EnsureSelectionVisible(){if(selected<0)return;int top=selected*ItemHeight;if(top<bar.Value)bar.Value=top;if(top+ItemHeight>bar.Value+Height-HeaderHeight)bar.Value=top+ItemHeight-(Height-HeaderHeight);}
    public ItemCollection Items { get;private set; }
    public event EventHandler SelectedIndexChanged;
    public int ItemHeight { get { return itemHeight; } set { itemHeight=Math.Max(20,value);UpdateRange(); } }
    protected int ContentOffset { get { return bar.Value; } }
    protected virtual int HeaderHeight { get { return 0; } }
    protected int ContentWidth { get { return Math.Max(1,ClientSize.Width-bar.Width); } }
    public object SelectedItem { get { return selected<0?null:Items[selected]; } }
    public int SelectedIndex {
        get { return selected; }
        set {
            if(value< -1||value>=Items.Count)throw new ArgumentOutOfRangeException("value");
            if(value==selected)return;selected=value;
            selectionMotion.Start(this,this,updateDepth==0&&Focused&&value>=0);
            SelectionChanged();
        }
    }
    public ThemedList() {
        Items=new ItemCollection(this);Dock=DockStyle.Fill;BackColor=Theme.Bg;ForeColor=Theme.Text;TabStop=true;
        AccessibleRole=AccessibleRole.List;
        SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        Controls.Add(bar);bar.ValueChanged+=(sender,args)=>Invalidate();
    }
    void ItemsChanged() {
        bool cleared=selected>=Items.Count;if(cleared)selected=-1;
        UpdateRange();if(cleared)SelectionChanged();
    }
    void UpdateRange() { if(updateDepth>0){rangeDirty=true;return;}if(bar==null||Items==null)return;bar.SetRange(Items.Count*ItemHeight,Math.Max(0,Height-HeaderHeight));bar.Visible=bar.Maximum>0;Invalidate(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e);UpdateRange();if(selected>=0&&Height>HeaderHeight){int top=selected*ItemHeight;if(top<bar.Value)bar.Value=top;else if(top+ItemHeight>bar.Value+Height-HeaderHeight)bar.Value=Math.Max(0,top+ItemHeight-(Height-HeaderHeight));} }
    protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e);if(bar!=null)bar.BackColor=BackColor; }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(BackColor);DrawHeader(e.Graphics);
        int first=bar.Value/ItemHeight;
        var saved=e.Graphics.Save();e.Graphics.SetClip(new Rectangle(0,HeaderHeight,ContentWidth,Math.Max(0,Height-HeaderHeight)));
        for(int index=first;index<Items.Count;index++) {
            int top=HeaderHeight+index*ItemHeight-bar.Value;if(top>=Height)break;
            DrawItem(e.Graphics,index,new Rectangle(0,top,ContentWidth,ItemHeight),index==selected);
        }
        e.Graphics.Restore(saved);
    }
    protected virtual void DrawHeader(Graphics graphics) { }
    protected virtual void DrawItem(Graphics graphics,int index,Rectangle bounds,bool active) {
        using(var brush=new SolidBrush(active?Theme.Card:BackColor))graphics.FillRectangle(brush,bounds);
        if(active)using(var brush=new SolidBrush(MotionPaint.Mix(Theme.Card,Theme.Accent,SelectionAmount)))graphics.FillRectangle(brush,bounds.Left,bounds.Top+10,3,bounds.Height-20);
        TextRenderer.DrawText(graphics,Convert.ToString(Items[index]),Font,new Rectangle(bounds.Left+15,bounds.Top,bounds.Width-25,bounds.Height),active?SelectionForeColor:ForeColor,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        if(active&&Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(graphics,Rectangle.Inflate(bounds,-4,-4));
    }
    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);if(e.Button!=MouseButtons.Left||e.Y<HeaderHeight)return;Focus();
        int index=(e.Y-HeaderHeight+bar.Value)/ItemHeight;if(index<Items.Count)SelectedIndex=index;
    }
    protected override void OnMouseWheel(MouseEventArgs e) { bar.ScrollWheel(e.Delta);base.OnMouseWheel(e); }
    protected override bool IsInputKey(Keys keyData) { return new[]{Keys.Up,Keys.Down,Keys.Home,Keys.End,Keys.PageUp,Keys.PageDown}.Contains(keyData&Keys.KeyCode)||base.IsInputKey(keyData); }
    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);if(Items.Count==0)return;int target=selected;
        switch(e.KeyCode){case Keys.Up:target--;break;case Keys.Down:target++;break;case Keys.Home:target=0;break;case Keys.End:target=Items.Count-1;break;case Keys.PageUp:target-=Math.Max(1,(Height-HeaderHeight)/ItemHeight);break;case Keys.PageDown:target+=Math.Max(1,(Height-HeaderHeight)/ItemHeight);break;default:return;}
        SelectedIndex=Math.Max(0,Math.Min(Items.Count-1,target));e.Handled=true;
    }
}

sealed class AdapterRow {
    public readonly string[] Cells;
    public AdapterRow(string[] cells) { Cells=cells; }
}
sealed class AdapterList : ThemedList {
    public event EventHandler ConfigureRequested{add{DoubleClick+=value;}remove{DoubleClick-=value;}}
    protected override int HeaderHeight{get{return (int)(40*DeviceDpi/96F);}}
    public AdapterList(){ItemHeight=48;Font=UiTypography.Make(10);}
    protected override void DrawHeader(Graphics graphics){var bounds=new Rectangle(0,0,ContentWidth,HeaderHeight);using(var brush=new SolidBrush(MotionPaint.Hex("#e9e3d8")))graphics.FillRectangle(brush,bounds);DrawCells(graphics,new[]{"ID","Подключение","Модель","MAC","Подсеть / узел"},bounds,true);using(var pen=new Pen(MotionPaint.Hex("#c2b6a5"))){graphics.DrawLine(pen,0,0,ContentWidth,0);graphics.DrawLine(pen,0,HeaderHeight-1,ContentWidth,HeaderHeight-1);}}
    protected override void DrawItem(Graphics graphics,int index,Rectangle bounds,bool active){using(var brush=new SolidBrush(active?MotionPaint.Hex("#eee0ce"):Theme.Bg))graphics.FillRectangle(brush,bounds);if(active)using(var brush=new SolidBrush(MotionPaint.Mix(Theme.Card,Theme.Accent,SelectionAmount)))graphics.FillRectangle(brush,bounds.Left,bounds.Top,3,bounds.Height);DrawCells(graphics,((AdapterRow)Items[index]).Cells,bounds,false);using(var pen=new Pen(MotionPaint.Hex("#ddd4c7")))graphics.DrawLine(pen,bounds.Left,bounds.Bottom-1,bounds.Right,bounds.Bottom-1);}
    void DrawCells(Graphics graphics,string[] cells,Rectangle bounds,bool header){float[] widths={.16F,.21F,.125F,.28F,.225F};int left=bounds.Left;using(var font=UiTypography.Make((header?8:10)*DeviceDpi/96F)){for(int i=0;i<5;i++){int width=i==4?bounds.Right-left:(int)(bounds.Width*widths[i]);TextRenderer.DrawText(graphics,cells[i],font,new Rectangle(left+9,bounds.Top,Math.Max(1,width-18),bounds.Height),header?MotionPaint.Hex("#6c5a47"):Theme.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);left+=width;}}}
}
