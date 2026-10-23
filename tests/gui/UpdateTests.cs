using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using QemuM.Update;

static class UpdateTests {
    static int count;
    static void Check(bool value,string text){if(!value)throw new Exception(text);count++;}
    static void Reject(Action action,string text){bool failed=false;try{action();}catch(IOException){failed=true;}Check(failed,text);}
    static string Make(string root,string version,string extra=null,bool corrupt=false,string body=null,int build=0){
        string zip=Path.Combine(root,Guid.NewGuid().ToString("N")+".zip");
        byte[] content=Encoding.UTF8.GetBytes("test executable "+version+(body??""));
        string hash;using(var stream=new MemoryStream(content))hash=Core.Hash(stream);
        var package=new Package{schema=1,min_launcher_protocol=1,channel="alpha",version=version,build=build,entrypoint="App.exe",
            files=new Dictionary<string,string>{{"App.exe",corrupt?new string('0',64):hash}}};
        using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)){
            using(var stream=archive.CreateEntry("App.exe").Open())stream.Write(content,0,content.Length);
            using(var writer=new StreamWriter(archive.CreateEntry("package.json").Open(),new UTF8Encoding(false)))writer.Write(new JavaScriptSerializer().Serialize(package));
            if(extra!=null)using(var writer=new StreamWriter(archive.CreateEntry(extra).Open()))writer.Write("extra");
        }
        return zip;
    }
    static void Main(string[] args){
        string root=Path.Combine(args[0],"update-"+Guid.NewGuid().ToString("N").Substring(0,12));Directory.CreateDirectory(root);
        try{
            Check(Core.Compare("0.1.0-alpha.10","0.1.0-alpha.2")>0,"numeric prerelease comparison");
            Check(Core.Compare("1.0.0-alpha.1","0.99.0-alpha.99")>0,"numeric major comparison");
            Check(Core.CompareBuild("0.1.0-alpha.4",2,"0.1.0-alpha.4",0)>0,"patch build upgrades a legacy manifest without changing version");
            Check(Core.CompareBuild("0.1.0-alpha.4",1,"0.1.0-alpha.4",2)<0,"older same-version build is a downgrade");
            Check(Core.CompareBuild("0.1.0-alpha.5",0,"0.1.0-alpha.4",999)>0,"next normal release outranks a patched earlier version");
            var patchFeed=new Feed{schema=1,min_launcher_protocol=1,channel="alpha",version="0.1.0-alpha.4",build=2,url="https://github.com/ivan22822/QemuM/releases/download/v0.1.0-alpha.4/patch.zip",sha256=new string('a',64)};
            Core.ValidateFeed(patchFeed);
            Action<string,string,long> fetch=(url,path,limit)=>{
                if(url==Core.PatchChannel)File.WriteAllText(path,new JavaScriptSerializer().Serialize(patchFeed));
                else if(url==Core.Api)File.WriteAllText(path,"[{\"tag_name\":\"v0.1.0-alpha.5\",\"draft\":false,\"assets\":[{\"name\":\"update.json\",\"browser_download_url\":\"https://github.com/ivan22822/QemuM/releases/download/v0.1.0-alpha.5/update.json\"}]}]");
                else File.WriteAllText(path,new JavaScriptSerializer().Serialize(new Feed{schema=1,min_launcher_protocol=1,channel="alpha",version="0.1.0-alpha.5",url="https://github.com/ivan22822/QemuM/releases/download/v0.1.0-alpha.5/app.zip",sha256=new string('b',64)}));
            };
            Check(Core.Latest(root,fetch).version=="0.1.0-alpha.5","normal release remains discoverable after patch channel introduction");
            Action<string,string,long> onlyPatch=(url,path,limit)=>{File.WriteAllText(path,url==Core.Api?"[]":new JavaScriptSerializer().Serialize(patchFeed));};
            Check(Core.Latest(root,onlyPatch).build==2,"patch discovered without a separate release");
            Reject(()=>Core.Latest(root,(url,path,limit)=>File.WriteAllText(path,"null")),"broken channel cannot silently fall back to an old release");
            bool networkFailed=false;try{Core.Latest(root,(url,path,limit)=>{throw new System.Net.WebException("fixture network failure");});}catch(System.Net.WebException){networkFailed=true;}Check(networkFailed,"network errors are surfaced instead of advertising stale metadata");
            foreach(string url in new[]{"https://example.invalid/app.zip","http://github.com/ivan22822/QemuM/releases/download/v0.1.0-alpha.4/app.zip","https://github.com/another/repo/releases/download/v0.1.0-alpha.4/app.zip","https://github.com/ivan22822/QemuM/releases/download/v0.1.0-alpha.3/app.zip"}){
                string previous=patchFeed.url;patchFeed.url=url;Reject(()=>Core.ValidateFeed(patchFeed),"untrusted or mismatched patch URL");patchFeed.url=previous;
            }
            patchFeed.build=-1;Reject(()=>Core.ValidateFeed(patchFeed),"negative patch build");patchFeed.build=2;
            Core.MatchFeed(new Package{version=patchFeed.version,build=2,min_launcher_protocol=1},patchFeed);
            Reject(()=>Core.MatchFeed(new Package{version=patchFeed.version,build=1,min_launcher_protocol=1},patchFeed),"package cannot claim a different build than the feed");
            var incompatible=new Package{schema=1,channel="stable",min_launcher_protocol=1,version="0.1.0-alpha.1",entrypoint="App.exe",files=new Dictionary<string,string>{{"App.exe",new string('0',64)}}};
            Reject(()=>Core.Validate(incompatible),"wrong channel");
            incompatible.channel="alpha";incompatible.min_launcher_protocol=2;
            Reject(()=>Core.Validate(incompatible),"new launcher protocol required");
            incompatible.min_launcher_protocol=1;incompatible.schema=2;
            Reject(()=>Core.Validate(incompatible),"unknown package schema");
            Reject(()=>{using(var source=new MemoryStream(new byte[10]))using(var target=new MemoryStream())Core.CopyBounded(source,target,1);},"lying archive size");
            Reject(()=>{using(var source=new MemoryStream(new byte[1]))using(var target=new MemoryStream())Core.CopyBounded(source,target,10);},"truncated archive entry");
            foreach(string invalid in new[]{"../bad","1.0.0","01.0.0-alpha.1","1.0.0-alpha.9999999"})Reject(()=>Core.Version(invalid),"invalid version "+invalid);
            foreach(string path in new[]{"../outside","/root","a\\b","a/../b","NUL.txt","a:file","a.","a//b"})Reject(()=>Core.Relative(path),"unsafe path "+path);
            string data=Path.Combine(root,"data");Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"sentinel.txt"),"KEEP USER DATA");
            string stands=Path.Combine(data,"stands");Directory.CreateDirectory(stands);string stand=Path.Combine(stands,"fixture.json");File.WriteAllText(stand,"KEEP LOGICAL STAND");
            string snapshotState=Path.Combine(data,"snapshot-state","test-fixture");Directory.CreateDirectory(snapshotState);string snapshotCatalog=Path.Combine(snapshotState,"catalog.json"),snapshotJournal=Path.Combine(snapshotState,"operation.json");File.WriteAllText(snapshotCatalog,"KEEP SNAPSHOT CATALOG");File.WriteAllText(snapshotJournal,"KEEP PENDING SNAPSHOT JOURNAL");
            string first=Make(root,"0.1.0-alpha.1");
            Reject(()=>Core.Stage(first,new string('0',64),root),"bad download hash");
            var one=Core.Stage(first,Core.Hash(first),root);Core.Activate(root,one);
            Check(Core.Current(root).current==one.version,"first activation");
            string bootstrap=Path.Combine(root,"bootstrap-source.exe"),bootstrapTarget=Path.Combine(root,"QemuM.exe");File.WriteAllText(bootstrap,"NEW BOOTSTRAP");File.WriteAllText(bootstrapTarget,"OLD BOOTSTRAP");Core.RefreshBootstrap(root,bootstrap);
            Check(File.ReadAllText(bootstrapTarget)=="NEW BOOTSTRAP"&&Directory.GetFiles(Path.Combine(root,"launcher-backups")).Any(path=>File.ReadAllText(path)=="OLD BOOTSTRAP"),"atomic bootstrap refresh retains previous loader");
            byte[] activeBefore=File.ReadAllBytes(Path.Combine(root,"active.json"));
            string standRuntime=Path.Combine(data,"stand-runtime",new string('1',32)+".json");Directory.CreateDirectory(Path.GetDirectoryName(standRuntime));File.WriteAllText(standRuntime,"KEEP STAND JOURNAL");
            string standMarker=Path.Combine(data,"stand-runtime","active",new string('2',32)+".lock");Directory.CreateDirectory(Path.GetDirectoryName(standMarker));
            using(var lease=new FileStream(standMarker,FileMode.Create,FileAccess.ReadWrite,FileShare.ReadWrite)){
                lease.WriteByte(1);lease.Flush();
                Reject(()=>Core.AssertNoStandOperations(root),"running stand worker blocks updating even after the frontend exits");
                Reject(()=>Core.RepairSameVersion(root,first,Core.Hash(first)),"repair refuses an active stand operation before changing code");
                Reject(()=>Core.Rollback(root,one.version),"rollback also refuses an active stand operation");
            }
            Core.AssertNoStandOperations(root);File.Delete(standMarker);
            Check(!Core.RepairSameVersion(root,first,Core.Hash(first)),"identical package does not replace the current version");
            string wrongBuild=Make(root,one.version,body:"wrong build",build:1);patchFeed.version=one.version;patchFeed.url="https://github.com/ivan22822/QemuM/releases/download/v"+one.version+"/patch.zip";
            Reject(()=>Core.RepairSameVersion(root,wrongBuild,Core.Hash(wrongBuild),expectedFeed:patchFeed),"build mismatch rejected before replacing installed application");
            Check(Core.VerifyDirectory(Path.Combine(root,"versions",one.version)).build==0&&!File.Exists(Path.Combine(root,"repair.json")),"rejected patch preserves installed build and creates no repair intent");
            string patch=Make(root,one.version,body:"patch",build:2);Core.RepairSameVersion(root,patch,Core.Hash(patch),expectedFeed:patchFeed);
            Check(Core.VerifyDirectory(Path.Combine(root,"versions",one.version)).build==2,"same-version patch records its build in installed manifest");
            Check(Core.Rollback(root,one.version)&&Core.VerifyDirectory(Path.Combine(root,"versions",one.version)).build==0,"failed patch health restores legacy build number");
            string revised=Make(root,one.version,body:" revised");
            using(Core.Lock(root,"running.lock"))using(Core.Lock(root,"update.lock"))Check(Core.RepairSameVersion(root,revised,Core.Hash(revised)),"same numbered package can be explicitly reinstalled");
            Check(File.ReadAllBytes(Path.Combine(root,"active.json")).SequenceEqual(activeBefore),"same version repair preserves the active version and previous version");
            Check(Core.VerifyDirectory(Path.Combine(root,"versions",one.version)).version==one.version,"reinstalled package verified");
            Check(File.ReadAllText(Path.Combine(data,"sentinel.txt"))=="KEEP USER DATA"&&File.ReadAllText(snapshotJournal)=="KEEP PENDING SNAPSHOT JOURNAL","repair preserves data and snapshot recovery journal");
            Check(Core.RecoverRepair(root),"new package remains pending until health acknowledgement");
            Check(Core.Rollback(root,one.version),"failed startup restores prior build with the SAME version");
            Check(File.ReadAllText(stand)=="KEEP LOGICAL STAND","same-version repair and rollback preserve stand definitions");
            Check(Core.Hash(Path.Combine(root,"versions",one.version,"App.exe"))==one.files["App.exe"],"exact prior build returned");
            Core.RepairSameVersion(root,revised,Core.Hash(revised));
            var interrupted=Core.Read<RepairIntent>(Path.Combine(root,"repair.json"));
            Directory.Move(Path.Combine(root,"versions",one.version),Path.Combine(root,"staging","simulated-interrupted-new"));
            Check(!Core.RecoverRepair(root)&&Core.VerifyDirectory(Path.Combine(root,"versions",one.version)).files["App.exe"]==one.files["App.exe"],"missing current directory recovered from exact retained original");
            Core.RepairSameVersion(root,revised,Core.Hash(revised));Core.CompleteRepair(root,one.version);
            Check(!File.Exists(Path.Combine(root,"repair.json"))&&Directory.GetFiles(Path.Combine(root,"repairs"),"receipt.json",SearchOption.AllDirectories).Length>=3,"completed repair has receipts and no pending intent");
            Reject(()=>Core.RepairSameVersion(root,Make(root,"0.1.0-alpha.2"),Core.Hash(first)),"repair rejects wrong package hash");
            foreach(string extra in new[]{"../escaped.txt","APP.exe","unexpected.py"}){
                string bad=Make(root,"0.1.0-alpha.2",extra);Reject(()=>Core.Stage(bad,Core.Hash(bad),root),"reject malformed zip "+extra);
                Check(Core.Current(root).current==one.version,"failed stage leaves active");
            }
            string corrupt=Make(root,"0.1.0-alpha.2",null,true);Reject(()=>Core.Stage(corrupt,Core.Hash(corrupt),root),"file hash mismatch");
            string second=Make(root,"0.1.0-alpha.2");var two=Core.Stage(second,Core.Hash(second),root);Core.Activate(root,two);
            Check(Core.Current(root).previous==one.version,"previous pointer retained");
            Check(Core.Rollback(root,two.version),"rollback available");
            Check(Core.Current(root).current==one.version,"rollback selects previous");
            Check(File.ReadAllText(stand)=="KEEP LOGICAL STAND","ordinary upgrade and rollback preserve stand definitions");
            Check(File.ReadAllText(standRuntime)=="KEEP STAND JOURNAL","upgrade, same-version repair and rollback preserve stand runtime journals");
            Check(File.ReadAllText(Path.Combine(data,"sentinel.txt"))=="KEEP USER DATA","data preserved");
            Check(File.ReadAllText(snapshotCatalog)=="KEEP SNAPSHOT CATALOG"&&File.ReadAllText(snapshotJournal)=="KEEP PENDING SNAPSHOT JOURNAL","snapshot metadata survives version activation and rollback");
            Check(Directory.Exists(Path.Combine(root,"versions",two.version)),"old/new code retained");
            Check(!Core.Rollback(root,two.version),"stale rollback cannot override current");
            Reject(()=>Core.Activate(root,one),"no silent downgrade/reinstall");
            using(Core.Lock(root,"running.lock"))Reject(()=>{using(Core.Lock(root,"running.lock")){}},"exclusive running lock");
            string injected=Path.Combine(root,"versions",one.version,"unexpected.py");File.WriteAllText(injected,"not allowed");
            Reject(()=>Core.VerifyDirectory(Path.GetDirectoryName(injected)),"unexpected installed code rejected");File.Delete(injected);
            File.WriteAllText(Path.Combine(root,"versions",one.version,"App.exe"),"tampered");
            Reject(()=>Core.VerifyDirectory(Path.Combine(root,"versions",one.version)),"tampered executable rejected");
            File.WriteAllText(Path.Combine(args[0],"update-results.json"),"{\"passed\":"+count+"}");
        }catch(Exception error){File.WriteAllText(Path.Combine(args[0],"update-error.txt"),error.ToString());Environment.ExitCode=1;}
        // Fixtures remain under ignored build/test-results for inspection.
    }
}
