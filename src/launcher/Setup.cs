using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using QemuM.Update;

static class SetupCore {
    public const string PythonUrl="https://www.python.org/ftp/python/3.14.8/python-3.14.8-embed-amd64.zip";
    public const string PythonHash="a93abe456ab01bd96d7a085b3cdb6566b3063f4241360d114142fbdb07f0a310";
    public const string QemuUrl="https://qemu.weilnetz.de/w64/2026/qemu-w64-setup-20260811.exe";
    public const string QemuHash="5bcf9eed634e8575a37b74f445af41a2fe4106da512d0c30c368301d4c105037fdfab40a5287367a28a957624cddebbc8c07e16c88ab6634f554cdf3d16bf543";
    public static string FindQemu(){
        var paths=(Environment.GetEnvironmentVariable("PATH")??"").Split(';').ToList();
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"qemu"));
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"QemuM-QEMU-20260811"));
        foreach(string key in new[]{"SOFTWARE\\QEMU","SOFTWARE\\qemu64"})
            using(var registry=Registry.LocalMachine.OpenSubKey(key))if(registry!=null)paths.Add(Convert.ToString(registry.GetValue("Install_Dir","")));
        return paths.Select(p=>p.Trim().Trim('"')).FirstOrDefault(p=>p.Length>0&&File.Exists(Path.Combine(p,"qemu-system-x86_64.exe"))&&File.Exists(Path.Combine(p,"qemu-img.exe")));
    }
    public static void ExtractPython(string zip,string target,Action<int,int> progress=null){
        Core.CheckHash(Core.Hash(zip),PythonHash);Core.NoLinks(target);
        if(Directory.Exists(target))throw new IOException("Каталог Python уже существует.");
        Directory.CreateDirectory(target);
        using(var archive=ZipFile.OpenRead(zip)){
            int completed=0;
            foreach(var entry in archive.Entries){
                string path=Path.Combine(target,Core.Relative(entry.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using(var input=entry.Open())using(var output=new FileStream(path,FileMode.CreateNew))input.CopyTo(output);
                if(progress!=null)progress(++completed,archive.Entries.Count);
            }
        }
        // Backend explicitly adds its own immutable version directory to sys.path.
        File.WriteAllText(Path.Combine(target,"python314._pth"),"python314.zip\r\n.\r\n",Encoding.ASCII);
    }
    static void Resource(string name,string target){
        using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream(name)){
            if(source==null)throw new IOException("Нет встроенного ресурса: "+name);
            using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))source.CopyTo(output);
        }
    }
    static string Probe(string executable,string arguments){
        using(var process=Process.Start(new ProcessStartInfo(executable,arguments){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true
        })){
            var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(20000)){process.Kill();process.WaitForExit();throw new IOException("QEMU не ответил на проверку.");}
            Task.WaitAll(output,errors);
            if(process.ExitCode!=0)throw new IOException("QEMU не прошёл проверку: "+errors.Result);
            return output.Result;
        }
    }
    public static void Install(string root,Action<InstallProgress> report){
        root=Path.GetFullPath(root);Core.NoLinks(root);
        if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new IOException("Выбери новую пустую папку. Существующая установка сохранена.");
        Directory.CreateDirectory(root);
        using(Core.Lock(root,"update.lock")){
            string downloads=Path.Combine(root,"downloads");Directory.CreateDirectory(downloads);
            report(new InstallProgress("Python","Загрузка Python 3.14.8"));
            string pythonZip=Path.Combine(downloads,"python.zip");Core.Download(PythonUrl,pythonZip,32*1024*1024,true,(done,total)=>report(new InstallProgress("Python","Загрузка",done,total)));
            report(new InstallProgress("Python","Проверка SHA-256"));
            ExtractPython(pythonZip,Path.Combine(root,"runtime","python"),(done,total)=>report(new InstallProgress("Python","Распаковка файлов",done,total)));
            report(new InstallProgress("Python","Установлен",1,1));
            string qemu=FindQemu();
            if(qemu==null){
                report(new InstallProgress("QEMU","Загрузка"));
                string setup=Path.Combine(downloads,"qemu-setup.exe");Core.Download(QemuUrl,setup,512L*1024*1024,true,(done,total)=>report(new InstallProgress("QEMU","Загрузка",done,total)));
                report(new InstallProgress("QEMU","Проверка SHA-512"));
                string actual;using(var source=File.OpenRead(setup))using(var hash=SHA512.Create())actual=BitConverter.ToString(hash.ComputeHash(source)).Replace("-","").ToLowerInvariant();
                if(actual!=QemuHash)throw new IOException("Контрольная сумма QEMU не совпадает.");
                qemu=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"QemuM-QEMU-20260811");
                if(Directory.Exists(qemu))throw new IOException("Каталог QEMU уже существует, но установка неполна. Выбери исправную установку QEMU через PATH.");
                report(new InstallProgress("QEMU","Установка · подтверди запрос Windows"));
                using(var process=Process.Start(new ProcessStartInfo(setup,"/S /D="+qemu){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden})){
                    process.WaitForExit();if(process.ExitCode!=0)throw new IOException("Установка QEMU завершилась с кодом "+process.ExitCode);
                }
            }
            report(new InstallProgress("QEMU","Проверка установленной сборки"));
            if(!File.Exists(Path.Combine(qemu,"qemu-system-x86_64.exe"))||!File.Exists(Path.Combine(qemu,"qemu-img.exe")))throw new IOException("Не найдены QEMU и qemu-img.");
            Probe(Path.Combine(qemu,"qemu-system-x86_64.exe"),"--version");
            Probe(Path.Combine(qemu,"qemu-img.exe"),"--version");
            if(!Probe(Path.Combine(qemu,"qemu-system-x86_64.exe"),"-display help").Split('\n').Any(line=>line.Trim()=="sdl"))
                throw new IOException("В найденной сборке QEMU отсутствует SDL. Установи сборку QEMU с SDL.");
            report(new InstallProgress("QEMU","Готов",1,1));
            report(new InstallProgress("QemuM","Проверка пакета"));
            string payload=Path.Combine(downloads,"app.zip");Resource("app.zip",payload);
            string expected;using(var reader=new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("app.sha256")))expected=reader.ReadToEnd().Trim();
            var package=Core.Stage(payload,expected,root,(done,total)=>report(new InstallProgress("QemuM","Распаковка файлов",done,total)));
            Resource("QemuM.exe",Path.Combine(root,"QemuM.exe"));
            Core.AtomicJson(Path.Combine(root,"runtime.json"),new RuntimeSettings{python="runtime\\python\\python.exe",qemu_dir=qemu});
            Directory.CreateDirectory(Path.Combine(root,"data"));Core.Activate(root,package);
            report(new InstallProgress("QemuM","Установлен",1,1));
        }
    }
    public static void Shortcut(string root){
        string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"QemuM Alpha");Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"QemuM Alpha.lnk");
        if(File.Exists(path))return;
        object shell=null,shortcut=null;
        try{
            Type type=Type.GetTypeFromProgID("WScript.Shell");shell=Activator.CreateInstance(type);
            shortcut=type.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{path});
            Type link=shortcut.GetType();
            link.InvokeMember("TargetPath",BindingFlags.SetProperty,null,shortcut,new object[]{Path.Combine(root,"QemuM.exe")});
            link.InvokeMember("WorkingDirectory",BindingFlags.SetProperty,null,shortcut,new object[]{root});
            link.InvokeMember("Description",BindingFlags.SetProperty,null,shortcut,new object[]{"QemuM Alpha"});
            link.InvokeMember("Save",BindingFlags.InvokeMethod,null,shortcut,null);
        }finally{if(shortcut!=null)Marshal.FinalReleaseComObject(shortcut);if(shell!=null)Marshal.FinalReleaseComObject(shell);}
    }
}

