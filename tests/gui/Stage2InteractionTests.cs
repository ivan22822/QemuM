using System;using System.IO;using System.Drawing;using System.Diagnostics;using System.Linq;using System.Reflection;using System.Threading;using System.Windows.Forms;
static class Stage2InteractionTests {
 static int checks;static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;}
 static T Field<T>(object value,string name){return (T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);}
 static void Pump(Func<bool> ready){var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<20000){Application.DoEvents();Thread.Sleep(5);}Check(ready(),"decorative worker finishes within its test deadline");}
 [STAThread]static int Main(string[] args){try{Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Program.TestMode=true;
  var heartbeat=new Stopwatch();double longest=0;using(var timer=new System.Windows.Forms.Timer{Interval=15}){
   timer.Tick+=(s,e)=>{if(heartbeat.IsRunning)longest=Math.Max(longest,heartbeat.Elapsed.TotalMilliseconds);heartbeat.Restart();};timer.Start();
   using(var owner=new Form{ClientSize=new Size(900,500),StartPosition=FormStartPosition.Manual,Location=new Point(-6000,-5000),BackColor=Color.Beige})
   using(var dialog=new Form{ClientSize=new Size(200,120),StartPosition=FormStartPosition.Manual,Location=new Point(-5500,-4500)})
   using(var backdrop=new ReferenceDialogBackdrop(dialog)){
    owner.Controls.Add(new Label{Text="Synthetic owner",AutoSize=true,Location=new Point(30,30)});owner.Show();dialog.Show(owner);
    Pump(()=>owner.Controls.OfType<ReferenceBackdropSurface>().Any(c=>c.Snapshot!=null));var surface=owner.Controls.OfType<ReferenceBackdropSurface>().Single();
    Check(backdrop.LastError==null&&surface.Snapshot.Size==owner.ClientSize,"owner frame is processed and installed at the requested size");
    var previous=surface.Snapshot;int generation=surface.Generation;owner.ClientSize=new Size(920,520);
    Check(Object.ReferenceEquals(previous,surface.Snapshot),"resize preserves the previous frame until replacement is ready");
    owner.ClientSize=new Size(940,540);owner.ClientSize=new Size(960,560);
    Pump(()=>surface.Generation>generation&&surface.Snapshot.Size==owner.ClientSize);
    Check(backdrop.LastError==null,"rapid resize applies the latest geometry without a stale-frame error");
    dialog.Hide();Pump(()=>!owner.Controls.OfType<ReferenceBackdropSurface>().Any());
    Check(GraphicsResources.Bytes("frame")==0,"hidden dialog releases its owner frame");
    dialog.Show(owner);owner.ClientSize=new Size(980,580);dialog.Close();Pump(()=>GraphicsResources.Bytes("frame")==0);owner.Close();
   }
   Check(longest<100,"UI heartbeat remains responsive while owner blur runs: "+longest+"ms");
   timer.Stop();
  }
  using(var owner=new Form{ClientSize=new Size(900,500),StartPosition=FormStartPosition.Manual,Location=new Point(-6000,-5000)}){
   var actions=Theme.Flow();actions.Controls.Add(Theme.Button("Запустить",()=>{}));var stage=new StateStage(Theme.Label("",10),actions){Dock=DockStyle.Fill};owner.Controls.Add(stage);owner.Show();
   stage.Present("running");stage.Present("paused");stage.Present("running");Pump(()=>stage.BackgroundReady);
   Check(Field<string>(stage,"backgroundKey").Contains("RUNNING"),"rapid state changes retain only the latest background");
   using(var image=new Bitmap(stage.Width,stage.Height)){var watch=Stopwatch.StartNew();stage.DrawToBitmap(image,stage.ClientRectangle);Check(watch.ElapsedMilliseconds<100,"settled paint only draws prepared graphics");image.Save(Path.Combine(args[1],"state-ready.png"));}
   owner.Close();
  }
  Pump(()=>GraphicsResources.Bytes("frame")==0);Check(GraphicsResources.Bytes("decor")<=GraphicsResources.DecorLimit,"inactive decorative caches obey their byte budget");
  File.WriteAllText(Path.Combine(args[1],"stage2-interaction-results.json"),J.Json().Serialize(J.O("passed",checks,"ui_heartbeat_max_ms",longest,"working_bytes",BoundedBackdropBlur.WorkingBytes)));return 0;
 }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"stage2-interaction-error.txt"),error.ToString());return 1;}}
}
