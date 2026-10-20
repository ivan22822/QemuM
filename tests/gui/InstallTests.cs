using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using QemuM.Update;

// Explicit release smoke test. All installation writes stay in its supplied build fixture.
static class InstallTests {
    [DllImport("user32.dll")]static extern bool ShowWindow(IntPtr window,int command);
    static void Check(bool value,string text){if(!value)throw new Exception(text);}
    static int Main(string[] args){
        string root=Path.Combine(args[0],"install-"+Guid.NewGuid().ToString("N"));
        Process launcher=null,app=null;
        try{
            Directory.CreateDirectory(root);
            SetupCore.ExtractPython(args[3],Path.Combine(root,"runtime","python"));
            var package=Core.Stage(args[1],Core.Hash(args[1]),root);Core.Activate(root,package);
            Core.AtomicJson(Path.Combine(root,"runtime.json"),new RuntimeSettings{python="runtime\\python\\python.exe",qemu_dir=args[4]});
            File.Copy(args[2],Path.Combine(root,"QemuM.exe"));
            string expected=Path.Combine(root,"versions",package.version,"VMManager.exe");
            launcher=Process.Start(new ProcessStartInfo(Path.Combine(root,"QemuM.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
            DateTime deadline=DateTime.UtcNow.AddSeconds(30);
            bool ready=false;
            while(DateTime.UtcNow<deadline&&!launcher.HasExited){
                if(app==null)foreach(var candidate in Process.GetProcessesByName("VMManager")){
                    try{if(String.Equals(candidate.MainModule.FileName,expected,StringComparison.OrdinalIgnoreCase)){app=candidate;break;}}
                    catch(System.ComponentModel.Win32Exception){}
                    if(candidate!=app)candidate.Dispose();
                }
                if(app!=null&&app.MainWindowHandle!=IntPtr.Zero)ShowWindow(app.MainWindowHandle,0);
                string health=Path.Combine(root,"health");
                if(Directory.Exists(health)&&Directory.GetFiles(health,"*.ready").Any()){ready=true;break;}
                Thread.Sleep(100);
            }
            Check(ready,"installed frontend and embedded Python reached readiness");
            Check(app!=null&&!app.HasExited,"owned frontend process found");
            Check(Directory.Exists(Path.Combine(root,"data")),"separate data root created");
            Core.VerifyDirectory(Path.Combine(root,"versions",package.version));
            Check(!Directory.Exists(Path.Combine(root,"versions",package.version,"machines")),"no user state in app version");
            bool locked=false;try{using(Core.Lock(root,"running.lock")){}}catch(IOException){locked=true;}
            Check(locked,"running frontend blocks update");
            // WM_CLOSE is sent only to the executable at the exact test fixture path.
            ShowWindow(app.MainWindowHandle,5);Check(app.CloseMainWindow(),"close owned frontend");
            Check(app.WaitForExit(15000)&&launcher.WaitForExit(15000),"frontend and launcher exit cleanly");
            Check(Core.Current(root).current==package.version,"healthy version remains active");
            Core.VerifyDirectory(Path.Combine(root,"versions",package.version));
            var future=Core.Stage(args[5],Core.Hash(args[5]),root);Core.Activate(root,future);
            launcher.Dispose();
            launcher=Process.Start(new ProcessStartInfo(Path.Combine(root,"QemuM.exe")){UseShellExecute=false,CreateNoWindow=true});
            Check(launcher.WaitForExit(15000),"rewritten frontend completed");
            Check(File.ReadAllText(Path.Combine(root,"data","future-frontend.txt"))=="protocol-1","different frontend works without Python");
            Check(Core.Current(root).current==future.version,"replacement frontend acknowledged readiness");
            Check(Core.Current(root).previous==package.version,"previous implementation retained");
            File.WriteAllText(Path.Combine(args[0],"install-results.json"),"{\"passed\":13,\"existing_installation_touched\":false}");
            return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[0],"install-error.txt"),error.ToString());return 1;}
        finally{
            if(app!=null){if(!app.HasExited)app.Kill();app.Dispose();}
            if(launcher!=null){if(!launcher.HasExited)launcher.Kill();launcher.Dispose();}
        }
    }
}
