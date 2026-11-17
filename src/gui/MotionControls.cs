using System;
using System.Drawing;
using System.Windows.Forms;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;

static class MotionPaint {
    [DllImport("user32.dll")]static extern bool SystemParametersInfo(uint action,uint parameter,out bool value,uint flags);
    public static bool Reduced {get{bool enabled;return SystemParametersInfo(0x1042,0,out enabled,0)&&!enabled;}}
    public static Font Font(string family,float pixels,FontStyle style=FontStyle.Regular){return UiTypography.Make(pixels,family=="Arial Black"?900:(style&FontStyle.Bold)!=0?700:400);}
    public static Color Hex(string value){return ColorTranslator.FromHtml(value);}
    public static Color Mix(Color background,Color foreground,float alpha){alpha=Math.Max(0,Math.Min(1,alpha));return Color.FromArgb((int)(background.R+(foreground.R-background.R)*alpha),(int)(background.G+(foreground.G-background.G)*alpha),(int)(background.B+(foreground.B-background.B)*alpha));}
    public static float Ease(float progress,float x1=.25F,float y1=.1F,float x2=.25F,float y2=1){if(progress<=0)return 0;if(progress>=1)return 1;float lo=0,hi=1,t=progress;for(int i=0;i<20;i++){t=(lo+hi)/2;float x=3*(1-t)*(1-t)*t*x1+3*(1-t)*t*t*x2+t*t*t;if(x<progress)lo=t;else hi=t;}return 3*(1-t)*(1-t)*t*y1+3*(1-t)*t*t*y2+t*t*t;}
    public static float Alternate(double elapsed,double seconds){double position=(elapsed%(seconds*2))/seconds;return Ease((float)(position<=1?position:2-position),.42F,0,.58F,1);}
    sealed class CachedTextPath { public GraphicsPath Path;public float Width; }
    static readonly Dictionary<string,CachedTextPath> textPaths=new Dictionary<string,CachedTextPath>();
    static readonly Queue<string> textPathOrder=new Queue<string>();
    internal static int TextPathBuildCount;
    public static GraphicsPath TextPath(string text,float pixels,float spacing,out float width,int weight=900){
        string key=weight+"/"+pixels.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"/"+spacing.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"/"+text;
        lock(textPaths){
            CachedTextPath cached;if(textPaths.TryGetValue(key,out cached)){width=cached.Width;return (GraphicsPath)cached.Path.Clone();}
        }
        // Geometry construction may run in a decorative worker. Do not hold the
        // shared cache lock while measuring glyphs needed by interactive paint.
        var path=BuildTextPath(text,pixels,spacing,out width,weight);
        lock(textPaths){
            CachedTextPath cached;if(textPaths.TryGetValue(key,out cached)){path.Dispose();width=cached.Width;return (GraphicsPath)cached.Path.Clone();}TextPathBuildCount++;
            if(textPaths.Count>=256){string expired=textPathOrder.Dequeue();textPaths[expired].Path.Dispose();textPaths.Remove(expired);}
            textPaths[key]=new CachedTextPath{Path=(GraphicsPath)path.Clone(),Width=width};textPathOrder.Enqueue(key);return path;
        }
    }
    static GraphicsPath BuildTextPath(string text,float pixels,float spacing,out float width,int weight=900){
        var path=new GraphicsPath(FillMode.Winding);width=0;using(var font=UiTypography.Make(pixels,weight))using(var bitmap=new Bitmap(1,1))using(var g=Graphics.FromImage(bitmap))using(var format=(StringFormat)StringFormat.GenericTypographic.Clone()){
            format.FormatFlags|=StringFormatFlags.MeasureTrailingSpaces;foreach(char letter in text){path.AddString(letter.ToString(),UiTypography.Family(weight),(int)UiTypography.Style(weight),pixels,new PointF(width,0),format);width+=g.MeasureString(letter.ToString(),font,PointF.Empty,format).Width+spacing;}
        }width=Math.Max(0,width-spacing);return path;
    }
    public static Bitmap Ghost(string text,float pixels,float spacing,Color color,float sigma){
        float width;using(var path=TextPath(text,pixels,spacing,out width)){
            int pad=(int)Math.Ceiling(sigma*3)+4;var bounds=path.GetBounds();using(var bitmap=new Bitmap(Math.Max(1,(int)Math.Ceiling(width)+pad*2),Math.Max(1,(int)Math.Ceiling(bounds.Height)+pad*2),PixelFormat.Format32bppPArgb)){
                using(var g=Graphics.FromImage(bitmap)){g.SmoothingMode=SmoothingMode.AntiAlias;g.TranslateTransform(pad,pad-bounds.Top);using(var brush=new SolidBrush(color))g.FillPath(brush,path);}return BlurMask(bitmap,sigma,color);
            }
        }
    }
    public static Bitmap BlurMask(Bitmap source,float sigma,Color tint){
        return BoundedBackdropBlur.Mask(source,sigma,tint);
    }

    public static Bitmap BlurBackdrop(Bitmap source,float sigma){
        return BoundedBackdropBlur.Render(source,sigma);
    }

}

// Captures only this process's owner client. Each owner retains one pair of frames.
sealed class ReferenceBackdropSurface : Control {
    internal Bitmap Snapshot;
    internal int Generation;
    internal bool BlockInput;
    Bitmap source;
    float opacity;
    readonly System.Drawing.Imaging.ImageAttributes blend = new System.Drawing.Imaging.ImageAttributes();
    readonly string resourceOwner = GraphicsResources.Owner("owner-frame");

    public ReferenceBackdropSurface() {
        // An overlay covers the client, independently of docked application
        // chrome. Dock.Fill would reserve the header/footer a second time.
        Dock = DockStyle.None;
        Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        TabStop = false;
        AccessibleRole = AccessibleRole.None;
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
    }
    internal float Blend {
        get { return opacity; }
        set {
            opacity = Math.Max(0, Math.Min(1, value));
            if (opacity > 0 && opacity < 1) blend.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
            Invalidate();
        }
    }
    protected override void WndProc(ref Message message) {
        if (message.Msg == 0x84 && !BlockInput) { message.Result = new IntPtr(-1); return; }
        base.WndProc(ref message);
    }
    protected override void OnPaint(PaintEventArgs e) {
        if (Snapshot == null) return;
        if (opacity >= 1) { PaintFrame(e.Graphics,Snapshot); return; }
        PaintFrame(e.Graphics,source);
        if (opacity > 0)
            e.Graphics.DrawImage(Snapshot, ClientRectangle, 0, 0,
                Snapshot.Width, Snapshot.Height, GraphicsUnit.Pixel, blend);
    }
    void PaintFrame(Graphics graphics,Bitmap image){if(image.Size==ClientSize)graphics.DrawImageUnscaled(image,0,0);else graphics.DrawImage(image,ClientRectangle);}
    internal Bitmap CopySource(Size size){
        if(source==null)return null;
        if(source.Size==size)return (Bitmap)source.Clone();
        var copy=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
        using(var graphics=Graphics.FromImage(copy))graphics.DrawImage(source,new Rectangle(Point.Empty,size));
        return copy;
    }
    internal void SetFrames(Bitmap original, Bitmap effect) {
        ClearFrames();
        source = original;
        Snapshot = effect;
        GraphicsResources.Set(resourceOwner, "frame", GraphicsResources.ImageBytes(source) + GraphicsResources.ImageBytes(Snapshot));
        Generation++;
        Invalidate();
    }
    internal void SetEffect(Bitmap effect){if(Snapshot!=null)Snapshot.Dispose();Snapshot=effect;GraphicsResources.Set(resourceOwner,"frame",GraphicsResources.ImageBytes(source)+GraphicsResources.ImageBytes(Snapshot));Generation++;Invalidate();}
    void ClearFrames() {
        if (source != null) { source.Dispose(); source = null; }
        if (Snapshot != null) { Snapshot.Dispose(); Snapshot = null; }
        GraphicsResources.Set(resourceOwner, "frame", 0);
    }
    protected override void Dispose(bool disposing) {
        if (disposing) { ClearFrames(); blend.Dispose(); }
        base.Dispose(disposing);
    }
}

sealed class ReferenceDialogBackdrop : IDisposable {
    static readonly Dictionary<Form, ReferenceDialogBackdrop> attached = new Dictionary<Form, ReferenceDialogBackdrop>();
    static readonly Dictionary<Form, ReferenceDialogBackdrop> owners = new Dictionary<Form, ReferenceDialogBackdrop>();
    static readonly System.Threading.SemaphoreSlim worker = new System.Threading.SemaphoreSlim(1, 1);

