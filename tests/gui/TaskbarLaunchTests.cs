using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

static class TaskbarLaunchTests {
    static void Check(bool value,string text){if(!value)throw new Exception(text);}
    [STAThread] static int Main(string[] args) {
        string self=Assembly.GetExecutingAssembly().Location;
        if(Path.GetFileName(self).Equals("QemuM.exe",StringComparison.OrdinalIgnoreCase)){
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(self),"loader-started.txt"),"started");return 0;
        }
        if(Path.GetFileName(self).Equals("VMManager.exe",StringComparison.OrdinalIgnoreCase)){
            typeof(Program).GetMethod("Main",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{new string[0]});return 0;
        }
        try {
            string root=Path.Combine(args[0],"installed fixture"),app=Path.Combine(root,"versions","0.1.0-alpha.7");
            Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(app,"package.json"),"{}");
            File.WriteAllText(Path.Combine(root,"active.json"),"{}");
            File.WriteAllText(Path.Combine(root,"runtime.json"),"{}");
            string loader=Path.Combine(root,"QemuM.exe"),entry=Path.Combine(app,"VMManager.exe");
            File.Copy(self,loader);File.Copy(self,entry);
            Check(Program.InstalledLauncher(app,null,new string[0])==loader,"pinned executable resolves the installation loader");
            Check(Program.InstalledLauncher(app,Path.Combine(root,"data"),new string[0])==null,"loader child does not loop");
            Check(Program.InstalledLauncher(app,null,new[]{"--root",args[0]})==null,"explicit development root is preserved");
            Check(Program.InstalledLauncher(app,null,new[]{"--preview","scene.png"})==null,"preview is preserved");
            Check(Program.InstalledLauncher(args[0],null,new string[0])==null,"development executable stays standalone");
            var start=new ProcessStartInfo(entry){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=app};
            start.EnvironmentVariables.Remove("QEMUM_DATA_DIR");
            start.EnvironmentVariables.Remove("QEMUM_INSTALL_DIR");
            start.EnvironmentVariables.Remove("QEMUM_PYTHON");
            using(var child=Process.Start(start)){
                if(!child.WaitForExit(10000)){child.Kill();throw new Exception("pinned launch did not hand off");}
                Check(child.ExitCode==0,"pinned entry exits successfully");
            }
            var clock=Stopwatch.StartNew();
            while(!File.Exists(Path.Combine(root,"loader-started.txt"))&&clock.ElapsedMilliseconds<10000)Thread.Sleep(20);
            Check(File.Exists(Path.Combine(root,"loader-started.txt")),"actual pinned process starts loader");
            Check(Directory.GetFiles(app).Length==2,"pinned launch writes no state into version directory");
            File.Delete(Path.Combine(root,"runtime.json"));bool rejected=false;
            try{Program.InstalledLauncher(app,null,new string[0]);}catch(IOException){rejected=true;}
            Check(rejected,"incomplete installation cannot fall back to writing into binaries");
            File.WriteAllText(Path.Combine(args[1],"taskbar-launch-results.json"),"{\"passed\":true,\"checks\":9,\"private_fixture_only\":true}");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);File.WriteAllText(Path.Combine(args[1],"taskbarlaunch-error.txt"),error.ToString());return 1;}
    }
}
