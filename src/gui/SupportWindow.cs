using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

static class AlphaIntegration {
    public const string Version="0.1.0-alpha.1";
    public const string Issues="https://github.com/ivan22822/QemuM/issues/new/choose";
    public static string AppRoot(string fallback) { return Environment.GetEnvironmentVariable("QEMUM_APP_DIR") ?? fallback; }
    public static void OpenIssues() { Process.Start(new ProcessStartInfo(Issues){UseShellExecute=true}); }
    public static void Updates(IWin32Window owner) {
        string root=Environment.GetEnvironmentVariable("QEMUM_INSTALL_DIR");
        string launcher=root==null?null:Path.Combine(root,"QemuM.exe");
        if(launcher==null||!File.Exists(launcher)){
            MessageBox.Show(owner,"Это сборка из исходников. Обновления доступны в установленной альфа-версии.\nВерсия: "+Version,"Обновления");return;
        }
        Process.Start(new ProcessStartInfo(launcher,"--check-updates"){UseShellExecute=false,CreateNoWindow=true});
    }
    public static void Ready() {
        string path=Environment.GetEnvironmentVariable("QEMUM_READY_FILE");
        if(!String.IsNullOrEmpty(path))File.WriteAllText(path,Version);
    }
}

sealed class SupportWindow : Form {
    readonly MainWindow main;
    readonly ThemedSelector machine=new ThemedSelector();
    readonly ThemedEditor description=new ThemedEditor();
    readonly Dictionary<string,CheckBox> sections=new Dictionary<string,CheckBox>();
    readonly CheckBox redact=new CheckBox{Text="Скрывать известные пути и секреты в тексте",Checked=true,AutoSize=true};
    readonly Label result=Theme.Label("",10,Theme.Muted);
    readonly Button save;
    readonly List<string> names=new List<string>();
    bool collecting;
    public SupportWindow(MainWindow main,string selected) {
        this.main=main;Text="Сообщить о проблеме · QemuM";Theme.Form(this,800,770);MinimumSize=new Size(720,650);
        var body=Theme.Flow(true);body.Dock=DockStyle.Top;body.Padding=new Padding(24);body.WrapContents=false;
        var title=Theme.Label("Помочь улучшить QemuM",22,null,true);body.Controls.Add(title);
        var instruction=Theme.Label("1. Опиши, что делал и что ожидал.\n2. Выбери данные и сохрани отчёт.\n3. Проверь файлы и приложи ZIP к GitHub Issue.",11,Theme.Muted);
        instruction.Margin=new Padding(0,12,0,18);body.Controls.Add(instruction);
        body.Controls.Add(Theme.Label("Виртуальная машина",10,Theme.Muted));
        machine.Dock=DockStyle.None;machine.Width=690;machine.Margin=new Padding(0,6,0,14);
        foreach(var row in main.Rows){string name=J.S(row,"name");names.Add(name);machine.Items.Add(J.S(J.V(row,"data"),"label",name)+"  ·  "+name);}
        if(names.Count>0)machine.SelectedIndex=Math.Max(0,names.IndexOf(selected??""));body.Controls.Add(machine);
        body.Controls.Add(Theme.Label("Что произошло",10,Theme.Muted));
        description.Dock=DockStyle.None;description.Size=new Size(690,100);description.Margin=new Padding(0,6,0,14);body.Controls.Add(description);
        AddSection(body,"logs","Живой лог QEMU и события менеджера (последние 2 МБ)");
        AddSection(body,"parameters","Параметры VM и предварительные аргументы запуска");
        AddSection(body,"state","Текущее состояние QMP и подключённые диски");
        AddSection(body,"diagnostics","Диагностика Windows, Python и доступности образов");
        AddSection(body,"screenshot","Скриншот экрана VM (VM должна работать)");
        redact.Margin=new Padding(0,12,0,10);body.Controls.Add(redact);
        var warning=Theme.Label("Проверь отчёт перед публикацией: в тексте и на скриншоте могут быть\nличные данные. Скриншот не маскируется. Отправка — только вручную.",10,Theme.Muted);
        warning.Margin=new Padding(0,0,0,16);body.Controls.Add(warning);
        var buttons=Theme.Flow();
        save=Theme.Button("Сохранить отчёт…",()=>Export(),true);
        buttons.Controls.Add(save);buttons.Controls.Add(Theme.Button("Открыть GitHub Issues",AlphaIntegration.OpenIssues));body.Controls.Add(buttons);
        result.MaximumSize=new Size(690,0);result.Margin=new Padding(0,12,0,8);body.Controls.Add(result);
        Controls.Add(new ScrollSurface(body));
        body.SizeChanged+=(sender,args)=>{int width=Math.Max(1,body.ClientSize.Width-body.Padding.Horizontal);machine.Width=description.Width=width;result.MaximumSize=warning.MaximumSize=instruction.MaximumSize=new Size(width,0);};
        FormClosing+=(sender,args)=>{if(collecting)args.Cancel=true;};
    }
    void AddSection(Control parent,string key,string text){
        var box=new CheckBox{Text=text,AutoSize=true,Checked=false,Margin=new Padding(0,5,0,5)};
        sections.Add(key,box);parent.Controls.Add(box);
    }
    async void Export(){
        if(collecting||main.Busy||main.Backend==null){result.Text="Дождись завершения текущей операции менеджера.";return;}
        string[] selected=sections.Where(pair=>pair.Value.Checked).Select(pair=>pair.Key).ToArray();
        if(machine.SelectedIndex<0||selected.Length==0){result.Text="Выбери VM и хотя бы один раздел.";return;}
        using(var dialog=new SaveFileDialog{Filter="ZIP-отчёт|*.zip",FileName="QemuM-report-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip",OverwritePrompt=true}){
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            if(File.Exists(dialog.FileName)){result.Text="Выбери новое имя: существующий файл не будет заменён.";return;}
            var args=J.O("name",names[machine.SelectedIndex],"sections",selected,"redact",redact.Checked,"description",description.Text,"path",dialog.FileName);
            collecting=true;main.Busy=true;save.Enabled=false;result.Text="Собираю выбранные данные…";
            try{
                object response=await Task.Run(()=>main.Backend.Call("support_export",args));
                int errors=J.D(J.V(response,"errors")).Count;
                result.Text="Сохранено: "+J.S(response,"path")+(errors>0?"\nЧасть данных недоступна; причины записаны в report.json.":"")+"\nПроверь ZIP перед отправкой.";
            }catch(Exception error){result.Text=error.Message;}
            finally{collecting=false;main.Busy=false;save.Enabled=true;}
        }
    }
}