    readonly Form dialog;
    readonly Func<Bitmap,float,Bitmap> blur;
    readonly Timer resize = new Timer { Interval = 150 };
    readonly string captureOwner = GraphicsResources.Owner("capture");
    readonly Stopwatch fade = new Stopwatch();
    Form owner;
    ReferenceBackdropSurface surface;
    System.Threading.Tasks.TaskCompletionSource<bool> ready;
    System.Threading.CancellationTokenSource lifetime;
    bool refreshing, disposed, dirty, exiting, attachedForVisibility, ownerInteractive;
    int version;
    internal string LastError { get; private set; }

    internal static System.Threading.Tasks.Task WaitFor(Form form) {
        ReferenceDialogBackdrop backdrop;
        if (!attached.TryGetValue(form, out backdrop)) return System.Threading.Tasks.Task.FromResult(true);
        backdrop.Attach();
        return backdrop.ready == null ? System.Threading.Tasks.Task.FromResult(true) : backdrop.ready.Task;
    }
    internal static double BeginExit(Form form) {
        ReferenceDialogBackdrop backdrop;
        if (!attached.TryGetValue(form, out backdrop) || backdrop.surface == null) return 0;
        backdrop.exiting = true;
        AnimationClock.Cancel(backdrop.surface);
        return backdrop.surface.Blend;
    }
    internal static void SetExitOpacity(Form form, double opacity) {
        ReferenceDialogBackdrop backdrop;
        if (attached.TryGetValue(form, out backdrop) && backdrop.surface != null)
            backdrop.surface.Blend = (float)opacity;
    }

    public ReferenceDialogBackdrop(Form dialog, Func<Bitmap,float,Bitmap> blur = null) {
        this.dialog = dialog;
        this.blur = blur ?? MotionPaint.BlurBackdrop;
        attached[dialog] = this;
        dialog.Shown += Shown;
        dialog.Activated += DialogActivated;
        dialog.VisibleChanged += Visibility;
        dialog.Disposed += Disposed;
        resize.Tick += (s,e) => { resize.Stop(); Refresh(); };
    }
    void Shown(object sender, EventArgs e) { Attach(); }
    void Visibility(object sender, EventArgs e) {
        if (!dialog.Visible) { attachedForVisibility = false; Release(); }
        else if (dialog.IsHandleCreated) Attach();
    }
    void Attach() {
        if (disposed || attachedForVisibility || !dialog.Visible || !dialog.IsHandleCreated ||
            dialog.Owner == null || dialog.Owner.IsDisposed) return;
        attachedForVisibility = true;
        owner = dialog.Owner;
        var parent = owner as MotionDialog;
        if (parent != null && parent.Presentation != null && parent.Presentation.Active)
            parent.Presentation.FinishOpening();
        ReferenceDialogBackdrop previous;
        if (owners.TryGetValue(owner, out previous)) previous.Release();
        owners[owner] = this;
        ready = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime = new System.Threading.CancellationTokenSource();
        surface = DialogLaunch.Take(owner);
        if(surface!=null){ready.TrySetResult(true);dirty=false;}
        else{surface = new ReferenceBackdropSurface { Visible = false, Bounds = owner.ClientRectangle };owner.Controls.Add(surface);}
        owner.ClientSizeChanged += Resize;
        owner.DpiChanged += DpiChanged;
        owner.Activated += OwnerActivated;
        owner.Disposed += OwnerDisposed;
        version++;
        dirty = !ready.Task.IsCompleted;
        if(dirty)dialog.BeginInvoke((Action)Refresh);
    }
    void DialogActivated(object sender, EventArgs args) {
        if (!ownerInteractive || surface == null || exiting) return;
        ownerInteractive = false;
        dirty = true;
        dialog.BeginInvoke((Action)Refresh);
    }
    void OwnerActivated(object sender, EventArgs args) {
        if (dialog.Modal || surface == null || exiting) return;
        // A modeless editor does not freeze a workspace the user returned to.
        ownerInteractive = true;
        version++;
        dirty = false;
        resize.Stop();
        AnimationClock.Cancel(surface);
        surface.Hide();
        ready.TrySetResult(false);
    }
    void DpiChanged(object sender, EventArgs args) {
        Resize(sender, args);
    }
    void Resize(object sender, EventArgs e) {
        if (surface == null || exiting || ownerInteractive) return;
        version++;
        dirty = true;
        resize.Stop();
        resize.Start();
    }
    async void Refresh() {
        if (!dirty || disposed || owner == null || owner.IsDisposed || surface == null || exiting || ownerInteractive) return;
        if (refreshing) return;
        refreshing = true;
        dirty = false;
        bool entered = false;
        Bitmap client = null, image = null;
        var cancellation = lifetime.Token;
        var timing = NetworkOpenTiming.For(dialog);
        try {
            await worker.WaitAsync(cancellation);
            entered = true;
            if (cancellation.IsCancellationRequested || exiting || ownerInteractive || owner.IsDisposed) return;
            if (!owner.Visible || owner.WindowState == FormWindowState.Minimized) { ready.TrySetResult(false); return; }
            dirty = false;
            int request = version;
            var target = surface;
            var sourceOwner = owner;
            var size = owner.ClientSize;
            int dpi = owner.DeviceDpi;
            if (timing != null) timing.Mark("backdrop_capture_start");
            // Keep one scene for the entire modal presentation. Rebuilding a
            // resize/DPI effect reuses its unblurred source, so no layer needs
            // to be hidden and live polling cannot replace the frozen scene.
            client = target.Visible ? target.CopySource(size) : WindowFrame.Capture(owner);
            if (timing != null) timing.Mark("backdrop_capture");
            if (client == null) { ready.TrySetResult(false); return; }
            GraphicsResources.Set(captureOwner, "frame", GraphicsResources.ImageBytes(client));
            var source = client;
            image = await System.Threading.Tasks.Task.Run(() => GraphicsResources.WithCancellation(
                () => blur(source, 10 * dpi / 96F), cancellation));
            if (cancellation.IsCancellationRequested || exiting || request != version || target.IsDisposed ||
                sourceOwner.ClientSize != size || sourceOwner.DeviceDpi != dpi) return;
            bool first = !target.Visible;
            target.SetFrames(client, image);
            client = image = null;
            target.Show();
            target.BringToFront();
            if (first || target.Blend < 1) {
                float start=first?0:target.Blend;
                target.Blend = start;
                fade.Restart();
                AnimationClock.Run(target, motion => {
                    if(request!=version||exiting||ownerInteractive)return false;
                    float progress = motion ? MotionPaint.Ease((float)fade.Elapsed.TotalMilliseconds / DialogEntrance.Duration, .2F, .8F, .2F, 1) : 1;
                    target.Blend = start + (1-start) * progress;
                    if(progress>=1){target.Update();ready.TrySetResult(true);}
                    return progress < 1;
                });
            }else ready.TrySetResult(true);
            target.Update();
            LastError = null;
            if (timing != null) timing.Mark("backdrop_ready");
        } catch (OperationCanceledException) {
        } catch (Exception error) {
            if (!cancellation.IsCancellationRequested) {
                LastError = error.GetType().Name + ": " + error.Message;
                ready.TrySetResult(false);
            }
        } finally {
            if (client != null) client.Dispose();
            if (image != null) image.Dispose();
            GraphicsResources.Set(captureOwner, "frame", 0);
            if (entered) worker.Release();
            refreshing = false;
            if (dirty && !resize.Enabled && !disposed && dialog.IsHandleCreated)
                dialog.BeginInvoke((Action)Refresh);
        }
    }
    void Release() {
        version++;
        dirty = exiting = ownerInteractive = false;
        resize.Stop();
        if (ready != null) { ready.TrySetCanceled(); ready = null; }
        if (lifetime != null) { lifetime.Cancel(); lifetime.Dispose(); lifetime = null; }
        if (owner != null) {
            owners.Remove(owner);
            owner.ClientSizeChanged -= Resize;
            owner.DpiChanged -= DpiChanged;
            owner.Activated -= OwnerActivated;
            owner.Disposed -= OwnerDisposed;
        }
        if (surface != null) { AnimationClock.Cancel(surface); surface.Dispose(); surface = null;if(owner!=null&&!owner.IsDisposed&&owner.Visible)WindowFrame.PaintLive(owner); }
        owner = null;
    }
    void OwnerDisposed(object sender, EventArgs e) { Release(); }
    void Disposed(object sender, EventArgs e) { Dispose(); }
    public void Dispose() {
        if (disposed) return;
        disposed = true;
        attached.Remove(dialog);
        Release();
        resize.Dispose();
        dialog.Shown -= Shown;
        dialog.Activated -= DialogActivated;
        dialog.VisibleChanged -= Visibility;
        dialog.Disposed -= Disposed;
    }
}

