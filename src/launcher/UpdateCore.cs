using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace QemuM.Update {
    // Protocol 1 is independent of the frontend language and of Python.
    public sealed class Package {
        public int schema {get;set;}
        public int min_launcher_protocol {get;set;}
        public string version {get;set;}
        public int build {get;set;}
        public string channel {get;set;}
        public string entrypoint {get;set;}
        public bool requires_python {get;set;}
        public Dictionary<string,string> files {get;set;}
    }
    public sealed class Feed {
        public int schema {get;set;}
        public int min_launcher_protocol {get;set;}
        public string version {get;set;}
        public int build {get;set;}
        public string channel {get;set;}
        public string url {get;set;}
        public string sha256 {get;set;}
    }
    public sealed class Active {
        public int schema {get;set;}
        public string current {get;set;}
        public string previous {get;set;}
    }
    public sealed class RepairIntent {
        public int schema {get;set;}
        public string id {get;set;}
        public string version {get;set;}
        public string old_manifest {get;set;}
        public string new_manifest {get;set;}
    }
    public sealed class RuntimeSettings {
        public string python {get;set;}
        public string qemu_dir {get;set;}
    }
    public static class Core {
        public const int Protocol=1;
        public const string Repository="https://github.com/ivan22822/QemuM";
        public const string Api="https://api.github.com/repos/ivan22822/QemuM/releases?per_page=100";
        public const string PatchChannel="https://raw.githubusercontent.com/ivan22822/QemuM/codex/alpha-update-channel/alpha.json";
        static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=2*1024*1024};}
        public static T Read<T>(string path){
            if(new FileInfo(path).Length>2*1024*1024)throw new IOException("Слишком большой manifest.");
            return Json().Deserialize<T>(File.ReadAllText(path,Encoding.UTF8));
        }
        public static void AtomicJson(string path,object value){
            NoLinks(path);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{
                byte[] bytes=Encoding.UTF8.GetBytes(Json().Serialize(value));
                using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
        public static int[] Version(string value){
            var match=Regex.Match(value??"",@"^(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})-alpha\.(0|[1-9]\d{0,5})$");
            if(!match.Success)throw new IOException("Неподдерживаемая версия alpha.");
            return Enumerable.Range(1,4).Select(i=>Int32.Parse(match.Groups[i].Value)).ToArray();
        }
        public static int Compare(string left,string right){
            int[] a=Version(left),b=Version(right);
            for(int i=0;i<a.Length;i++){int result=a[i].CompareTo(b[i]);if(result!=0)return result;}return 0;
        }
        public static int CompareBuild(string left,int leftBuild,string right,int rightBuild){
            int version=Compare(left,right);return version==0?leftBuild.CompareTo(rightBuild):version;
        }
        public static string Display(string version,int build){return version+" · сборка "+build;}
        public static void ValidateFeed(Feed feed){
            if(feed==null||feed.schema!=1||feed.channel!="alpha"||feed.build<0||feed.min_launcher_protocol<1||
               !Regex.IsMatch(feed.sha256??"",@"^[a-f0-9]{64}$"))throw new IOException("Некорректный manifest обновления.");
            Version(feed.version);Uri uri;
            if(!Uri.TryCreate(feed.url,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Host!="github.com"||uri.Port!=443||
               !String.IsNullOrEmpty(uri.UserInfo)||!String.IsNullOrEmpty(uri.Query)||!String.IsNullOrEmpty(uri.Fragment)||
               !Regex.IsMatch(uri.AbsolutePath,"^/ivan22822/QemuM/releases/download/v"+Regex.Escape(feed.version)+@"/[a-zA-Z0-9_.-]+\.zip$"))
                throw new IOException("Источник пакета обновления не разрешён.");
        }
        public static void MatchFeed(Package package,Feed feed){
            ValidateFeed(feed);
            if(package.version!=feed.version||package.build!=feed.build||package.min_launcher_protocol!=feed.min_launcher_protocol)
                throw new IOException("Сборка пакета не соответствует обновлению.");
        }
        public static string Relative(string path){
            if(String.IsNullOrEmpty(path)||path.Length>180||path.Contains("\\")||path.StartsWith("/")||path.EndsWith("/"))throw new IOException("Недопустимый путь пакета.");
            foreach(string part in path.Split('/')){
                if(!Regex.IsMatch(part,@"^[a-zA-Z0-9_.-]+$")||part=="."||part==".."||part.EndsWith(".")||
                   Regex.IsMatch(part,@"^(con|prn|aux|nul|com[0-9]|lpt[0-9])(?:\.|$)",RegexOptions.IgnoreCase))throw new IOException("Недопустимое имя файла пакета.");
            }
            return path.Replace('/',Path.DirectorySeparatorChar);
        }
        public static void NoLinks(string path){
            for(string current=Path.GetFullPath(path);!String.IsNullOrEmpty(current);current=Path.GetDirectoryName(current)){
                if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("Установка через ссылки и перенаправленные каталоги запрещена.");
            }
        }
        public static string Hash(Stream stream){
            using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
        }
        public static string Hash(string path){using(var stream=File.OpenRead(path))return Hash(stream);}
        public static void CheckHash(string actual,string expected){
            if(!Regex.IsMatch(expected??"",@"^[a-f0-9]{64}$")||actual!=expected)throw new IOException("Контрольная сумма не совпадает.");
        }
        public static void CopyBounded(Stream source,Stream target,long expected){
            byte[] buffer=new byte[65536];long total=0;int count;
            while((count=source.Read(buffer,0,buffer.Length))>0){
                total+=count;if(total>expected)throw new IOException("Распакованный файл превышает заявленный размер.");
                target.Write(buffer,0,count);
            }
            if(total!=expected)throw new IOException("Распакованный файл имеет неверный размер.");
        }
        public static void Validate(Package package){
            if(package==null||package.schema!=1||package.channel!="alpha"||package.min_launcher_protocol<1||package.min_launcher_protocol>Protocol)
                throw new IOException("Пакет требует другую версию установщика.");
            Version(package.version);
            if(package.build<0)throw new IOException("Некорректный номер сборки.");
            if(package.files==null||package.files.Count==0||package.files.Count>2000)throw new IOException("Некорректный список файлов.");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var file in package.files){
                Relative(file.Key);
                if(!names.Add(file.Key)||file.Key.Equals("package.json",StringComparison.OrdinalIgnoreCase)||
                   !Regex.IsMatch(file.Value??"",@"^[a-f0-9]{64}$"))throw new IOException("Некорректные файлы manifest.");
            }
            Relative(package.entrypoint);
            if(!package.entrypoint.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)||!package.files.ContainsKey(package.entrypoint))
                throw new IOException("В пакете отсутствует программа запуска.");
        }
        public static Package VerifyDirectory(string directory){
            NoLinks(directory);var package=Read<Package>(Path.Combine(directory,"package.json"));Validate(package);
            var actual=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Inventory(directory,directory,actual);
            if(!actual.SetEquals(package.files.Keys.Concat(new[]{"package.json"})))throw new IOException("Установленная версия содержит неожиданные файлы.");
            foreach(var pair in package.files){
                string path=Path.Combine(directory,Relative(pair.Key));NoLinks(path);CheckHash(Hash(path),pair.Value);
            }
            return package;
        }
        static void Inventory(string root,string directory,HashSet<string> files){
            NoLinks(directory);
            foreach(string path in Directory.EnumerateFiles(directory)){NoLinks(path);files.Add(path.Substring(root.Length+1).Replace('\\','/'));}
            foreach(string path in Directory.EnumerateDirectories(directory))Inventory(root,path,files);
        }
        public static Package Stage(string zip,string expectedHash,string root,Action<int,int> progress=null,string preparedId=null){
            Guid preparedGuid;if(preparedId!=null&&!Guid.TryParseExact(preparedId,"N",out preparedGuid))throw new IOException("Некорректный ID подготовки.");
            NoLinks(root);CheckHash(Hash(zip),expectedHash);
            string stage=Path.Combine(root,"staging",Guid.NewGuid().ToString("N"));
            NoLinks(stage);Directory.CreateDirectory(stage);
            // A failed staging folder is retained for diagnosis; never recursively delete user paths.
            using(var archive=ZipFile.OpenRead(zip)){
                if(archive.Entries.Count>2001)throw new IOException("Слишком много файлов в пакете.");
                var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
                foreach(var entry in archive.Entries){
                    Relative(entry.FullName);
                    if(!seen.Add(entry.FullName)||entry.Length>64*1024*1024||(total+=entry.Length)>128*1024*1024)
                        throw new IOException("Некорректный размер или дубликат файла в пакете.");
                    int kind=(entry.ExternalAttributes>>16)&0xF000;
                    if(kind!=0&&kind!=0x8000)throw new IOException("Пакет содержит ссылку или каталог.");
                }
                var manifest=archive.GetEntry("package.json");
                if(manifest==null||manifest.Length>2*1024*1024)throw new IOException("Нет manifest пакета.");
                Package package;
                using(var source=manifest.Open())using(var buffer=new MemoryStream()){
                    CopyBounded(source,buffer,manifest.Length);package=Json().Deserialize<Package>(Encoding.UTF8.GetString(buffer.ToArray()));
                }
                Validate(package);
                if(seen.Count!=package.files.Count+1||!seen.SetEquals(package.files.Keys.Concat(new[]{"package.json"})))
                    throw new IOException("Состав ZIP не соответствует manifest.");
                int completed=0;
                foreach(var entry in archive.Entries){
                    string path=Path.Combine(stage,Relative(entry.FullName));Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using(var source=entry.Open())using(var target=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))CopyBounded(source,target,entry.Length);
                    if(progress!=null)progress(++completed,archive.Entries.Count);
                }
                VerifyDirectory(stage);
                string destination=preparedId==null?Path.Combine(root,"versions",package.version):Path.Combine(root,"staging","repair-"+preparedId);NoLinks(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                if(Directory.Exists(destination))throw new IOException("Эта версия уже есть. Существующие файлы сохранены.");
                Directory.Move(stage,destination);
                return package;
            }
        }
        public static Active Current(string root){
            string path=Path.Combine(root,"active.json");NoLinks(path);
            if(!File.Exists(path))return null;
            Active state=Read<Active>(path);
            if(state==null||state.schema!=1)throw new IOException("Повреждён указатель установленной версии.");
            Version(state.current);if(state.previous!=null)Version(state.previous);return state;
        }
        static RepairIntent RepairState(string root){
            string path=Path.Combine(root,"repair.json");NoLinks(path);if(!File.Exists(path))return null;
            var state=Read<RepairIntent>(path);Guid id;
            if(state==null||state.schema!=1||!Guid.TryParseExact(state.id,"N",out id)||!Regex.IsMatch(state.old_manifest??"","^[0-9a-f]{64}$")||!Regex.IsMatch(state.new_manifest??"","^[0-9a-f]{64}$"))throw new IOException("Повреждён журнал переустановки; файлы сохранены.");
            Version(state.version);var current=Current(root);if(current==null||current.current!=state.version)throw new IOException("Версия изменилась во время переустановки.");return state;
        }
        static string RepairBackup(string root,RepairIntent state){return Path.Combine(root,"repairs",state.id,"previous");}
        static void VerifyManifest(string directory,string expected){VerifyDirectory(directory);CheckHash(Hash(Path.Combine(directory,"package.json")),expected);}
        static void FinishRepair(string root,RepairIntent state){
            string folder=Path.Combine(root,"repairs",state.id);NoLinks(folder);Directory.CreateDirectory(folder);
            AtomicJson(Path.Combine(folder,"receipt.json"),state);File.Delete(Path.Combine(root,"repair.json"));
        }
        // Caller holds both running.lock and update.lock, including during recovery.
        public static bool RecoverRepair(string root){
            var state=RepairState(root);if(state==null)return false;
            string target=Path.Combine(root,"versions",state.version),backup=RepairBackup(root,state);NoLinks(target);NoLinks(backup);
            if(Directory.Exists(target)){
                string hash=Hash(Path.Combine(target,"package.json"));
                if(hash==state.new_manifest){VerifyManifest(target,state.new_manifest);VerifyManifest(backup,state.old_manifest);return true;}
                if(hash!=state.old_manifest)throw new IOException("Неизвестные файлы на пути переустановки. Нужна ручная проверка.");
                VerifyManifest(target,state.old_manifest);FinishRepair(root,state);return false;
            }
            VerifyManifest(backup,state.old_manifest);Directory.Move(backup,target);FinishRepair(root,state);return false;
        }
        public static bool RepairSameVersion(string root,string zip,string expectedHash,Action<int,int> progress=null,Feed expectedFeed=null){
            RecoverRepair(root);
            if(File.Exists(Path.Combine(root,"repair.json")))throw new IOException("Сначала запусти и проверь уже переустановленную версию.");
            var active=Current(root);if(active==null)throw new IOException("Установка не найдена.");
            string id=Guid.NewGuid().ToString("N"),preparedRoot=Path.Combine(root,"staging","repair-"+id);
            var package=Stage(zip,expectedHash,root,progress,id);
            if(expectedFeed!=null)MatchFeed(package,expectedFeed);
            if(package.version!=active.current)throw new IOException("Для переустановки требуется тот же номер версии.");
            string target=Path.Combine(root,"versions",active.current);VerifyDirectory(target);
            var state=new RepairIntent{schema=1,id=id,version=active.current,old_manifest=Hash(Path.Combine(target,"package.json")),new_manifest=Hash(Path.Combine(preparedRoot,"package.json"))};
            if(state.old_manifest==state.new_manifest)return false;
            string backup=RepairBackup(root,state);NoLinks(backup);Directory.CreateDirectory(Path.GetDirectoryName(backup));
            AtomicJson(Path.Combine(root,"repair.json"),state);
            try{Directory.Move(target,backup);Directory.Move(preparedRoot,target);VerifyManifest(target,state.new_manifest);}
            catch{RecoverRepair(root);throw;}
            return true;
        }
        public static void RefreshBootstrap(string root,string source){
            string target=Path.Combine(root,"QemuM.exe");NoLinks(source);NoLinks(target);
            if(String.Equals(Path.GetFullPath(source),Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase))return;
            string expected=Hash(source);if(File.Exists(target)&&Hash(target)==expected)return;
            string backups=Path.Combine(root,"launcher-backups");NoLinks(backups);Directory.CreateDirectory(backups);
            string temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";NoLinks(temporary);
            try{
                using(var input=File.OpenRead(source))using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){CopyBounded(input,output,input.Length);output.Flush(true);}
                CheckHash(Hash(temporary),expected);
                if(File.Exists(target))File.Replace(temporary,target,Path.Combine(backups,Guid.NewGuid().ToString("N")+".exe"));else File.Move(temporary,target);
            }finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        public static void CompleteRepair(string root,string version){var state=RepairState(root);if(state!=null&&state.version==version){VerifyManifest(Path.Combine(root,"versions",version),state.new_manifest);FinishRepair(root,state);}}
        public static void Activate(string root,Package package){
            if(File.Exists(Path.Combine(root,"repair.json")))throw new IOException("Сначала заверши проверку переустановки текущей версии.");
            Validate(package);var previous=Current(root);
            if(previous!=null&&Compare(package.version,previous.current)<=0)throw new IOException("Обычное обновление не понижает версию.");
            VerifyDirectory(Path.Combine(root,"versions",package.version));
            AtomicJson(Path.Combine(root,"active.json"),new Active{schema=1,current=package.version,previous=previous==null?null:previous.current});
        }
        public static bool Rollback(string root,string failed){
            var repair=RepairState(root);
            if(repair!=null&&repair.version==failed){
                string target=Path.Combine(root,"versions",failed),backup=RepairBackup(root,repair),rejected=Path.Combine(root,"repairs",repair.id,"rejected");
                NoLinks(rejected);VerifyManifest(target,repair.new_manifest);VerifyManifest(backup,repair.old_manifest);
                Directory.Move(target,rejected);Directory.Move(backup,target);FinishRepair(root,repair);return true;
            }
            var state=Current(root);
            if(state==null||state.current!=failed||state.previous==null)return false;
            VerifyDirectory(Path.Combine(root,"versions",state.previous));
            AtomicJson(Path.Combine(root,"active.json"),new Active{schema=1,current=state.previous,previous=null});return true;
        }
        public static FileStream Lock(string root,string name){
            NoLinks(Path.Combine(root,name));Directory.CreateDirectory(root);
            return new FileStream(Path.Combine(root,name),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        }
        static bool Allowed(Uri uri,bool dependencies){
            if(uri.Scheme!="https"||uri.Port!=443||!String.IsNullOrEmpty(uri.UserInfo))return false;
            if(uri.Host=="api.github.com")return uri.AbsolutePath.StartsWith("/repos/ivan22822/QemuM/",StringComparison.OrdinalIgnoreCase);
            if(uri.Host=="raw.githubusercontent.com")return uri.AbsoluteUri==PatchChannel;
            if(uri.Host=="github.com")return uri.AbsolutePath.StartsWith("/ivan22822/QemuM/releases/download/",StringComparison.OrdinalIgnoreCase);
            if(uri.Host=="release-assets.githubusercontent.com"||uri.Host=="objects.githubusercontent.com")return true;
            return dependencies&&(uri.Host=="www.python.org"||uri.Host=="qemu.weilnetz.de");
        }
        public static void Download(string url,string path,long limit,bool dependencies=false,Action<long,long?> progress=null){
            Uri uri=new Uri(url);
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            for(int redirect=0;redirect<6;redirect++){
                if(!Allowed(uri,dependencies))throw new IOException("Источник загрузки не разрешён.");
                var request=(HttpWebRequest)WebRequest.Create(uri);request.UserAgent="QemuM-Launcher/1";request.Accept="application/vnd.github+json";
                request.Timeout=20000;request.ReadWriteTimeout=30000;request.AllowAutoRedirect=false;
                using(var response=(HttpWebResponse)request.GetResponse()){
                    if((int)response.StatusCode>=300&&(int)response.StatusCode<400){uri=new Uri(uri,response.Headers["Location"]);continue;}
                    if(response.StatusCode!=HttpStatusCode.OK||response.ContentLength>limit)throw new IOException("Невозможно загрузить пакет.");
                    using(var source=response.GetResponseStream())using(var target=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                        byte[] buffer=new byte[65536];long total=0;int count;
                        var clock=System.Diagnostics.Stopwatch.StartNew();
                        while((count=source.Read(buffer,0,buffer.Length))>0){total+=count;if(total>limit)throw new IOException("Файл превышает лимит.");target.Write(buffer,0,count);
                            if(progress!=null&&clock.ElapsedMilliseconds>=100){progress(total,response.ContentLength>0?(long?)response.ContentLength:null);clock.Restart();}
                        }
                        if(progress!=null)progress(total,response.ContentLength>0?(long?)response.ContentLength:null);
                        target.Flush(true);
                    }
                    return;
                }
            }
            throw new IOException("Слишком много перенаправлений.");
        }
        public static Feed Latest(string work,Action<string,string,long> fetch=null){
            if(fetch==null)fetch=(url,path,limit)=>Download(url,path,limit);
            // Only a missing channel falls back. Malformed metadata or a network error fails closed.
            string channel=Path.Combine(work,Guid.NewGuid().ToString("N")+".json");
            Feed latest=null;
            try{fetch(PatchChannel,channel,65536);latest=Read<Feed>(channel);ValidateFeed(latest);}
            catch(WebException error){var response=error.Response as HttpWebResponse;if(response==null||response.StatusCode!=HttpStatusCode.NotFound)throw;}
            string releases=Path.Combine(work,Guid.NewGuid().ToString("N")+".json");fetch(Api,releases,2*1024*1024);
            var rows=Json().DeserializeObject(File.ReadAllText(releases)) as object[];
            foreach(object raw in rows??new object[0]){
                var row=raw as Dictionary<string,object>;if(row==null||Convert.ToBoolean(row["draft"]))continue;
                string tag=Convert.ToString(row["tag_name"]);
                if(!Regex.IsMatch(tag??"",@"^v\d+\.\d+\.\d+-alpha\.\d+$"))continue;
                foreach(var asset in (row["assets"] as object[]??new object[0]).OfType<Dictionary<string,object>>()){
                    if(Convert.ToString(asset["name"])!="update.json")continue;
                    string manifest=Path.Combine(work,Guid.NewGuid().ToString("N")+".json");
                    fetch(Convert.ToString(asset["browser_download_url"]),manifest,65536);
                    var feed=Read<Feed>(manifest);
                    ValidateFeed(feed);
                    if(tag!="v"+feed.version)throw new IOException("Некорректный manifest обновления.");
                    if(latest==null||CompareBuild(feed.version,feed.build,latest.version,latest.build)>0)latest=feed;
                }
            }
            return latest;
        }
    }
}
