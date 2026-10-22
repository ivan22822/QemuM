using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Real forms, local RPC, private temporary media and real qemu-img. The test
// host injects an empty process inventory for this fresh private-only library.
static class SnapshotRpcTests {
    static T Field<T>(object value,string name){return (T)value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(value);}
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static void Until(Func<bool> ready){var deadline=DateTime.UtcNow.AddSeconds(90);while(!ready()){Application.DoEvents();Thread.Sleep(10);if(DateTime.UtcNow>deadline)throw new Exception("RPC UI timeout");}Application.DoEvents();}
    static void Complete(Task task){Until(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    static void Tool(string exe,string arguments){using(var process=Process.Start(new ProcessStartInfo(exe,arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true})){string error=process.StandardError.ReadToEnd();process.StandardOutput.ReadToEnd();Check(process.WaitForExit(15000)&&process.ExitCode==0,error);}}
    [STAThread]static int Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Program.TestMode=true;
        string output=args[0],root=args[1],python=args[2];Backend backend=null;MainWindow main=null;bool guestStarted=false;
        try{
            string disk=Path.Combine(root,"snapshot-private.qcow2");Tool("qemu-img","create -f qcow2 "+J.Quote(disk)+" 8M");
            File.WriteAllText(Path.Combine(root,"machines","snapshot.json"),J.Json().Serialize(J.O("disk_path",disk,"disk_format","qcow2","iso_path","","ram_mb",128,"cores",1,"extra_args",new[]{"-S","-display","none"},"launch_profile","tcg")));
            backend=new Backend(root,python);backend.Open();main=new MainWindow(root,python,false,false);main.Backend=backend;main.Show();main.Reload();Until(()=>!main.Busy);
            var service=new LegacySnapshotUiService(main,"snapshot");var load=service.Load();Complete(load);
            using(var window=new SnapshotsWindow(service,"snapshot",load.Result)){
                window.Show();Application.DoEvents();int accepted=0;var responsive=Stopwatch.StartNew();long lastTick=0,maxGap=0;int ticks=0;
                using(var timer=new System.Windows.Forms.Timer{Interval=30}){
                    timer.Tick+=(sender,eventArgs)=>{
                        long now=responsive.ElapsedMilliseconds;if(lastTick>0)maxGap=Math.Max(maxGap,now-lastTick);lastTick=now;ticks++;
                        foreach(Form form in Application.OpenForms.Cast<Form>().ToArray()){
                            var options=form as SnapshotOperationDialog;
                            if(options!=null){var fields=Field<Dictionary<string,Control>>(options,"fields");if(fields.ContainsKey("label"))fields["label"].Text="Через кнопку";Field<Button>(options,"apply").PerformClick();}
                            var plan=form as SnapshotPlanDialog;if(plan!=null&&plan.DialogResult!=DialogResult.OK){accepted++;((Button)plan.AcceptButton).PerformClick();}
                        }
                    };
                    timer.Start();Field<Button>(window,"create").PerformClick();Until(()=>Field<bool>(window,"busy"));Until(()=>!Field<bool>(window,"busy"));timer.Stop();
                }
                Check(accepted==1,"actual create button must confirm one server plan: "+accepted+" · "+Field<Label>(window,"note").Text);
                Check(ticks>2&&maxGap<2000,"window timer remains responsive while disk job runs");File.WriteAllText(Path.Combine(output,"snapshot-ui-responsiveness.json"),J.Json().Serialize(J.O("timer_ticks",ticks,"max_interval_ms",maxGap,"create_total_ms",responsive.ElapsedMilliseconds)));
                var selected=Field<SnapshotUiEntry>(window,"selected");Check(selected!=null&&selected.Name=="Через кнопку","created group is selected");
                string id=selected.Id;Tool("qemu-io","-f qcow2 -c "+J.Quote("write -P 0x66 0 4096")+" "+J.Quote(disk));
                using(var timer=new System.Windows.Forms.Timer{Interval=30}){
                    timer.Tick+=(sender,eventArgs)=>{foreach(var plan in Application.OpenForms.OfType<SnapshotPlanDialog>().ToArray())((Button)plan.AcceptButton).PerformClick();};timer.Start();
                    Complete(window.Perform(new SnapshotUiRequest("restore",selected.Raw,J.O("restore_scope","disks"))));timer.Stop();
                }
                Tool("qemu-io","-f qcow2 -c "+J.Quote("read -P 0x00 0 4096")+" "+J.Quote(disk));
                Check(J.S(backend.Call("states"),"unused")=="","service remains responsive");
                Complete(window.Reload());Check(Field<SnapshotUiEntry>(window,"selected").Id==id,"refresh preserves stable selection");
                using(var timer=new System.Windows.Forms.Timer{Interval=30}){
                    timer.Tick+=(sender,eventArgs)=>{foreach(var plan in Application.OpenForms.OfType<SnapshotPlanDialog>().ToArray())if(plan.DialogResult!=DialogResult.OK)((Button)plan.AcceptButton).PerformClick();};timer.Start();
                    guestStarted=true;Complete(window.Perform(new SnapshotUiRequest("restore_start",Field<SnapshotUiEntry>(window,"selected").Raw,J.O("restore_scope","disks"))));timer.Stop();
                }
                object online=backend.Call("snapshots",J.O("name","snapshot"));Check(J.B(online,"online"),"restore and start reaches owned paused TCG process");
                backend.Call("control",J.O("name","snapshot","action","quit","confirm","snapshot"));guestStarted=false;
                Until(()=>{try{return !J.B(backend.Call("snapshots",J.O("name","snapshot")),"online");}catch(InvalidOperationException){return false;}});
                Complete(window.Reload());
                using(var timer=new System.Windows.Forms.Timer{Interval=30}){
                    timer.Tick+=(sender,eventArgs)=>{foreach(var plan in Application.OpenForms.OfType<SnapshotPlanDialog>().ToArray())((Button)plan.AcceptButton).PerformClick();};timer.Start();
                    Complete(window.Perform(new SnapshotUiRequest("delete",Field<SnapshotUiEntry>(window,"selected").Raw)));timer.Stop();
                }
                Check(!J.A(J.V(backend.Call("snapshots",J.O("name","snapshot")),"rows")).Any(row=>J.S(row,"id")==id),"exact selected UUID deleted");
                window.Close();
            }
            main.Close();File.WriteAllText(Path.Combine(output,"snapshot-rpc-results.txt"),"Passed: actual create button and plan confirmation, async RPC, offline restore without boot, exact UUID delete and stable selection. Private process inventory injected; actual image leases and qemu-img used.");return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(output,"snapshot-rpc-error.txt"),error.ToString());return 1;}
        finally{if(guestStarted&&backend!=null){try{backend.Call("control",J.O("name","snapshot","action","quit","confirm","snapshot"));}catch(InvalidOperationException){}}if(main!=null)main.Dispose();if(backend!=null)backend.Dispose();}
    }
}
