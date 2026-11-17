using System;using System.IO;using System.Linq;using System.Drawing;using System.Text;using System.Threading;using System.Threading.Tasks;using System.Diagnostics;using System.Reflection;using System.Windows.Forms;
static class Stage1InteractionTests {
 static readonly System.Collections.Generic.List<double> navigation=new System.Collections.Generic.List<double>();
 static int checks;static string trace;static T Field<T>(object value,string name){if(value is SnapshotsWindow&&value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)==null)value=((SnapshotsWindow)value).View;return (T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);}
 static void Set(object value,string name,object item){value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(value,item);}
 static void Check(bool ok,string message){if(trace!=null)File.AppendAllText(trace,(ok?"PASS ":"FAIL ")+message+Environment.NewLine);if(!ok)throw new Exception(message);checks++;}
 static void Pump(Func<bool> ready,int milliseconds=5000){var watch=Stopwatch.StartNew();while(!ready()&&watch.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(5);}Check(ready(),"UI operation finishes within its deadline");}
 static void For(int milliseconds){var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(5);}}
 static string ReadRecord(string path){if(!File.Exists(path))return "";using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var reader=new StreamReader(stream,Encoding.UTF8))return reader.ReadToEnd();}
 static int Lines(string path){return ReadRecord(path).Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries).Length;}
 sealed class SnapshotService:ISnapshotUiService {
  public bool Fail;public event Action<SnapshotUiProgress> Progress{add{}remove{}}
  public bool Supports(string feature){return true;}
  public Task<object> Load(){var result=new TaskCompletionSource<object>();if(Fail)result.SetException(new IOException("fixture refresh failure"));else result.SetResult(Catalog());return result.Task;}
  public Task<object> Execute(SnapshotUiRequest request){throw new InvalidOperationException("No write allowed in this fixture");}
 }
 static object Catalog(){return J.O("rows",new object[]{J.O("id","cached","name","Cached snapshot","date",1700000000)},"vm_state","stopped");}
 [STAThread]static int Main(string[] args){try{
  Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Application.EnableVisualStyles();Program.TestMode=true;
  string root=args[0],output=args[1],python=args[2],script=Path.Combine(root,"reads.py"),record=Path.Combine(root,"requests.txt");trace=Path.Combine(output,"stage1-interaction-progress.txt");File.WriteAllText(trace,"");
  File.WriteAllText(script,"import sys,json,time,base64\nfor line in sys.stdin:\n r=json.loads(line);a=r['args'];c=r['command']\n with open(sys.argv[1],'a',encoding='utf-8') as f:f.write(c+' '+str(a)+'\\n')\n if c=='states':time.sleep(.4)\n if c=='logs':\n  time.sleep(.04);offset=a.get('offset',0);data='Яx'.encode();part=data[:1] if offset==0 else data[offset:]\n  result=dict(file='fixture.log',offset=offset+len(part),content=base64.b64encode(part).decode(),events=[dict(id=1,text='E')] if a.get('event',0)==0 else [])\n else:result={}\n print(json.dumps(dict(id=r['id'],ok=True,result=result)),flush=True)\n",new UTF8Encoding(false));
  using(var process=Process.Start(new ProcessStartInfo(python,"-u "+J.Quote(script)+" "+J.Quote(record)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true}))
  using(var main=new MainWindow(root,null,false,false)){
   main.StartPosition=FormStartPosition.Manual;main.Location=new Point(-6000,-5000);main.Show();
   var backend=new Backend(root);Set(backend,"process",process);Set(backend,"input",process.StandardInput);main.Backend=backend;
   var footer=Field<Label>(main,"footer");footer.Text="Ready";bool done=false;
   main.Read("Transient loading",()=>1,value=>done=true,null,"fast");Pump(()=>done);
   Check(footer.Text=="Ready","fast cached read never flashes loading text");
   using(var gate=new ManualResetEventSlim()){
    done=false;main.Read("Delayed loading",()=>{gate.Wait(30000);return 1;},value=>done=true,null,"slow");
    var watch=new Stopwatch();for(int i=0;i<100;i++){watch.Restart();Field<WorkspaceTabs>(main,"workspace").SelectedIndex=i%2;Application.DoEvents();navigation.Add(watch.Elapsed.TotalMilliseconds);}
    double navigation95=navigation.OrderBy(x=>x).ElementAt(94);Check(!main.Busy&&navigation95<=50,"navigation p95 remains within 50ms while data is pending: "+navigation95+"ms");
    Pump(()=>footer.Text=="Delayed loading");gate.Set();Pump(()=>done);Check(footer.Text=="","delayed indicator clears after completion");
   }
   var host=new EmbeddedToolPage(name=>new LiveLogsView(main,name,false,true)){Dock=DockStyle.Fill};main.Controls.Add(host);host.BringToFront();host.SelectMachine("test");
   using(var logs=(LiveLogsView)host.Tool){
    Pump(()=>Field<long>(logs,"offset")==1&&!Field<bool>(logs,"pending"));
    Check(Field<ThemedEditor>(logs,"qemu").Text=="","incomplete UTF-8 character stays in the decoder");
    int before=Lines(record);host.Visible=false;For(1200);
    Check(!Field<System.Windows.Forms.Timer>(logs,"timer").Enabled&&Lines(record)==before,"hidden journal stops timer and polling: timer="+Field<System.Windows.Forms.Timer>(logs,"timer").Enabled+" lines="+before+"->"+Lines(record));
    host.Visible=true;Pump(()=>Field<ThemedEditor>(logs,"qemu").Text=="Яx");
    Check(Field<long>(logs,"offset")==3&&Field<ThemedEditor>(logs,"events").Text=="E"+Environment.NewLine,"reactivation resumes offsets, UTF-8 decoder and event cursor without duplicates");
    logs.Paused=true;before=Lines(record);For(1200);Check(Lines(record)==before,"pause stops polling");
    logs.SetHostVisible(false);
   }
   host.Dispose();
   var block=backend.CallAsync("states",null,true);Pump(()=>ReadRecord(record).Contains("states"));int callbacks=0;
   main.Read("Cancelled read",()=>backend.Call("logs",J.O("name","cancelled")),value=>callbacks++,null,"cancel-me");main.CancelRead("cancel-me");
   Pump(()=>block.IsCompleted);For(100);Check(callbacks==0&&!ReadRecord(record).Contains("cancelled"),"cancelled queued read never reaches transport or applies a callback");
   main.Close();
  }
  var service=new SnapshotService();
  using(var view=new SnapshotsWindow(service,"test",Catalog())){view.StartPosition=FormStartPosition.Manual;view.Location=new Point(-6000,-5000);view.Show();Application.DoEvents();service.Fail=true;var reload=view.Reload();Pump(()=>reload.IsCompleted);Check(Field<Label>(view,"note").Text.Contains("fixture refresh failure"),"cached catalog keeps a local refresh error with retry available");Check(Field<SnapshotUiList>(view,"list").Visible&&Field<SnapshotUiList>(view,"list").Items.Count==1,"failed refresh preserves the displayed cached snapshot");Check(Field<Button>(view,"refresh").Enabled,"refresh can be retried after an error");view.Close();}
  File.WriteAllText(Path.Combine(output,"stage1-interaction-results.json"),J.Json().Serialize(J.O("passed",checks,"navigation_samples",navigation.Count,"navigation_p95_ms",navigation.OrderBy(x=>x).ElementAt(94),"navigation_max_ms",navigation.Max())));return 0;
 }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"stage1-interaction-error.txt"),error.ToString());return 1;}}
}