sealed class SettingsSeparator : Control {
    float scale=1;public SettingsSeparator(){Height=34;SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer,true);}
    protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Width;base.ScaleControl(factor,specified);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(var font=UiTypography.Make(8*scale))using(var pen=new Pen(Theme.Line)){var rect=new Rectangle(0,0,(int)(150*scale),Height);TextRenderer.DrawText(e.Graphics,"ДИСКИ И ОБРАЗЫ",font,rect,Theme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.Left);e.Graphics.DrawLine(pen,150*scale,Height/2,Width,Height/2);}}
}

sealed class SettingsHeading : Control {
    float scale=1;protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Width;base.ScaleControl(factor,specified);}
    readonly string title,tag;public SettingsHeading(string title,string tag){this.title=title;this.tag=tag;Dock=DockStyle.Top;Height=56;BackColor=Theme.Bg;SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer,true);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);float width;using(var path=MotionPaint.TextPath(title,17.5F*scale,-17.5F*.062F*scale,out width,700))using(var ink=new SolidBrush(Theme.Text)){var bounds=path.GetBounds();using(var matrix=new Matrix()){matrix.Translate(0,4*scale-bounds.Top);path.Transform(matrix);}e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.FillPath(ink,path);}using(var pen=new Pen(MotionPaint.Hex("#c9c0b3")))e.Graphics.DrawLine(pen,0,35*scale,Width,35*scale);using(var font=UiTypography.Make(8*scale))TextRenderer.DrawText(e.Graphics,tag,font,new Rectangle(Math.Max(0,Width-(int)(110*scale)),0,(int)(110*scale),(int)(28*scale)),MotionPaint.Hex("#a2734b"),TextFormatFlags.Right|TextFormatFlags.VerticalCenter);}
}

sealed class ReferenceDialogChrome : NativeWindow,IDisposable {
    readonly Form form;
    [DllImport("user32.dll")]static extern bool ReleaseCapture();
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    public ReferenceDialogChrome(Form form,Control header){this.form=form;form.FormBorderStyle=FormBorderStyle.None;form.Padding=new Padding(1);form.HandleCreated+=(s,e)=>{AssignHandle(form.Handle);Shape();};form.HandleDestroyed+=(s,e)=>ReleaseHandle();form.SizeChanged+=(s,e)=>Shape();form.Disposed+=(s,e)=>Dispose();header.MouseDown+=(s,e)=>{if(e.Button==MouseButtons.Left){ReleaseCapture();SendMessage(form.Handle,0xA1,new IntPtr(2),IntPtr.Zero);}};form.Paint+=(s,e)=>{using(var path=Drawing.Rounded(new Rectangle(0,0,form.Width-1,form.Height-1),(int)(16*form.DeviceDpi/96F)))using(var pen=new Pen(MotionPaint.Hex("#c1b6a5")))e.Graphics.DrawPath(pen,path);};if(form.IsHandleCreated)AssignHandle(form.Handle);Shape();}
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    internal static bool SmoothCorners(Control window){try{int preference=2;return DwmSetWindowAttribute(window.Handle,33,ref preference,4)>=0;}catch(DllNotFoundException){return false;}catch(EntryPointNotFoundException){return false;}}
    void Shape(){if(form.Width<1||form.Height<1)return;
        if(form.IsHandleCreated&&SmoothCorners(form)){var previous=form.Region;form.Region=null;if(previous!=null)previous.Dispose();return;}
using(var path=Drawing.Rounded(new Rectangle(0,0,form.Width,form.Height),(int)(16*form.DeviceDpi/96F))){var old=form.Region;form.Region=new Region(path);if(old!=null)old.Dispose();}}
    protected override void WndProc(ref Message message){
        if(message.Msg==0x84){int value=message.LParam.ToInt32();Point p=form.PointToClient(new Point((short)(value&0xffff),(short)(value>>16)));int edge=Math.Max(4,(int)(6*form.DeviceDpi/96F));bool left=p.X<edge,right=p.X>=form.Width-edge,top=p.Y<edge,bottom=p.Y>=form.Height-edge;int hit=top?(left?13:right?14:12):bottom?(left?16:right?17:15):left?10:right?11:0;if(hit!=0){message.Result=new IntPtr(hit);return;}}
        base.WndProc(ref message);
    }
    public void Dispose(){if(Handle!=IntPtr.Zero)ReleaseHandle();}
}

