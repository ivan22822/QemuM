using System;using System.IO;using System.Drawing;using System.Linq;using System.Reflection;using System.Diagnostics;using System.Threading;using System.Runtime.InteropServices;using System.Windows.Forms;
static class WindowEntranceTests {
 static int checks;static string trace;static void Check(bool ok,string message){if(trace!=null)File.AppendAllText(trace,(ok?"PASS ":"FAIL ")+message+Environment.NewLine);if(!ok)throw new Exception(message);checks++;}
 static void Pump(int ms){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(5);}}
 static void PumpUntil(Func<bool> ready){var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<10000)Pump(5);if(!ready())throw new TimeoutException("first window presentation");}
 static T Field<T>(object value,string name){return (T)value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(value);}
 [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int msg,IntPtr word,IntPtr value);
 sealed class Probe : Control {internal Color Fill{get;set;}internal Probe(){Fill=Color.Red;}protected override void OnPaint(PaintEventArgs e){e.Graphics.Clear(Fill);}}
 sealed class ReadService : ISnapshotUiService {
  internal object Catalog;public bool Supports(string feature){return true;}
  public System.Threading.Tasks.Task<object> Load(){return System.Threading.Tasks.Task.FromResult(Catalog);}
  public System.Threading.Tasks.Task<object> Execute(SnapshotUiRequest request){throw new InvalidOperationException("Read-only fixture");}
  public event Action<SnapshotUiProgress> Progress{add{}remove{}}
 }
 static void CheckRealDialog(Form form,string output,string label){
  using(form){
   form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-6000,-5000);double first=-1;form.Shown+=(s,e)=>first=form.Opacity;
   form.Show();
   PumpUntil(()=>first>=0&&(form.Opacity>0||form.IsDisposed));
   if(!MotionPaint.Reduced){
    Check(first==0,label+" hides native initialization before its first visible frame");
    Check(!form.Controls.Cast<Control>().Any(c=>c.GetType().Name=="Frame"),label+" live native controls remain visible throughout entrance without a replacement bitmap");
    using(var image=WindowFrame.Capture(form)){Check(image.Size==form.ClientSize,label+" live entrance preserves full client geometry");image.Save(Path.Combine(output,label+"-first.png"));}
   }
   var native=form.Controls.Cast<Control>().Where(c=>c.GetType().Name!="Frame").ToDictionary(c=>c,c=>c.Bounds);Pump(350);
   Check(form.Opacity==1&&native.All(pair=>pair.Key.Bounds==pair.Value),label+" retains the assembled layout when live controls appear");
   using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(output,label+"-ready.png"));}
   bool savedMode=Program.TestMode;Program.TestMode=true;try{form.Close();}finally{Program.TestMode=savedMode;}
  }
 }
 [STAThread]static int Main(string[] args){try{Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Program.TestMode=false;trace=Path.Combine(args[1],"window-entrance-progress.txt");File.WriteAllText(trace,"");
  using(var form=new Form{ClientSize=new Size(800,550),StartPosition=FormStartPosition.Manual,Location=new Point(-6000,-5000)}){
   var probe=new Probe{Location=new Point(100,150),Size=new Size(300,220)};var input=new TextBox{Location=new Point(80,90),Text="ready",Width=180};form.Controls.Add(probe);form.Controls.Add(input);
   using(var entrance=new DialogEntrance(form)){
    long before=DialogEntrance.CapturedFrames;bool premature=false;input.VisibleChanged+=(s,e)=>{if(input.Visible&&form.Visible&&form.Opacity>0&&!MotionPaint.Reduced)premature=true;};double shownOpacity=-1;
    form.Shown+=(s,e)=>{shownOpacity=form.Opacity;probe.Fill=Color.FromArgb(10,90,180);probe.Invalidate();};
    Check(form.Opacity==0,"native window is hidden before any child can be presented");var size=form.Size;var bounds=input.Bounds;form.Show();Point final=form.Location;
    if(!MotionPaint.Reduced){
     Pump(25);Check(shownOpacity==0&&!premature,"first native visibility never exposes separate child blocks: shown="+shownOpacity+" premature="+premature);Pump(20);Check(entrance.Active&&form.Opacity>0&&form.Opacity<1,"assembled window starts a finite opacity transition");
     using(var image=WindowFrame.Capture(form))Check(image.GetPixel(150,200).ToArgb()==probe.Fill.ToArgb(),"live first frame includes changes from every Shown handler");
     Check(DialogEntrance.CapturedFrames==before,"opening does not replace native text with a bitmap");Pump(80);Check(input.Bounds==bounds&&form.Size==size,"animation never changes native layout dimensions");
     Pump(300);Check(!entrance.Active&&form.Location==final&&form.Opacity==1,"300ms transition settles at the requested location");
     Check(!form.Controls.Cast<Control>().Any(c=>c.GetType().Name=="Frame"),"settled window releases its presentation frame");
    }else{Pump(30);Check(form.Opacity==1&&DialogEntrance.CapturedFrames==before,"reduced motion shows live controls without a snapshot");}
    form.Close();
   }
  }
  if(!MotionPaint.Reduced)using(var form=new Form{ClientSize=new Size(500,300),StartPosition=FormStartPosition.Manual,Location=new Point(-6000,-5000)}){
   var input=new TextBox{Dock=DockStyle.Top,Text=""};form.Controls.Add(input);using(var entrance=new DialogEntrance(form)){
    form.Show();PumpUntil(()=>entrance.Active);Check(entrance.Active,"second window begins independently");var message=Message.Create(input.Handle,0x100,new IntPtr((int)Keys.X),IntPtr.Zero);Check(!entrance.PreFilterMessage(ref message)&&!entrance.Active&&form.Opacity==1,"first user input ends the visual layer and passes through immediately");SendMessage(input.Handle,0x102,new IntPtr((int)'x'),IntPtr.Zero);Check(input.Text=="x","first typed character reaches the live editor");form.Close();
   }
  }
  using(var form=new Form{ClientSize=new Size(400,250),StartPosition=FormStartPosition.Manual,Location=new Point(-6000,-5000)})using(var entrance=new DialogEntrance(form)){long before=DialogEntrance.CapturedFrames;form.Show();form.Close();Pump(30);Check(DialogEntrance.CapturedFrames==before,"closing before deferred preparation allocates no snapshot");}
  using(var owner=new Form{ClientSize=new Size(700,450),StartPosition=FormStartPosition.Manual,Location=new Point(-6000,-5000)})using(var dialog=new Form{ClientSize=new Size(450,300),StartPosition=FormStartPosition.Manual,Location=new Point(-5800,-4800)})using(var backdrop=new ReferenceDialogBackdrop(dialog))using(var entrance=new DialogEntrance(dialog)){
   owner.Show();dialog.Show(owner);var ready=ReferenceDialogBackdrop.WaitFor(dialog);bool premature=false;var waiting=Stopwatch.StartNew();while(!ready.IsCompleted&&waiting.ElapsedMilliseconds<10000){if(dialog.Opacity>0)premature=true;Application.DoEvents();Thread.Sleep(2);}Check(ready.IsCompleted&&!premature,"owned dialog remains transparent until its background is ready");
   var layer=owner.Controls.OfType<ReferenceBackdropSurface>().Single();Check(layer.Visible&&layer.Snapshot!=null&&layer.Generation>0,"blurred owner is painted before the dialog entrance");Pump(400);Check(dialog.Opacity==1&&!entrance.Active,"dialog appears after the prepared background");dialog.Close();owner.Close();
  }
  Program.TestMode=true;Pump(50);Check(AnimationClock.ActiveCount==0&&!AnimationClock.Running,"closing every window stops the shared clock");Check(GraphicsResources.Bytes("frame")==0,"all transition bitmaps have explicit disposal");
  if(!MotionPaint.Reduced){Program.TestMode=false;var service=new ReadService{Catalog=J.O("vm_state","stopped","rows",new object[]{J.O("id","fixture","name","Before upgrade","state","ready")})};CheckRealDialog(new SnapshotsWindow(service,"fixture",service.Catalog),args[1],"snapshots");using(var main=new MainWindow(args[0],null,true,false)){main.Demo();CheckRealDialog(new EditDialog(main,"ubuntu-desktop",main.Row("ubuntu-desktop")),args[1],"settings");}}
  File.WriteAllText(Path.Combine(args[1],"window-entrance-results.json"),J.Json().Serialize(J.O("passed",checks,"duration_ms",DialogEntrance.Duration,"reduced_motion",MotionPaint.Reduced)));return 0;
 }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"window-entrance-error.txt"),"After "+checks+" checks: "+error);return 1;}}
}
