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
            using(var main=new MainWindow(args[0],null,true,false)){
                main.Demo();
                using(var create=new CreateDialog(main,new object[0])){
                    create.Show();create.ClientSize=new Size(800,580);Application.DoEvents();
                    var common=Field<PageDeck>(create,"tabs").Pages.First();var scroll=All(common).OfType<ScrollSurface>().Single();
                    Check(scroll.ScrollBar.Visible&&!All(common).OfType<ScrollableControl>().Any(control=>control.AutoScroll),"creation form uses themed scrolling without white native scrollbars");
                    scroll.ScrollOffset=scroll.ScrollBar.Maximum;Save(create,Path.Combine(args[0],"create-form-themed-scroll.png"));create.Close();
                }
                using(var deletion=new DeleteDialog(main,J.O("name","test","token","preview","remove_files",false,"files",new[]{"test.json"}))){
                    deletion.Show();var saveBackup=Field<CheckBox>(deletion,"saveBackup");var confirm=Field<FieldInput>(deletion,"confirm");var backup=Field<FieldInput>(deletion,"backup");var remove=Field<Button>(deletion,"delete");
                    confirm.Text="test";Check(!remove.Enabled,"default backup still requires its name");saveBackup.Checked=false;
                    Check(remove.Enabled&&!backup.Enabled&&remove.Text=="Убрать из списка","unregister can omit a backup while retaining exact confirmation");
                    confirm.Text="tes";Check(!remove.Enabled,"unregister without backup still rejects a partial name");
                    Save(deletion,Path.Combine(args[0],"unregister-options.png"));deletion.Close();
                }
            }
            ExtraExample captured=null;
            using(var form=new Form())using(var help=new ExtraHelpView(example=>captured=example)){
                Theme.Form(form,1000,730);form.Controls.Add(help);form.Show();Field<FieldInput>(help,"search").Text="клавиши Windows";Application.DoEvents();
                All(help).OfType<Button>().Single(button=>button.Text=="Вставить пример").PerformClick();
                Check(captured!=null&&captured.Args.Contains("qemu-vdagent,id=vdagent,clipboard=on,mouse=off")&&captured.Args.Contains("gtk,clipboard=on"),"clipboard help inserts the complete requested GTK and vdagent example");
                Check(Field<ThemedEditor>(help,"body").Text.Contains("Ctrl+Alt+G"),"clipboard help explains releasing keyboard capture");
                Save(form,Path.Combine(args[0],"clipboard-help.png"));form.Close();
            }
            using(var main=new MainWindow(args[0],null,true,false)){
                main.Demo();var states=Field<Dictionary<string,string>>(main,"states");
                using(var panel=new VmWindow(main,"ubuntu-desktop")){
                    panel.Show();Application.DoEvents();
                    foreach(string state in new[]{"shutdown","stopped","running","error"}){
                        states["ubuntu-desktop"]=state;panel.RefreshState();Application.DoEvents();
                        Check(!panel.IsDisposed&&panel.Visible&&panel.Text.Contains(Theme.State(state)),"VM panel remains open and updates its state: "+state);
                        Check(!main.Visible,"VM state changes do not bring the main window forward: "+state);
                    }
                    Check(All(panel).OfType<Button>().Single(button=>button.Text=="▶ Запустить").Enabled,"restart action remains available after the guest stops");
                    Save(panel,Path.Combine(args[0],"vm-panel-after-stop.png"));panel.Close();Check(!panel.Visible,"VM panel still closes when explicitly requested");
                }
            }
            using(var form=new Form())using(var summary=new ResourceSummary()){
                Theme.Form(form,670,220);summary.Dock=DockStyle.Top;form.Controls.Add(summary);form.Show();
                string title="WHPX · аппаратное ускорение — длинное название пользовательского профиля";
                summary.ShowData(J.O("ram_mb",4096,"cores",4,"launch_profile","whpx","launch_profile_label",title),true);Application.DoEvents();
                var value=Field<Label>(summary,"profile");var tooltip=Field<ToolTip>(summary,"profileTip");
                Check(value.Text=="WHPX"&&!value.AutoSize&&value.Bottom<=summary.Height,"resource acceleration stays on one line inside the summary");
                Check(tooltip.GetToolTip(value)==title,"full profile name remains available without expanding the resource row");
                summary.ShowData(J.O("ram_mb",4096,"cores",4,"launch_profile","custom-example","launch_profile_label",title,"launch_profile_settings",J.O("accelerator","tcg")),true);
                Check(value.Text=="TCG","custom profile resource shows its selected accelerator");
                Save(form,Path.Combine(args[0],"resource-summary-layout.png"));form.Close();
            }
            using(var main=new MainWindow(args[0],null,true,false))using(var doctor=new DoctorWindow(main,J.O("checks",Enumerable.Range(0,25).Select(index=>J.O("title","Проверка "+index,"scope","host","state","ok","detail","Строка диагностики\r\nДополнительные сведения")).ToArray()))){
                doctor.Show();Application.DoEvents();var scroll=All(doctor).OfType<ScrollSurface>().Single();
                Check(scroll.ScrollBar.Visible&&scroll.ScrollBar.Maximum>0&&!All(doctor).OfType<ScrollableControl>().Any(control=>control.AutoScroll),"Doctor uses a themed scrollbar without native white scroll chrome");
                scroll.ScrollOffset=scroll.ScrollBar.Maximum;Application.DoEvents();
                Check(scroll.ScrollOffset>0,"Doctor report can scroll to its last checks");
                Save(doctor,Path.Combine(args[0],"doctor-scroll.png"));doctor.Close();
            }
            string folder=Path.Combine(Path.GetTempPath(),"QemuM test disks");
            Check(Theme.NewDiskPath(folder,"ubuntu-desktop")==Path.Combine(folder,"ubuntu-desktop.qcow2"),"new disk folder selection generates a predictable filename");
            bool traversal=false;try{Theme.NewDiskPath(folder,"../outside");}catch(InvalidOperationException){traversal=true;}Check(traversal,"generated disk name cannot escape the selected folder");
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
