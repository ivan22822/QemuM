using System;using System.Drawing;using System.Diagnostics;using System.IO;using System.Linq;using System.Runtime.InteropServices;using System.Threading;using System.Threading.Tasks;using System.Windows.Forms;
static class DialogResponseTests {
 static int checks;static double feedbackMs;static string trace;static void Check(bool value,string text){if(trace!=null)File.AppendAllText(trace,(value?"PASS ":"FAIL ")+text+Environment.NewLine);if(!value)throw new Exception(text);checks++;}
 static Form root;static void Wait(Func<bool> done){var watch=Stopwatch.StartNew();while(!done()&&watch.ElapsedMilliseconds<10000){Application.DoEvents();Thread.Sleep(2);}if(!done()&&root!=null)File.AppendAllText(trace,"TIMEOUT clock="+AnimationClock.ActiveCount+"/"+AnimationClock.Running+" layers="+String.Join(";",root.Controls.OfType<ReferenceBackdropSurface>().Select(l=>l.Generation+"/"+l.Blend+"/"+l.Visible))+Environment.NewLine);Check(done(),"operation completed");}
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr word,IntPtr value);
 [DllImport("user32.dll")]static extern bool PostMessage(IntPtr window,int message,IntPtr word,IntPtr value);
 sealed class ClickProbe:PolarButton{internal void Press(){OnMouseEnter(EventArgs.Empty);OnMouseDown(new MouseEventArgs(MouseButtons.Left,1,8,8,0));}internal void ActivateButton(){OnClick(EventArgs.Empty);}}
 [STAThread]static int Main(string[] args){try{trace=Path.Combine(args[1],"dialog-response-trace.txt");File.WriteAllText(trace,"");Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Program.TestMode=false;
  using(var owner=new SmoothForm{ClientSize=new Size(640,420),StartPosition=FormStartPosition.Manual,Location=new Point(30,30),BackColor=Theme.Bg}){
   root=owner;
   var button=new ClickProbe{Text="Открыть",Bounds=new Rectangle(30,30,160,40),BackColor=Theme.Card,ForeColor=Theme.Text};owner.Controls.Add(button);
   var original=new FieldInput{Bounds=new Rectangle(30,110,350,40),Dock=DockStyle.None,Text="джщсьшдсь — VM_12"};owner.Controls.Add(original);owner.Show();button.Focus();WindowFrame.PaintLive(owner);
   using(var neutral=WindowFrame.Capture(owner)){
    MotionDialog dialog=null;FieldInput editor=null;IntPtr native=IntPtr.Zero;bool constructing=false;var elapsed=new Stopwatch();
    button.Click+=(s,e)=>DialogLaunch.Open(owner,async()=>{
     Check(owner.Controls.OfType<ReferenceBackdropSurface>().Single().Blend==1,"owner blur finishes before expensive work starts");constructing=true;
     await Task.Delay(250);dialog=new MotionDialog();Theme.Form(dialog,480,290);editor=new FieldInput{Text="Имя VM",Dock=DockStyle.Top};var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Закрыть",dialog.Close));Theme.Dialog(dialog,editor,buttons,"Настройки","Проверка текста");dialog.Shown+=(a,b)=>native=editor.Input.Handle;dialog.Show(owner);
    });
    button.Press();elapsed.Start();button.ActivateButton();
    var layer=owner.Controls.OfType<ReferenceBackdropSurface>().Single();Check(!constructing,"click paints an immediate response before dialog construction");
    using(var source=layer.CopySource(owner.ClientSize)){
     int different=0;for(int y=35;y<65;y+=3)for(int x=35;x<180;x+=3)if(source.GetPixel(x,y).ToArgb()!=neutral.GetPixel(x,y).ToArgb())different++;
     Check(different==0,"the frozen owner contains the released, neutral opening button");
    }
    Wait(()=>layer.Blend>0);double firstFeedback=elapsed.Elapsed.TotalMilliseconds;feedbackMs=firstFeedback;Check(firstFeedback<700,"blur begins before backend/constructor delay: "+firstFeedback);
    Wait(()=>dialog!=null&&dialog.Opacity>0);Check(editor.Input.Handle==native&&!dialog.Controls.Cast<Control>().Any(c=>c.GetType().Name=="Frame"),"native text handle is retained throughout live opacity animation");
    editor.Focus();SendMessage(editor.Input.Handle,0x102,new IntPtr((int)'Ж'),IntPtr.Zero);Check(editor.Text.Contains("Ж"),"first Unicode character reaches the native field during entrance");
    Wait(()=>dialog.Opacity==1);Check(owner.Controls.OfType<ReferenceBackdropSurface>().Single()==layer,"opening transfers one prepared background without removing or replacing it");
    using(var image=WindowFrame.Capture(dialog)){image.Save(Path.Combine(args[1],"live-native-input.png"));Check(image.GetPixel(50,50).A==255,"native capture is opaque even where GDI text does not maintain alpha");}
    dialog.Close();Wait(()=>dialog.IsDisposed);Check(!owner.Controls.OfType<ReferenceBackdropSurface>().Any()&&original.Text=="джщсьшдсь — VM_12","closing restores one live owner with unchanged text");
    using(var image=WindowFrame.Capture(owner))image.Save(Path.Combine(args[1],"owner-after-close.png"));
   }
   int cancelled=0;DialogLaunch.Open(owner,()=>{cancelled++;return Task.FromResult(true);});PostMessage(owner.Handle,0x100,new IntPtr((int)Keys.Escape),IntPtr.Zero);Wait(()=>!owner.Controls.OfType<ReferenceBackdropSurface>().Any());Check(cancelled==0,"Escape cancels pending opening before any constructor or read runs");
   var gate=new TaskCompletionSource<bool>();bool reading=false,finished=false;
   DialogLaunch.Open(owner,async active=>{reading=true;await gate.Task;if(active())cancelled++;finished=true;});
   Wait(()=>reading);PostMessage(owner.Handle,0x100,new IntPtr((int)Keys.Escape),IntPtr.Zero);Wait(()=>!owner.Controls.OfType<ReferenceBackdropSurface>().Any());
   var nextGate=new TaskCompletionSource<bool>();bool nextReading=false;DialogLaunch.Open(owner,async()=>{nextReading=true;await nextGate.Task;});Wait(()=>nextReading);
   gate.SetResult(true);Wait(()=>finished);Check(cancelled==0,"cancelled read completion never reopens a dialog or adopts a newer opening");nextGate.SetResult(true);Wait(()=>!owner.Controls.OfType<ReferenceBackdropSurface>().Any());
   owner.Close();
  }
  Wait(()=>GraphicsResources.Bytes("frame")==0&&!AnimationClock.Running);File.WriteAllText(Path.Combine(args[1],"dialog-response-results.json"),J.Json().Serialize(J.O("passed",checks,"first_blur_ms",feedbackMs)));return 0;
 }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"dialog-response-error.txt"),error.ToString());return 1;}}
}
