using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class SelectorTests {
    static readonly List<string> checks=new List<string>();
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(owner);}
    static void Check(bool condition,string label){if(!condition)throw new Exception(label);checks.Add(label);}
    [STAThread]static int Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Program.TestMode=true;
        try{
            using(var form=new Form())using(var selector=Theme.Combo("NAT · Интернет","TCP · слушать","TAP · Windows")){
                Theme.Form(form,480,250);selector.Dock=DockStyle.Top;form.Controls.Add(selector);form.Show();Application.DoEvents();
                var popup=Field<ToolStripDropDown>(selector,"popup");var list=Field<ThemedList>(selector,"choices");
                Check(selector.SelectedIndex==0&&selector.Text=="NAT · Интернет","selector initializes its chosen text");
                SendMessage(selector.Handle,0x100,new IntPtr((int)Keys.Down),IntPtr.Zero);
                Check(selector.SelectedIndex==1,"closed selector supports arrow-key selection");
                for(int i=0;i<15;i++){
                    selector.ShowChoices();Application.DoEvents();Check(selector.PopupVisible,"popup can be reopened "+i);
                    list.SelectedIndex=2;
                    if(i%2==0){SendMessage(list.Handle,0x100,new IntPtr((int)Keys.Escape),IntPtr.Zero);Check(selector.SelectedIndex==1,"Escape cancels pending selection "+i);}
                    else{SendMessage(list.Handle,0x100,new IntPtr((int)Keys.Enter),IntPtr.Zero);Check(selector.SelectedIndex==2,"Enter commits selected item "+i);selector.SelectedIndex=1;}
                    Application.DoEvents();Check(!selector.PopupVisible&&!popup.IsDisposed,"auto-close keeps popup reusable "+i);
                }
                selector.ShowChoices();Application.DoEvents();list.SelectedIndex=0;
                SendMessage(list.Handle,0x201,new IntPtr(1),new IntPtr((19<<16)|35));SendMessage(list.Handle,0x202,IntPtr.Zero,new IntPtr((19<<16)|35));
                Check(selector.SelectedIndex==0&&!selector.PopupVisible,"native mouse click commits a popup row");
                selector.ShowChoices();Application.DoEvents();
                using(var bitmap=new Bitmap(popup.Width,popup.Height)){popup.DrawToBitmap(bitmap,popup.ClientRectangle);bitmap.Save(Path.Combine(args[0],"selector-popup.png"));}
                popup.Close();selector.DropDownStyle=ComboBoxStyle.DropDown;selector.Text="my-custom-nic";
                Check(selector.SelectedIndex==-1&&selector.Text=="my-custom-nic","editable NIC selector preserves a custom model");
                var input=Field<TextBox>(selector,"input");input.Text="TAP · Windows";
                Check(selector.SelectedIndex==2,"typing an existing choice updates selection");
                input.Text="virtio-net-pci";selector.ShowChoices();Application.DoEvents();popup.Close();
                Check(selector.Text=="virtio-net-pci","cancelling a popup preserves custom typed text");
                selector.ShowChoices();Application.DoEvents();form.Close();selector.Dispose();
                Check(popup.IsDisposed,"owner disposal releases an open popup safely");
            }
            File.WriteAllText(Path.Combine(args[0],"selector-results.json"),J.Json().Serialize(J.O("passed",checks.Count,"checks",checks)));return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[0],"selector-results.json"),J.Json().Serialize(J.O("passed",checks.Count,"error",error.ToString())));return 1;}
    }
}
