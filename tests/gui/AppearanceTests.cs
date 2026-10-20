using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class AppearanceTests {
    static readonly List<string> checks=new List<string>();
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr FindWindow(string type,string title);
    [DllImport("user32.dll")]static extern IntPtr GetDlgItem(IntPtr window,int id);
    static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(owner);}
    static IEnumerable<Control> All(Control parent){foreach(Control child in parent.Controls){yield return child;foreach(var nested in All(child))yield return nested;}}
    static void Click(Control parent,string text){All(parent).OfType<Button>().First(item=>item.Text==text).PerformClick();Application.DoEvents();}
    static void Check(bool condition,string label){if(!condition)throw new Exception(label);checks.Add(label);}
    static void Save(Form form,string path){Application.DoEvents();using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(path);}}
    static void Wait(){DateTime until=DateTime.UtcNow.AddMilliseconds(450);while(DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(10);}}
    [STAThread]static int Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Program.TestMode=true;
        string root=args[0],output=args[1];
        try{
            using(var number=Theme.Number(4096,1,1048576)){
                Check(number.Text=="4096"&&!All(number).OfType<NumericUpDown>().Any(),"number is a plain text field without spinner or thousands separators");
                foreach(string invalid in new[]{"","4,096","1.5","-1","0","1048577"}){number.Text=invalid;bool rejected=false;try{decimal value=number.Value;}catch(InvalidOperationException){rejected=true;}Check(rejected,"invalid integer rejected: "+invalid);}
                number.Text="8192";Check(number.Value==8192,"typed integer is read without stepping");
                number.CreateControl();SendMessage(number.Handle,0x20A,new IntPtr(120<<16),IntPtr.Zero);Check(number.Value==8192,"wheel does not modify a number");
            }
            using(var store=new AppearanceStore(root)){
                Check(store.Choices().Count>=25,"offline catalogue contains at least 25 distributions and systems");
                foreach(var row in store.Choices())Check(store.Logo(row)!=null,"PNG loads: "+row.Name);
                Check(store.Resolve("auto",J.O("iso_path","ubuntu-24.04-desktop-amd64.iso"),"other").Id=="ubuntu","ISO filename identifies Ubuntu");
                Check(store.Resolve("auto",J.O("iso_path","kubuntu-24.04.iso"),"Ubuntu").Id=="kubuntu","derived distribution takes precedence over its base");
                Check(store.Resolve("auto",J.O("iso_path","tails-amd64-7.15.iso"),"Ubuntu").Id=="tails","ISO selection takes precedence over VM name");
                string iso=Path.Combine(root,"unknown.iso");byte[] bytes=new byte[20*2048];bytes[16*2048]=1;Encoding.ASCII.GetBytes("CD001").CopyTo(bytes,16*2048+1);Encoding.ASCII.GetBytes("Fedora-Workstation-Live").CopyTo(bytes,16*2048+40);File.WriteAllBytes(iso,bytes);
                Check(store.Resolve("auto",J.O("iso_path",iso),"something").Id=="fedora","anonymous ISO is detected from its ISO9660 volume label");
                Check(store.Resolve("auto",J.O(),"Ubuntu Server").Id=="ubuntu","existing VM can use its name when ISO is absent");
                Check(store.Resolve("auto",J.O(),"completely unknown")==null&&store.Resolve("generic",J.O(),"Ubuntu")==null,"unknown system and generic choice keep the monitor emblem");
                Distro custom;using(var dialog=new CustomDistroDialog(store)){
                    dialog.Show();Field<FieldInput>(dialog,"name").Text="Моя лабораторная ОС";Field<FieldInput>(dialog,"png").Text=Path.Combine(root,"visual-assets","ubuntu.png");Click(dialog,"Добавить");custom=dialog.Value;
                }
                Check(custom!=null&&File.Exists(custom.File),"custom distro is copied through the actual graphical form");
                store.SaveChoice("existing",custom.Id);
                using(var reopened=new AppearanceStore(root)){
                    Check(reopened.Choice("existing")==custom.Id&&reopened.Choices().Any(row=>row.Id==custom.Id),"custom catalogue and per-VM choice survive reopening");
                    Check(reopened.Resolve(custom.Id,J.O("iso_path","fedora.iso"),"Ubuntu").Id==custom.Id,"manual custom icon overrides automatic detection");
                }
                store.SaveChoice("existing","auto");Check(store.Choice("existing")=="auto","automatic mode removes the per-VM override");
                File.WriteAllText(Path.Combine(root,"not-png.png"),"not an image");bool rejected=false;try{store.AddCustom("Broken",Path.Combine(root,"not-png.png"));}catch(InvalidOperationException){rejected=true;}Check(rejected,"fake PNG is rejected without adding a catalogue entry");
            }
            using(var backend=new Backend(root))using(var main=new MainWindow(root,null,true,false)){
                backend.Open();main.Backend=backend;main.Show();main.Demo();Wait();Save(main,Path.Combine(output,"appearance-main.png"));
                var search=Field<FieldInput>(main,"search");Check(search.SearchIcon&&search.Height>=40,"search uses a spacious themed field with a drawn icon");
                search.Text="ubuntu-server";Check(Field<FlowLayoutPanel>(main,"cards").Controls.Count==1,"new search field filters the existing library");search.Clear();
                var menu=Field<ContextMenuStrip>(main,"libraryMenu");menu.Show(main,new Point(240,70));Application.DoEvents();using(var bitmap=new Bitmap(menu.Width,menu.Height)){menu.DrawToBitmap(bitmap,menu.ClientRectangle);bitmap.Save(Path.Combine(output,"library-menu.png"));}menu.Close();Check(!menu.ShowImageMargin&&menu.Items[0].Height>=36,"library menu has readable rows without empty image gutters");
                var original=backend.Call("get",J.O("name","test"));byte[] before=File.ReadAllBytes(Path.Combine(root,"machines","test.json"));
                using(var editor=new EditDialog(main,"test",original)){
                    editor.Show();var deck=Field<PageDeck>(editor,"tabs");deck.SelectedPage=deck.Pages.Cast<PageView>().First(page=>page.Text=="Оформление");
                    editor.appearance.selector.SelectedItem=editor.appearance.selector.Items.Cast<Distro>().First(row=>row.Id=="kali");Wait();Save(editor,Path.Combine(output,"appearance-editor.png"));Click(editor,"Сохранить");
                }
                Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"machines","test.json"))),"saving only appearance leaves all VM configuration bytes unchanged");
                Check(!Directory.GetFiles(Path.Combine(root,"machines"),"*.bak").Any(),"appearance-only save creates no VM parameter backup or network migration");
                Check(main.Visuals.Choice("test")=="kali","appearance-only save stores the choice separately");
                using(var editor=new EditDialog(main,"test",backend.Call("get",J.O("name","test")))){
                    editor.Show();editor.appearance.selector.SelectedItem=editor.appearance.selector.Items.Cast<Distro>().First(row=>row.Id=="fedora");Click(editor,"Отмена");
                }
                Check(main.Visuals.Choice("test")=="kali","cancelling the editor preserves the saved icon choice");
                using(var creation=new CreateDialog(main,backend.Call("template_list"))){Check(creation.appearance.selector.Items.Cast<Distro>().Any(row=>row.Name=="Моя лабораторная ОС"),"previously added custom distro is selectable for the next VM");}
                using(var editor=new EditDialog(main,"test",backend.Call("get",J.O("name","test")))){
                    editor.Show();var deck=Field<PageDeck>(editor,"tabs");deck.SelectedPage=deck.Pages.Cast<PageView>().First(page=>page.Text=="Оформление");
                    var view=editor.appearance;var button=Field<Button>(view,"delete");view.selector.SelectedIndex=0;Check(!button.Enabled,"automatic mode cannot be deleted");view.selector.SelectedIndex=1;Check(!button.Enabled,"neutral computer icon cannot be deleted");
                    var custom=main.Visuals.AddCustom("Удаляемая ОС",Path.Combine(root,"visual-assets","ubuntu.png"));
                    main.Visuals.SaveChoice("test",custom.Id);main.Visuals.SaveChoice("second-vm",custom.Id);
                    view.selector.SelectedItem=view.selector.Items.Cast<Distro>().First(row=>row.Id==custom.Id);Check(button.Enabled,"custom distro has an enabled delete button");
                    string unchanged=File.ReadAllText(Path.Combine(root,"appearance.json"));
                    using(var timer=new System.Windows.Forms.Timer{Interval=40}){
                        timer.Tick+=(sender,eventArgs)=>{IntPtr box=FindWindow("#32770","Удаление дистрибутива");if(box!=IntPtr.Zero){timer.Stop();SendMessage(GetDlgItem(box,7),0xF5,IntPtr.Zero,IntPtr.Zero);}};timer.Start();button.PerformClick();
                    }
                    Check(File.Exists(custom.File)&&File.ReadAllText(Path.Combine(root,"appearance.json"))==unchanged,"cancelling actual deletion confirmation preserves catalogue PNG and choices");
                    using(var timer=new System.Windows.Forms.Timer{Interval=40}){
                        timer.Tick+=(sender,eventArgs)=>{IntPtr box=FindWindow("#32770","Удаление дистрибутива");if(box!=IntPtr.Zero){timer.Stop();SendMessage(GetDlgItem(box,6),0xF5,IntPtr.Zero,IntPtr.Zero);}};timer.Start();button.PerformClick();
                    }
                    Check(!File.Exists(custom.File)&&!main.Visuals.Choices().Any(row=>row.Id==custom.Id),"confirmed graphical removal deletes the custom catalogue entry and private PNG");
                    Check(main.Visuals.Choice("test")=="generic"&&main.Visuals.Choice("second-vm")=="generic","all explicit references to the deleted distro become neutral icons");
                    Check(view.Choice=="generic"&&!button.Enabled&&!view.selector.Items.Cast<Distro>().Any(row=>row.Id==custom.Id),"open appearance selector immediately refreshes after deletion");
                    main.Visuals.SaveChoice("ubuntu-desktop","ubuntu");Wait();
                    Check(Field<MonitorEmblem>(main,"emblem").Logo!=null,"main window initially displays the selected built-in logo");
                    view.selector.SelectedItem=view.selector.Items.Cast<Distro>().First(row=>row.Id=="ubuntu");
                    Check(main.Visuals.RemoveDistro("ubuntu")==null,"built-in distro is removable from the catalogue");Wait();
                    Check(!main.Visuals.Choices().Any(row=>row.Id=="ubuntu")&&File.Exists(Path.Combine(root,"visual-assets","ubuntu.png")),"built-in removal hides its record while preserving bundled assets");
                    Check(Field<MonitorEmblem>(main,"emblem").Logo==null,"deleting an in-use logo refreshes the main window without restart");
                    Check(main.Visuals.Resolve("auto",J.O("iso_path","ubuntu-24.04.iso"),"Ubuntu")==null,"automatic detection does not restore a deleted built-in distro");
                    using(var reopened=new AppearanceStore(root))Check(!reopened.Choices().Any(row=>row.Id=="ubuntu"||row.Id==custom.Id)&&reopened.Choice("test")=="generic","catalogue deletions and replacement icons survive reopening");
                    bool protectedMode=false;try{main.Visuals.RemoveDistro("auto");}catch(InvalidOperationException){protectedMode=true;}Check(protectedMode,"storage layer also rejects deleting a protected automatic mode");
                    Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"machines","test.json"))),"catalogue deletion does not modify VM launch configuration bytes");
                    Save(editor,Path.Combine(output,"appearance-delete.png"));editor.Close();
                }
                Wait();main.Close();
            }
            File.WriteAllText(Path.Combine(output,"appearance-results.json"),J.Json().Serialize(J.O("passed",checks.Count,"checks",checks)));return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(output,"appearance-results.json"),J.Json().Serialize(J.O("passed",checks.Count,"error",error.ToString())));return 1;}
    }
}
