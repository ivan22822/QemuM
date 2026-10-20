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
    readonly Label status=new Label{Dock=DockStyle.Fill,AutoSize=false,Padding=new Padding(24),ForeColor=Color.White};
    readonly Button check=new Button{Text="Проверить",AutoSize=true};
    readonly Button install=new Button{Text="Установить обновление",AutoSize=true,Enabled=false};
    Feed feed;string work;bool busy;
    public UpdateWindow(string root){
        this.root=root;Text="Обновления QemuM · alpha";ClientSize=new Size(600,250);MinimumSize=Size;StartPosition=FormStartPosition.CenterScreen;
        BackColor=Color.FromArgb(5,14,37);Font=new Font("Segoe UI",11);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=64,Padding=new Padding(20,8,0,0)};
        foreach(var button in new[]{check,install}){button.BackColor=Color.FromArgb(101,199,255);button.ForeColor=Color.Black;button.FlatStyle=FlatStyle.Flat;button.Margin=new Padding(0,0,12,0);buttons.Controls.Add(button);}
        Controls.Add(status);Controls.Add(buttons);
        check.Click+=(sender,args)=>Check();install.Click+=(sender,args)=>Install();
        Shown+=(sender,args)=>Check();FormClosing+=(sender,args)=>{if(busy)args.Cancel=true;};
    }
    async void Check(){
        if(busy)return;busy=true;check.Enabled=install.Enabled=false;status.Text="Проверяю выпуски GitHub…";
        try{
            var current=Core.Current(root);
            work=Path.Combine(root,"downloads",Guid.NewGuid().ToString("N"));Core.NoLinks(work);Directory.CreateDirectory(work);
            feed=await Task.Run(()=>Core.Latest(work));
            if(feed==null)status.Text="Опубликованных обновлений alpha пока нет.";
            else if(current!=null&&Core.Compare(feed.version,current.current)<=0)status.Text="Установлена актуальная версия: "+current.current;
            else if(feed.min_launcher_protocol>Core.Protocol||feed.min_launcher_protocol<1)
                status.Text="Версия "+feed.version+" требует нового установщика. Открой Releases проекта.";
            else{status.Text="Доступна версия "+feed.version+".\n\nПеред установкой закрой окно менеджера. Работающие VM продолжат работу. Предыдущая версия сохранится.";install.Enabled=true;}
        }catch(Exception error){status.Text="Проверка не завершена: "+error.Message;}
        finally{busy=false;check.Enabled=true;}
    }
    async void Install(){
        if(busy||feed==null)return;
        if(MessageBox.Show(this,"Установить "+feed.version+"? Закрой менеджер перед продолжением.","QemuM",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
        busy=true;check.Enabled=install.Enabled=false;
        try{
            using(Core.Lock(root,"running.lock"))using(Core.Lock(root,"update.lock")){
                status.Text="Скачиваю и проверяю пакет…";
                await Task.Run(()=>{
                    string zip=Path.Combine(work,Guid.NewGuid().ToString("N")+".zip");Core.Download(feed.url,zip,128*1024*1024);
                    var package=Core.Stage(zip,feed.sha256,root);
                    if(package.version!=feed.version||package.min_launcher_protocol!=feed.min_launcher_protocol)
                        throw new IOException("Версия пакета не соответствует обновлению.");
                    Core.Activate(root,package);
                });
            }
            status.Text="Установлена версия "+feed.version+". Открой QemuM через прежний ярлык.";
        }catch(IOException error){status.Text="Обновление не установлено. Проверь, что менеджер закрыт.\n"+error.Message;}
        catch(Exception error){status.Text="Обновление не установлено: "+error.Message;}
        finally{busy=false;check.Enabled=true;}
    }
}
