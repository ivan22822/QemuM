using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
static class BackendWaitTests {
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static void Set(object o,string key,object value){o.GetType().GetField(key,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(o,value);}
 [STAThread]static int Main(string[] args){try{
 string root=args[0],python=args[1],script=Path.Combine(root,"protocol.py"),count=Path.Combine(root,"requests.txt");
 File.WriteAllText(script,"import sys,json,time\nfor line in sys.stdin:\n r=json.loads(line)\n with open(sys.argv[1],'a') as f:f.write(r['command']+'\\n')\n if r['command'] in ('list','write'):time.sleep(.25)\n print(json.dumps(dict(id=r['id'],ok=True,result=r['command'])),flush=True)\n",new UTF8Encoding(false));
 var start=new ProcessStartInfo(python,"-u "+J.Quote(script)+" "+J.Quote(count)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
 using(var process=Process.Start(start))using(var backend=new Backend(root)){
 Set(backend,"process",process);Set(backend,"input",process.StandardInput);Backend.ReadTimeoutMilliseconds=40;Backend.WriteTimeoutMilliseconds=40;Backend.QueueTimeoutMilliseconds=30;
 var sw=Stopwatch.StartNew();bool timeout=false;try{backend.Call("list");}catch(TimeoutException){timeout=true;}Check(timeout&&sw.ElapsedMilliseconds<2000,"read deadline releases caller");
 Backend.ReadTimeoutMilliseconds=1500;Check((string)backend.Call("logs")=="logs","late read response drained; next reply id preserved");
 timeout=false;try{backend.Call("write");}catch(TimeoutException){timeout=true;}Check(timeout,"unconfirmed write has a deadline");Check((string)backend.Call("states")=="states","late write response drained before next read");
 Check(File.ReadAllLines(count).Count(x=>x=="write")==1,"write submitted once; never repeated on timeout");
 Backend.ReadTimeoutMilliseconds=1500;var first=Task.Run(()=>backend.Call("list"));System.Threading.Thread.Sleep(40);timeout=false;try{backend.Call("logs");}catch(TimeoutException){timeout=true;}Check(timeout,"queue wait is bounded");Check(first.Wait(2000),"original request finishes");
 }
 File.WriteAllText(Path.Combine(root,"result.txt"),"PASS: read deadline, write uncertainty, late response alignment, no write retry, bounded queue");return 0;
 }catch(Exception e){File.WriteAllText(Path.Combine(args[0],"error.txt"),e.ToString());return 1;}}
}
