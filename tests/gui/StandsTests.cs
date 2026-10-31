using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Threading.Tasks;

static class StandsTests {
    static int checks;
    static T Field<T>(object x,string n){return (T)x.GetType().GetField(n,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(x);}
    static IEnumerable<Control> All(Control x){foreach(Control c in x.Controls){yield return c;foreach(var d in All(c))yield return d;}}
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
    static void Wait(MainWindow main){var until=DateTime.UtcNow.AddSeconds(15);while(main.Busy&&DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(20);}Check(!main.Busy&&main.TestError==null,"RPC did not complete cleanly: "+main.TestError);}
    [STAThread]static int Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Program.TestMode=true;
        try{
            var multicast=J.O("id","lan","type","socket","mode","mcast","address","239.192.10.20","port",23456,"localaddr","127.0.0.1","model","e1000e","mac","52:54:00:00:00:01");
            string unchanged=J.Json().Serialize(multicast);
            using(var adapter=new NetworkAdapterDialog(multicast)){
                adapter.Show();Application.DoEvents();
                Check(Field<ThemedSelector>(adapter,"mode").SelectedIndex==5,"MCAST has a dedicated adapter mode");
                Check(Field<FieldInput>(adapter,"localaddr").Visible,"MCAST exposes host interface field");
                All(adapter).OfType<Button>().Single(b=>b.Text=="Применить").PerformClick();
                Check(J.S(adapter.Value,"mode")=="mcast"&&J.S(adapter.Value,"address")=="239.192.10.20"&&J.S(adapter.Value,"localaddr")=="127.0.0.1","editing MCAST preserves group and host interface");
            }
            Check(J.Json().Serialize(multicast)==unchanged,"adapter dialog never mutates original configuration");
            using(var owner=new Form()){
                owner.Show();Application.DoEvents();var groups=new List<object>();var endpoints=new List<object>{J.O("vm","a","nic","lan"),J.O("vm","b","nic","lan")};
                var edit=typeof(MainWindow).GetMethod("EditMulticast",BindingFlags.NonPublic|BindingFlags.Static);
                owner.BeginInvoke((Action)(()=>{var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Общая сеть · локальный коммутатор");All(form).OfType<Button>().Single(b=>b.Text=="Отмена").PerformClick();}));
                edit.Invoke(null,new object[]{owner,groups,endpoints,(Action)(()=>{}),-1,true});Check(groups.Count==0,"cancel switch network dialog changes no draft data");
                owner.BeginInvoke((Action)(()=>{var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Общая сеть · локальный коммутатор");All(form).OfType<FieldInput>().Single().Text="Тестовая общая сеть";foreach(var check in All(form).OfType<CheckBox>())check.Checked=true;foreach(var button in All(form).OfType<Button>().Where(b=>b.Text=="Применить"||b.Text=="Отмена"))Check(form.ClientRectangle.Contains(form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))),"network footer button fully visible");using(var shot=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(shot,new Rectangle(Point.Empty,form.Size));shot.Save(Path.Combine(args[1],"switch-network-editor.png"));}All(form).OfType<Button>().Single(b=>b.Text=="Применить").PerformClick();}));
                edit.Invoke(null,new object[]{owner,groups,endpoints,(Action)(()=>{}),-1,true});Check(groups.Count==1&&J.S(groups[0],"name")=="Тестовая общая сеть"&&J.A(J.V(groups[0],"members")).Length==2,"common network draft contains name and selected NICs");
                string savedGroup=J.Json().Serialize(groups[0]);
                owner.BeginInvoke((Action)(()=>{var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Общая сеть · локальный коммутатор");All(form).OfType<FieldInput>().Single().Text="Renamed";All(form).OfType<Button>().Single(b=>b.Text=="Отмена").PerformClick();}));
                edit.Invoke(null,new object[]{owner,groups,endpoints,(Action)(()=>{}),0,true});Check(J.Json().Serialize(groups[0])==savedGroup,"cancel network edit preserves existing topology");
                owner.BeginInvoke((Action)(()=>{var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Общая сеть · локальный коммутатор");All(form).OfType<FieldInput>().Single().Text="Renamed";All(form).OfType<Button>().Single(b=>b.Text=="Применить").PerformClick();}));
                edit.Invoke(null,new object[]{owner,groups,endpoints,(Action)(()=>{}),0,true});Check(J.S(groups[0],"name")=="Renamed","network name editing preserves selected NICs");
                owner.BeginInvoke((Action)(()=>{var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Общая сеть · локальный коммутатор");Check(All(form).OfType<CheckBox>().Any(c=>!c.Enabled&&c.Text.Contains("отсутствует")),"missing NIC remains visible and cannot silently disappear");All(form).OfType<Button>().Single(b=>b.Text=="Применить").PerformClick();Check(!form.IsDisposed,"missing NIC prevents saving instead of silently replacing topology");All(form).OfType<Button>().Single(b=>b.Text=="Отмена").PerformClick();}));
                edit.Invoke(null,new object[]{owner,groups,new List<object>{endpoints[0]},(Action)(()=>{}),0,true});
                owner.Close();
            }
            string diagramStandId=Guid.NewGuid().ToString("N"),diagramNetworkId=Guid.NewGuid().ToString("N");
            var graphStand=J.O("id",diagramStandId,"name","Учебная лаборатория", "members",new[]{"Kali","Ubuntu Server","FreeBSD Zombie"},"switches",new[]{J.O("id",diagramNetworkId,"name","Локальная сеть","members",new[]{J.O("vm","Kali","nic","lan"),J.O("vm","Ubuntu Server","nic","lan"),J.O("vm","FreeBSD Zombie","nic","lan")})},"links",new object[0],"multicast",new object[0]);
            string graphBefore=J.Json().Serialize(graphStand);
            using(var graph=new Form()){
                Theme.Form(graph,980,650);graph.Text="Тест схемы";graph.Controls.Add(new ScrollSurface(new StandTopologyCanvas(graphStand)){Dock=DockStyle.Fill});
                graph.Show();Application.DoEvents();var canvas=All(graph).OfType<StandTopologyCanvas>().Single();Check(canvas.AccessibleName=="Схема сетей стенда","network diagram exposes an accessible name");Check(canvas.Height>=330&&canvas.Width>400,"network diagram retains visible drawing area inside scroll surface");
                canvas.AllowMove=true;Dictionary<string,Point> moved=null;canvas.PlacementChanged+=values=>moved=values;
                var start=new Point(canvas.ClientSize.Width-80,80);
                Check((int)typeof(StandTopologyCanvas).GetMethod("NetworkAt",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{start})==0,"switch node is hit at its default position: width="+canvas.ClientSize.Width+" x="+start.X);
                typeof(StandTopologyCanvas).GetMethod("OnMouseDown",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{new MouseEventArgs(MouseButtons.Left,1,start.X,start.Y,0)});
                Check(Field<string>(canvas,"dragKey")!=null,"mouse down begins node drag; capture="+canvas.Capture);
                typeof(StandTopologyCanvas).GetMethod("OnMouseMove",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{new MouseEventArgs(MouseButtons.Left,1,start.X-60,start.Y+55,0)});
                Check(Field<bool>(canvas,"moved"),"mouse movement updates node; capture="+canvas.Capture+" drag="+Field<string>(canvas,"dragKey"));
                typeof(StandTopologyCanvas).GetMethod("OnMouseUp",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{new MouseEventArgs(MouseButtons.Left,1,start.X-60,start.Y+55,0)});
                Check(moved!=null&&moved.ContainsKey("switch:"+diagramNetworkId)&&moved["switch:"+diagramNetworkId].Y>55,"dragging a switch moves its node and reports local positions: "+(moved==null?"no event":J.Json().Serialize(moved)));
                StandDiagramLayout.Save(args[0],diagramStandId,moved);var recovered=StandDiagramLayout.Read(args[0],diagramStandId);Check(recovered["switch:"+diagramNetworkId]==moved["switch:"+diagramNetworkId],"diagram placement persists independently of stand JSON");
                canvas.Focus();int priorX=canvas.Positions()["switch:"+diagramNetworkId].X;typeof(StandTopologyCanvas).GetMethod("OnKeyDown",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{new KeyEventArgs(Keys.Control|Keys.Right)});Check(canvas.Positions()["switch:"+diagramNetworkId].X>priorX,"Ctrl+arrow moves the selected network node by keyboard");
                int oldHeight=canvas.Height;canvas.ZoomPercent=125;Check(canvas.Height>oldHeight&&canvas.Positions()["switch:"+diagramNetworkId]==moved["switch:"+diagramNetworkId],"zoom preserves logical node positions and increases the scrollable scene");
                canvas.ApplyStatus(J.O("members",J.O("Kali","running","Ubuntu Server","stopped","FreeBSD Zombie","stopped"),"network_diagnostics",new[]{J.O("id",diagramNetworkId,"name","Локальная сеть","state","helper-ready","members",new[]{J.O("vm","Kali","nic","lan","connected",true)})}));
                Check(canvas.AccessibleDescription.Contains("Локальная сеть")&&canvas.AccessibleDescription.Contains("гостевых ОС не проверена"),"diagram exposes switch status and guest-connectivity limitation to keyboard and assistive users");
                using(var shot=new Bitmap(graph.Width,graph.Height)){graph.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(args[1],"stand-network-graph.png"));}
                graph.Close();Check(J.Json().Serialize(graphStand)==graphBefore,"viewing the graph does not edit topology");
            }
            using(var broken=new StandTopologyCanvas(graphStand,name=>name=="Kali"?null:J.O("data",J.O("network_adapters",new object[0])))){
                broken.ApplyStatus(J.O("members",J.O("Kali","unknown"),"network_diagnostics",new[]{J.O("id",diagramNetworkId,"state","unknown","members",new object[0])}));
                Check(broken.AccessibleDescription.Contains("VM отсутствует")&&broken.AccessibleDescription.Contains("СОСТОЯНИЕ НЕИЗВЕСТНО"),"missing VM and lost switch are distinct in diagram status");
            }
            using(var tall=new Form()){
                Theme.Form(tall,780,440);var longGraph=new StandTopologyCanvas(J.O("members",Enumerable.Range(0,8).Select(i=>"node"+i).ToArray(),"switches",new object[0],"links",new object[0],"multicast",new object[0]));var surface=new ScrollSurface(longGraph){Dock=DockStyle.Fill};tall.Controls.Add(surface);tall.Show();Application.DoEvents();longGraph.ZoomPercent=150;surface.PerformLayout();Check(surface.ScrollBar.Maximum>0,"large zoomed topology remains vertically scrollable");longGraph.ZoomPercent=75;Check(longGraph.ZoomPercent==75,"topology supports compact zoom without changing stored coordinates");tall.Close();
            }
            StandDiagramLayout.Delete(args[0],diagramStandId);Check(StandDiagramLayout.Read(args[0],diagramStandId).Count==0,"deleting a stand can remove only its UI layout");
            bool invalidLayoutId=false;try{StandDiagramLayout.Read(args[0],"..\\test");}catch(ArgumentException){invalidLayoutId=true;}Check(invalidLayoutId,"layout path rejects non-stand identifiers");
            using(var showcase=new MainWindow(args[0],null,true,false)){
                showcase.Size=new Size(1240,850);showcase.Show();showcase.Demo();Application.DoEvents();
                var switchDemo=J.O("id","demo-lab","name","Лаборатория сетевой безопасности","description","Три машины в одной общей сети","members",new[]{"ubuntu-desktop","ubuntu-server","kali"},"order",new[]{"ubuntu-server","kali","ubuntu-desktop"},"links",new object[0],"switches",new[]{J.O("name","Общий коммутатор","members",new[]{J.O("vm","ubuntu-desktop","nic","lan"),J.O("vm","ubuntu-server","nic","lan"),J.O("vm","kali","nic","lan")})});
                All(showcase).OfType<CheckBox>().Single(c=>c.Text=="Режим стендов").Checked=true;
                typeof(MainWindow).GetField("stands",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(showcase,new List<object>{switchDemo});
                typeof(MainWindow).GetField("standError",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(showcase,"");
                typeof(MainWindow).GetField("selectedStand",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(showcase,switchDemo);
                typeof(MainWindow).GetMethod("RenderStands",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(showcase,null);
                typeof(MainWindow).GetMethod("ShowStand",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(showcase,null);
                Application.DoEvents();Check(Field<bool>(showcase,"standMode")&&Field<Panel>(showcase,"standDetail").Visible&&All(Field<Panel>(showcase,"standDetail")).OfType<StandTopologyCanvas>().Count()==0,"main stand workspace delegates the graph to the network window");
                using(var shot=new Bitmap(showcase.Width,showcase.Height)){showcase.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(args[1],"stand-main-three-vms.png"));}
                string originalStand=J.Json().Serialize(switchDemo);
                using(var editor=new StandNetworkEditorWindow(switchDemo,name=>{string machine=name=="ubuntu-desktop"?"01":name=="ubuntu-server"?"02":"03";return J.O("data",J.O("network_adapters",new[]{J.O("id","lan","type","user","model","e1000e","mac","52:54:00:00:"+machine+":01"),J.O("id","link","type","user","model","e1000e","mac","52:54:00:00:"+machine+":02"),J.O("id","mcast","type","user","model","e1000e","mac","52:54:00:00:"+machine+":03")}));},(window,draft)=>{})){
                    editor.Show(showcase);Application.DoEvents();
                    Check(All(editor).OfType<StandTopologyCanvas>().Count()==1&&All(editor).OfType<WorkspaceTabs>().Count()==1,"network editor presents a graph and editing controls in one window");
                    var addTcp=All(editor).OfType<Button>().FirstOrDefault(b=>b.Text=="Добавить TCP");Check(addTcp!=null,"TCP action is visible: "+String.Join(" | ",All(editor).OfType<Button>().Select(b=>b.Text)));Field<ThemedList>(editor,"from").SelectedIndex=0;Field<ThemedList>(editor,"to").SelectedIndex=3;addTcp.PerformClick();
                    Check(J.A(J.V(editor.Result(),"links")).Length==0&&Field<Label>(editor,"notice").Text.Contains("уже использует"),"occupied switch NIC is rejected before adding a TCP link: "+Field<Label>(editor,"notice").Text);
                    Field<ThemedList>(editor,"from").SelectedIndex=4;Field<ThemedList>(editor,"to").SelectedIndex=7;addTcp.PerformClick();
                    Check(J.A(J.V(editor.Result(),"links")).Length==1&&J.Json().Serialize(switchDemo)==originalStand,"direct TCP link updates only the network draft");
                    var order=Field<List<string>>(editor,"order");order.Reverse();bool rejectedOrder=false;try{editor.Result();}catch(InvalidOperationException error){rejectedOrder=error.Message.Contains("слушателя");}Check(rejectedOrder,"TCP listener must precede connector before save");order.Reverse();
                    var canvas=All(editor).OfType<StandTopologyCanvas>().Single();int nodeWidth=(int)typeof(StandTopologyCanvas).GetMethod("NodeWidth",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,null);int targetX=canvas.ClientSize.Width-16-nodeWidth/2;
                    typeof(StandTopologyCanvas).GetMethod("OnMouseClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{new MouseEventArgs(MouseButtons.Left,1,targetX,75,0)});
                    Check(Field<WorkspaceTabs>(editor,"tabs").SelectedIndex==1,"clicking a switch node opens its controls without another window");
                    canvas.Focus();typeof(StandTopologyCanvas).GetMethod("OnKeyDown",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{new KeyEventArgs(Keys.Down)});typeof(StandTopologyCanvas).GetMethod("OnKeyDown",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{new KeyEventArgs(Keys.Enter)});Check(Field<WorkspaceTabs>(editor,"tabs").SelectedIndex==0,"keyboard navigation can open the next network without a dialog");
                    Field<FieldInput>(editor,"switchName").Text="Переименованный коммутатор";
                    Check(J.S(J.A(J.V(editor.Result(),"switches"))[0],"name")=="Переименованный коммутатор","switch name updates the network graph draft");
                    Field<WorkspaceTabs>(editor,"tabs").SelectedIndex=2;
                    All(editor).OfType<Button>().Single(b=>b.Text=="＋ MCAST").PerformClick();
                    var mcastChecks=Field<FlowLayoutPanel>(editor,"multicastMembers").Controls.OfType<CheckBox>().ToArray();
                    mcastChecks.Single(c=>c.Text=="ubuntu-desktop / mcast").Checked=true;mcastChecks.Single(c=>c.Text=="ubuntu-server / mcast").Checked=true;
                    Check(J.A(J.V(editor.Result(),"multicast")).Length==1&&J.A(J.V(J.A(J.V(editor.Result(),"multicast"))[0],"members")).Length==2,"MCAST members can be assigned in the same editor without another dialog");
                    typeof(StandTopologyCanvas).GetMethod("OnMouseClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{new MouseEventArgs(MouseButtons.Left,1,targetX,165,0)});
                    Check(Field<WorkspaceTabs>(editor,"tabs").SelectedIndex==2,"clicking an MCAST node opens its controls");
                    typeof(StandTopologyCanvas).GetMethod("OnMouseClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{new MouseEventArgs(MouseButtons.Left,1,targetX,255,0)});
                    Check(Field<WorkspaceTabs>(editor,"tabs").SelectedIndex==0,"clicking a TCP node opens the direct-link controls");
                    using(var shot=new Bitmap(editor.Width,editor.Height)){editor.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(args[1],"stand-network-editor.png"));}
                    editor.Size=new Size(990,650);Application.DoEvents();var statusTab=All(editor).OfType<Button>().Single(button=>button.Text=="Состояние");var tabbar=Field<WorkspaceTabs>(editor,"tabs");Check(statusTab.Visible&&tabbar.ClientRectangle.Contains(tabbar.RectangleToClient(statusTab.RectangleToScreen(statusTab.ClientRectangle))),"status tab remains fully visible at the minimum editor width");using(var shot=new Bitmap(editor.Width,editor.Height)){editor.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(args[1],"stand-network-narrow.png"));}editor.Size=new Size(1240,820);Application.DoEvents();
                    Field<WorkspaceTabs>(editor,"tabs").SelectedIndex=1;Application.DoEvents();using(var shot=new Bitmap(editor.Width,editor.Height)){editor.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(args[1],"stand-network-switch.png"));}
                    Field<WorkspaceTabs>(editor,"tabs").SelectedIndex=3;Application.DoEvents();Check(All(editor).OfType<Label>().Any(label=>label.Text.Contains("занят: коммутатор"))&&All(editor).OfType<Label>().Any(label=>label.Text.Contains("занят: TCP-связь")),"network workspace lists individual NICs and their current connections");using(var shot=new Bitmap(editor.Width,editor.Height)){editor.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(args[1],"stand-network-adapters.png"));}
                    typeof(StandTopologyCanvas).GetMethod("OnMouseClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{new MouseEventArgs(MouseButtons.Left,1,70,75,0)});Check(Field<WorkspaceTabs>(editor,"tabs").SelectedIndex==3&&All(editor).OfType<Label>().Any(label=>label.Text=="ubuntu-desktop"&&label.ForeColor==Theme.Accent),"clicking a VM node reveals its adapters in the same window");
                    editor.Close();Check(J.Json().Serialize(switchDemo)==originalStand,"closing network editor without saving preserves the original stand");
                }
                using(var duplicateMac=new StandNetworkEditorWindow(switchDemo,name=>J.O("data",J.O("network_adapters",new[]{J.O("id","lan","type","user","model","e1000e","mac","52:54:00:00:00:01")})),(window,draft)=>{})){
                    bool rejected=false;try{duplicateMac.Result();}catch(InvalidOperationException error){rejected=error.Message.Contains("повторяется MAC");}Check(rejected,"duplicate MAC in one switch is rejected before backend save");
                }
                showcase.Close();
            }
            var fixtureNetwork=J.O("id",diagramNetworkId,"name","Лабораторная сеть с длинным названием для проверки переноса текста", "state","helper-ready","category","connection","guest_connectivity","not-tested","advice","Проверь участников и выполни сверку.","members",new[]{J.O("vm","Ubuntu Server","nic","lan","mac","52:54:00:00:00:01","model","e1000e","connected",false,"port",23456,"nat",new[]{"internet"})},"counters",J.O("received",5000000000L,"sent",7,"dropped",2,"rejected",1,"learned",3),"last_event_utc","2026-10-30T10:00:00Z","events",new[]{J.O("time","2026-10-30T10:00:00Z","category","malformed-frame","port_index",0)});
            using(var statusEditor=new StandNetworkEditorWindow(graphStand,name=>J.O("data",J.O("network_adapters",new[]{J.O("id","lan","model","e1000e","mac","52:54:00:00:00:01")})),(window,draft)=>{},null,null,()=>J.O("members",J.O("Kali","running","Ubuntu Server","stopped"),"network_diagnostics",new[]{fixtureNetwork}))){
                statusEditor.OpenStatus();statusEditor.Show();var until=DateTime.UtcNow.AddSeconds(3);while(!Field<ThemedEditor>(statusEditor,"diagnostics").Text.Contains("5000000000")&&DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(10);}
                Check(Field<ThemedEditor>(statusEditor,"diagnostics").Text.Contains("5000000000")&&Field<ThemedEditor>(statusEditor,"diagnostics").Text.Contains("НЕ ПРОВЕРЕНА"),"integrated status tab shows counters, events and guest-connectivity limit without a second window");
                using(var shot=new Bitmap(statusEditor.Width,statusEditor.Height)){statusEditor.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(args[1],"stand-network-status.png"));}statusEditor.Close();
            }
            using(var networks=new StandNetworksWindow(null,new string('a',32))){
                int reads=0;networks.StatusProvider=()=>{reads++;return Task.FromResult<object>(J.O("network_diagnostics",new[]{fixtureNetwork}));};networks.Show();Application.DoEvents();
                var output=All(networks).OfType<ThemedEditor>().Single();Check(output.ReadOnly&&output.Text.Contains("не проверена")&&output.Text.Contains("5000000000"),"network diagnostics distinguishes guest readiness and preserves 64-bit counters");
                Check(output.Text.Contains("52:54:00:00:00:01")&&output.Text.Contains("internet")&&output.Text.Contains("Некорректный"),"network diagnostics shows NIC/MAC/NAT and categorized frame events");
                Check(All(networks).OfType<ThemedSelector>().Single().AccessibleName=="Выбрать общую сеть","network chooser has a keyboard-accessible name");
                networks.Size=new Size(800,600);Application.DoEvents();foreach(var button in All(networks).OfType<Button>().Where(b=>b.Text=="Обновить"||b.Text=="Закрыть"))Check(networks.ClientRectangle.Contains(networks.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))),"network diagnostics footer remains visible in a narrow window");
                using(var shot=new Bitmap(networks.Width,networks.Height)){networks.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(args[1],"switch-network-diagnostics.png"));}
                All(networks).OfType<Button>().Single(b=>b.Text=="Обновить").PerformClick();Check(reads==2,"diagnostics refresh requests only the supplied read-only overview");networks.Close();Check(reads==2,"closing diagnostics sends no extra command");
            }
            using(var networks=new StandNetworksWindow(null,new string('a',32))){
                var response=new TaskCompletionSource<object>();networks.StatusProvider=()=>response.Task;networks.Show();Application.DoEvents();Check(networks.Visible&&!All(networks).OfType<Button>().Single(b=>b.Text=="Обновить").Enabled,"pending network read keeps window responsive and prevents duplicate queries");networks.Close();response.SetResult(J.O("network_diagnostics",new object[0]));Application.DoEvents();Check(networks.IsDisposed,"late diagnostics response does not reopen a closed window");
            }
            byte[] vm=File.ReadAllBytes(Path.Combine(args[0],"machines","test.json"));
            using(var main=new MainWindow(args[0],null,false,false)){
                main.Backend=new Backend(args[0]);main.Backend.Open();main.Show();Application.DoEvents();
                typeof(MainWindow).GetMethod("Fill",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,new[]{main.Backend.Call("list")});
                Check(Field<Label>(main,"mediaSummary").Text.Contains("ISO:")&&Field<Label>(main,"mediaSummary").Text.Contains("Сеть:"),"VM overview includes disk, ISO and network beside resources and acceleration");
                var toggle=All(main).OfType<CheckBox>().Single(c=>c.Text=="Режим стендов");toggle.Checked=true;Wait(main);
                Check(Field<bool>(main,"standMode"),"stand toggle updates mode");
                main.BeginInvoke((Action)(()=>{var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Новый стенд");All(form).OfType<Button>().Single(b=>b.Text=="Отмена").PerformClick();}));
                All(main).OfType<Button>().Single(b=>b.Text=="＋  Создать стенд").PerformClick();
                Check(J.A(J.V(main.Backend.Call("stand_list"),"rows")).Length==0&&!Directory.Exists(Path.Combine(args[0],"stands")),"cancel creates no stand or directory");
                main.BeginInvoke((Action)(()=>{var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Новый стенд");All(form).OfType<FieldInput>().First().Text="Длинное название пользовательского лабораторного стенда";All(form).OfType<CheckBox>().Single().Checked=true;All(form).OfType<Button>().Single(b=>b.Text=="Сохранить").PerformClick();}));
                All(main).OfType<Button>().Single(b=>b.Text=="＋  Создать стенд").PerformClick();Wait(main);Application.DoEvents();
                var saved=J.A(J.V(main.Backend.Call("stand_list"),"rows"));Check(saved.Length==1,"create dialog writes a real stand through backend");
                string id=J.S(saved[0],"id");Check(J.A(J.V(saved[0],"members")).Select(Convert.ToString).SequenceEqual(new[]{"test"}),"membership persisted");
                Check(Field<Panel>(main,"standDetail").Visible,"stand parameters are on the right");
                Check(All(Field<Panel>(main,"standDetail")).OfType<StandTopologyCanvas>().Count()==0&&Application.OpenForms.Cast<Form>().Count(form=>form!=main)==0,"main workspace contains no network preview");
                var standPage=Field<Panel>(main,"standDetail");
                Check(Field<WorkspaceTabs>(main,"standTabs").SelectedIndex==1,"stand opens directly on launch controls");
                Check(All(standPage).OfType<StandOperationPanel>().Count()==1&&All(standPage).OfType<ThemedEditor>().Any(editor=>editor.ReadOnly),"operation journal and plan preview live in the selected stand's main pane");
                byte[] beforePlan=File.ReadAllBytes(Path.Combine(args[0],"stands",J.S(saved[0],"id")+".json"));
                Field<WorkspaceTabs>(main,"standTabs").SelectedIndex=1;
                All(standPage).OfType<Button>().Single(button=>button.Text=="План выключения").PerformClick();Wait(main);
                Check(Field<object>(main,"standPendingPlan")!=null&&Application.OpenForms.Cast<Form>().Count(form=>form!=main)==0,"stop plan preview is inline and opens no extra window");
                Check(Field<ThemedEditor>(main,"standPlanText").Text.Contains("Штатное выключение")&&Field<Button>(main,"standConfirmPlan").Enabled,"inline plan displays action and requires a separate confirmation");
                using(var shot=new Bitmap(main.Width,main.Height)){main.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(args[1],"stand-plan-inline.png"));}
                Field<Button>(main,"standDiscardPlan").PerformClick();Wait(main);
                Check(Field<object>(main,"standPendingPlan")==null&&beforePlan.SequenceEqual(File.ReadAllBytes(Path.Combine(args[0],"stands",J.S(saved[0],"id")+".json"))),"discarding inline plan changes neither stand definition nor VM");
                Field<WorkspaceTabs>(main,"standTabs").SelectedIndex=1;
                All(standPage).OfType<Button>().Single(button=>button.Text=="Выключить стенд").PerformClick();Wait(main);
                Check(Field<object>(main,"standPendingPlan")==null&&!String.IsNullOrEmpty(Field<string>(main,"standActiveOperationId")),"ordinary shutdown validates and executes without a second plan confirmation");
                var jobLimit=DateTime.UtcNow.AddSeconds(10);bool settled=false;
                while(DateTime.UtcNow<jobLimit){
                    string state=J.S(main.Backend.Call("stand_status",J.O("operation_id",Field<string>(main,"standActiveOperationId"))),"state");
                    if(state!="queued"&&state!="running"){
                        try{foreach(string lease in Directory.GetFiles(Path.Combine(args[0],"stand-runtime","active"),"*.lock"))using(File.Open(lease,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}settled=true;break;}catch(IOException){}
                    }
                    Application.DoEvents();System.Threading.Thread.Sleep(20);
                }
                Check(settled,"one-click operation finishes and releases its private fixture locks");
                typeof(MainWindow).GetMethod("UpdateStandStatus",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,null);
                var activityLimit=DateTime.UtcNow.AddSeconds(5);while(Field<int>(main,"StandJobs")>0&&DateTime.UtcNow<activityLimit){Application.DoEvents();System.Threading.Thread.Sleep(20);}
                Check(Field<int>(main,"StandJobs")==0,"completed operation no longer blocks manager closure");
                Field<WorkspaceTabs>(main,"standTabs").SelectedIndex=Field<int>(main,"standJournalTabIndex");
                All(Field<StandOperationPanel>(main,"standOperationPanel")).OfType<Button>().Single(button=>button.Text=="Обновить журнал").PerformClick();Wait(main);
                Check(Field<StandOperationPanel>(main,"standOperationPanel").Visible&&Application.OpenForms.Cast<Form>().Count(form=>form!=main)==0,"operation status remains in the journal tab without a window");
                Field<WorkspaceTabs>(main,"standTabs").SelectedIndex=0;Application.DoEvents();

                using(var shot=new Bitmap(main.Width,main.Height)){main.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(args[1],"stand-main-embedded.png"));}
                var split=All(main).OfType<SplitContainer>().Single();split.SplitterDistance=380;Application.DoEvents();
                Check(Field<FieldInput>(main,"search").Width>260,"left search expands with library");
                Check(split.Panel1.Controls.Count==1&&split.Panel2.Controls.Contains(Field<Panel>(main,"detail")),"library left, details right");
                main.Backend.Call("stand_save",J.O("stand",J.O("schema_version",1,"id",id,"revision",1,"name","Updated stand","description","notes","members",new[]{"test"},"order",new[]{"test"},"links",new object[0]),"revision",1));
                Check(J.S(J.A(J.V(main.Backend.Call("stand_list"),"rows"))[0],"name")=="Updated stand","edit survives RPC reread");
                typeof(MainWindow).GetMethod("LoadStands",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,null);Wait(main);
                Check(J.S(Field<object>(main,"selectedStand"),"id")==id&&J.S(Field<object>(main,"selectedStand"),"name")=="Updated stand","refresh retains selection and replaces stale metadata");
                byte[] standBefore=File.ReadAllBytes(Path.Combine(args[0],"stands",id+".json"));
                main.BeginInvoke((Action)(()=>{var editor=Application.OpenForms.OfType<StandNetworkEditorWindow>().Single();Check(All(editor).OfType<StandTopologyCanvas>().Count()==1,"network button opens one graph editor without a nested window");var canvas=All(editor).OfType<StandTopologyCanvas>().Single();
                    typeof(StandTopologyCanvas).GetMethod("OnMouseDown",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{new MouseEventArgs(MouseButtons.Left,1,70,75,0)});
                    typeof(StandTopologyCanvas).GetMethod("OnMouseMove",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{new MouseEventArgs(MouseButtons.Left,1,105,120,0)});
                    typeof(StandTopologyCanvas).GetMethod("OnMouseUp",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(canvas,new object[]{new MouseEventArgs(MouseButtons.Left,1,105,120,0)});
                    All(editor).OfType<Button>().Single(b=>b.Text=="Отмена").PerformClick();}));
                All(main).OfType<Button>().Single(b=>b.Text=="Сеть и соединения").PerformClick();
                Check(standBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(args[0],"stands",id+".json")))&&StandDiagramLayout.Read(args[0],id).ContainsKey("vm:test"),"cancelling network edits keeps stand byte-identical while local diagram placement persists");
                main.BeginInvoke((Action)(()=>{var editor=Application.OpenForms.OfType<StandNetworkEditorWindow>().Single();Field<NumberInput>(editor,"delay").Value=2;All(editor).OfType<Button>().Single(b=>b.Text=="Сохранить стенд").PerformClick();}));
                All(main).OfType<Button>().Single(b=>b.Text=="Сеть и соединения").PerformClick();Wait(main);
                var savedNetwork=J.A(J.V(main.Backend.Call("stand_list"),"rows")).Single(row=>J.S(row,"id")==id);
                Check(J.N(J.V(J.V(savedNetwork,"startup"),"test"),"delay")==2,"one-window network save persists startup settings through backend revision check");
                standBefore=File.ReadAllBytes(Path.Combine(args[0],"stands",id+".json"));
                main.BeginInvoke((Action)(()=>{
                    var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Соединения и запуск стенда");
                    Check(All(form).OfType<ThemedSelector>().Count()==3,"topology uses themed endpoint and readiness selectors");
                    Check(All(form).OfType<NumberInput>().Count()==3,"topology exposes bounded delay, timeout and TCP port");
                    var scroll=All(form).OfType<ScrollSurface>().Single();scroll.PerformLayout();Application.DoEvents();
                    Check(scroll.ScrollBar.Maximum>0,"topology exposes scrolling to connections below the first viewport");
                    scroll.ScrollOffset=scroll.ScrollBar.Maximum;Application.DoEvents();
                    var addLink=All(form).OfType<Button>().Single(b=>b.Text=="Добавить связь");
                    Check(form.RectangleToClient(addLink.RectangleToScreen(addLink.ClientRectangle)).Bottom<form.ClientSize.Height,"connection actions can be reached by scrolling");
                    using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,form.ClientRectangle);image.Save(Path.Combine(args[1],"stands-topology.png"));}
                    All(form).OfType<Button>().Single(b=>b.Text=="Отмена").PerformClick();
                }));
                typeof(MainWindow).GetMethod("EditStandTopology",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,new[]{Field<object>(main,"selectedStand")});
                Check(standBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(args[0],"stands",id+".json"))),"cancelling topology never changes the definition");
                var draftSeed=J.D(J.Json().DeserializeObject(System.Text.Encoding.UTF8.GetString(standBefore)));draftSeed["schema_version"]=4;draftSeed["switches"]=new[]{J.O("id",new string('a',32),"name","Draft LAN","members",new[]{J.O("vm","test","nic","lan"),J.O("vm","missing","nic","lan")})};string seedBefore=J.Json().Serialize(draftSeed);
                main.BeginInvoke((Action)(()=>{var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Соединения и запуск стенда");var networkList=All(form).OfType<ThemedList>().Single(l=>l.Items.Cast<object>().Any(item=>Convert.ToString(item).StartsWith("Draft LAN")));networkList.SelectedIndex=0;All(form).OfType<Button>().Single(b=>b.Text=="Удалить общую сеть").PerformClick();Check(networkList.Items.Count==0,"delete common network changes the draft list");All(form).OfType<Button>().Single(b=>b.Text=="Отмена").PerformClick();}));
                typeof(MainWindow).GetMethod("EditStandTopology",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,new object[]{draftSeed});Check(seedBefore==J.Json().Serialize(draftSeed)&&standBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(args[0],"stands",id+".json"))),"cancel after deleting a network leaves definition and VM references unchanged");
                main.BeginInvoke((Action)(()=>{var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Копия стенда");All(form).OfType<Button>().Single(b=>b.Text=="Отмена").PerformClick();}));
                Field<WorkspaceTabs>(main,"standTabs").SelectedIndex=4;
                All(main).OfType<Button>().Single(b=>b.Text=="Дублировать стенд").PerformClick();
                Check(J.A(J.V(main.Backend.Call("stand_list"),"rows")).Length==1,"cancelling duplicate creates no definition or image");
                main.BeginInvoke((Action)(()=>{var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Копия стенда");All(form).OfType<FieldInput>().Single().Text="Копия лаборатории";All(form).OfType<Button>().Single(b=>b.Text=="Создать копию").PerformClick();}));
                Field<WorkspaceTabs>(main,"standTabs").SelectedIndex=4;
                All(main).OfType<Button>().Single(b=>b.Text=="Дублировать стенд").PerformClick();Wait(main);
                Check(J.A(J.V(main.Backend.Call("stand_list"),"rows")).Length==2,"duplicate UI persists references through RPC");
                bool previewClosed=false;
                using(var previewTimer=new System.Windows.Forms.Timer{Interval=25}){
                    previewTimer.Tick+=(s,e)=>{var form=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Text=="Предпросмотр отчёта стенда");if(form==null)return;previewTimer.Stop();var text=All(form).OfType<ThemedEditor>().Single();Check(text.ReadOnly&&!text.Text.Contains(args[0]),"stand report is read-only and hides private source paths before save");All(form).OfType<Button>().Single(b=>b.Text=="Отмена").PerformClick();previewClosed=true;};
                    Field<WorkspaceTabs>(main,"standTabs").SelectedIndex=4;previewTimer.Start();All(main).OfType<Button>().Single(b=>b.Text=="Экспорт отчёта").PerformClick();
                    var deadline=DateTime.UtcNow.AddSeconds(10);while(!previewClosed&&DateTime.UtcNow<deadline){Application.DoEvents();System.Threading.Thread.Sleep(10);}
                }
                Check(previewClosed,"report preview opens and cancels without creating a file");
                var cards=Field<FlowLayoutPanel>(main,"cards");
                var folders=cards.Controls.OfType<Button>().Where(b=>b.Tag!=null).ToArray();
                folders[0].PerformClick();Application.DoEvents();
                Check(cards.Controls.OfType<Button>().Count(b=>b.AccessibleDescription=="stand-member")==1,"expanding one folder adds its member only");
                var memberButton=cards.Controls.OfType<StandMemberButton>().Single();
                Check(memberButton.Left>=folders[0].Left+20&&memberButton.Right<=folders[0].Right,"stand member is visibly indented and remains inside the group width");
                Check(memberButton.TextAlign==ContentAlignment.MiddleLeft&&memberButton.Padding.Left>=34,"member names align after a dedicated tree branch gutter");
                var untouched=folders[1];folders[0].PerformClick();Application.DoEvents();
                Check(!untouched.IsDisposed&&cards.Controls.Contains(untouched)&&cards.Controls.OfType<Button>().All(b=>b.AccessibleDescription!="stand-member"),"collapse retains unrelated folder controls and removes only children");
                folders[0].PerformClick();Application.DoEvents();
                cards.Controls.OfType<Button>().Single(b=>b.AccessibleDescription=="stand-member").PerformClick();Application.DoEvents();
                Check(Field<string>(main,"selected")=="test"&&!Field<Panel>(main,"standDetail").Visible,"nested member opens its real VM detail");
                folders[0].PerformClick();Application.DoEvents();
                Check(Field<Panel>(main,"standDetail").Visible,"returning to a folder restores its stand detail");
                string config=Path.Combine(args[0],"machines","test.json"),missing=config+".missing";
                File.Move(config,missing);
                try{
                    typeof(MainWindow).GetMethod("Fill",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,new[]{main.Backend.Call("list")});Wait(main);
                    typeof(MainWindow).GetMethod("LoadStands",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,null);Wait(main);
                    Check(All(Field<Panel>(main,"standDetail")).OfType<Button>().Any(b=>b.Text=="Открыть состав стенда"),"membership remains accessible from the overview");
                }finally{File.Move(missing,config);}
                typeof(MainWindow).GetMethod("Fill",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,new[]{main.Backend.Call("list")});Wait(main);
                Size normalSize=main.Size;main.Size=new Size(800,600);split.SplitterDistance=240;Application.DoEvents();
                Check(split.Panel2.Width>0&&Field<Panel>(main,"standDetail").Width>0,"narrow layout retains right-side stand controls");
                Field<WorkspaceTabs>(main,"standTabs").SelectedIndex=Field<int>(main,"standPlanTabIndex");Application.DoEvents();
                var planButton=Field<Button>(main,"standConfirmPlan");
                Check(planButton.RectangleToScreen(planButton.ClientRectangle).Right<=main.RectangleToScreen(main.ClientRectangle).Right&&planButton.TabStop,"narrow inline plan keeps its confirmation in the right pane and keyboard focus order");
                Check(!Field<Panel>(main,"standDetail").Controls.OfType<ScrollSurface>().Any(),"main stand pane has no outer scrolling");
                Check(planButton.RectangleToScreen(planButton.ClientRectangle).Bottom<=main.RectangleToScreen(main.ClientRectangle).Bottom,"narrow view can scroll to the plan confirmation without another window");
                using(var image=new Bitmap(main.Width,main.Height)){main.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(args[1],"stand-plan-narrow.png"));}
                main.Size=normalSize;split.SplitterDistance=380;Application.DoEvents();
                using(var image=new Bitmap(main.Width,main.Height)){main.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(args[1],"stands-folders.png"));}
                toggle.Checked=false;Check(!Field<Panel>(main,"standDetail").Visible,"normal VM view restored");
                Check(vm.SequenceEqual(File.ReadAllBytes(Path.Combine(args[0],"machines","test.json"))),"stand CRUD leaves VM byte-identical");
                using(var image=new Bitmap(main.Width,main.Height)){main.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(args[1],"stands-library.png"));}
                main.Close();Application.DoEvents();
                Check(J.N(J.Json().DeserializeObject(File.ReadAllText(Path.Combine(args[0],"stand-library-ui.json"))),"width")==380,"library width persisted");
            }
            using(var operation=new StandOperationWindow(null,new string('1',32))){
                var pending=new TaskCompletionSource<object>();operation.StatusProvider=stop=>pending.Task;
                operation.Show();Application.DoEvents();operation.Text="Responsive while waiting";
                Check(operation.Visible&&operation.Text=="Responsive while waiting","operation status never blocks the UI thread");
                pending.SetResult(J.O("state","completed","phase","QMP готов; ОС не измеряется","current","test","events",new[]{J.O("vm","test","phase","Процесс создан")},"error",""));
                var until=DateTime.UtcNow.AddSeconds(3);while(All(operation).OfType<Button>().Single(b=>b.Text=="Отменить будущие этапы").Enabled&&DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(10);}
                Check(!All(operation).OfType<Button>().Single(b=>b.Text=="Отменить будущие этапы").Enabled,"completed operation disables cancellation");
                Check(All(operation).OfType<ThemedEditor>().Single().Text.Contains("Процесс создан"),"operation window displays real phase events");
                using(var image=new Bitmap(operation.Width,operation.Height)){operation.DrawToBitmap(image,operation.ClientRectangle);image.Save(Path.Combine(args[1],"stands-operation.png"));}
                operation.Close();
            }
            using(var host=new Form())using(var inline=new StandOperationPanel(null)){
                Theme.Form(host,760,540);host.Text="Встроенный журнал стенда";inline.Dock=DockStyle.Fill;host.Controls.Add(inline);
                inline.StatusProvider=stop=>Task.FromResult((object)J.O("state","completed","phase","QMP готов","current","test","events",new[]{J.O("vm","test","phase","Процесс создан")},"error",""));
                host.Show();Application.DoEvents();inline.SetOperation(new string('2',32));
                var until=DateTime.UtcNow.AddSeconds(3);while(!All(inline).OfType<ThemedEditor>().Single().Text.Contains("Процесс создан")&&DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(10);}
                Check(All(inline).OfType<ThemedEditor>().Single().Text.Contains("Процесс создан")&&!All(inline).OfType<Button>().Single(b=>b.Text=="Отменить будущие этапы").Enabled,"inline journal renders real event and disables cancellation after completion");
                using(var shot=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(args[1],"stand-journal-inline.png"));}
                host.Close();
            }
            File.WriteAllText(Path.Combine(args[1],"stands-results.json"),J.Json().Serialize(J.O("passed",checks)));return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"stands-error.txt"),error.ToString());Console.Error.WriteLine(error);return 1;}
    }
}