sealed class SetupWindow : Form {
    readonly BootstrapPathField path=new BootstrapPathField{Dock=DockStyle.Top};
    readonly Label status=new Label{Dock=DockStyle.Fill,Padding=new Padding(0,20,0,0)};
    readonly Button install=BootstrapUI.Button("Установить",true);
    readonly Button open=BootstrapUI.Button("Открыть QemuM");
    readonly System.Collections.Generic.Dictionary<string,ComponentProgress> components=new System.Collections.Generic.Dictionary<string,ComponentProgress>();
    bool busy;string installedRoot;
    public SetupWindow(){
        BootstrapUI.Style(this,"Установка QemuM · Alpha",650);open.Enabled=false;
        path.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QemuM Alpha");
        path.BackColor=Color.FromArgb(11,27,54);path.ForeColor=Color.White;
        var body=new Panel{Dock=DockStyle.Fill,Padding=new Padding(28,24,28,0)};
        var intro=BootstrapUI.Header("Установка Alpha");
        var cards=new TableLayoutPanel{Dock=DockStyle.Top,Height=324,ColumnCount=1,RowCount=3,Padding=new Padding(0,20,0,0)};
        foreach(string name in new[]{"Python","QEMU","QemuM"}){var card=new ComponentProgress(name){Dock=DockStyle.Fill};components.Add(name,card);cards.Controls.Add(card);}
        status.ForeColor=BootstrapUI.Muted;status.Padding=new Padding(0,14,0,0);
        status.Text="Нужен интернет. При отсутствии QEMU Windows запросит права для его установки. Выбери новую пустую папку.";
        body.Controls.Add(status);body.Controls.Add(cards);body.Controls.Add(path);body.Controls.Add(intro);
        Controls.Add(body);Controls.Add(BootstrapUI.Buttons(install,open));
        install.Click+=(sender,args)=>Install();
        open.Click+=(sender,args)=>{Process.Start(new ProcessStartInfo(Path.Combine(installedRoot,"QemuM.exe")){UseShellExecute=false,CreateNoWindow=true});Close();};
        FormClosing+=(sender,args)=>{if(busy)args.Cancel=true;};
    }
    void Report(InstallProgress item){BeginInvoke(new Action(()=>components[item.Component].Report(item.Phase,item.Done,item.Total)));}
    async void Install(){
        if(busy)return;busy=true;install.Enabled=false;path.Enabled=false;
        string root=path.Text.Trim();
        try{
            await Task.Run(()=>SetupCore.Install(root,Report));installedRoot=Path.GetFullPath(root);
            string shortcut="";
            try{SetupCore.Shortcut(installedRoot);}catch(Exception error){shortcut="\nЯрлык не создан: "+error.Message+"\nЗапуск: "+Path.Combine(installedRoot,"QemuM.exe");}
            status.Text="Готово. QemuM доступен в меню «Пуск».\n\nДобавь свой ISO или диск. Если WHPX недоступен, выбери профиль «Авто» или TCG в параметрах VM."+shortcut;
            open.Enabled=true;
        }catch(Exception error){status.Text="Установка не завершена: "+error.Message+"\nИсправь причину и укажи новую пустую папку. Существующие файлы сохранены.";install.Enabled=true;path.Enabled=true;}
        finally{busy=false;}
    }
}

static class Setup {
    [STAThread] static void Main(){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new SetupWindow());}
}
