using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class StandsHierarchyTests {
    static int checks;
    static T Field<T>(object x,string n){return (T)x.GetType().GetField(n,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(x);}
    static IEnumerable<Control> All(Control x){foreach(Control c in x.Controls){yield return c;foreach(var d in All(c))yield return d;}}
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
    static void Wait(MainWindow main){var until=DateTime.UtcNow.AddSeconds(20);while(main.Busy&&DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(10);}Check(!main.Busy&&main.TestError==null,"load completes cleanly");}
    [STAThread]static int Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Program.TestMode=true;
        try{
            string original=Path.Combine(args[0],"machines","test.json");
            foreach(string name in new[]{"desktop","server","kali"}){
                var config=J.D(J.Json().DeserializeObject(File.ReadAllText(original)));config["label"]=name=="desktop"?"Ubuntu Desktop For IPV6":name=="server"?"Ubuntu Server For IPV6":"Kali · диагностическая машина";
                File.WriteAllText(Path.Combine(args[0],"machines",name+".json"),J.Json().Serialize(config));
            }
            using(var main=new MainWindow(args[0],null,false,false)){
                main.Backend=new Backend(args[0]);main.Backend.Open();
                foreach(var item in new[]{J.O("name","IPV6","members",new[]{"desktop","server"}),J.O("name","Исследовательская лаборатория","members",new[]{"server","kali"})}){
                    var members=J.A(J.V(item,"members"));main.Backend.Call("stand_save",J.O("revision",0,"stand",J.O("schema_version",1,"id",Guid.NewGuid().ToString("N"),"revision",0,"name",J.S(item,"name"),"description","","members",members,"order",members,"links",new object[0])));
                }
                main.Show();Application.DoEvents();typeof(MainWindow).GetMethod("Fill",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,new[]{main.Backend.Call("list")});
                All(main).OfType<CheckBox>().Single(c=>c.Text=="Режим стендов").Checked=true;Wait(main);
                var split=All(main).OfType<SplitContainer>().Single();split.SplitterDistance=400;Application.DoEvents();
                var cards=Field<FlowLayoutPanel>(main,"cards");var folders=cards.Controls.OfType<Button>().Where(b=>b.Tag!=null).ToArray();
                Check(folders.Length==2,"both saved stands appear after switching library modes");
                foreach(var folder in folders)folder.PerformClick();Application.DoEvents();
                var children=cards.Controls.OfType<StandMemberButton>().ToArray();Check(children.Length==4,"both stands expose separate member rows");
                foreach(var member in children){var folder=folders.Single(b=>J.S(b.Tag,"id")==member.StandId);Check(member.Left>=folder.Left+20&&member.Right<=folder.Right,"tree indent remains within parent group");Check(member.AccessibleName.Contains(J.S(folder.Tag,"name")),"accessible name exposes the owning stand");}
                var shared=children.Where(b=>b.VmName=="server").ToArray();shared[0].PerformClick();Application.DoEvents();
                Check(shared[0].Selected&&!shared[1].Selected,"same VM in two stands selects only the clicked membership");
                Check(Field<Label>(main,"breadcrumb").Text.Contains(J.S(folders.Single(b=>J.S(b.Tag,"id")==shared[0].StandId).Tag,"name")),"right-side breadcrumb identifies the selected stand");
                main.ApplyStates(J.O("server","stopped","desktop","stopped","kali","stopped"));Application.DoEvents();
                Check(shared[0].Selected&&shared[0].BackColor==Theme.Card&&Field<Label>(main,"breadcrumb").Text.Contains(J.S(folders.Single(b=>J.S(b.Tag,"id")==shared[0].StandId).Tag,"name")),"state refresh preserves membership highlight and stand breadcrumb");
                int width=children[0].Width;split.SplitterDistance=440;Application.DoEvents();Check(children[0].Width>width,"nested rows stretch with the library");
                foreach(var folder in folders){int textHeight=TextRenderer.MeasureText(folder.Text,folder.Font,new Size(Math.Max(1,folder.Width-folder.Padding.Horizontal-1),Int32.MaxValue),TextFormatFlags.WordBreak).Height;Check(folder.Height-folder.Padding.Vertical>=textHeight,"wrapped stand heading and counters are not vertically clipped");}
                using(var image=new Bitmap(main.Width,main.Height)){main.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(args[1],"stands-hierarchy.png"));}
                main.Close();
            }
            File.WriteAllText(Path.Combine(args[1],"stands-hierarchy-results.json"),J.Json().Serialize(J.O("passed",checks)));return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"stands-hierarchy-error.txt"),error.ToString());return 1;}
    }
}
