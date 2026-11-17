using System;using System.Collections.Generic;using System.Linq;using System.Windows.Forms;

// One UI-thread timer exists only while at least one visible transition needs frames.
static class AnimationClock {
 [ThreadStatic]static Clock current;
 sealed class Entry {internal Control Owner;internal Func<bool,bool> Frame;internal EventHandler Disposed;}
 sealed class Clock {
  internal readonly Timer Timer=new Timer{Interval=16};internal readonly Dictionary<Control,Entry> Entries=new Dictionary<Control,Entry>();internal long Frames;
  internal Clock(){Timer.Tick+=(s,e)=>Tick();}
  internal void Remove(Control owner){Entry entry;if(!Entries.TryGetValue(owner,out entry))return;Entries.Remove(owner);owner.Disposed-=entry.Disposed;if(Entries.Count==0)Timer.Stop();}
  void Tick(){foreach(var entry in Entries.Values.ToArray()){Entry live;if(!Entries.TryGetValue(entry.Owner,out live)||!Object.ReferenceEquals(entry,live))continue;bool keep=false;try{Frames++;keep=entry.Frame(Allowed(entry.Owner));}finally{if((!keep||!Allowed(entry.Owner))&&Entries.TryGetValue(entry.Owner,out live)&&Object.ReferenceEquals(entry,live))Remove(entry.Owner);}}}
 }
 static bool Allowed(Control owner){var form=owner.FindForm();return !owner.IsDisposed&&owner.IsHandleCreated&&owner.Visible&&!MotionPaint.Reduced&&!Program.TestMode&&(form==null||form.Visible&&form.WindowState!=FormWindowState.Minimized);}
 internal static void Run(Control owner,Func<bool,bool> frame){
  if(owner.IsDisposed)return;if(current==null)current=new Clock();current.Remove(owner);
  if(!Allowed(owner)){frame(false);return;}var entry=new Entry{Owner=owner,Frame=frame};entry.Disposed=(s,e)=>current.Remove(owner);current.Entries.Add(owner,entry);owner.Disposed+=entry.Disposed;current.Timer.Start();
 }
 internal static void Cancel(Control owner){if(current!=null)current.Remove(owner);}
 internal static int ActiveCount{get{return current==null?0:current.Entries.Count;}}
 internal static bool Running{get{return current!=null&&current.Timer.Enabled;}}
 internal static long Frames{get{return current==null?0:current.Frames;}}
}

// A single small selection accent per tab bar. The page stays interactive.
sealed class SelectionMotion {
    readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
    Control selected;
    float amount = 1;

    internal static void AccentOnShow(Control panel, System.Drawing.Color accent) {
        var transition = new SelectionMotion();
        panel.VisibleChanged += (s,e) => transition.Start(panel, panel, panel.Visible);
        panel.Paint += (s,e) => {
            using (var pen = new System.Drawing.Pen(MotionPaint.Mix(panel.BackColor, accent, transition.Amount(panel))))
                e.Graphics.DrawLine(pen, 0, 0, panel.Width, 0);
        };
    }

    internal float Amount(Control control) { return control == selected ? amount : 1; }

    internal void Start(Control owner, Control target, bool animate) {
        selected = target;
        AnimationClock.Cancel(owner);
        amount = 1;
        if (!animate) return;
        amount = .55F;
        watch.Restart();
        AnimationClock.Run(owner, motion => {
            float progress = motion ? MotionPaint.Ease((float)watch.Elapsed.TotalMilliseconds / 140) : 1;
            amount = .55F + .45F * progress;
            target.Invalidate();
            return progress < 1;
        });
    }
}
