using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class MenuTests {
    static readonly List<string> checks=new List<string>();
    static readonly List<string> errors=new List<string>();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    static void Check(bool condition,string name) {
        if(!condition) throw new InvalidOperationException(name);
        checks.Add(name);
    }
    static void Show(ContextMenuStrip menu,Form owner) {
        menu.Show(owner,new Point(10,10));
        menu.Location=owner.Location;
        Application.DoEvents();
    }
    static void Select(ContextMenuStrip menu,int index) {
        Rectangle bounds=menu.Items[index].Bounds;
        int point=((bounds.Top+bounds.Height/2)<<16)|(bounds.Left+bounds.Width/2);
        SendMessage(menu.Handle,0x201,new IntPtr(1),new IntPtr(point));
        SendMessage(menu.Handle,0x202,IntPtr.Zero,new IntPtr(point));
        Application.DoEvents();
    }
    [STAThread] static int Main(string[] args) {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException+=(sender,eventArgs)=>errors.Add(eventArgs.Exception.ToString());
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        Program.TestMode=true;
        ContextMenuStrip popup=null;
        try {
            using(var main=new MainWindow(Path.GetFullPath(args[1]),null,true,false)) {
                main.Show();main.Demo();Application.DoEvents();
                int clicks=0;
                var builder=typeof(MainWindow).GetMethod("BuildMenu",BindingFlags.Instance|BindingFlags.NonPublic);
                popup=(ContextMenuStrip)builder.Invoke(main,new object[]{new object[]{"Проверочный пункт",(Action)(()=>clicks++)}});
                Show(popup,main);popup.Close(ToolStripDropDownCloseReason.Keyboard);Application.DoEvents();
                Check(!popup.IsDisposed,"dismissed menu remains alive while its owner is open");
                for(int index=0;index<12;index++) {
                    Show(popup,main);Select(popup,0);
                    Check(!popup.IsDisposed&&!popup.Visible,"item selection closes but does not dispose the menu");
                }
                Check(clicks==12,"twelve native mouse selections invoke the action exactly once each");
                foreach(string field in new[]{"libraryMenu","machineMenu"}) {
                    var menu=(ContextMenuStrip)typeof(MainWindow).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(main);
                    for(int index=0;index<menu.Items.Count;index++) {
                        Show(menu,main);Select(menu,index);
                        Check(!menu.IsDisposed,"real "+field+" survives selection "+index);
                    }
                }
                var mode=(LaunchModeSelector)typeof(MainWindow).GetField("launchMode",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(main);
                var modeMenu=(ContextMenuStrip)typeof(LaunchModeSelector).GetField("menu",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(mode);
                Check(!modeMenu.ShowImageMargin&&!modeMenu.ShowCheckMargin,"launch mode shares the menu style without a system checkbox gutter");
                Show(modeMenu,main);using(var image=new Bitmap(modeMenu.Width,modeMenu.Height)){modeMenu.DrawToBitmap(image,modeMenu.ClientRectangle);image.Save(Path.Combine(Path.GetDirectoryName(args[0]),"launch-mode-menu.png"));}modeMenu.Close();
                for(int index=0;index<6;index++) {
                    Show(modeMenu,main);Select(modeMenu,index%2);
                    Check(mode.SelectedIndex==index%2&&!modeMenu.IsDisposed,"launch mode can be selected and reopened");
                }
                main.Close();
            }
            Check(popup.IsDisposed,"owner disposal releases its menu components");
            Check(errors.Count==0,"no WinForms thread exception during menu closing or selection");
            File.WriteAllText(args[0],J.Json().Serialize(J.O("passed",checks.Count,"checks",checks,"guest_operations",false)));
            return 0;
        } catch(Exception error) {
            File.WriteAllText(args[0],J.Json().Serialize(J.O("passed",checks.Count,"error",error.ToString(),"thread_errors",errors)));
            return 1;
        }
    }
}
