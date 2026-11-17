using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

// Only QemuM dialogs defer a close. WinForms still owns the modal loop and disposal.
class SmoothForm : Form {
    public SmoothForm(){DoubleBuffered=true;SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.ResizeRedraw,true);}
    protected override CreateParams CreateParams{get{var value=base.CreateParams;value.ExStyle|=0x02000000;return value;}}
}

class MotionDialog : SmoothForm {
    internal DialogEntrance Presentation { get; set; }
    bool checkingClose, closeApproved, immediateClose, writing;
    DialogResult approvedResult;
    internal bool ClosingWithMotion { get { return closeApproved; } }

    protected override void OnFormClosing(FormClosingEventArgs args) {
        if (closeApproved) {
            // WinForms re-enters on repeated WM_CLOSE and at final modal completion.
            if (!immediateClose && Presentation.Exiting && IsUserClose(args.CloseReason)) args.Cancel = true;
            return;
        }
        if (checkingClose || writing) { args.Cancel = true; return; }
        checkingClose = true;
        try { base.OnFormClosing(args); }
        finally { checkingClose = false; }
        // Let the native owner cascade run once, including every owned form's veto.
        if (args.Cancel || immediateClose || OwnedForms.Length != 0 ||
            Presentation == null || !IsUserClose(args.CloseReason) ||
            Modal && DialogResult == DialogResult.None) return;

        var result = DialogResult;
        if (!Presentation.BeginExit(CompleteClose)) return;
        approvedResult = result;
        closeApproved = true;
        args.Cancel = true;
    }

    internal void CloseImmediately() {
        immediateClose = true;
        try { Close(); }
        finally { immediateClose = false; }
    }

    internal void RunWrite(MainWindow main, string message, Func<object> work,
        Action<object> done, Action<Exception> failed = null) {
        if (writing || closeApproved || main.Busy) return;
        var enabled = Controls.Cast<Control>().Where(control => control.Enabled).ToArray();
        writing = true;
        foreach (var control in enabled) control.Enabled = false;
        Action release = () => {
            writing = false;
            if (!IsDisposed) foreach (var control in enabled) control.Enabled = true;
        };
        try {
            main.Run(message, work, result => {
                release();
                if (!IsDisposed) done(result);
            }, error => {
                release();
                if (!IsDisposed && failed != null) failed(error);
            });
            if (!main.Busy) release();
        } catch {
            release();
            throw;
        }
    }

    static bool IsUserClose(CloseReason reason) {
        return reason == CloseReason.UserClosing || reason == CloseReason.None;
    }

    void CompleteClose() {
        if (IsDisposed || Disposing || !Visible) return;
        if (Modal && approvedResult != DialogResult.None) DialogResult = approvedResult;
        else Close();
    }

    protected override void OnVisibleChanged(EventArgs args) {
        if (Visible) closeApproved = false;
        base.OnVisibleChanged(args);
    }

    protected override void OnFormClosed(FormClosedEventArgs args) {
        if (Presentation != null) Presentation.Stop();
        base.OnFormClosed(args);
    }
}

// Native HWNDs and layout stay live at their final size throughout the transition.
sealed class DialogEntrance : IDisposable, IMessageFilter {
    internal const int Duration = 300, ExitDuration = 170;
    readonly Form form;
    readonly bool animate;
    readonly Stopwatch time = new Stopwatch();
    Point target;
    bool active, disposed, shown, pending, presented, liveInput;
    int generation;
    internal bool Active { get { return active; } }
    internal bool Exiting { get; private set; }
    internal float Progress { get; private set; }
    internal static long CapturedFrames{get{return 0;}}
    internal ReferenceHeader Header { get; set; }
    internal event Action Presented;

    public DialogEntrance(Form form, bool animate = true) {
        this.form = form;
        this.animate = animate;
        var dialog = form as MotionDialog;
        if (dialog != null) dialog.Presentation = this;
        if (!Program.TestMode) form.Opacity = 0;
        form.Shown += Shown;
        form.VisibleChanged += Visibility;
        form.ClientSizeChanged += GeometryChanged;
        form.DpiChanged += GeometryChanged;
        form.Disposed += FormDisposed;
    }

