using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using QemuM.Update;

// Explicit release smoke test. All installation writes stay in its supplied build fixture.
static class InstallTests {
    [DllImport("user32.dll")]static extern bool ShowWindow(IntPtr window,int command);
    delegate bool EnumProc(IntPtr window,IntPtr data);
    [DllImport("user32.dll")]static extern bool EnumWindows(EnumProc callback,IntPtr data);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr window,StringBuilder name,int count);
    [DllImport("user32.dll")]static extern IntPtr PostMessage(IntPtr window,int message,IntPtr wparam,IntPtr lparam);
    static bool CloseOwnedFrontend(Process app){
        DateTime deadline=DateTime.UtcNow.AddSeconds(15);
        while(!app.HasExited&&DateTime.UtcNow<deadline){
            EnumWindows((window,data)=>{uint pid;GetWindowThreadProcessId(window,out pid);if(pid==(uint)app.Id){var name=new StringBuilder(64);GetClassName(window,name,64);if(name.ToString()=="#32770")PostMessage(window,0x111,new IntPtr(6),IntPtr.Zero);}return true;},IntPtr.Zero);
            app.CloseMainWindow();Thread.Sleep(200);
        }
        return app.HasExited;
    }
    static void Check(bool value,string text){if(!value)throw new Exception(text);}
    static int Main(string[] args){
        string root=Path.Combine(args[0],"install-"+Guid.NewGuid().ToString("N").Substring(0,12));
        Process launcher=null,app=null;
        try{
            Directory.CreateDirectory(root);
            var installer=System.Reflection.Assembly.LoadFile(Path.GetFullPath(args[8]));
            Check(ResourceHash(installer,"app.zip")==Core.Hash(args[1]),"published Setup embeds the exact checked app package");
            Check(ResourceHash(installer,"QemuM.exe")==Core.Hash(args[9]),"published Setup embeds the new root launcher");
            SetupCore.ExtractPython(args[3],Path.Combine(root,"runtime","python"));
            if(args.Length>6&&!String.IsNullOrEmpty(args[6])){var previous=Core.Stage(args[6],Core.Hash(args[6]),root);Core.Activate(root,previous);}
            string userData=Path.Combine(root,"data");Directory.CreateDirectory(userData);
            string machineFolder=Path.Combine(userData,"machines");Directory.CreateDirectory(machineFolder);
            string privateDisk=Path.Combine(userData,"private-sentinel.qcow2");File.WriteAllText(privateDisk,"PRIVATE MEDIA SENTINEL - NEVER BOOT");
            string machine=Path.Combine(machineFolder,"upgrade-test.json");File.WriteAllText(machine,"{\"disk_path\":\""+privateDisk.Replace("\\","\\\\")+"\",\"iso_path\":\"\",\"ram_mb\":128,\"cores\":1,\"extra_args\":[]}");
            // Backend will find this durable pending operation after upgrading.
            string snapshotFolder;
            // Derive the VM key rather than depending on a machine/user path.
            using(var hash=System.Security.Cryptography.SHA256.Create())snapshotFolder=@"\\?\"+Path.Combine(userData,"snapshot-state",BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes("upgrade-test"))).Replace("-","").ToLowerInvariant());
            Directory.CreateDirectory(Path.Combine(snapshotFolder,"operations"));
            string journal=Path.Combine(snapshotFolder,"operations","f7b09d64-2b38-416f-aa8f-993a8815bb5b.json");
            File.WriteAllText(journal,"{\"schema_version\":1,\"id\":\"f7b09d64-2b38-416f-aa8f-993a8815bb5b\",\"name\":\"upgrade-test\",\"action\":\"metadata\",\"state\":\"uncertain\",\"created\":1,\"steps\":[]}");
            byte[] machineBytes=File.ReadAllBytes(machine),journalBytes=File.ReadAllBytes(journal);string diskHash=Core.Hash(privateDisk);
            Package package;
            var before=Core.Current(root);
            if(before!=null&&Core.Read<Package>(Path.Combine(root,"versions",before.current,"package.json")).version==Core.Read<Package>(ManifestFromZip(args[1],root)).version){
                using(Core.Lock(root,"running.lock"))using(Core.Lock(root,"update.lock"))Core.RepairSameVersion(root,args[1],Core.Hash(args[1]));
                package=Core.VerifyDirectory(Path.Combine(root,"versions",before.current));
                Check(package.build>0,"legacy same-version installation upgraded to a numbered build");
            }else{package=Core.Stage(args[1],Core.Hash(args[1]),root);Core.Activate(root,package);}
            Check(File.ReadAllBytes(machine).SequenceEqual(machineBytes)&&File.ReadAllBytes(journal).SequenceEqual(journalBytes)&&Core.Hash(privateDisk)==diskHash,"activation preserves VM registration, pending journal and media bytes");
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
                    catch(InvalidOperationException){} // A candidate may exit during enumeration.
                    catch(NullReferenceException){} // .NET ProcessModule can lose its native process during FileName.
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
            Check(File.ReadAllBytes(machine).SequenceEqual(machineBytes)&&File.ReadAllBytes(journal).SequenceEqual(journalBytes)&&Core.Hash(privateDisk)==diskHash,"first startup preserves all existing user state");
            var inspect=new ProcessStartInfo(Path.Combine(root,"runtime","python","python.exe"),"-B -X utf8 -u "+Quote(Path.Combine(root,"versions",package.version,"vm_backend.py"))+" --root "+Quote(userData)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var backend=Process.Start(inspect)){backend.StandardInput.WriteLine("{\"id\":1,\"command\":\"list\"}");string reply=backend.StandardOutput.ReadLine();backend.StandardInput.Close();Check(backend.WaitForExit(10000)&&reply!=null&&reply.Contains("\"snapshot_recovery\": true"),"upgraded backend detects the preserved unfinished journal");}
            Core.VerifyDirectory(Path.Combine(root,"versions",package.version));
            var patch=new Feed{schema=1,min_launcher_protocol=1,version=package.version,build=package.build+1,channel="alpha",url="https://github.com/ivan22822/QemuM/releases/download/v"+package.version+"/fixture-patch.zip",sha256=new string('a',64)};
            var next=Core.Latest(root,(url,path,limit)=>File.WriteAllText(path,url==Core.Api?"[]":new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(patch)));
            Check(Core.CompareBuild(next.version,next.build,package.version,package.build)>0,"clean/upgraded installation detects a later build of the same alpha version");
            Check(!Directory.Exists(Path.Combine(root,"versions",package.version,"machines")),"no user state in app version");
            bool locked=false;try{using(Core.Lock(root,"running.lock")){}}catch(IOException){locked=true;}
            Check(locked,"running frontend blocks update");
            // WM_CLOSE is sent only to the executable at the exact test fixture path.
            ShowWindow(app.MainWindowHandle,5);Check(app.CloseMainWindow(),"close owned frontend");
            Check(CloseOwnedFrontend(app)&&launcher.WaitForExit(15000),"frontend and launcher exit cleanly");
            Check(Core.Current(root).current==package.version,"healthy version remains active");
            Check(!File.Exists(Path.Combine(root,"repair.json")),"healthy patched frontend completes same-version upgrade");
            Core.VerifyDirectory(Path.Combine(root,"versions",package.version));
            using(Core.Lock(root,"running.lock"))using(Core.Lock(root,"update.lock"))Core.RepairSameVersion(root,args[7],Core.Hash(args[7]));
            launcher.Dispose();launcher=Process.Start(new ProcessStartInfo(Path.Combine(root,"QemuM.exe")){UseShellExecute=false,CreateNoWindow=true});
            Check(launcher.WaitForExit(15000),"same version replacement frontend exits cleanly");
            Check(Core.Current(root).current==package.version&&!File.Exists(Path.Combine(root,"repair.json")),"health acknowledgement completes reinstall without version increment");
            Check(File.ReadAllBytes(machine).SequenceEqual(machineBytes)&&File.ReadAllBytes(journal).SequenceEqual(journalBytes)&&Core.Hash(privateDisk)==diskHash,"same version reinstall preserves VM data and pending recovery");
            using(Core.Lock(root,"running.lock"))using(Core.Lock(root,"update.lock")){Core.RepairSameVersion(root,args[1],Core.Hash(args[1]));Core.CompleteRepair(root,package.version);}
            var future=Core.Stage(args[5],Core.Hash(args[5]),root);Core.Activate(root,future);
            launcher.Dispose();
            launcher=Process.Start(new ProcessStartInfo(Path.Combine(root,"QemuM.exe")){UseShellExecute=false,CreateNoWindow=true});
            Check(launcher.WaitForExit(15000),"rewritten frontend completed");
            Check(File.ReadAllText(Path.Combine(root,"data","future-frontend.txt"))=="protocol-1","different frontend works without Python");
            Check(Core.Current(root).current==future.version,"replacement frontend acknowledged readiness");
            Check(Core.Current(root).previous==package.version,"previous implementation retained");
            File.WriteAllText(Path.Combine(args[0],"install-results.json"),"{\"passed\":19,\"existing_installation_touched\":false,\"pending_journal_preserved\":true,\"setup_payload_verified\":true,\"same_version_patch_discovered\":true}");
            return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[0],"install-error.txt"),error.ToString());return 1;}
        finally{
            if(app!=null){if(!app.HasExited)app.Kill();app.Dispose();}
            if(launcher!=null){if(!launcher.HasExited)launcher.Kill();launcher.Dispose();}
        }
    }
    static string Quote(string value){return "\""+value.Replace("\"","\\\"")+"\"";}
    static string ResourceHash(System.Reflection.Assembly assembly,string name){
        using(var input=assembly.GetManifestResourceStream(name))using(var hash=System.Security.Cryptography.SHA256.Create()){
            if(input==null)throw new Exception("Missing installer resource: "+name);
            return BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").ToLowerInvariant();
        }
    }
    static string ManifestFromZip(string zip,string root){
        string path=Path.Combine(root,"incoming-manifest.json");
        using(var archive=System.IO.Compression.ZipFile.OpenRead(zip))using(var input=archive.GetEntry("package.json").Open())using(var output=File.Create(path))input.CopyTo(output);
        return path;
    }
}
