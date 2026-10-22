using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using QemuM.Update;

static class Launcher {
    [STAThread] static void Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string root=AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        try{
            if(args.Length==0&&String.Equals(Path.GetFileName(Application.ExecutablePath),"QemuM-Refresh.exe",StringComparison.OrdinalIgnoreCase)){
                root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QemuM Alpha");Core.NoLinks(root);
                if(!File.Exists(Path.Combine(root,"active.json")))throw new IOException("Стандартная установка QemuM не найдена. Для другой папки используй --check-updates --install-root <папка>.");
                Application.Run(new UpdateWindow(root));return;
            }
            if(args.Length==3&&args[0]=="--check-updates"&&args[1]=="--install-root"){
                root=Path.GetFullPath(args[2]);Core.NoLinks(root);
                if(!File.Exists(Path.Combine(root,"active.json")))throw new IOException("Не найдена установка QemuM.");
                Application.Run(new UpdateWindow(root));return;
            }
            if(args.Length==1&&args[0]=="--check-updates"){Application.Run(new UpdateWindow(root));return;}
            if(args.Length!=0)throw new IOException("Неизвестные параметры запуска.");
            Run(root);
        }catch(Exception error){MessageBox.Show(error.Message,"QemuM",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
    static void Run(string root){
        FileStream running;
        try{running=Core.Lock(root,"running.lock");}
        catch(IOException){MessageBox.Show("QemuM уже открыт. Переключись на его окно.","QemuM");return;}
        using(running){
            Process child;string ready,version;
            using(Core.Lock(root,"update.lock")){
                Core.RecoverRepair(root);
                var state=Core.Current(root);if(state==null)throw new IOException("Установка не завершена. Запусти установщик заново в новой папке.");
                version=state.current;
                string app=Path.Combine(root,"versions",version);var package=Core.VerifyDirectory(app);
                var runtime=Core.Read<RuntimeSettings>(Path.Combine(root,"runtime.json"));
                string data=Path.Combine(root,"data");Core.NoLinks(data);Directory.CreateDirectory(data);
                string health=Path.Combine(root,"health");Core.NoLinks(health);Directory.CreateDirectory(health);
                ready=Path.Combine(health,Guid.NewGuid().ToString("N")+".ready");
                var start=new ProcessStartInfo(Path.Combine(app,Core.Relative(package.entrypoint))){
                    UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=app
                };
                start.EnvironmentVariables["QEMUM_APP_DIR"]=app;
                start.EnvironmentVariables["QEMUM_DATA_DIR"]=data;
                start.EnvironmentVariables["QEMUM_INSTALL_DIR"]=root;
                start.EnvironmentVariables["QEMUM_READY_FILE"]=ready;
                if(package.requires_python){
                    string python=Path.IsPathRooted(runtime.python)?runtime.python:Path.Combine(root,runtime.python);
                    if(!File.Exists(python))throw new IOException("Python установки недоступен.");
                    start.EnvironmentVariables["QEMUM_PYTHON"]=python;
                }
                if(!String.IsNullOrEmpty(runtime.qemu_dir))start.EnvironmentVariables["PATH"]=runtime.qemu_dir+";"+Environment.GetEnvironmentVariable("PATH");
                try{child=Process.Start(start);}
                catch{Core.Rollback(root,version);throw;}
            }
            // Keep the run lock for the child's lifetime. Updates never terminate guests or the GUI.
            using(child){
                bool healthy=false;
                while(!child.WaitForExit(250)){if(File.Exists(ready))healthy=true;}
                healthy=healthy||File.Exists(ready);
                if(File.Exists(ready))File.Delete(ready);
                if(healthy){using(Core.Lock(root,"update.lock"))Core.CompleteRepair(root,version);}
                if(!healthy){
                    bool rolledBack;using(Core.Lock(root,"update.lock"))rolledBack=Core.Rollback(root,version);
                    MessageBox.Show(rolledBack?"Приложение завершилось до готовности. Возвращена предыдущая версия; открой QemuM снова.":
                        "Приложение завершилось до готовности. Предыдущей версии нет. Открой Issues проекта и укажи версию "+version+".","QemuM");
                }
            }
        }
    }
}

sealed class UpdateWindow : Form {
    readonly string root;
    readonly Label status=new Label{Dock=DockStyle.Fill,AutoSize=false,ForeColor=BootstrapUI.Muted};
    readonly Button check=BootstrapUI.Button("Проверить");
    readonly Button install=BootstrapUI.Button("Установить обновление",true);
    readonly ComponentProgress progress=new ComponentProgress("Пакет приложения"){Dock=DockStyle.Bottom,Visible=false};
    Feed feed;string work;bool busy;
    public UpdateWindow(string root,bool autoCheck=true){
        this.root=root;BootstrapUI.Style(this,"Обновления QemuM · Alpha",390);install.Enabled=false;
        var body=new Panel{Dock=DockStyle.Fill,Padding=new Padding(28,24,28,12)};
        var header=BootstrapUI.Header("Обновления приложения");
        body.Controls.Add(status);body.Controls.Add(progress);body.Controls.Add(header);
        Controls.Add(body);Controls.Add(BootstrapUI.Buttons(check,install));
        check.Click+=(sender,args)=>Check();install.Click+=(sender,args)=>Install();
        if(autoCheck)Shown+=(sender,args)=>Check();FormClosing+=(sender,args)=>{if(busy)args.Cancel=true;};
    }
    async void Check(){
        if(busy)return;busy=true;check.Enabled=install.Enabled=false;status.Text="Проверяю обновления GitHub…";
        try{
            var current=Core.Current(root);
            var local=current==null?null:Core.Read<Package>(Path.Combine(root,"versions",current.current,"package.json"));
            work=Path.Combine(root,"downloads",Guid.NewGuid().ToString("N"));Core.NoLinks(work);Directory.CreateDirectory(work);
            feed=await Task.Run(()=>Core.Latest(work));
            if(feed==null)status.Text="Опубликованных обновлений alpha пока нет.";
            else if(local!=null&&Core.CompareBuild(feed.version,feed.build,local.version,local.build)<0)status.Text="Локальная сборка новее опубликованной: "+Core.Display(local.version,local.build);
            else if(feed.min_launcher_protocol>Core.Protocol||feed.min_launcher_protocol<1)
                status.Text="Версия "+feed.version+" требует нового установщика. Открой Releases проекта.";
            else{bool same=local!=null&&Core.CompareBuild(feed.version,feed.build,local.version,local.build)==0;install.Text=same?"Переустановить сборку":"Установить обновление";status.Text=same?"Установлена "+Core.Display(local.version,local.build)+". Можно сверить пакет с GitHub и переустановить сборку.\n\nПрежние файлы программы сохранятся. Данные VM не заменяются. Перед действием закрой менеджер.":"Доступна "+Core.Display(feed.version,feed.build)+".\n\nПеред установкой закрой окно менеджера. Работающие VM продолжат работу. Прежние файлы программы сохранятся.";install.Enabled=true;}
        }catch(Exception error){status.Text="Проверка не завершена: "+error.Message;}
        finally{busy=false;check.Enabled=true;}
    }
    async void Install(){
        if(busy||feed==null)return;
        if(MessageBox.Show(this,"Установить "+Core.Display(feed.version,feed.build)+"? Закрой менеджер перед продолжением.","QemuM",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
        busy=true;check.Enabled=install.Enabled=false;
        try{
            using(Core.Lock(root,"running.lock"))using(Core.Lock(root,"update.lock")){
                Core.RecoverRepair(root);
                status.Text="Скачиваю и проверяю пакет…";progress.Visible=true;
                var updates=new Progress<InstallProgress>(item=>progress.Report(item.Phase,item.Done,item.Total));
                IProgress<InstallProgress> reporter=updates;
                await Task.Run(()=>{
                    string zip=Path.Combine(work,Guid.NewGuid().ToString("N")+".zip");Core.Download(feed.url,zip,128*1024*1024,false,(done,total)=>reporter.Report(new InstallProgress("QemuM","Загрузка",done,total)));
                    reporter.Report(new InstallProgress("QemuM","Проверка SHA-256"));
                    var current=Core.Current(root);
                    if(current!=null){var local=Core.Read<Package>(Path.Combine(root,"versions",current.current,"package.json"));if(Core.CompareBuild(feed.version,feed.build,local.version,local.build)<0)throw new IOException("Обновление устарело. Проверь наличие новой сборки ещё раз.");}
                    if(current!=null&&current.current==feed.version){Core.RefreshBootstrap(root,Application.ExecutablePath);Core.RepairSameVersion(root,zip,feed.sha256,(done,total)=>reporter.Report(new InstallProgress("QemuM","Установка сборки",done,total)),feed);return;}
                    var package=Core.Stage(zip,feed.sha256,root,(done,total)=>reporter.Report(new InstallProgress("QemuM","Распаковка",done,total)));
                    Core.MatchFeed(package,feed);
                    Core.Activate(root,package);
                });
            }
            progress.Report("Готово",1,1);
            status.Text="Установлена "+Core.Display(feed.version,feed.build)+". Открой QemuM через прежний ярлык.";
        }catch(IOException error){status.Text="Обновление не установлено. Проверь, что менеджер закрыт.\n"+error.Message;}
        catch(Exception error){status.Text="Обновление не установлено: "+error.Message;}
        finally{busy=false;check.Enabled=true;}
    }
}
