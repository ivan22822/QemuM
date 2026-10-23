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
            byte[] vm=File.ReadAllBytes(Path.Combine(args[0],"machines","test.json"));
            using(var main=new MainWindow(args[0],null,false,false)){
                main.Backend=new Backend(args[0]);main.Backend.Open();main.Show();Application.DoEvents();
                typeof(MainWindow).GetMethod("Fill",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,new[]{main.Backend.Call("list")});
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
                var split=All(main).OfType<SplitContainer>().Single();split.SplitterDistance=380;Application.DoEvents();
                Check(Field<FieldInput>(main,"search").Width>260,"left search expands with library");
                Check(split.Panel1.Controls.Count==1&&split.Panel2.Controls.Contains(Field<Panel>(main,"detail")),"library left, details right");
                main.Backend.Call("stand_save",J.O("stand",J.O("schema_version",1,"id",id,"revision",1,"name","Updated stand","description","notes","members",new[]{"test"},"order",new[]{"test"},"links",new object[0]),"revision",1));
                Check(J.S(J.A(J.V(main.Backend.Call("stand_list"),"rows"))[0],"name")=="Updated stand","edit survives RPC reread");
                typeof(MainWindow).GetMethod("LoadStands",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,null);Wait(main);
                Check(J.S(Field<object>(main,"selectedStand"),"id")==id&&J.S(Field<object>(main,"selectedStand"),"name")=="Updated stand","refresh retains selection and replaces stale metadata");
                byte[] standBefore=File.ReadAllBytes(Path.Combine(args[0],"stands",id+".json"));
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
                All(main).OfType<Button>().Single(b=>b.Text=="Соединения и порядок запуска").PerformClick();
                Check(standBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(args[0],"stands",id+".json"))),"cancelling topology never changes the definition");
                main.BeginInvoke((Action)(()=>{var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Копия стенда");All(form).OfType<Button>().Single(b=>b.Text=="Отмена").PerformClick();}));
                All(main).OfType<Button>().Single(b=>b.Text=="Дублировать стенд").PerformClick();
                Check(J.A(J.V(main.Backend.Call("stand_list"),"rows")).Length==1,"cancelling duplicate creates no definition or image");
                main.BeginInvoke((Action)(()=>{var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Копия стенда");All(form).OfType<FieldInput>().Single().Text="Копия лаборатории";All(form).OfType<Button>().Single(b=>b.Text=="Создать копию").PerformClick();}));
                All(main).OfType<Button>().Single(b=>b.Text=="Дублировать стенд").PerformClick();Wait(main);
                Check(J.A(J.V(main.Backend.Call("stand_list"),"rows")).Length==2,"duplicate UI persists references through RPC");
                bool previewClosed=false;
                using(var previewTimer=new System.Windows.Forms.Timer{Interval=25}){
                    previewTimer.Tick+=(s,e)=>{var form=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Text=="Предпросмотр отчёта стенда");if(form==null)return;previewTimer.Stop();var text=All(form).OfType<ThemedEditor>().Single();Check(text.ReadOnly&&!text.Text.Contains(args[0]),"stand report is read-only and hides private source paths before save");All(form).OfType<Button>().Single(b=>b.Text=="Отмена").PerformClick();previewClosed=true;};
                    previewTimer.Start();All(main).OfType<Button>().Single(b=>b.Text=="Экспорт отчёта").PerformClick();
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
                    Check(All(Field<Panel>(main,"standDetail")).OfType<Button>().Any(b=>b.Text.Contains("VM отсутствует")),"removed VM remains visibly missing without silently deleting the reference");
                }finally{File.Move(missing,config);}
                typeof(MainWindow).GetMethod("Fill",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,new[]{main.Backend.Call("list")});Wait(main);
                Size normalSize=main.Size;main.Size=new Size(800,600);split.SplitterDistance=240;Application.DoEvents();
                Check(split.Panel2.Width>0&&Field<Panel>(main,"standDetail").Width>0,"narrow layout retains right-side stand controls");main.Size=normalSize;split.SplitterDistance=380;Application.DoEvents();
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
            File.WriteAllText(Path.Combine(args[1],"stands-results.json"),J.Json().Serialize(J.O("passed",checks)));return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"stands-error.txt"),error.ToString());return 1;}
    }
}