    void Shown(object sender, EventArgs args) {
        shown = true;
        if (form.Visible) QueuePresentation();
    }

    void QueuePresentation() {
        int request = ++generation;
        presented = liveInput = false;
        if (Program.TestMode) { Progress = 1; form.Opacity = 1; SignalPresented(); return; }
        pending = true;
        Application.AddMessageFilter(this);
        form.BeginInvoke((Action)(() => Prepare(request)));
    }

    async void Prepare(int request) {
        if (!IsCurrent(request)) return;
        var timing = NetworkOpenTiming.For(form);
        try {
            if (timing != null) timing.Mark("layout_start");
            form.PerformLayout();
            if (timing != null) timing.Mark("first_layout");
            var backdrop = ReferenceDialogBackdrop.WaitFor(form);
            if (Header == null) await backdrop;
            else await Task.WhenAll(backdrop, Header.WhenPrepared());
            if (!IsCurrent(request)) return;
            if (!animate || liveInput || MotionPaint.Reduced || form.WindowState != FormWindowState.Normal) {
                FinishOpening(); return;
            }
            target = form.Location;
            // Fade the live HWND tree. Replacing a bitmap with native edit and
            // button windows caused a second paint and different text rasterization.
            WindowFrame.PaintLive(form);
            if (!IsCurrent(request)) return;
            pending = false;
            active = true;
            time.Restart();
            AnimationClock.Run(form, Advance);
        } catch (OperationCanceledException) {
            if (IsCurrent(request)) FinishOpening();
        } catch (Exception) {
            if (IsCurrent(request) || active) FinishOpening();
        }
    }

    bool IsCurrent(int request) {
        return !disposed && pending && request == generation && form.Visible;
    }

    bool Advance(bool motion) {
        if (!active) return false;
        if (!motion || time.ElapsedMilliseconds >= Duration) { FinishOpening(); return false; }
        Progress = MotionPaint.Ease((float)time.Elapsed.TotalMilliseconds / Duration, .2F, .8F, .2F, 1);
        form.Location = new Point(target.X, target.Y + (int)Math.Round(18F * form.DeviceDpi / 96F * (1-Progress)));
        if (!active) return false;
        bool first = Progress > 0 && !presented;
        form.Opacity = Progress;
        if (first) SignalPresented();
        return active;
    }

    internal bool BeginExit(Action complete) {
        if (disposed || !form.Visible || !form.IsHandleCreated || form.Opacity <= 0 ||
            form.WindowState == FormWindowState.Minimized || Program.TestMode || MotionPaint.Reduced) return false;
        int request = ++generation;
        var initialOpacity = form.Opacity;
        pending = false;
        active = false;
        Exiting = true;
        Application.RemoveMessageFilter(this);
        Application.AddMessageFilter(this);
        double initialBackdrop = ReferenceDialogBackdrop.BeginExit(form);
        time.Restart();
        AnimationClock.Run(form, motion => {
            float progress = motion ? MotionPaint.Ease((float)time.Elapsed.TotalMilliseconds / ExitDuration, .4F, 0, 1, 1) : 1;
            form.Opacity = initialOpacity * (1 - progress);
            ReferenceDialogBackdrop.SetExitOpacity(form, initialBackdrop * (1 - progress));
            if (progress < 1) return true;
            // Let WinForms finish processing the canceled first WM_CLOSE.
            if (form.IsHandleCreated) form.BeginInvoke((Action)(() => {
                if (!Exiting || request != generation) return;
                Exiting = false;
                complete();
            }));
            return false;
        });
        return true;
    }

    public bool PreFilterMessage(ref Message message) {
        if (!active && !pending && !Exiting) return false;
        bool input = message.Msg >= 0x100 && message.Msg <= 0x109 ||
            message.Msg >= 0x200 && message.Msg <= 0x20E && message.Msg != 0x200 ||
            message.Msg >= 0xA1 && message.Msg <= 0xAD;
        if (!input) return false;
        var control = Control.FromChildHandle(message.HWnd);
        if (message.HWnd != form.Handle && (control == null || control.FindForm() != form)) return false;
        if (Exiting) return true;
        // Input during preparation is delivered to the live control. The first
        // visible frame still waits for the owner's prepared background.
        if (active) FinishOpening();
        else liveInput = true;
        return false;
    }

