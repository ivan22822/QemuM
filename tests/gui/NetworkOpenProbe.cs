using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// Opens the real editor on a WinForms button click and uses isolated in-memory
// stands. The same probe also compiles against the handoff sources.
static class NetworkOpenProbe {
    static Dictionary<string, double> measurements;
    static Stopwatch elapsed;
    static double lastHeartbeat, longestHeartbeat;
    static Button open;
    static Form owner;
    static StandNetworkEditorWindow editor;

    internal static void Mark(string phase) {
        if (elapsed == null) return;
        lock (measurements) {
            if (!measurements.ContainsKey(phase)) measurements.Add(phase, elapsed.Elapsed.TotalMilliseconds);
        }
    }

    static void TimeNativePhases() {
        var instrument = typeof(StandNetworkEditorWindow).Assembly.GetType("NetworkOpenTiming");
        if (instrument == null) return;
        var observer = instrument.GetProperty("Observer", BindingFlags.Static | BindingFlags.NonPublic);
        observer.SetValue(null, (Action<string,double>)((phase, offset) => {
            lock (measurements) {
                double origin;
                if (!measurements.TryGetValue("ctor_call", out origin)) origin = 0;
                if (!measurements.ContainsKey(phase)) measurements.Add(phase, origin + offset);
            }
        }), null);
    }

    static void Flush(Func<bool> done, int timeout, string operation) {
        var wait = Stopwatch.StartNew();
        while (!done() && wait.ElapsedMilliseconds < timeout) {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        if (!done()) throw new TimeoutException(operation);
    }

    static void RegisterClick(int size, string root) {
        var names = Enumerable.Range(0, size).Select(i => "fixture_" + i.ToString("00")).ToArray();
        var rows = names.Select((name, i) => new {
            Key = name, Row = (object)J.O("data", J.O("network_adapters", Enumerable.Range(0, 4).Select(n =>
                J.O("id", "net" + n, "type", "user", "model", "e1000e", "mac", "52:54:00:00:" + i.ToString("x2") + ":" + n.ToString("x2"))).ToArray()))
        }).ToDictionary(item => item.Key, item => item.Row);

        string standId = Guid.NewGuid().ToString("N");
        var stand = J.O("id", standId, "name", "QemuM test network " + size, "revision", 4, "members", names, "order", names,
            "switches", new[] { J.O("id", Guid.NewGuid().ToString("N"), "name", "Switch", "members",
                names.Select(name => J.O("vm", name, "nic", "net1")).ToArray()) },
            "multicast", new[] { J.O("id", Guid.NewGuid().ToString("N"), "address", "239.192.0.1", "port", 20000, "members",
                names.Select(name => J.O("vm", name, "nic", "net2")).ToArray()) },
            "links", Enumerable.Range(0, size / 2).Select(i => J.O("id", Guid.NewGuid().ToString("N"),
                "listen", J.O("vm", names[i*2], "nic", "net3"), "connect", J.O("vm", names[i*2+1], "nic", "net3"))).ToArray());
        StandDiagramLayout.Save(root, standId, names.Select((name, index) => new { name, index }).ToDictionary(item => "vm:" + item.name, item => new Point(0, 55 + item.index * 90)));
        open.Click += (s, e) => {
            Mark("click_handler");
            var placement = StandDiagramLayout.Read(root, standId);
            Mark("placement_read");
            Mark("ctor_call");
            using (editor = new StandNetworkEditorWindow(stand, name => rows[name], (window, value) => { }, placement, null, () => {
                Mark("diagnostics_provider_start");
                var result = J.O("members", names.ToDictionary(name => name, name => (object)"stopped"));
                Mark("diagnostics_provider_ready");
                return result;
            })) {
                Mark("ctor_return");
                editor.StartPosition = FormStartPosition.CenterParent;
                editor.Shown += (sender, args) => Mark("native_shown");
                editor.ShowDialog(owner);
            }
            editor = null;
        };
    }

    static Dictionary<string, double> Sample() {
        measurements = new Dictionary<string, double>();
        lastHeartbeat = longestHeartbeat = 0;
        elapsed = Stopwatch.StartNew();
        using (var timer = new System.Windows.Forms.Timer { Interval = 15 }) {
            timer.Tick += (s, e) => {
                double now = elapsed.Elapsed.TotalMilliseconds;
                longestHeartbeat = Math.Max(longestHeartbeat, now - lastHeartbeat);
                lastHeartbeat = now;
                if (editor == null) return;
                if (editor.Visible && editor.Opacity > 0) Mark("first_window_sample");
                if (editor.Visible && editor.Opacity == 1) Mark("fully_opaque_sample");
                bool diagnosis = HasDiagnosis();
                if (editor.Visible && editor.Opacity == 1 && diagnosis) {
                    timer.Stop();
                    Mark("ready_screen_sample");
                    editor.Close();
                } else if (now > 12000) {
                    timer.Stop();
                    editor.CloseImmediatelyForProbe();
                }
            };
            timer.Start();
            open.PerformClick();
            timer.Stop();
        }
        measurements["heartbeat_max_ms"] = longestHeartbeat;
        if (!measurements.ContainsKey("ready_screen_sample")) throw new TimeoutException("network editor never became ready");
        return measurements;
    }

    static bool HasDiagnosis() {
        var label = (Label)typeof(StandNetworkEditorWindow).GetField("snapshotStatus", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(editor);
        if (!label.Text.StartsWith("Срез сохранённого стенда", StringComparison.Ordinal)) return false;
        Mark("diagnostics_ui_sample");
        return true;
    }

    // Reflection keeps the same source compatible with the original editor.
    static void CloseImmediatelyForProbe(this Form form) {
        var method = form.GetType().GetMethod("CloseImmediately", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null) form.Close();
        else method.Invoke(form, null);
    }

    [STAThread]
    static int Main(string[] args) {
        Application.EnableVisualStyles();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        int size = Int32.Parse(args[0]);
        try {
            TimeNativePhases();
            using (owner = new Form { Text = "QemuM Windows benchmark", ClientSize = new Size(1000, 660) }) {
                open = new Button { Text = "Сеть и соединения", AutoSize = true, Location = new Point(18, 18) };
                owner.Controls.Add(open);
                RegisterClick(size, args[1]);
                owner.Show();
                Flush(() => owner.Visible, 2000, "owner");
                var samples = Enumerable.Range(0, 5).Select(i => Sample()).ToArray();
                owner.Close();
                File.WriteAllText(args[2], J.Json().Serialize(J.O("members", size, "process_x64", Environment.Is64BitProcess, "system_reduced_motion",
                    MotionPaint.Reduced, "samples", samples)));
            }
            return 0;
        } catch (Exception error) {
            File.WriteAllText(args[2] + ".error.txt", error.ToString());
            return 1;
        }
    }
}
