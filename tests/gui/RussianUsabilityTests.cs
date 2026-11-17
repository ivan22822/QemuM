using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
static class RussianUsabilityTests {
 sealed class Service:ISnapshotUiService {
  public TaskCompletionSource<object> Pending=new TaskCompletionSource<object>();public int Calls;
  public bool Supports(string x){return false;}public Task<object> Load(){Calls++;return Pending.Task;}public Task<object> Execute(SnapshotUiRequest r){throw new Exception("Unexpected write");}public event Action<SnapshotUiProgress> Progress{add{}remove{}}
 }
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static T Field<T>(object o,string key){return (T)o.GetType().GetField(key,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);}
 static void Pump(Task task){var until=DateTime.UtcNow.AddSeconds(5);while(!task.IsCompleted&&DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(10);}Check(task.IsCompleted,"request UI completes");task.GetAwaiter().GetResult();}
 static void Save(Form f,string path){Application.DoEvents();using(var b=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(path);}}
 [STAThread]static int Main(string[] args){Point oldCursor=Cursor.Position;try{Application.EnableVisualStyles();Program.TestMode=true;string output=args[0];SnapshotsWindow.CatalogLoadTimeoutMilliseconds=80;
 var service=new Service();var entry=J.O("id","one","name","Снимок 2026-10-22 15:07 — перед обновлением системы","date",1700000000,"kind","disk","state","ready");var catalog=J.O("online",false,"vm_state","stopped","rows",new object[]{entry});
 using(var window=new SnapshotsWindow(service,"fixture",catalog)){window.Show();Save(window,Path.Combine(output,"snapshots.png"));Check(window.Text.StartsWith("Снимки"),"Russian catalog title");var read=window.Reload();Pump(read);Check(!Field<bool>(window,"busy")&&!Field<bool>(window,"loading"),"timeout resets catalog loading and actions");Check(Field<Label>(window,"note").Text.Contains("Не удалось"),"timeout visible with retry");service.Pending.SetResult(catalog);Pump(window.Reload());Check(service.Calls==2&&!Field<bool>(window,"failed"),"catalog retry succeeds; stale completion does not overwrite");window.Close();}
 using(var window=new SnapshotsWindow(new Service(),"fixture",catalog)){window.Show();var pending=window.Reload();window.Close();Check(window.IsDisposed,"read-only loading can close");Pump(pending);}
 using(var form=new Form{Size=new Size(600,450),StartPosition=FormStartPosition.Manual,Location=new Point(120,120)}){
 var content=Theme.Flow(true);for(int n=0;n<10;n++){var card=new ConfigurationInspector("Адаптер "+n){Size=new Size(500,200)};card.Value("Модель","virtio-net-pci");content.Controls.Add(card);}var scroll=new ScrollSurface(content);form.Controls.Add(scroll);form.Show();form.BringToFront();Application.DoEvents();var label=content.Controls[0].Controls[0];Cursor.Position=label.PointToScreen(new Point(30,30));Application.DoEvents();int before=scroll.ScrollOffset;var wheel=Message.Create(label.Handle,0x020A,new IntPtr(unchecked((int)0xFF880000)),IntPtr.Zero);bool handled=scroll.PreFilterMessage(ref wheel);var hit=Control.FromHandle((IntPtr)typeof(ScrollSurface).GetMethod("WindowFromPoint",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{Cursor.Position}));string chain="";for(var c=hit;c!=null;c=c.Parent)chain+=c.GetType().Name+" "+c.Bounds+"; ";Check(handled&&scroll.ScrollOffset>before,"wheel over adapter child scrolls nearest surface; handled="+handled+" before="+before+" after="+scroll.ScrollOffset+" max="+scroll.ScrollBar.Maximum+" hit="+chain);form.Close();}
 using(var form=new Form{Size=new Size(300,400)}){var flow=Theme.Flow(true);flow.Controls.Add(new StandFolderButton("Исследовательский стенд\nВыключен · 0/2",()=>{},()=>{}));flow.Controls.Add(new StandMemberButton("fixture","machine",true){Text="Ubuntu Server For IPV6"});form.Controls.Add(flow);form.Show();Check(flow.Controls.OfType<PolarButton>().All(b=>b.CornerRadius>=0&&b.CornerRadius<=6),"stand rows have compact corners");Save(form,Path.Combine(output,"stand-library.png"));form.Close();}
 File.WriteAllText(Path.Combine(output,"result.txt"),"PASS: Russian catalog, deadline and retry, close during read, wheel over adapter child, compact stand rows");return 0;
 }catch(Exception e){File.WriteAllText(Path.Combine(args[0],"error.txt"),e.ToString());return 1;}finally{Cursor.Position=oldCursor;}}
}