sealed class ReferenceHeader : BufferedPanel {
    string title,subtitle,eyebrow;readonly Button close;float scale=1;bool compact;string ghostText;float compactSize=38;bool network;
    internal static string EnglishGhost(string value){
        if(String.IsNullOrEmpty(value))return "QEMU";
        if(value.All(c=>c<128))return value;
        string lower=value.ToLowerInvariant();
        if(lower.Contains("снимк")||lower.Contains("восстановлен"))return lower.Contains("новый")?"NEW":"SNAPSHOT";
        if(lower.Contains("сет")||lower.Contains("адаптер"))return "NETWORK";
        if(lower.Contains("стенд"))return "STAND";
        if(lower.Contains("удал"))return "DELETE";
        if(lower.Contains("новая")||lower.Contains("создан"))return "CREATE";
        if(lower.Contains("параметр")||lower.Contains("настройк"))return "SETTINGS";
        if(lower.Contains("операц"))return "OPERATION";
        if(lower.Contains("сервис"))return "SERVICE";
        if(lower.Contains("журнал")||lower.Contains("событ"))return "EVENTS";
        return "TOOLS";
    }
    public void Compact(string ghost,float size=38){compact=true;ghostText=network?"NETWORK":EnglishGhost(ghost);compactSize=network?31:size;Height=(int)((network?160:155)*scale);PrepareGhost();Invalidate();}
    public void NetworkPresentation(){network=true;Compact("СЕТЬ",31);Height=(int)(160*scale);}
    public void Present(string title,string subtitle,string eyebrow){this.title=title;this.subtitle=subtitle;this.eyebrow=eyebrow;PrepareGhost();AccessibleName=title+" / "+subtitle;Invalidate();}
    protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Width;base.ScaleControl(factor,specified);}
    Bitmap ghost;bool preparingGhost,ghostDisposed;string ghostKey,wantedGhost;readonly string ghostOwner=GraphicsResources.Owner("header-ghost");System.Threading.CancellationTokenSource ghostCancellation;
    System.Threading.Tasks.TaskCompletionSource<bool> ghostReady;
    internal bool GhostReady{get{return ghost!=null&&ghostKey==wantedGhost;}}
    internal System.Threading.Tasks.Task WhenPrepared(){
        PrepareGhost();
        return ghostReady==null?System.Threading.Tasks.Task.FromResult(true):ghostReady.Task;
    }
    public ReferenceHeader(string title,string subtitle,string eyebrow){
        this.title=title;this.subtitle=subtitle;this.eyebrow=eyebrow;AccessibleName=title+" / "+subtitle;Height=166;Dock=DockStyle.Top;
        close=Theme.Button("",()=>{var form=FindForm();if(form!=null)form.Close();});close.AutoSize=false;close.MinimumSize=Size.Empty;close.Size=new Size(34,34);close.AccessibleName="Закрыть";((PolarButton)close).IconName="dialog:close";Controls.Add(close);
        VisibleChanged+=(s,e)=>PrepareGhost();HandleCreated+=(s,e)=>PrepareGhost();
    }
    async void PrepareGhost(){
        if(ghostDisposed||!IsHandleCreated||!Visible)return;
        string text=ghostText??EnglishGhost(title);
        float pixels=network?118:compact?91:110,spacing=network?-11.21F:compact?-8.645F:-8;
        string key=text+"|"+pixels+"|"+spacing;
        if(preparingGhost&&wantedGhost!=key&&ghostCancellation!=null)ghostCancellation.Cancel();
        wantedGhost=key;
        if(preparingGhost||ghostKey==key)return;
        if(ghostReady==null||ghostReady.Task.IsCompleted)
            ghostReady=new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        preparingGhost=true;
        Bitmap image=null;
        var source=new System.Threading.CancellationTokenSource();
        ghostCancellation=source;
        var token=source.Token;
        try{
            image=await System.Threading.Tasks.Task.Run(()=>GraphicsResources.WithCancellation(
                ()=>MotionPaint.Ghost(text,pixels,spacing,Color.FromArgb(16,255,59,0),3),token),token);
            if(!ghostDisposed&&wantedGhost==key){
                if(GraphicsResources.TryDecor(ghostOwner,GraphicsResources.ImageBytes(image))){
                    var old=ghost;ghost=image;image=null;
                    if(old!=null)old.Dispose();
                    Invalidate();
                }
                ghostKey=key;
            }
        }catch(OperationCanceledException){}
        catch(Exception){ghostKey=key;}
        finally{
            if(image!=null)image.Dispose();
            ghostCancellation=null;
            source.Dispose();
            preparingGhost=false;
            if(!ghostDisposed&&Visible&&ghostKey!=wantedGhost)PrepareGhost();
            else ghostReady.TrySetResult(ghostKey==wantedGhost);
        }
    }
    protected override void OnLayout(LayoutEventArgs e){base.OnLayout(e);if(close!=null)close.Location=new Point(Math.Max(0,Width-(int)(56*scale)),(int)(21*scale));}
    protected override void OnPaintBackground(PaintEventArgs e){
        if(Width<1||Height<1)return;using(var fill=new LinearGradientBrush(ClientRectangle,MotionPaint.Hex("#f8f5ef"),MotionPaint.Hex("#eee5d8"),120))e.Graphics.FillRectangle(fill,ClientRectangle);
    }
    protected override void OnPaint(PaintEventArgs e){
        base.OnPaint(e);var g=e.Graphics;var paintState=g.Save();g.ScaleTransform(scale,scale);float logicalWidth=Width/scale,logicalHeight=Height/scale;g.SmoothingMode=SmoothingMode.AntiAlias;
        if(ghost!=null){const float drift=0;var saved=g.Save();float x=logicalWidth-(network?65:80)-ghost.Width,y=network?34:29;g.TranslateTransform(x+ghost.Width/2-(network?20:15)*drift,y+ghost.Height/2+(network?9:4)*drift);g.RotateTransform(-5+3*drift);g.DrawImageUnscaled(ghost,-ghost.Width/2,-ghost.Height/2);g.Restore(saved);}
        using(var mono=MotionPaint.Font("Courier New",9))using(var ink=new SolidBrush(Theme.Muted))g.DrawString(eyebrow,mono,ink,network?32:30,network?24:27);

        var owner=FindForm();int viewport=owner!=null&&owner.Owner!=null?owner.Owner.ClientSize.Width:Screen.FromControl(this).WorkingArea.Width;float size=compact?compactSize:viewport<=740?37:56,width;using(var path=MotionPaint.TextPath(title,size,compact?-size*(network?.06F:.078F):viewport<=740?-1.7F:-2.6F,out width,compact?800:900))using(var ink=new SolidBrush(Theme.Text)){var bounds=path.GetBounds();var transform=new Matrix();transform.Translate(network?32:compact?27:30,(network?58:compact?55:64)-bounds.Top);path.Transform(transform);transform.Dispose();g.FillPath(ink,path);}
        float subtitleWidth;using(var path=MotionPaint.TextPath(subtitle,compact?10:12,0,out subtitleWidth,400))using(var ink=new SolidBrush(Theme.Text)){var bounds=path.GetBounds();using(var transform=new Matrix()){transform.Translate(network?48:compact?43:45,(network?113:compact?115:136)-bounds.Top-bounds.Height/2);path.Transform(transform);}g.FillPath(ink,path);}
        if(!String.IsNullOrWhiteSpace(subtitle))using(var ink=new SolidBrush(MotionPaint.Hex("#6b7b54")))g.FillEllipse(ink,network?32:compact?27:30,network?110:compact?112:133,6,6);using(var pen=new Pen(MotionPaint.Hex("#c9c0b3")))g.DrawLine(pen,0,logicalHeight-1,logicalWidth,logicalHeight-1);g.Restore(paintState);
    }
    protected override void Dispose(bool disposing){if(disposing){ghostDisposed=true;if(ghostReady!=null)ghostReady.TrySetCanceled();if(ghostCancellation!=null)ghostCancellation.Cancel();if(ghost!=null){ghost.Dispose();ghost=null;}GraphicsResources.Set(ghostOwner,"decor",0);}base.Dispose(disposing);}
}

sealed class TrackedWord : Control {
    float scale=1;
    protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Width;base.ScaleControl(factor,specified);}
    Bitmap settled;string settledKey;internal int PaintCacheBuilds;readonly string wordOwner=GraphicsResources.Owner("word-cache");
    string word="";readonly Stopwatch changed=Stopwatch.StartNew();public PointF Offset;public string Suffix="";public bool StageColors;public bool Running;
    public string Word{get{return word;}set{if(word==value)return;word=value??"";AccessibleName=word;changed.Restart();if(settled!=null){settled.Dispose();settled=null;}GraphicsResources.Set(wordOwner,"decor",0);Invalidate();}}
    internal int AnimationGeneration {get;private set;}
    public void Replay(){changed.Restart();AnimationGeneration++;Invalidate();}
    internal bool Animating{get{return !MotionPaint.Reduced&&!Program.TestMode&&changed.Elapsed.TotalMilliseconds<220;}}
    public TrackedWord(){SetStyle(ControlStyles.SupportsTransparentBackColor|ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);BackColor=Color.Transparent;AccessibleRole=AccessibleRole.StaticText;}
    protected override void OnPaint(PaintEventArgs e){
        base.OnPaint(e);if(word.Length==0||Width<1||Height<1)return;
        bool complete=MotionPaint.Reduced||Program.TestMode||changed.Elapsed.TotalMilliseconds>=220;
        if(complete){
            string key=word+"/"+Size+"/"+StageColors+"/"+Running+"/"+Suffix+"/"+scale;
            if(settled==null||settledKey!=key){if(settled!=null){settled.Dispose();settled=null;}if(!GraphicsResources.TryDecor(wordOwner,(long)Width*Height*4)){var state=e.Graphics.Save();e.Graphics.TranslateTransform(Offset.X,Offset.Y);RenderWord(e.Graphics);e.Graphics.Restore(state);return;}settled=new Bitmap(Width,Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);using(var graphics=Graphics.FromImage(settled))RenderWord(graphics);settledKey=key;PaintCacheBuilds++;}
            e.Graphics.DrawImageUnscaled(settled,(int)Offset.X,(int)Offset.Y);
        }else{var state=e.Graphics.Save();e.Graphics.TranslateTransform(Offset.X,Offset.Y);RenderWord(e.Graphics);e.Graphics.Restore(state);}
    }
    protected override void Dispose(bool disposing){if(disposing){if(settled!=null){settled.Dispose();settled=null;}GraphicsResources.Set(wordOwner,"decor",0);}base.Dispose(disposing);}
    void RenderWord(Graphics g){g.SmoothingMode=SmoothingMode.AntiAlias;float size=StageColors?Math.Min(112*scale,Math.Max(38*scale,Width*.135F)):66*scale;float spacing=StageColors?-size*.075F:-3*scale;float measured;using(var measure=MotionPaint.TextPath(word,size,spacing,out measured)){if(measured>Width){float ratio=Width/Math.Max(1,measured);size*=ratio;spacing*=ratio;}}float total;using(var whole=MotionPaint.TextPath(word,size,spacing,out total)){
        var bounds=whole.GetBounds();float x=(StageColors?(Width-bounds.Width)/2-bounds.Left:0) ,y=(Height-bounds.Height)/2-bounds.Top;
        if(!StageColors){using(var brush=new SolidBrush(Theme.Text)){var saved=g.Save();g.TranslateTransform(x,y);g.FillPath(brush,whole);g.Restore(saved);}}
        float offset=0;for(int i=0;i<word.Length;i++){float advance;using(var glyph=MotionPaint.TextPath(word[i].ToString(),size,0,out advance)){
            Color color=StageColors?(Running&&i==1?Theme.Accent:(i+1)%3==0?MotionPaint.Hex("#3f2c19"):Theme.Text):i>=word.Length-Suffix.Length?Theme.Accent:Theme.Text;
            if(glyph.PointCount>0&&(StageColors||color==Theme.Accent)){float progress=MotionPaint.Reduced||Program.TestMode?1:MotionPaint.Ease((float)(changed.Elapsed.TotalMilliseconds-(word.Length<=1?0:i*60.0/(word.Length-1)))/160,.17F,.85F,.28F,1);var matrix=new Matrix();matrix.Translate(x+offset,y+(1-progress)*35*scale);if(StageColors&&progress<1){var boundsGlyph=glyph.GetBounds();glyph.Flatten(null,.2F);var points=glyph.PathPoints;var types=glyph.PathTypes;float angle=(float)((-15*(1-progress))*Math.PI/180),originX=boundsGlyph.Left+boundsGlyph.Width/2,originY=boundsGlyph.Top+boundsGlyph.Height/2;for(int n=0;n<points.Length;n++){float dy=points[n].Y-originY,z=dy*(float)Math.Sin(angle),factor=1000*scale/(1000*scale-z);points[n]=new PointF(originX+(points[n].X-originX)*factor,originY+dy*(float)Math.Cos(angle)*factor);}using(var projected=new GraphicsPath(points,types,FillMode.Winding)){glyph.Reset();glyph.FillMode=FillMode.Winding;glyph.AddPath(projected,false);}}glyph.Transform(matrix);matrix.Dispose();using(var brush=new SolidBrush(Color.FromArgb((int)(progress*255),color)))g.FillPath(brush,glyph);}offset+=advance+spacing;
        }}
    }}
}

