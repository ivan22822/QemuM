using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class InputStyleTests {
    static readonly List<string> checks=new List<string>();
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(owner);}
    static IEnumerable<Control> All(Control parent){foreach(Control child in parent.Controls){yield return child;foreach(var nested in All(child))yield return nested;}}
    static void Check(bool condition,string label){if(!condition)throw new Exception(label);checks.Add(label);}
    static void Save(Form form,string path){Application.DoEvents();using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(path);}}
    static Rectangle Screen(Control control){return control.RectangleToScreen(control.ClientRectangle);}
    [STAThread]static int Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Program.TestMode=true;
        try{
            using(var form=new Form())using(var field=Theme.Input()){
                Theme.Form(form,500,200);field.Dock=DockStyle.Top;form.Controls.Add(field);form.Show();Application.DoEvents();
                string value="C:\\Test Data\\Лаборатория\\диск,копия.qcow2";field.Text=value;
                Check(field.Text==value&&field.Height==40&&field.Input.BorderStyle==BorderStyle.None,"single-line field uses shared height and preserves Unicode spaces commas and slashes");
                field.Input.SelectAll();SendMessage(field.Input.Handle,0x102,new IntPtr((int)'Z'),new IntPtr(1));field.Input.Undo();Check(field.Text==value,"styled input retains native selection editing and undo");
            }
            using(var adapter=new NetworkAdapterDialog(J.A(J.V(LaunchSettingsUi.Defaults(),"network_adapters"))[0])){
                adapter.Show();var fields=All(adapter).OfType<FieldInput>().ToArray();
                Check(fields.Length>=9&&fields.All(field=>field.Height==40),"network fields and numeric input share the selector height");Save(adapter,Path.Combine(args[0],"input-style-network.png"));adapter.Close();
            }
            using(var dialog=new ExtraArgsDialog("-device usb-tablet")){
                dialog.Show();Check(dialog.Editor.BorderVisible&&dialog.Editor.Padding.Left>=12,"standalone extra args editor has the shared frame and comfortable text padding");
                Save(dialog,Path.Combine(args[0],"input-style-extra-editor.png"));dialog.Close();
            }
            using(var form=new Form())using(var help=new ExtraHelpView(example=>{})){
                Theme.Form(form,800,620);form.Controls.Add(help);form.Show();Application.DoEvents();
                var search=Field<FieldInput>(help,"search");var examples=Field<ThemedSelector>(help,"examples");var body=Field<ThemedEditor>(help,"body");var insert=Field<Button>(help,"insertButton");
                search.Text="hostfwd";Application.DoEvents();
                Check(body.ReadOnly&&search.SearchIcon,"help article is read-only and search uses the same styled field");
                Check(Screen(insert).Top-Screen(examples).Bottom>=12,"example selector is separated from action buttons");
                Save(form,Path.Combine(args[0],"input-style-help.png"));
                Check(examples.Parent.Height<220,"example card shrinks to its content without an empty lower half");
                Save(form,Path.Combine(args[0],"input-style-help.png"));
                form.MinimumSize=new Size(560,400);form.ClientSize=new Size(620,500);Application.DoEvents();
                Save(form,Path.Combine(args[0],"input-style-help-narrow.png"));
                Check(Screen(insert).Top>=Screen(examples).Bottom+12&&Screen(examples).Right<=Screen(form).Right,"narrow help layout keeps examples and actions apart and within the window");
                Save(form,Path.Combine(args[0],"input-style-help-narrow.png"));
                search.Text="this-topic-does-not-exist";Application.DoEvents();Check(!examples.Enabled&&!insert.Enabled,"empty search disables unavailable example actions");
                search.Clear();Check(Field<ThemedList>(help,"topics").Items.Count>0&&insert.Enabled,"clearing search restores topics and available examples");form.Close();
            }
            File.WriteAllText(Path.Combine(args[0],"input-style-results.json"),J.Json().Serialize(J.O("passed",checks.Count,"checks",checks)));return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[0],"input-style-results.json"),J.Json().Serialize(J.O("passed",checks.Count,"error",error.ToString())));return 1;}
    }
}
