using System;using System.IO;using System.Linq;using System.Drawing;using System.Reflection;using System.Diagnostics;using System.Threading;using System.Threading.Tasks;using System.Windows.Forms;
static class Stage3LifecycleTests {
 static int checks;static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
 static T Field<T>(object value,string name){return (T)value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(value);}
 static void Pump(Func<bool> ready){var time=Stopwatch.StartNew();while(!ready()&&time.ElapsedMilliseconds<4000){Application.DoEvents();Thread.Sleep(5);}Check(ready(),"pending UI work completes");}
 sealed class Probe : UserControl,IToolVisibility {internal readonly TextBox Input=new TextBox();internal bool Active;internal Probe(){Controls.Add(Input);}public void SetHostVisible(bool visible){Active=visible;}}
 sealed class Service : ISnapshotUiService,ISnapshotReadLifetime {
  internal int Loads,Cancels,Subscriptions,Executions;internal TaskCompletionSource<object> Next,Write;
  Action<SnapshotUiProgress> progress;public event Action<SnapshotUiProgress> Progress{add{progress+=value;Subscriptions++;}remove{progress-=value;Subscriptions--;}}
  public bool Supports(string feature){return true;}
  public Task<object> Load(){Loads++;return Next==null?Task.FromResult(Catalog("ready")):Next.Task;}
  public void CancelLoad(){Cancels++;}
  public Task<object> Execute(SnapshotUiRequest request){Executions++;return Write==null?Task.FromResult((object)J.O()):Write.Task;}
 }
 static object Catalog(string name){return J.O("vm_state","stopped","rows",new object[]{J.O("id",name,"name",name,"state","ready")});}
 [STAThread]static int Main(string[] args){try{Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Program.TestMode=true;
  using(var list=new ThemedList()){
   int notifications=0;list.SelectedIndexChanged+=(s,e)=>notifications++;
   list.BeginUpdate();list.Items.AddRange(Enumerable.Range(0,10000).Cast<object>().ToArray());list.SelectedIndex=9999;Check(notifications==0,"nested batch defers selection events");list.EndUpdate();Check(notifications==1&&list.SelectedIndex==9999,"bulk selection emits one final event");
   notifications=0;list.BeginUpdate();list.Items.Clear();Check(list.SelectedItem==null,"selection is valid immediately after clear inside a batch");list.Items.AddRange(new object[]{"a","b"});list.SelectedIndex=1;list.EndUpdate();Check(notifications==1&&Convert.ToString(list.SelectedItem)=="b","replacement publishes only the final selection");
  }
  using(var host=new Form{ClientSize=new Size(950,650),StartPosition=FormStartPosition.Manual,Location=new Point(-6000,-5000)}){
   int created=0;var page=new EmbeddedToolPage(name=>{created++;return new Probe();}){Dock=DockStyle.Fill};host.Controls.Add(page);page.SelectMachine("a");Check(created==0,"hidden page does not construct views");host.Show();Application.DoEvents();var a=(Probe)page.Tool;a.Input.Text="retained input";
   page.SelectMachine("b");var b=(Probe)page.Tool;Check(!a.Active&&b.Active&&!a.IsDisposed,"deactivation retains exactly the previous view");page.SelectMachine("a");Check(Object.ReferenceEquals(a,page.Tool)&&a.Input.Text=="retained input","returning to previous machine restores its view and input");
   page.SelectMachine("c");Check(b.IsDisposed&&page.Controls.Count==2,"third machine evicts the inactive view");var c=(Probe)page.Tool;
   for(int i=0;i<100;i++)page.SelectMachine(i%2==0?"a":"c");Check(created==3&&page.Controls.Count==2,"100 switches reuse a bounded pair of views");page.Visible=false;Check(!a.Active&&!c.Active,"hidden page deactivates every retained view");page.Dispose();Check(a.IsDisposed&&c.IsDisposed,"host disposal releases both cached views");host.Close();
  }
  var service=new Service();var initialRead=new TaskCompletionSource<object>();service.Next=initialRead;
  using(var host=new Form{ClientSize=new Size(1000,700),StartPosition=FormStartPosition.Manual,Location=new Point(-6000,-5000)})using(var view=new SnapshotCatalogView(service,"fixture",null,false,true)){
   Check(service.Loads==0&&service.Executions==0,"snapshot view construction performs no I/O");host.Controls.Add(view);host.Show();view.SetHostVisible(true);Check(service.Loads==1,"activation starts one catalog read");
   view.SetHostVisible(false);Check(service.Cancels==1&&!Field<bool>(view,"busy"),"deactivation cancels reading and releases its loading state");var second=new TaskCompletionSource<object>();service.Next=second;view.SetHostVisible(true);Check(service.Loads==2,"reactivation starts a fresh generation");second.SetResult(Catalog("fresh"));Pump(()=>!Field<bool>(view,"busy"));initialRead.SetResult(Catalog("stale"));Application.DoEvents();var list=Field<SnapshotUiList>(view,"list");Check(((SnapshotUiEntry)list.Items[0]).Name=="fresh","late cancelled catalog cannot replace the active generation");
   service.Next=null;service.Write=new TaskCompletionSource<object>();var operation=view.Perform(new SnapshotUiRequest("edit"));Check(view.WriteBusy&&!view.CanClose(),"active write prevents closing or view replacement");view.SetHostVisible(false);Check(!operation.IsCompleted&&service.Cancels==1,"deactivation preserves an active write");service.Write.SetResult(J.O());Pump(()=>operation.IsCompleted);Check(view.CanClose()&&service.Executions==1,"confirmed write completes once and permits closing");host.Close();
  }
  Check(service.Subscriptions==0,"snapshot disposal removes its progress subscription");
  var guardedService=new Service();using(var host=new Form{ClientSize=new Size(1000,700),StartPosition=FormStartPosition.Manual,Location=new Point(-6000,-5000)}){
   var page=new EmbeddedToolPage(name=>new SnapshotCatalogView(guardedService,name,Catalog(name),false,true)){Dock=DockStyle.Fill};host.Controls.Add(page);host.Show();page.SelectMachine("first");var first=(SnapshotCatalogView)page.Tool;guardedService.Write=new TaskCompletionSource<object>();var write=first.Perform(new SnapshotUiRequest("edit"));page.SelectMachine("second");Check(Object.ReferenceEquals(first,page.Tool)&&!first.IsDisposed&&!page.CanSelectMachine("second"),"page cannot replace a view while it owns an active write");guardedService.Write.SetResult(J.O());Pump(()=>write.IsCompleted);page.SelectMachine("second");Check(!Object.ReferenceEquals(first,page.Tool)&&page.CanSelectMachine("first"),"confirmed completion releases the navigation guard");host.Close();
  }
  Check(guardedService.Subscriptions==0,"guarded page releases both retained subscriptions");
  File.WriteAllText(Path.Combine(args[1],"stage3-lifecycle-results.json"),J.Json().Serialize(J.O("passed",checks)));return 0;
 }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"stage3-lifecycle-error.txt"),"After "+checks+" checks: "+error);return 1;}}
}