sealed class OperationSurface : BufferedPanel {
    float scale=1;protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Width;base.ScaleControl(factor,specified);}
    readonly TrackedWord word=new TrackedWord{Word="SAVING",Suffix="ING"};readonly Label phase=Theme.Label("",10,Theme.Muted),kind=Theme.Label("СОЗДАНИЕ СНИМКА",9,Theme.Muted);readonly Panel bar=new BufferedPanel();
    readonly Stopwatch transition=new Stopwatch();readonly Stopwatch painted=Stopwatch.StartNew();double from,current,target;bool determinate,finished;readonly Stopwatch unknown=Stopwatch.StartNew();
    public double DisplayProgress{get{return current;}}public bool Determinate{get{return determinate;}}
    Form motionOwner;
    public OperationSurface(){BackColor=Theme.Bg;Height=220;Controls.Add(word);Controls.Add(kind);Controls.Add(phase);Controls.Add(bar);kind.Font=phase.Font=MotionPaint.Font("Courier New",9);bar.Paint+=PaintProgress;VisibleChanged+=OwnerMotionChanged;}
    void OwnerMotionChanged(object sender,EventArgs args){Clock();}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);motionOwner=FindForm();if(motionOwner!=null){motionOwner.Resize+=OwnerMotionChanged;motionOwner.VisibleChanged+=OwnerMotionChanged;}Clock();}
    protected override void OnHandleDestroyed(EventArgs e){if(motionOwner!=null){motionOwner.Resize-=OwnerMotionChanged;motionOwner.VisibleChanged-=OwnerMotionChanged;motionOwner=null;}AnimationClock.Cancel(this);base.OnHandleDestroyed(e);}
    protected override void OnPaintBackground(PaintEventArgs e){if(Width>0&&Height>0)using(var fill=new LinearGradientBrush(ClientRectangle,MotionPaint.Hex("#e9e1d5"),MotionPaint.Hex("#faf6ee"),130))e.Graphics.FillRectangle(fill,ClientRectangle);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(var pen=new Pen(MotionPaint.Hex("#c3b5a2")))e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1);}
    public void Begin(string action){kind.Text=action=="create"?"СОЗДАНИЕ СНИМКА":"ОПЕРАЦИЯ СО СНИМКОМ";word.Word=action=="create"?"SAVING":"WORKING";word.Suffix="ING";phase.Text="Ожидание подтверждения операции";finished=false;determinate=false;from=current=target=0;Clock();}
    public void Report(string text,long? done,long? total){phase.Text=text??"";determinate=done.HasValue&&total.HasValue&&total>0;from=current;target=determinate?Math.Max(0,Math.Min(1,(double)done.Value/total.Value)):0;transition.Restart();if(MotionPaint.Reduced)current=target;Clock();bar.Invalidate();}
    public void Finish(string state){finished=true;if(state=="completed"){determinate=true;from=current;target=1;transition.Restart();word.Word="SAVED";word.Suffix="ED";}else{word.Word=state=="cancelled"?"CANCELLED":"ERROR";word.Suffix="";}Clock();bar.Invalidate();}
    public void Message(string text){phase.Text=text;}
    void Clock(){AnimationClock.Run(this,motion=>{if(!motion){current=target;bar.Invalidate();word.Invalidate();return false;}Advance();return word.Animating||determinate&&Math.Abs(current-target)>.00001||!determinate&&!finished;});}
    void Advance(){current=from+(target-from)*MotionPaint.Ease((float)transition.Elapsed.TotalMilliseconds/180);if(determinate&&transition.ElapsedMilliseconds>=180)current=target;if(painted.ElapsedMilliseconds>=33||determinate&&current==target){painted.Restart();bar.Invalidate();word.Invalidate();}}
    void PaintProgress(object sender,PaintEventArgs e){e.Graphics.Clear(MotionPaint.Hex("#d7cdbf"));using(var ink=new SolidBrush(Theme.Accent)){if(determinate)e.Graphics.FillRectangle(ink,0,0,(float)(bar.Width*current),bar.Height);else if(!finished){float offset=MotionPaint.Reduced?0:(float)(unknown.Elapsed.TotalSeconds%1.5/1.5);e.Graphics.FillRectangle(ink,(bar.Width+bar.Width*.25F)*offset-bar.Width*.25F,0,bar.Width*.25F,bar.Height);}}}
    protected override void OnLayout(LayoutEventArgs e){base.OnLayout(e);if(word==null)return;int pad=(int)(24*scale),width=Math.Max(1,Width-2*pad);kind.SetBounds(pad,(int)(32*scale),width,(int)(18*scale));word.SetBounds(pad,(int)(56*scale),width,(int)(86*scale));bar.SetBounds(pad,(int)(155*scale),width,Math.Max(1,(int)(4*scale)));phase.SetBounds(pad,(int)(178*scale),width,(int)(24*scale));}
    protected override void Dispose(bool disposing){if(disposing)AnimationClock.Cancel(this);base.Dispose(disposing);}
}

sealed class ReferenceOperationWindow : MotionDialog {
    readonly OperationSurface surface=new OperationSurface{Dock=DockStyle.Top};
    readonly FlowLayoutPanel phases=Theme.Flow(true);readonly List<string> observed=new List<string>();
    readonly Button cancel,close;readonly Func<System.Threading.Tasks.Task> requestCancel;bool active=true;
    public ReferenceOperationWindow(string scope,Func<System.Threading.Tasks.Task> requestCancel){
        this.requestCancel=requestCancel;Text="Операция";Theme.Form(this,700,660);
        var body=new BufferedPanel();var history=new ScrollSurface(phases){Dock=DockStyle.Fill,Padding=new Padding(0,20,0,0)};body.Controls.Add(history);body.Controls.Add(surface);
        var buttons=Theme.Flow();cancel=Theme.Button("Отменить операцию",async()=>{cancel.Enabled=false;await this.requestCancel();},false,true);cancel.Enabled=false;
        close=Theme.Button("Закрыть",Close);close.Enabled=false;buttons.Controls.Add(cancel);buttons.Controls.Add(close);Theme.Dialog(this,body,buttons,"Операция",scope,"ОПЕРАЦИЯ");
        FormClosing+=(s,e)=>{if(active)e.Cancel=true;};surface.Begin("create");
    }
    public void Progress(SnapshotUiProgress value,bool canCancel){
        if(IsDisposed)return;surface.Report(value.Phase,value.Done,value.Total);cancel.Enabled=active&&canCancel;
        if(!String.IsNullOrEmpty(value.Phase)&&(observed.Count==0||observed[observed.Count-1]!=value.Phase)){observed.Add(value.Phase);DrawPhases();}
    }
    void DrawPhases(){
        foreach(Control control in phases.Controls.Cast<Control>().ToArray())control.Dispose();phases.Controls.Clear();
        for(int i=0;i<observed.Count;i++){var row=new BufferedPanel{Width=Math.Max(200,ClientSize.Width-60),Height=30};var icon=new PictureBox{Width=18,Height=18,Location=new Point(0,2),Visible=i<observed.Count-1||!active};if(i<observed.Count-1||!active)icon.Image=ReferenceIcons.Image("dialog:check",14,Theme.Accent);else{var index=Theme.Label((i+1).ToString("00"),10,Theme.Accent);index.Font=MotionPaint.Font("Courier New",11);index.Location=new Point(0,2);row.Controls.Add(index);}var label=Theme.Label(observed[i],11,i==observed.Count-1?Theme.Text:Theme.Muted);label.Font=MotionPaint.Font("Courier New",11);label.Location=new Point(26,2);label.MaximumSize=new Size(Math.Max(100,row.Width-30),0);row.Controls.Add(icon);row.Controls.Add(label);phases.Controls.Add(row);icon.Disposed+=(s,e)=>{if(icon.Image!=null)icon.Image.Dispose();};}
    }
    public void Finish(string state,string message){if(IsDisposed)return;active=false;surface.Message(message);surface.Finish(state);cancel.Enabled=false;close.Enabled=true;DrawPhases();}
}

