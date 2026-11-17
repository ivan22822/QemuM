using System;using System.IO;using System.Drawing;using System.Linq;using System.Reflection;using System.Diagnostics;using System.Threading;using System.Windows.Forms;
static class AnimationClockTests {
 static int checks;static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
 static void Pump(int ms){var t=Stopwatch.StartNew();while(t.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(5);}}
 static T Field<T>(object value,string name){return (T)value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(value);}
 static void Mouse(Control control,string action){typeof(Control).GetMethod(action,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(control,new object[]{EventArgs.Empty});}
 [STAThread]static int Main(string[] args){try{Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Program.TestMode=false;
  using(var form=new Form{ClientSize=new Size(650,370),StartPosition=FormStartPosition.Manual,Location=new Point(-6000,-5000)}){
   var buttons=Enumerable.Range(0,20).Select(i=>new PolarButton{Text="Button "+i,Size=new Size(100,30),Location=new Point(i%5*110,i/5*40)}).ToArray();form.Controls.AddRange(buttons);form.Show();Pump(30);
   foreach(var button in buttons)Mouse(button,"OnMouseEnter");Check(AnimationClock.ActiveCount==(MotionPaint.Reduced?0:20),"many buttons share a single active clock");
   int clicks=0;buttons[0].Click+=(s,e)=>clicks++;buttons[0].PerformClick();Check(clicks==1,"transition never delays the action handler");Pump(45);float previous=Field<float>(buttons[0],"hoverAmount");Mouse(buttons[0],"OnMouseLeave");Check(Math.Abs(previous-Field<float>(buttons[0],"hoverAmount"))<.001,"reversed hover continues from its displayed value");
   Pump(250);Check(!AnimationClock.Running&&AnimationClock.ActiveCount==0,"all hover transitions stop after settling");long idleFrames=AnimationClock.Frames;Pump(200);Check(idleFrames==AnimationClock.Frames,"idle buttons request no decorative frames");
   Mouse(buttons[0],"OnMouseEnter");Thread.Sleep(250);Application.DoEvents();Check(!AnimationClock.Running&&Field<float>(buttons[0],"hoverAmount")==1,"delayed frame skips to the elapsed-time endpoint");
   Mouse(buttons[0],"OnMouseLeave");buttons[0].Visible=false;Pump(50);Check(!AnimationClock.Running,"hidden control removes its pending transition");buttons[0].Visible=true;
   var operation=new OperationSurface{Location=new Point(0,150),Size=new Size(640,220)};form.Controls.Add(operation);operation.Begin("create");Pump(60);Check(AnimationClock.Running!=MotionPaint.Reduced,"unknown progress runs only while motion is enabled");
   form.WindowState=FormWindowState.Minimized;Pump(80);Check(!AnimationClock.Running,"minimized window stops its progress frames");form.WindowState=FormWindowState.Normal;Pump(50);Check(AnimationClock.Running!=MotionPaint.Reduced,"restored active operation resumes its small progress indicator");
   operation.Report("Real progress",1,4);Pump(300);Check(Math.Abs(operation.DisplayProgress-.25)<.00001&&!AnimationClock.Running,"determinate progress reaches the backend value and settles");
   operation.Finish("completed");Pump(300);Check(operation.DisplayProgress==1&&!AnimationClock.Running,"confirmed completion stops every decorative frame");operation.Dispose();
   Program.TestMode=true;Mouse(buttons[0],"OnMouseEnter");Check(!AnimationClock.Running&&Field<float>(buttons[0],"hoverAmount")==1,"suppressed motion applies the final state immediately");Program.TestMode=false;
   foreach(var button in buttons){Mouse(button,"OnMouseLeave");button.Dispose();}Check(AnimationClock.ActiveCount==0&&!AnimationClock.Running,"disposal unregisters all active controls");form.Close();
  }
  using(var form=new Form{ClientSize=new Size(650,350),StartPosition=FormStartPosition.Manual,Location=new Point(-6000,-5000)}){
   var header=new ReferenceHeader("Settings","Fixture","TEST");form.Controls.Add(header);form.Show();var time=Stopwatch.StartNew();while(!header.GhostReady&&time.ElapsedMilliseconds<5000)Pump(10);Check(header.GhostReady,"header prepares its static image");long frames=AnimationClock.Frames;Pump(250);Check(frames==AnimationClock.Frames&&!AnimationClock.Running,"static header has no continuous animation");form.Close();
  }
  File.WriteAllText(Path.Combine(args[1],"animation-clock-results.json"),J.Json().Serialize(J.O("passed",checks,"system_reduced_motion",MotionPaint.Reduced,"active_after",AnimationClock.ActiveCount)));return 0;
 }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"animation-clock-error.txt"),"After "+checks+" checks: "+error);return 1;}}
}