    void SignalPresented() {
        if (presented) return;
        presented = true;
        var timing = NetworkOpenTiming.For(form);
        if (timing != null) timing.Mark("first_visible_frame");
        if (Presented != null) Presented();
    }

    internal void FinishOpening() {
        if (Exiting) return;
        bool restoreLocation = active;
        Stop();
        Progress = 1;
        if (form.IsDisposed || form.Disposing || !form.Visible) return;
        if (restoreLocation) form.Location = target;
        if (!presented) WindowFrame.PaintLive(form);
        form.Opacity = 1;
        SignalPresented();
    }

    void GeometryChanged(object sender, EventArgs args) {
        if (active) FinishOpening();
    }
    void Visibility(object sender, EventArgs args) {
        if (form.Visible) {
            if (shown) QueuePresentation();
            return;
        }
        bool restoreLocation = active;
        Stop();
        if (!form.IsDisposed && !form.Disposing && !Program.TestMode) {
            if (restoreLocation) form.Location = target;
            form.Opacity = 0;
        }
    }
    void FormDisposed(object sender, EventArgs args) { Dispose(); }
    internal void Stop() {
        active = pending = Exiting = false;
        generation++;
        AnimationClock.Cancel(form);
        Application.RemoveMessageFilter(this);
    }
    public void Dispose() {
        if (disposed) return;
        disposed = true;
        Stop();
        form.Shown -= Shown;
        form.VisibleChanged -= Visibility;
        form.ClientSizeChanged -= GeometryChanged;
        form.DpiChanged -= GeometryChanged;
        form.Disposed -= FormDisposed;
        Header = null;
        Presented = null;
    }
}