sealed class ReferenceDataCard : Control {
    string[] labels=new string[0],values=new string[0];
    public ReferenceDataCard(){SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);Height=190;}
    public void Present(string[] labels,string[] values){this.labels=labels;this.values=values;Height=(int)((40+labels.Length*38)*DeviceDpi/96F);AccessibleName=String.Join("; ",labels.Select((label,index)=>label+": "+values[index]));Invalidate();}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);float scale=DeviceDpi/96F;var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;using(var path=Drawing.Rounded(new Rectangle(0,0,Width-1,Height-1),(int)(10*scale)))using(var fill=new SolidBrush(MotionPaint.Hex("#faf6ee")))using(var pen=new Pen(MotionPaint.Hex("#c7bcac"))){g.FillPath(fill,path);g.DrawPath(pen,path);}for(int i=0;i<labels.Length;i++){int y=(int)((27+i*38)*scale),pad=(int)(20*scale);using(var font=MotionPaint.Font("Segoe UI",11*scale))TextRenderer.DrawText(g,labels[i],font,new Rectangle(pad,y,Width/2-pad,(int)(24*scale)),Theme.Muted,TextFormatFlags.EndEllipsis);using(var font=MotionPaint.Font("Courier New",11*scale))TextRenderer.DrawText(g,values[i],font,new Rectangle(Width/2,y,Math.Max(1,Width/2-pad),(int)(24*scale)),Theme.Text,TextFormatFlags.Right|TextFormatFlags.EndEllipsis);if(i<labels.Length-1)using(var pen=new Pen(MotionPaint.Hex("#ddd4c7")))g.DrawLine(pen,pad,y+29*scale,Width-pad,y+29*scale);}}
}

sealed class ReferenceServiceTile : PolarButton {
    readonly string icon,heading,description;
    public ReferenceServiceTile(string icon,string heading,string description,Action action){this.icon=icon;this.heading=heading;this.description=description;Text=heading;AccessibleName=heading;AccessibleDescription=description;Dock=DockStyle.Fill;Margin=new Padding(0,0,12,12);Click+=(s,e)=>action();}
    protected override void OnPaint(PaintEventArgs e){
        var g=e.Graphics;g.Clear(Parent==null?Theme.Bg:Parent.BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;float scale=DeviceDpi/96F;
        using(var path=Drawing.Rounded(new Rectangle(0,0,Width-1,Height-1),(int)(10*scale)))using(var brush=new SolidBrush(MotionPaint.Hex("#ede7dd")))using(var pen=new Pen(MotionPaint.Hex("#c5baaa"))){g.FillPath(brush,path);g.DrawPath(pen,path);}
        Color ink=Enabled?Theme.Text:MotionPaint.Mix(Theme.Bg,Theme.Text,.35F),accent=Enabled?Theme.Accent:MotionPaint.Mix(Theme.Bg,Theme.Accent,.35F);
        ReferenceIcons.Draw(g,icon,new RectangleF(22*scale,22*scale,23*scale,23*scale),accent);
        using(var font=MotionPaint.Font("Segoe UI",14*scale,FontStyle.Bold))TextRenderer.DrawText(g,heading,font,new Rectangle((int)(22*scale),(int)(65*scale),Width-(int)(44*scale),(int)(24*scale)),ink,TextFormatFlags.EndEllipsis);
        using(var font=MotionPaint.Font("Courier New",10*scale))TextRenderer.DrawText(g,description,font,new Rectangle((int)(22*scale),(int)(88*scale),Width-(int)(44*scale),(int)(20*scale)),Enabled?Theme.Muted:ink,TextFormatFlags.EndEllipsis);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-4,-4),Theme.Accent,BackColor);
    }
}

sealed class ReferenceBrand : Control {
    float scale=1;protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Width;base.ScaleControl(factor,specified);}
    public float VisualCenterY {get{float width;using(var path=MotionPaint.TextPath("qvm",47,-5,out width))return path.GetBounds().Height*scale/2;}}
    public ReferenceBrand(){Dock=DockStyle.Left;Width=320;AccessibleName="QVM";SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer,true);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);var g=e.Graphics;var saved=g.Save();g.ScaleTransform(scale,scale);g.SmoothingMode=SmoothingMode.AntiAlias;float width;using(var path=MotionPaint.TextPath("qvm",47,-5,out width))using(var ink=new SolidBrush(Theme.Text)){var bounds=path.GetBounds();using(var transform=new Matrix()){transform.Translate(0,-bounds.Top);path.Transform(transform);}g.FillPath(ink,path);using(var font=MotionPaint.Font("Arial",21,FontStyle.Bold))using(var accent=new SolidBrush(Theme.Accent))g.DrawString("\u2197",font,accent,width+6,-1);}
        using(var pen=new Pen(MotionPaint.Hex("#b9ad9b")))g.DrawLine(pen,width+36,1,width+36,33);g.Restore(saved);
    }
}

sealed class ConfigurationDisclosure : BufferedPanel {
    readonly Button summary;
    readonly Control inspector;
    readonly ToolStripDropDown popup=new ToolStripDropDown{Padding=Padding.Empty,Margin=Padding.Empty,AutoSize=false,DropShadowEnabled=false};
    readonly ToolStripControlHost host;
    float scale=1F;
    public bool Expanded {get{return popup.Visible;}set{
        if(value==Expanded)return;if(!value){popup.Close();return;}
        var card=inspector as ConfigurationInspector;int width=(int)(300*scale);
        inspector.Width=width;inspector.PerformLayout();
        int height=Math.Min((int)(500*scale),card==null?(int)(350*scale):card.CardHeight);
        popup.Size=new Size(width,height);host.Size=popup.Size;inspector.Size=popup.Size;
        using(var path=Drawing.Rounded(new Rectangle(0,0,width,height),12)){var old=popup.Region;popup.Region=new Region(path);if(old!=null)old.Dispose();}
        inspector.Visible=true;popup.Show(this,new Point(Width-width,Height));if(ReferenceDialogChrome.SmoothCorners(popup)){var previous=popup.Region;popup.Region=null;if(previous!=null)previous.Dispose();}
    }}
    public ConfigurationDisclosure(Control inspector){
        this.inspector=inspector;BackColor=Theme.Bg;AccessibleName="Параметры";
        summary=Theme.Button("Параметры",()=>Expanded=!Expanded);summary.AutoSize=false;summary.MinimumSize=Size.Empty;summary.Margin=Padding.Empty;summary.AccessibleName="Открыть параметры";((PolarButton)summary).IconName="sliders-horizontal";summary.Font=MotionPaint.Font("Arial",11);
        host=new ToolStripControlHost(inspector){AutoSize=false,Margin=Padding.Empty,Padding=Padding.Empty};popup.Items.Add(host);Controls.Add(summary);
        Size=new Size(205,40);
    }
    protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Width;base.ScaleControl(factor,specified);if(inspector!=null)inspector.Scale(factor);}
    protected override void OnLayout(LayoutEventArgs e){
        base.OnLayout(e);if(summary!=null)summary.Bounds=ClientRectangle;
    }
    protected override void Dispose(bool disposing){if(disposing)popup.Dispose();base.Dispose(disposing);}
}

sealed class SelectionHeader : BufferedPanel {
    readonly Control heading;
    readonly ConfigurationDisclosure configuration;
    float scale=1F;
    public SelectionHeader(Control heading,Control inspector){
        this.heading=heading;configuration=new ConfigurationDisclosure(inspector);Controls.Add(heading);Controls.Add(configuration);BackColor=Theme.Bg;
    }
    protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Width;base.ScaleControl(factor,specified);}
    protected override void OnLayout(LayoutEventArgs e){
        base.OnLayout(e);if(heading==null)return;
        bool narrow=Width<550*scale;int width=narrow?Width:(int)(205*scale);
        int top=narrow?(int)(65*scale):0;heading.SetBounds(0,0,Math.Max(1,Width-(narrow?0:width+(int)(20*scale))),Math.Min(Height,(int)(66*scale)));
        configuration.SetBounds(Math.Max(0,Width-width),top,width,(int)(40*scale));configuration.PerformLayout();configuration.BringToFront();
    }
}

