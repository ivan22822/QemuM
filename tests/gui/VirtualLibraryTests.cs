using System;using System.IO;using System.Linq;using System.Drawing;using System.Reflection;using System.Diagnostics;using System.Threading;using System.Runtime.InteropServices;using System.Windows.Forms;
static class VirtualLibraryTests {
 [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr word,IntPtr value);
 static int checks;static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
 static T Field<T>(object value,string name){return (T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);}
 static void Fill(MainWindow main,object[] rows){typeof(MainWindow).GetMethod("Fill",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(main,new[]{J.O("rows",rows,"mode","normal")});}
 static object[] Rows(int count){return Enumerable.Range(0,count).Select(i=>(object)J.O("name","vm-"+i,"data",J.O("label","Virtual VM "+i,"ram_mb",256,"cores",1))).ToArray();}
 static void Pump(Func<bool> ready){var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<10000){Application.DoEvents();Thread.Sleep(5);}Check(ready(),"virtual viewport update completes");}
 [STAThread]static int Main(string[] args){try{Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Program.TestMode=true;double fillMs,warmMs;
  using(var main=new MainWindow(args[0],null,false,false)){main.StartPosition=FormStartPosition.Manual;main.Location=new Point(-6000,-5000);main.Show();var data=Rows(200);var watch=Stopwatch.StartNew();Fill(main,data);fillMs=watch.Elapsed.TotalMilliseconds;
   var panel=(VirtualLibraryPanel)Field<FlowLayoutPanel>(main,"cards");var scroll=Field<ScrollSurface>(main,"libraryScroll");
   Check(panel.VirtualMode&&panel.ModelCount==200,"all 200 rows are present in the model");Check(panel.Controls.Count>0&&panel.Controls.Count<32,"native controls are limited to the viewport");
   var first=panel.Controls.OfType<LibraryCard>().Single(c=>Convert.ToString(c.Tag)=="vm-0");watch.Restart();Fill(main,data);warmMs=watch.Elapsed.TotalMilliseconds;Check(Object.ReferenceEquals(first,panel.Controls.OfType<LibraryCard>().Single(c=>Convert.ToString(c.Tag)=="vm-0")),"unchanged row keeps its control across refresh");
   Check(panel.AccessibilityObject.GetChildCount()==200,"accessibility exposes offscreen rows");
   panel.EnsureKeyVisible("vm/vm-199",true);Application.DoEvents();Check(Field<string>(main,"selected")=="vm-199"&&scroll.ScrollOffset>0,"last row can be selected and scrolled into view");
   Check(panel.Controls.Count<32&&panel.Controls.OfType<LibraryCard>().Any(c=>Convert.ToString(c.Tag)=="vm-199"),"last row is materialized without creating preceding controls");
   panel.AccessibilityObject.GetChild(0).DoDefaultAction();Application.DoEvents();Check(Field<string>(main,"selected")=="vm-0"&&scroll.ScrollOffset==0,"accessible default action restores an offscreen row");
   for(int i=0;i<20;i++){var focused=panel.Controls.OfType<LibraryCard>().Single(c=>c.ContainsFocus);SendMessage(focused.Handle,0x100,new IntPtr((int)Keys.Down),IntPtr.Zero);Application.DoEvents();}
   Check(Field<string>(main,"selected")=="vm-20"&&scroll.ScrollOffset>0,"keyboard navigation crosses viewport boundaries");
   var visible=panel.Controls.Cast<Control>().OrderBy(c=>c.Top).ToArray();Check(visible.Zip(visible.Skip(1),(a,b)=>a.Bottom<=b.Top).All(v=>v),"visible rows never overlap");
   var search=Field<FieldInput>(main,"search");search.Text="vm-19";Pump(()=>panel.ModelCount==11);Check(panel.AccessibilityObject.GetChildCount()==11,"search filters model rows as well as visible controls");search.Clear();Pump(()=>panel.ModelCount==200);
   Fill(main,Rows(4));Check(!panel.VirtualMode&&panel.Controls.OfType<LibraryCard>().Count()==4,"small library preserves its ordinary controls");
   var stands=Enumerable.Range(0,100).Select(i=>(object)J.O("id","stand-"+i,"name","Стенд "+i,"revision",1,"members",new object[]{"vm-0","vm-1"},"order",new object[]{"vm-0","vm-1"},"links",new object[0])).ToList();
   typeof(MainWindow).GetField("stands",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(main,stands);typeof(MainWindow).GetField("standMode",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(main,true);
   typeof(MainWindow).GetMethod("RenderStands",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(main,null);Check(panel.VirtualMode&&panel.ModelCount==100&&panel.Controls.Count<32,"large stand library uses the same bounded viewport");
   Fill(main,Rows(4));Check(panel.ModelCount==101&&panel.Controls.OfType<StandFolderButton>().Any(),"refreshing machine data preserves stand viewport ownership and displays the unavailable backend error");
   panel.EnsureKeyVisible("stand/stand-99",false);Check(panel.Controls.OfType<StandFolderButton>().Any(c=>J.S(c.Tag,"id")=="stand-99"),"last stand materialized at offset "+scroll.ScrollOffset+" height "+panel.Height+" keys "+String.Join(",",panel.Controls.Cast<Control>().Select(c=>J.S(c.Tag,"id"))));var folder=panel.Controls.OfType<StandFolderButton>().Single(c=>J.S(c.Tag,"id")=="stand-99");folder.Controls.OfType<Button>().Single().PerformClick();Application.DoEvents();Check(panel.ModelCount==103,"last stand expands into model member rows");
   panel.EnsureKeyVisible("member/stand-99/vm-1",true);Check(Field<string>(main,"standSelectedParent")=="stand-99"&&Field<string>(main,"standSelectedVm")=="vm-1","virtual stand membership keeps its parent identity");main.Close();
  }
  File.WriteAllText(Path.Combine(args[1],"virtual-library-results.json"),J.Json().Serialize(J.O("passed",checks,"apply_200_ms",fillMs,"warm_apply_200_ms",warmMs)));return 0;
 }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"virtual-library-error.txt"),"After "+checks+" checks: "+error.ToString());return 1;}}
}
