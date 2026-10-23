using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class StandsScaleTests {
    static int checks;
    static T Field<T>(object x,string n){return (T)x.GetType().GetField(n,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(x);}
    static IEnumerable<Control> All(Control x){foreach(Control c in x.Controls){yield return c;foreach(var d in All(c))yield return d;}}
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
    static void Wait(MainWindow main){var until=DateTime.UtcNow.AddSeconds(30);while(main.Busy&&DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(10);}Check(!main.Busy&&main.TestError==null,"background load completes");}
    static void Call(MainWindow main,string name,params object[] args){typeof(MainWindow).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,args);}
    [DllImport("user32.dll")]static extern uint GetGuiResources(IntPtr handle,uint flags);
    static void Trace(string output,string phase){File.AppendAllText(Path.Combine(output,"stands-performance-progress.txt"),DateTime.UtcNow.ToString("o")+" "+phase+Environment.NewLine);}
    [STAThread]static int Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Program.TestMode=true;
        try{
            File.WriteAllText(Path.Combine(args[1],"stands-performance-progress.txt"),"");Trace(args[1],"create fixture");
            var names=Enumerable.Range(0,500).Select(i=>"node"+i).ToArray();
            foreach(var name in names)File.Copy(Path.Combine(args[0],"machines","test.json"),Path.Combine(args[0],"machines",name+".json"));
            var measurements=new Dictionary<string,object>();
            using(var main=new MainWindow(args[0],null,false,false)){
                main.Backend=new Backend(args[0]);main.Backend.Open();
                for(int i=0;i<100;i++){
                    var members=names.Skip(i*5).Take(5).ToArray();
                    main.Backend.Call("stand_save",J.O("revision",0,"stand",J.O("schema_version",1,"id",Guid.NewGuid().ToString("N"),"revision",0,"name","Стенд "+i+" · длинное название лаборатории для проверки списка","description","","members",members,"order",members,"links",new object[0])));
                    if(i%20==0)Trace(args[1],"definitions "+i);
                }
                main.Show();Application.DoEvents();var toggle=All(main).OfType<CheckBox>().Single(c=>c.Text=="Режим стендов");toggle.Checked=true;Wait(main);
                Trace(args[1],"read library");
                var watch=Stopwatch.StartNew();var rows=main.Backend.Call("list");watch.Stop();measurements["read_501_vm_ms"]=watch.ElapsedMilliseconds;
                watch.Restart();Call(main,"Fill",rows);Wait(main);watch.Stop();measurements["render_100_stands_ms"]=watch.ElapsedMilliseconds;
                var cards=Field<FlowLayoutPanel>(main,"cards");Check(cards.Controls.OfType<Button>().Count()==100,"large stand list renders every folder");
                Trace(args[1],"open folders");
                GC.Collect();GC.WaitForPendingFinalizers();uint gdiBefore=GetGuiResources(Process.GetCurrentProcess().Handle,0);
                watch.Restart();for(int i=0;i<30;i++){cards.Controls.OfType<Button>().First().PerformClick();Application.DoEvents();}watch.Stop();measurements["open_folder_30_times_ms"]=watch.ElapsedMilliseconds;
                watch.Restart();for(int i=0;i<50;i++)Call(main,"PaintStandStatus");watch.Stop();measurements["paint_100_statuses_50_times_ms"]=watch.ElapsedMilliseconds;
                Trace(args[1],"focus and scaling");
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();uint gdiAfter=GetGuiResources(Process.GetCurrentProcess().Handle,0);measurements["gdi_delta"]=((long)gdiAfter)-gdiBefore;Check(gdiAfter<=gdiBefore+300,"folder refresh has bounded GDI resources");
                var search=Field<FieldInput>(main,"search");search.Text="Стенд 0";search.Focus();Application.DoEvents();string before=search.Text;Call(main,"PaintStandStatus");Check(search.Text==before&&search.ContainsFocus,"status paint preserves search focus and text");search.Text="";
                var split=All(main).OfType<SplitContainer>().Single();int width=split.SplitterDistance;var key=new KeyEventArgs(Keys.Right);typeof(Control).GetMethod("OnKeyDown",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(split,new object[]{key});Check(split.SplitterDistance>=width&&key.Handled,"splitter accepts keyboard resizing");
                foreach(float scale in new[]{1F,1.25F,1.5F,2F})using(var operation=new StandOperationWindow(null,new string('1',32))){
                    operation.StatusProvider=stop=>System.Threading.Tasks.Task.FromResult<object>(J.O("state","completed","phase","Проверка размеров","current","node0","events",new object[0]));
                    operation.Scale(new SizeF(scale,scale));operation.Show();Application.DoEvents();
                    Check(All(operation).OfType<Button>().All(b=>b.Width>0&&b.Height>0),"operation layout supports synthetic scale "+scale);
                    using(var image=new Bitmap(operation.Width,operation.Height)){operation.DrawToBitmap(image,operation.ClientRectangle);image.Save(Path.Combine(args[1],"stands-layout-scale-"+scale.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)+".png"));}operation.Close();
                }
                main.Close();Application.DoEvents();
                Trace(args[1],"complete");
            }
            measurements["checks"]=checks;measurements["scaling"]="synthetic layout; physical monitors and real DPI not tested";measurements["physical_screens_detected"]=Screen.AllScreens.Length;
            File.WriteAllText(Path.Combine(args[1],"stands-performance.json"),J.Json().Serialize(measurements));return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"stands-scale-error.txt"),error.ToString());return 1;}
    }
}