sealed class StateStage : BufferedPanel {
    readonly Label caption=Theme.Label("QEMU",8,Theme.Muted),metadata=Theme.Label("",9,Theme.Muted),description=Theme.Label("",12,Theme.Muted),marker=Theme.Label("",8,Theme.Muted),footer=Theme.Label("СОСТОЯНИЕ МАШИНЫ",8,Theme.Muted);
    int ActionRadius{get{return dock.Height>(int)(60*scale)?(int)(16*scale):dock.Height/2;}}
    readonly TrackedWord word=new TrackedWord{StageColors=true};readonly Control actions;readonly BufferedPanel dock=new BufferedPanel();readonly Control legacy;
    Bitmap backdropFrame;bool preparingBackground,backgroundDisposed,backgroundQueued;string backgroundKey,requestedBackground;
    System.Threading.CancellationTokenSource backgroundCancellation;readonly string backgroundOwner=GraphicsResources.Owner("state-frame");internal bool BackgroundReady{get{return backdropFrame!=null&&backgroundKey==requestedBackground&&BackgroundError==null;}}
    internal string BackgroundError{get;private set;}PointF pointerFrom,pointerTarget,pointerWord,pointerOrb;readonly Stopwatch pointerTime=Stopwatch.StartNew();
    string id="",memory="",cpu="",accelerator="",phase="checking";bool stand,arranging;float scale=1;
    public StateStage(Control state,Control actions){
        this.legacy=state;this.actions=actions;BackColor=Theme.Bg;state.Visible=false;Controls.Add(state);Controls.Add(caption);Controls.Add(marker);Controls.Add(word);Controls.Add(description);Controls.Add(metadata);Controls.Add(dock);Controls.Add(footer);dock.Controls.Add(actions);dock.BackColor=Color.Transparent;actions.BackColor=dock.BackColor;foreach(Control child in actions.Controls)child.BackColor=dock.BackColor;dock.Paint+=(s,e)=>{e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(var path=Drawing.Rounded(new Rectangle(0,0,dock.Width-1,dock.Height-1),ActionRadius))using(var fill=new SolidBrush(MotionPaint.Hex("#f9f6ee")))using(var pen=new Pen(MotionPaint.Hex("#c4baab"))){e.Graphics.FillPath(fill,path);e.Graphics.DrawPath(pen,path);}};
        foreach(var text in new[]{caption,marker,metadata,footer})text.Font=MotionPaint.Font("Courier New",9);
        description.Font=MotionPaint.Font("Arial",12);description.AutoSize=metadata.AutoSize=caption.AutoSize=marker.AutoSize=footer.AutoSize=false;description.TextAlign=metadata.TextAlign=ContentAlignment.MiddleCenter;marker.TextAlign=ContentAlignment.MiddleRight;caption.TextAlign=footer.TextAlign=ContentAlignment.MiddleLeft;
        foreach(var text in new[]{caption,marker,metadata,footer,description})text.BackColor=Color.Transparent;
        actions.Layout+=(s,e)=>{if(!arranging&&Visible)PerformLayout();};MouseMove+=(s,e)=>Clock();word.MouseMove+=(s,e)=>Clock();MouseLeave+=(s,e)=>Clock();word.MouseLeave+=(s,e)=>Clock();VisibleChanged+=(s,e)=>{Clock();if(Visible){if(backdropFrame!=null)GraphicsResources.Set(backgroundOwner,"frame",GraphicsResources.ImageBytes(backdropFrame));PerformLayout();RequestBackground();}else if(backdropFrame!=null&&!GraphicsResources.TryDecor(backgroundOwner,GraphicsResources.ImageBytes(backdropFrame))){backdropFrame.Dispose();backdropFrame=null;backgroundKey=null;GraphicsResources.Set(backgroundOwner,"frame",0);}};HandleCreated+=(s,e)=>{Clock();RequestBackground();};
        Present("checking");
    }
    void UpdatePointer(){
        Point p=PointToClient(Cursor.Position);PointF target=ClientRectangle.Contains(p)?new PointF((p.X/(float)Math.Max(1,Width)-.5F)*9*scale,(p.Y/(float)Math.Max(1,Height)-.5F)*6*scale):PointF.Empty;
        if(target!=pointerTarget){pointerFrom=pointerOrb;pointerTarget=target;pointerTime.Restart();}
        float a=MotionPaint.Ease((float)pointerTime.Elapsed.TotalMilliseconds/110),b=MotionPaint.Ease((float)pointerTime.Elapsed.TotalMilliseconds/180);
        pointerWord=new PointF((pointerFrom.X+(pointerTarget.X-pointerFrom.X)*a)*.45F,(pointerFrom.Y+(pointerTarget.Y-pointerFrom.Y)*a)*.45F);pointerOrb=new PointF(pointerFrom.X+(pointerTarget.X-pointerFrom.X)*b,pointerFrom.Y+(pointerTarget.Y-pointerFrom.Y)*b);word.Offset=pointerWord;
    }
    void Clock(){AnimationClock.Run(this,motion=>{if(!motion){pointerWord=pointerOrb=pointerTarget=PointF.Empty;word.Offset=PointF.Empty;word.Invalidate();return false;}UpdatePointer();word.Invalidate();return word.Animating||pointerTime.ElapsedMilliseconds<200;});}
    protected override void ScaleControl(SizeF factor,BoundsSpecified specified){scale*=factor.Width;base.ScaleControl(factor,specified);}
    public void Present(string value,bool isStand=false){
        string next=(value??"checking").ToLowerInvariant();if(word.Word.Length>0&&stand==isStand&&phase==next)return;stand=isStand;phase=next;string text=phase=="prelaunch"||phase=="starting"?"STARTING":phase=="shutdown"||phase=="stopping"?"STOPPING":phase=="recovery-required"?"RECOVERY":phase=="no-qmp"?"NO QMP":phase=="unknown"?"CHECKING":phase.ToUpperInvariant();
        word.Word=text;word.Running=phase=="running";marker.Text=phase=="running"?"Работает":phase=="paused"?"На паузе":phase=="stopped"?(stand?"Выключен":"Выключена"):Theme.State(phase);marker.ForeColor=Theme.StateColor(phase);
        description.Text=phase=="running"?"Машина работает. Для работы с гостевой системой открой консоль.":phase=="paused"?"Машина на паузе. Продолжи текущий сеанс.":phase=="stopped"?(stand?"Запусти стенд с настроенными машинами или выбери машину для просмотра параметров.":"Машина готова к запуску с текущими параметрами."):phase=="error"?"Запуск завершился ошибкой. Проверь параметры и журнал событий.":phase=="saved"?"Сеанс сохранён. Восстанови его или запусти новый.":phase=="prelaunch"||phase=="starting"?"Подготовка гостевой системы к запуску.":phase=="shutdown"||phase=="stopping"?"Ожидание завершения работы гостевой системы.":legacy.Text;
        caption.Visible=marker.Visible=footer.Visible=false;footer.Text="СОСТОЯНИЕ МАШИНЫ";UpdateContext();Clock();RequestBackground();PerformLayout();Invalidate(true);
    }
    public void Context(string id,string memory,string cpu){bool selectionChanged=this.id!=id;this.id=id;this.memory=memory;this.cpu=cpu;UpdateContext();if(selectionChanged){word.Replay();Clock();}}
    public void Acceleration(string value){accelerator=value;UpdateContext();}
    void UpdateContext(){caption.Text=(stand?"СТЕНД / ":"QEMU / ")+id;metadata.Text="ОЗУ: "+memory+"     CPU: "+cpu+(stand||accelerator.Length==0?"":"     "+accelerator);}
    Size ArrangeActions(Control group,int maximum){
        group.SuspendLayout();try{group.MaximumSize=Size.Empty;group.MinimumSize=Size.Empty;group.AutoSize=false;group.Dock=DockStyle.None;var flow=group as FlowLayoutPanel;bool vertical=flow!=null&&flow.FlowDirection==FlowDirection.TopDown;int x=0,y=0,line=0,width=0;
            foreach(Control child in group.Controls){if(!child.Visible)continue;Size size=child is FlowLayoutPanel?ArrangeActions(child,maximum):child.GetPreferredSize(new Size(maximum,0));size.Width=Math.Min(maximum,Math.Max(1,size.Width));size.Height=Math.Max(1,size.Height);int need=size.Width+child.Margin.Horizontal;if(!vertical&&x>0&&x+need>maximum){y+=line;x=0;line=0;}child.Size=size;if(vertical){width=Math.Max(width,need);y+=size.Height+child.Margin.Vertical;}else{x+=need;width=Math.Max(width,x);line=Math.Max(line,size.Height+child.Margin.Vertical);}}
            var result=new Size(Math.Max(1,width),Math.Max(1,y+(vertical?0:line)));group.Size=result;return result;
        }finally{group.ResumeLayout();}
    }
    protected override void OnLayout(LayoutEventArgs e){
        base.OnLayout(e);if(dock==null||actions==null||arranging)return;arranging=true;try{int pad=(int)(19*scale),head=0,foot=0;caption.SetBounds(pad,0,Math.Max(1,Width/2-pad),head);marker.SetBounds(Width/2,0,Math.Max(1,Width/2-pad),head);footer.SetBounds(pad,Height-foot,Math.Max(1,Width-pad*2),foot);
        int available=Math.Max(1,Width-(int)(48*scale));var size=ArrangeActions(actions,Math.Max(1,available-(int)(16*scale)));int dw=Math.Min(available,size.Width+(int)(16*scale)),dh=Math.Max((int)(49*scale),size.Height+(int)(16*scale));
        dock.SetBounds((Width-dw)/2,Height-foot-dh-(int)(22*scale),dw,dh);actions.SetBounds((int)(8*scale),(int)(8*scale),Math.Max(1,dw-(int)(16*scale)),Math.Max(1,dh-(int)(16*scale)));
        if(dock.Region!=null){var old=dock.Region;dock.Region=null;old.Dispose();}
        metadata.SetBounds(pad,dock.Top-(int)(44*scale),Math.Max(1,Width-pad*2),(int)(24*scale));int space=Math.Max(50,metadata.Top-head);int wordHeight=Math.Min((int)(112*scale),Math.Max((int)(38*scale),space-(int)(65*scale)));int group=wordHeight+(int)(60*scale),top=head+Math.Max(0,(space-group)/2);word.SetBounds(pad,top,Math.Max(1,Width-pad*2),wordHeight);description.SetBounds(Math.Max(pad,(Width-(int)(440*scale))/2),word.Bottom+(int)(18*scale),Math.Min((int)(440*scale),Math.Max(1,Width-pad*2)),(int)(42*scale));}finally{arranging=false;}RequestBackground();
    }
    protected override void OnPaintBackground(PaintEventArgs e){
        if(Width<1||Height<1)return;
        if(backdropFrame!=null){if(backdropFrame.Size==Size)e.Graphics.DrawImageUnscaled(backdropFrame,0,0);else e.Graphics.DrawImage(backdropFrame,ClientRectangle);}else PaintGradient(e.Graphics,ClientRectangle);
    }
    void RequestBackground(){
        if(backgroundDisposed||!Visible||!IsHandleCreated||Width<1||Height<1||word==null)return;
        string key=Size+"|"+scale+"|"+word.Word+"|"+MotionPaint.Reduced;
        if(preparingBackground&&requestedBackground!=key&&backgroundCancellation!=null)backgroundCancellation.Cancel();requestedBackground=key;
        if(preparingBackground||backgroundKey==key||backgroundQueued)return;backgroundQueued=true;
        BeginInvoke((Action)(()=>{backgroundQueued=false;PrepareBackground();}));
    }
    async void PrepareBackground(){
        if(backgroundDisposed||!Visible||!IsHandleCreated||Width<1||Height<1||word==null)return;Size size=Size;float factor=scale;string text=word.Word;bool reduced=MotionPaint.Reduced;
        string key=size+"|"+factor+"|"+text+"|"+reduced;if(preparingBackground&&requestedBackground!=key&&backgroundCancellation!=null)backgroundCancellation.Cancel();requestedBackground=key;if(preparingBackground||backgroundKey==key)return;preparingBackground=true;var source=new System.Threading.CancellationTokenSource();backgroundCancellation=source;var token=source.Token;
        Bitmap image=null;try{image=await System.Threading.Tasks.Task.Run(()=>GraphicsResources.WithCancellation(()=>BuildBackground(size,factor,text,reduced),token),token);
            if(!backgroundDisposed&&requestedBackground==key&&Visible){var previous=backdropFrame;backdropFrame=image;image=null;backgroundKey=key;BackgroundError=null;GraphicsResources.Set(backgroundOwner,"frame",GraphicsResources.ImageBytes(backdropFrame));if(previous!=null)previous.Dispose();Invalidate();}
        }catch(OperationCanceledException){}catch(Exception error){BackgroundError=error.GetType().Name+": "+error.Message;backgroundKey=key;}
        finally{if(image!=null)image.Dispose();backgroundCancellation=null;source.Dispose();preparingBackground=false;if(!backgroundDisposed&&Visible&&backgroundKey!=requestedBackground)RequestBackground();}
    }
    static void PaintGradient(Graphics g,Rectangle bounds){using(var fill=new LinearGradientBrush(bounds,Theme.Bg,Theme.Bg,135)){fill.InterpolationColors=new ColorBlend{Colors=new[]{MotionPaint.Hex("#eae5dc"),Theme.Bg,MotionPaint.Hex("#e9e2d6")},Positions=new[]{0F,.5F,1F}};g.FillRectangle(fill,bounds);}}
    static Bitmap BuildBackground(Size size,float scale,string word,bool reduced){
        int width=size.Width,height=size.Height;var frame=new Bitmap(width,height,PixelFormat.Format32bppPArgb);
        try{using(var mask=new Bitmap(width,height,PixelFormat.Format32bppPArgb)){
            using(var graphics=Graphics.FromImage(mask))using(var path=new GraphicsPath()){path.AddEllipse(width*(.83F-.62F*.65F/2),height*(.565F-.75F*.65F/2),width*.62F*.65F,height*.75F*.65F);using(var glow=new PathGradientBrush(path)){glow.CenterColor=Color.FromArgb(36,255,59,0);glow.SurroundColors=new[]{Color.Transparent};graphics.FillPath(glow,path);}}
            using(var orb=MotionPaint.BlurMask(mask,17*scale,Theme.Accent)){
                mask.Dispose();
                float px=Math.Min(140*scale,Math.Max(60*scale,width*.17F));
                using(var ghostA=MotionPaint.Ghost(word,px,-px*.085F,Color.FromArgb(reduced?15:23,255,59,0),4*scale))using(var ghostB=MotionPaint.Ghost(word,px,-px*.085F,Color.FromArgb(reduced?15:14,255,59,0),12*scale))using(var g=Graphics.FromImage(frame)){
                    PaintGradient(g,new Rectangle(Point.Empty,size));g.DrawImageUnscaled(orb,0,0);
                    var saved=g.Save();g.TranslateTransform(width*.08F+ghostA.Width/2-8*scale,height*.25F+ghostA.Height/2+20*scale);g.RotateTransform(-7);g.DrawImageUnscaled(ghostA,-ghostA.Width/2,-ghostA.Height/2);g.Restore(saved);
                    saved=g.Save();g.TranslateTransform(width*.02F+ghostB.Width/2+35*scale,height*.38F+ghostB.Height/2-6*scale);g.RotateTransform(5);g.DrawImageUnscaled(ghostB,-ghostB.Width/2,-ghostB.Height/2);g.Restore(saved);
                }
            }
        }return frame;}catch{frame.Dispose();throw;}
    }
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);if(Width<1||Height<1)return;using(var path=Drawing.Rounded(ClientRectangle,(int)(12*scale))){var old=Region;Region=new Region(path);if(old!=null)old.Dispose();}}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(var path=Drawing.Rounded(new Rectangle(0,0,Width-1,Height-1),(int)(12*scale)))using(var pen=new Pen(MotionPaint.Hex("#bcb3a5")))e.Graphics.DrawPath(pen,path);}
    protected override void Dispose(bool disposing){if(disposing){backgroundDisposed=true;if(backgroundCancellation!=null)backgroundCancellation.Cancel();AnimationClock.Cancel(this);if(backdropFrame!=null){backdropFrame.Dispose();backdropFrame=null;}GraphicsResources.Set(backgroundOwner,"frame",0);}base.Dispose(disposing);}
}
