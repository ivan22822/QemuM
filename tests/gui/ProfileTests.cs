using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class ProfileTests {
    static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(owner);}
    static IEnumerable<Control> All(Control root){foreach(Control child in root.Controls){yield return child;foreach(var item in All(child))yield return item;}}
    static void Click(Control root,string title){All(root).OfType<Button>().Single(button=>button.Text==title).PerformClick();Application.DoEvents();}
    static void Check(bool value,string title){if(!value)throw new Exception(title);}
    [STAThread]static int Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Program.TestMode=true;
        try{
            using(var backend=new Backend(args[0])){
                backend.Open();string vmPath=Path.Combine(args[0],"machines","test.json");byte[] before=File.ReadAllBytes(vmPath);object selected;
                using(var dialog=new LaunchProfileDialog(backend,"whpx-compatible")){
                    dialog.Show();Application.DoEvents();
                    Check(Field<FieldInput>(dialog,"options").Text=="kernel-irqchip=off","compatible preset loads its options");
                    Click(dialog,"Создать свой");Field<FieldInput>(dialog,"title").Text="My Intel profile";
                    Field<FieldInput>(dialog,"cpu").Text="host,vmx=off";Click(dialog,"Сохранить профиль");
                    Check(J.A(J.V(backend.Call("profile_list",J.O()),"profiles")).Any(row=>J.S(row,"label")=="My Intel profile"),"new profile persists through RPC");
                    using(var image=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(image,dialog.ClientRectangle);image.Save(Path.Combine(args[1],"launch-profiles.png"));}
                    Click(dialog,"Выбрать");selected=dialog.Selected;
                }
                Check(before.SequenceEqual(File.ReadAllBytes(vmPath)),"catalog editing leaves VM unchanged");
                var data=J.O("launch_profile",J.S(selected,"id"),"launch_profile_label",J.S(selected,"label"),"launch_profile_settings",J.V(selected,"settings"));
                using(var selector=LaunchProfiles.Selector())using(var owner=new Form()){
                    using(var field=LaunchProfiles.Field(selector,backend,owner)){
                        LaunchProfiles.Load(selector,data);var merged=J.O();LaunchProfiles.Apply(selector,merged);
                        Check(J.S(merged,"launch_profile")==J.S(selected,"id")&&J.S(J.V(merged,"launch_profile_settings"),"cpu")=="host,vmx=off","VM receives an exact profile snapshot");
                    }
                }
                var catalog=backend.Call("profile_list",J.O());backend.Call("profile_delete",J.O("hash",J.S(catalog,"hash"),"id",J.S(selected,"id")));
                using(var selector=LaunchProfiles.Selector())using(var owner=new Form())using(var field=LaunchProfiles.Field(selector,backend,owner)){
                    LaunchProfiles.Load(selector,data);var merged=J.O();LaunchProfiles.Apply(selector,merged);
                    Check(J.S(J.V(merged,"launch_profile_settings"),"cpu")=="host,vmx=off","deleted catalog preset remains usable from the VM snapshot");
                }
                using(var dialog=new LaunchProfileDialog(backend,"auto")){
                    dialog.Show();Application.DoEvents();Check(!Field<FieldInput>(dialog,"cpu").Enabled,"auto algorithm is read-only");dialog.Close();
                }
            }
            File.WriteAllText(Path.Combine(args[1],"profile-results.json"),"{\"passed\":6}");return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"profile-error.txt"),error.ToString());return 1;}
    }
}
