using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Windows.Forms;

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
                using(var image=new Bitmap(main.Width,main.Height)){main.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(args[1],"stands-folders.png"));}
                toggle.Checked=false;Check(!Field<Panel>(main,"standDetail").Visible,"normal VM view restored");
                Check(vm.SequenceEqual(File.ReadAllBytes(Path.Combine(args[0],"machines","test.json"))),"stand CRUD leaves VM byte-identical");
                using(var image=new Bitmap(main.Width,main.Height)){main.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(args[1],"stands-library.png"));}
                main.Close();Application.DoEvents();
                Check(J.N(J.Json().DeserializeObject(File.ReadAllText(Path.Combine(args[0],"stand-library-ui.json"))),"width")==380,"library width persisted");
            }
            File.WriteAllText(Path.Combine(args[1],"stands-results.json"),J.Json().Serialize(J.O("passed",checks)));return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"stands-error.txt"),error.ToString());return 1;}
    }
}
