using System;using System.IO;using System.Threading;using System.Diagnostics;using System.Windows.Forms;
static class ReadUiLifecycleTests {
 static int checks;static void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;}
 static void Pump(Func<bool> done){var clock=Stopwatch.StartNew();while(!done()&&clock.ElapsedMilliseconds<15000){Application.DoEvents();Thread.Sleep(5);}Check(done(),"read completes");}
 [STAThread]static int Main(string[] args){try{Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Program.TestMode=true;Control.CheckForIllegalCrossThreadCalls=true;int ui=Thread.CurrentThread.ManagedThreadId;
 using(var main=new MainWindow(args[0],args[2],false,false)){main.Show();Application.DoEvents();SynchronizationContext.SetSynchronizationContext(null);bool applied=false;
 main.Read("Read UI fixture",()=>{Thread.Sleep(25);return (object)1;},value=>{Check(Thread.CurrentThread.ManagedThreadId==ui,"apply stays on UI thread after context loss");Check(main.Reading,"read remains active while the scene is rebuilt");applied=true;});
 Pump(()=>applied&&!main.Reading);bool failed=false;
 main.Read("Read UI failure",()=>1,value=>{throw new InvalidOperationException("apply failed");},error=>{Check(Thread.CurrentThread.ManagedThreadId==ui,"failure callback stays on UI thread");failed=error.Message=="apply failed";});
 Pump(()=>failed&&!main.Reading);
 using(var gate=new ManualResetEvent(false)){bool first=false,second=false;
 main.Read("First",()=>1,value=>{first=true;main.Read("Second",()=>{gate.WaitOne(5000);return 2;},next=>second=true,key:"same");},key:"same");
 Pump(()=>first);Check(main.Reading,"finishing the first callback retains its replacement read");gate.Set();Pump(()=>second&&!main.Reading);}
 main.Close();}
 File.WriteAllText(Path.Combine(args[1],"read-ui-lifecycle-results.txt"),"PASS "+checks);return 0;
 }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"read-ui-lifecycle-error.txt"),error.ToString());return 1;}}
}
