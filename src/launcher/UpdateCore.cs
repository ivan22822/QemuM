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
        public string channel {get;set;}
        public string entrypoint {get;set;}
        public bool requires_python {get;set;}
        public Dictionary<string,string> files {get;set;}
    }
    public sealed class Feed {
        public int schema {get;set;}
        public int min_launcher_protocol {get;set;}
        public string version {get;set;}
        public string channel {get;set;}
        public string url {get;set;}
        public string sha256 {get;set;}
    }
    public sealed class Active {
        public int schema {get;set;}
        public string current {get;set;}
        public string previous {get;set;}
    }
    public sealed class RuntimeSettings {
        public string python {get;set;}
        public string qemu_dir {get;set;}
    }
    public static class Core {
        public const int Protocol=1;
        public const string Repository="https://github.com/ivan22822/QemuM";
        public const string Api="https://api.github.com/repos/ivan22822/QemuM/releases?per_page=100";
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
        public static Package Stage(string zip,string expectedHash,string root,Action<int,int> progress=null){
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
                string destination=Path.Combine(root,"versions",package.version);NoLinks(destination);
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
        public static void Activate(string root,Package package){
            Validate(package);var previous=Current(root);
            if(previous!=null&&Compare(package.version,previous.current)<=0)throw new IOException("Обычное обновление не понижает версию.");
            VerifyDirectory(Path.Combine(root,"versions",package.version));
            AtomicJson(Path.Combine(root,"active.json"),new Active{schema=1,current=package.version,previous=previous==null?null:previous.current});
        }
        public static bool Rollback(string root,string failed){
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
        public static Feed Latest(string work){
            string releases=Path.Combine(work,"releases.json");Download(Api,releases,2*1024*1024);
            var rows=Json().DeserializeObject(File.ReadAllText(releases)) as object[];
            Feed latest=null;
            foreach(object raw in rows??new object[0]){
                var row=raw as Dictionary<string,object>;if(row==null||Convert.ToBoolean(row["draft"]))continue;
                string tag=Convert.ToString(row["tag_name"]);
                if(!Regex.IsMatch(tag??"",@"^v\d+\.\d+\.\d+-alpha\.\d+$"))continue;
                foreach(var asset in (row["assets"] as object[]??new object[0]).OfType<Dictionary<string,object>>()){
                    if(Convert.ToString(asset["name"])!="update.json")continue;
                    string manifest=Path.Combine(work,Guid.NewGuid().ToString("N")+".json");
                    Download(Convert.ToString(asset["browser_download_url"]),manifest,65536);
                    var feed=Read<Feed>(manifest);
                    if(feed==null||feed.schema!=1||feed.channel!="alpha"||tag!="v"+feed.version)throw new IOException("Некорректный manifest обновления.");
                    Version(feed.version);
                    if(latest==null||Compare(feed.version,latest.version)>0)latest=feed;
                }
            }
            return latest;
        }
    }
}
