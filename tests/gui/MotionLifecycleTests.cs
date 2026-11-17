using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class MotionLifecycleTests {
    [DllImport("user32.dll")]
    static extern int GetGuiResources(IntPtr process, int kind);
    static int checks;
    static string output;

    static void Check(bool value, string label) {
        if (!value) throw new InvalidOperationException(label);
        checks++;
    }

    static void Wait(Func<bool> done, string label, int timeout = 8000) {
        var time = Stopwatch.StartNew();
        while (!done() && time.ElapsedMilliseconds < timeout) {
            Application.DoEvents();
            Thread.Sleep(2);
        }
        if (!done()) throw new TimeoutException(label);
    }

    static T Field<T>(object owner, string name) {
        return (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
    }

    sealed class FixtureDialog : MotionDialog {
        internal ReferenceDialogBackdrop Backdrop { get; private set; }
        internal FixtureDialog(Func<Bitmap,float,Bitmap> blur = null) {
            ClientSize = new Size(310, 155);
            StartPosition = FormStartPosition.CenterParent;
            Controls.Add(new TextBox { Dock = DockStyle.Top, Text = "Черновик" });
            Backdrop = new ReferenceDialogBackdrop(this, blur);
            new DialogEntrance(this);
        }
        internal bool PressEscape() { return ProcessDialogKey(Keys.Escape); }
    }

    static void CancelledAndRepeatedClose(Form owner, bool reduced) {
        using (var dialog = new FixtureDialog()) {
            int attempts = 0, closed = 0;
            bool saving = true, keepDraft = true;
            dialog.FormClosing += (s, e) => { attempts++; e.Cancel = saving || keepDraft; };
            dialog.FormClosed += (s, e) => closed++;
            dialog.Show(owner);
            Wait(() => dialog.Opacity == 1, "window entrance");
            var layer = owner.Controls.OfType<ReferenceBackdropSurface>().Single();
            Wait(() => layer.Blend == 1, "backdrop entrance");
            dialog.Close();
            Check(dialog.Visible && dialog.Opacity == 1 && !dialog.ClosingWithMotion && attempts == 1, "active write vetoes close");
            saving = false;
            dialog.Close();
            Check(dialog.Visible && dialog.Opacity == 1 && attempts == 2, "draft confirmation can veto close");
            keepDraft = false;
            dialog.Close();
            if (!reduced) {
                Check(dialog.Visible && dialog.Presentation.Exiting && !dialog.IsDisposed, "approved close defers disposal");
                dialog.Close();
                Check(attempts == 3 && dialog.Visible, "repeat WM_CLOSE does not replay guards");
                Wait(() => dialog.IsDisposed || layer.Blend < 1, "backdrop exit");
                Check(dialog.IsDisposed || (layer.Blend > 0 && dialog.Opacity < 1), "dialog and owner fade together");
            }
            Wait(() => dialog.IsDisposed, "modeless close");
            Check(attempts == 3 && closed == 1, "one final modeless close");
            Check(!owner.Controls.OfType<ReferenceBackdropSurface>().Any(), "closing releases owner layer");
        }
    }

    static void ModalResults(Form owner) {
        foreach (var expected in new[] { DialogResult.OK, DialogResult.Cancel }) {
            using (var dialog = new FixtureDialog())
            using (var timer = new System.Windows.Forms.Timer { Interval = 15 }) {
                int attempts = 0, closed = 0;
                var cancel = new Button { Text = "Отмена", Dock = DockStyle.Bottom, DialogResult = DialogResult.Cancel };
                dialog.Controls.Add(cancel);
                dialog.CancelButton = cancel;
                dialog.FormClosing += (s, e) => attempts++;
                dialog.FormClosed += (s, e) => closed++;
                var elapsed = Stopwatch.StartNew();
                timer.Tick += (s, e) => {
                    if (dialog.Opacity < 1 && elapsed.ElapsedMilliseconds < 8000) return;
                    timer.Stop();
                    if (expected == DialogResult.OK) dialog.DialogResult = DialogResult.OK;
                    else Check(dialog.PressEscape(), "Escape reaches the modal CancelButton");
                };
                dialog.Shown += (s, e) => timer.Start();
                Check(dialog.ShowDialog(owner) == expected && attempts == 1 && closed == 1, "ShowDialog returns " + expected + " after one close");
            }
        }
    }

    static void NestedAndOwnerClose(Form owner) {
        using (var parent = new FixtureDialog())
        using (var child = new FixtureDialog()) {
            parent.Show(owner);
            Wait(() => parent.Opacity == 1, "parent shown");
            child.Show(parent);
            Wait(() => child.Opacity == 1, "child shown");
            Check(parent.Controls.OfType<ReferenceBackdropSurface>().Count() == 1, "nested dialog captures its direct owner");
            CloseReason reason = CloseReason.None;
            child.FormClosing += (s, e) => reason = e.CloseReason;
            parent.CloseImmediately();
            Wait(() => parent.IsDisposed && child.IsDisposed, "owned close cascade");
            Check(reason == CloseReason.FormOwnerClosing, "owner cascade retains native close reason");
        }

        using (var dialog = new FixtureDialog()) {
            long frames = DialogEntrance.CapturedFrames;
            dialog.Show(owner);
            dialog.Close();
            Wait(() => dialog.IsDisposed && GraphicsResources.Bytes("frame") == 0, "cancel first presentation");
            Check(DialogEntrance.CapturedFrames == frames, "closing before presentation allocates no dialog frame");
        }
    }

    static void FailedGraphicsWorker(Form owner) {
        using (var dialog = new FixtureDialog((source, sigma) => { throw new IOException("synthetic blur failure"); })) {
            dialog.Show(owner);
            Wait(() => dialog.Opacity == 1, "graphics failure releases entrance");
            Check(dialog.Backdrop.LastError.Contains("synthetic blur failure") &&
                owner.Controls.OfType<ReferenceBackdropSurface>().All(layer => !layer.Visible), "background error falls back to native window");
            dialog.Close();
            Wait(() => dialog.IsDisposed && GraphicsResources.Bytes("frame") == 0, "failed blur releases images");
        }
    }

    static void SwitchTabs(Form owner, bool reduced) {
        using (var tabs = new WorkspaceTabs { Dock = DockStyle.Fill }) {
            var draft = new TextBox { Text = "Привет", Dock = DockStyle.Top };
            var second = new TextBox { Text = "Other page", Dock = DockStyle.Top };
            var pageA = new Panel();
            var pageB = new Panel();
            pageA.Controls.Add(draft);
            pageB.Controls.Add(second);
            tabs.Add("Первый", pageA);
            tabs.Add("Второй", pageB);
            owner.Controls.Add(tabs);
            owner.Activate();
            Check(draft.Focus(), "live editor can be focused");
            draft.Select(1, 3);
            tabs.SelectedIndex = 1;
            Check(pageB.Visible && !pageA.Visible && second.Focus(), "new page available in same call");
            tabs.SelectedIndex = 0;
            Check(draft.Focused && draft.SelectionStart == 1 && draft.SelectionLength == 3, "page keeps native focus and selection");
            for (int i = 0; i < 100; i++) tabs.SelectedIndex = 1 - tabs.SelectedIndex;
            Check(tabs.SelectedIndex == 0 && draft.Text == "Привет" && AnimationClock.ActiveCount <= (reduced ? 0 : 1), "100 switches keep last selection, draft, and one transition");
            owner.WindowState = FormWindowState.Minimized;
            Wait(() => !AnimationClock.Running, "minimized tab animation settles");
            owner.WindowState = FormWindowState.Normal;
        }
    }

    static void NetworkDraft() {
        Program.TestMode = true;
        using (var readStarted = new ManualResetEventSlim())
        using (var endRead = new ManualResetEventSlim()) {
            var stand = J.O("id", "fixture", "name", "Fixture", "revision", 7,
                "members", new[] { "a", "b" }, "order", new[] { "a", "b" });
            string before = J.Json().Serialize(stand);
            int saves = 0;
            using (var dialog = new StandNetworkEditorWindow(stand, n => null, (w, value) => saves++,
                null, null, () => { readStarted.Set(); endRead.Wait(8000); return J.O(); })) {
                var token = Field<CancellationTokenSource>(dialog, "statusReadCancellation").Token;
                try {
                    Check(Field<ThemedList>(dialog, "linksList") != null && Field<FlowLayoutPanel>(dialog, "adapterStack") == null &&
                        Field<ThemedList>(dialog, "orderList") == null && Field<ThemedEditor>(dialog, "diagnostics") == null, "only initial TCP page is materialized");
                    dialog.Show();
                    Wait(() => readStarted.IsSet, "network diagnostics start after presentation");
                    var tabs = Field<WorkspaceTabs>(dialog, "tabs");
                    tabs.SelectedIndex = 4;
                    var delay = Field<NumberInput>(dialog, "delay");
                    delay.Value = 13;
                    tabs.SelectedIndex = 3;
                    var adapterPanel = Field<FlowLayoutPanel>(dialog, "adapterStack");
                    tabs.SelectedIndex = 4;
                    Check(Field<NumberInput>(dialog, "delay") == delay && delay.Value == 13, "lazy pages retain edits");
                    var save = Field<Button>(dialog, "saveButton");
                    save.PerformClick();
                    save.PerformClick();
                    dialog.Close();
                    Check(saves == 1 && dialog.Visible, "network write prevents close and duplicate save");
                    dialog.SaveFailed(new IOException("synthetic failure"));
                    Check(save.Enabled && delay.Value == 13 && J.Json().Serialize(stand) == before, "failed save retains the local draft and original revision");
                    tabs.SelectedIndex = 3;
                    Check(Field<FlowLayoutPanel>(dialog, "adapterStack") == adapterPanel, "adapter page is reused within the editor");
                    dialog.Close();
                    Check(dialog.IsDisposed && token.IsCancellationRequested, "close cancels outstanding diagnosis");
                } finally { endRead.Set(); }
            }
        }
        Program.TestMode = false;
    }

    static Dictionary<string, object> Resources() {
        // A full collection belongs only to the fixture and measures retained state.
        long retained = GC.GetTotalMemory(true);
        using (var process = Process.GetCurrentProcess()) {
            return J.O("gdi", GetGuiResources(process.Handle, 0), "user", GetGuiResources(process.Handle, 1),
                "handles", process.HandleCount, "managed_retained", retained, "private_bytes", process.PrivateMemorySize64);
        }
    }

    static void Repeat(Form owner, List<object> samples) {
        for (int i = 1; i <= 100; i++) {
            using (var dialog = new FixtureDialog()) {
                dialog.Show(owner);
                Wait(() => dialog.Opacity > 0, "opening repeatedly");
                dialog.Close();
                Wait(() => dialog.IsDisposed && GraphicsResources.Bytes("frame") == 0, "closing repeatedly");
            }
            if (i % 20 == 0) samples.Add(J.O("cycle", i, "resources", Resources()));
        }
        Check(AnimationClock.ActiveCount == 0 && GraphicsResources.Bytes("frame") == 0, "100 dialogs release transition frames and subscriptions");
        var warm = J.V(samples[0], "resources");
        var last = J.V(samples[samples.Count-1], "resources");
        foreach (string key in new[] { "gdi", "user", "handles" })
            Check(J.N(last, key) <= J.N(warm, key) + 24, key + " stays near the warm resource level");
        Check(Convert.ToInt64(J.V(last, "managed_retained")) <= Convert.ToInt64(J.V(warm, "managed_retained")) + 2 * 1024 * 1024,
            "retained managed memory stays near the warm resource level");
    }

    [STAThread]
    static int Main(string[] args) {
        output = args[1];
        Application.EnableVisualStyles();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        var samples = new List<object>();
        bool reduced = MotionPaint.Reduced;
        try {
            using (var owner = new Form { ClientSize = new Size(425, 250), Text = "QemuM motion fixture" }) {
                owner.Show();
                CancelledAndRepeatedClose(owner, reduced);
                ModalResults(owner);
                NestedAndOwnerClose(owner);
                FailedGraphicsWorker(owner);
                SwitchTabs(owner, reduced);
                NetworkDraft();
                Repeat(owner, samples);
                owner.Close();
            }
            return 0;
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(output, "motionlifecycle-error.txt"), error.ToString());
            return 1;
        } finally {
            File.WriteAllText(Path.Combine(output, "motionlifecycle-results.json"),
                J.Json().Serialize(J.O("checks_completed", checks, "reduced_motion", reduced, "resource_samples", samples)));
        }
    }
}