// Prepare the owner's visual response before expensive dialog constructors or
// backend reads. The completed surface is transferred to the real dialog.
static class DialogLaunch {
    static readonly System.Collections.Generic.Dictionary<Form,Pending> pending=new System.Collections.Generic.Dictionary<Form,Pending>();
    sealed class Pending : IDisposable,IMessageFilter {
        internal readonly Form Owner;
        internal ReferenceBackdropSurface Surface;
        internal bool Executing,Disposed;
        readonly Stopwatch fade= new Stopwatch();
        TaskCompletionSource<bool> completed;
        public Pending(Form owner){Owner=owner;owner.FormClosed+=Cancel;owner.ClientSizeChanged+=Cancel;Application.AddMessageFilter(this);}
        void Cancel(object sender,EventArgs args){Dispose();}
        public bool PreFilterMessage(ref Message message){
            bool input=message.Msg>=0x100&&message.Msg<=0x109||message.Msg>=0x200&&message.Msg<=0x20e;
            if(!input||Surface==null)return false;
            var control=Control.FromChildHandle(message.HWnd);if(control==null||control.FindForm()!=Owner)return false;
            if(message.Msg==0x100&&message.WParam.ToInt32()==(int)Keys.Escape)Dispose();
            return true;
        }
        internal async Task<bool> Prepare(){
            if(Program.TestMode)return true;
            WindowFrame.PaintLive(Owner);
            var original=WindowFrame.Capture(Owner);if(original==null)return true;
            Surface=new ReferenceBackdropSurface{Bounds=Owner.ClientRectangle,BlockInput=true};
            Surface.SetFrames(original,(Bitmap)original.Clone());Surface.Blend=0;
            Owner.Controls.Add(Surface);Surface.BringToFront();Surface.Show();Surface.Update();
            Bitmap effect=null;
            try{
                int dpi=Owner.DeviceDpi;
                using(var work=(Bitmap)original.Clone())effect=await Task.Run(()=>MotionPaint.BlurBackdrop(work,10*dpi/96F));
                if(Disposed)return false;
                Surface.SetEffect(effect);effect=null;
                completed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                fade.Restart();
                AnimationClock.Run(Surface,motion=>{
                    if(Disposed)return false;
                    float amount=motion?MotionPaint.Ease((float)fade.Elapsed.TotalMilliseconds/DialogEntrance.Duration,.2F,.8F,.2F,1):1;
                    Surface.Blend=amount;
                    if(amount>=1){Surface.Update();completed.TrySetResult(true);}
                    return amount<1;
                });
                return await completed.Task;
            }finally{if(effect!=null)effect.Dispose();}
        }
        internal ReferenceBackdropSurface Take(){var surface=Surface;Surface=null;if(surface!=null)surface.BlockInput=false;return surface;}
        internal void ClearSurface(){if(Surface!=null){AnimationClock.Cancel(Surface);Surface.Dispose();Surface=null;if(!Owner.IsDisposed&&Owner.Visible)WindowFrame.PaintLive(Owner);}}
        public void Dispose(){
            if(Disposed)return;Disposed=true;Owner.FormClosed-=Cancel;Owner.ClientSizeChanged-=Cancel;Application.RemoveMessageFilter(this);
            Pending current;if(pending.TryGetValue(Owner,out current)&&Object.ReferenceEquals(current,this))pending.Remove(Owner);
            if(completed!=null)completed.TrySetResult(false);
            ClearSurface();
        }
    }
    internal static bool Defer(Form owner,Action action){Pending current;if(pending.TryGetValue(owner,out current)&&current.Executing)return false;Open(owner,()=>{action();return Task.FromResult(true);});return true;}
    internal static void Open(Form owner,Func<Task> action){Open(owner,active=>action());}
    internal static async void Open(Form owner,Func<Func<bool>,Task> action){
        if(owner.IsDisposed||!owner.Visible||pending.ContainsKey(owner))return;
        // WinForms can uninstall its synchronization context after a modeless
        // window closes. Decorative workers must always resume on this UI thread.
        if(!(System.Threading.SynchronizationContext.Current is WindowsFormsSynchronizationContext))
            System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var request=new Pending(owner);pending.Add(owner,request);
        try{
            bool prepared=false;
            try{prepared=await request.Prepare();}
            catch{request.ClearSurface();prepared=!request.Disposed;}
            if(prepared&&!request.Disposed&&!owner.IsDisposed){request.Executing=true;await action(()=>!request.Disposed&&!owner.IsDisposed);}
        }
        catch(Exception error){request.Dispose();if(!owner.IsDisposed){if(Program.TestMode)throw;MessageBox.Show(owner,error.Message,"Открытие окна",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
        finally{Pending current;if(pending.TryGetValue(owner,out current)&&Object.ReferenceEquals(current,request))pending.Remove(owner);request.Dispose();}
    }
    internal static ReferenceBackdropSurface Take(Form owner){Pending request;if(!pending.TryGetValue(owner,out request)||!request.Executing)return null;pending.Remove(owner);return request.Take();}
}

static class WindowFrame {
    [DllImport("user32.dll")]
    static extern bool RedrawWindow(IntPtr window, IntPtr updateRect, IntPtr region, uint flags);
    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr window, IntPtr target, uint flags);

    internal static void PaintLive(Form form) {
        const uint invalidate = 1, allChildren = 0x80, updateNow = 0x100, frame = 0x400;
        RedrawWindow(form.Handle, IntPtr.Zero, IntPtr.Zero, invalidate | allChildren | updateNow | frame);
    }

    internal static Bitmap Capture(Form form) {
        var size = form.Size;
        if (size.Width < 1 || size.Height < 1 || (long)size.Width * size.Height > 8500000 ||
            form.ClientSize.Width < 1 || form.ClientSize.Height < 1) return null;
        // DrawToBitmap prints WinForms children in a different stacking order
        // from the live HWND tree. A blur of that image looks like another page.
        // Ask this window to render its native client, without desktop capture.
        // GDI native edit controls do not preserve alpha. An opaque RGB target
        // prevents colored/transparent text fringes when the owner is blended.
        var clientImage = new Bitmap(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format32bppRgb);
        bool captured;
        using (var graphics = Graphics.FromImage(clientImage)) {
            var dc = graphics.GetHdc();
            try { captured = PrintWindow(form.Handle, dc, 1); }
            finally { graphics.ReleaseHdc(dc); }
        }
        if (captured) return clientImage;
        clientImage.Dispose();
        return null;
    }
}
