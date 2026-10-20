using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

sealed class Distro {
    public string Id,Name,File,Pattern;
    public override string ToString(){return Name;}
}

// Presentation data lives outside machines/*.json and never reaches QEMU.
sealed class AppearanceStore : IDisposable {
    readonly string assets,path;
    readonly List<Distro> builtins=new List<Distro>();
    readonly Dictionary<string,Image> images=new Dictionary<string,Image>();
    readonly ConcurrentDictionary<string,string> volumes=new ConcurrentDictionary<string,string>();
    public event EventHandler Changed;
    public string Error {get;private set;}
    public AppearanceStore(string root){
        assets=Path.Combine(root,"visual-assets");path=Path.Combine(root,"appearance.json");
        string builtinAssets=Path.Combine(AlphaIntegration.AppRoot(root),"visual-assets");
        try{foreach(var item in J.A(J.Json().DeserializeObject(System.IO.File.ReadAllText(Path.Combine(builtinAssets,"catalog.json"),Encoding.UTF8)))){
            string file=J.S(item,"file");if(Path.GetFileName(file)!=file)continue;
            builtins.Add(new Distro{Id=J.S(item,"id"),Name=J.S(item,"name"),File=Path.Combine(builtinAssets,file),Pattern=J.S(item,"pattern")});
        }}catch(IOException){Error="Каталог логотипов недоступен.";}catch(ArgumentException){Error="Не удалось прочитать каталог логотипов.";}
    }
    Dictionary<string,object> Read(){
        if(!System.IO.File.Exists(path))return J.O("machines",new Dictionary<string,object>(),"custom",new object[0]);
        if(new FileInfo(path).Length>1048576)throw new InvalidOperationException("Файл оформления слишком большой.");
        var data=J.Json().DeserializeObject(System.IO.File.ReadAllText(path,Encoding.UTF8)) as Dictionary<string,object>;
        if(data==null||!(J.V(data,"machines") is Dictionary<string,object>))throw new InvalidOperationException("Не удалось прочитать настройки оформления.");
        return data;
    }
    public List<Distro> Choices(){
        var data=Read();var hidden=J.A(J.V(data,"hidden")).Select(Convert.ToString).ToArray();
        var result=builtins.Where(row=>!hidden.Contains(row.Id)).ToList();
        foreach(var row in J.A(J.V(data,"custom"))){
            string file=J.S(row,"file");if(Path.GetFileName(file)!=file||!file.EndsWith(".png",StringComparison.OrdinalIgnoreCase))continue;
            result.Add(new Distro{Id=J.S(row,"id"),Name=J.S(row,"name"),File=Path.Combine(assets,"custom",file)});
        }
        return result;
    }
    public string Choice(string name){return J.S(J.V(Read(),"machines"),name??"","auto");}
    public void SaveChoice(string name,string choice){
        if(String.IsNullOrWhiteSpace(name))throw new InvalidOperationException("Не выбрана VM для оформления.");
        if(choice!="auto"&&choice!="generic"&&!Choices().Any(row=>row.Id==choice))throw new InvalidOperationException("Значок больше не доступен в каталоге.");
        if(Choice(name)==choice)return;
        Mutate(data=>{var machines=new Dictionary<string,object>(J.D(J.V(data,"machines")));if(choice=="auto")machines.Remove(name);else machines[name]=choice;data["machines"]=machines;});
    }
    void Mutate(Action<Dictionary<string,object>> action){
        // The exclusive lock also protects two simultaneously open managers.
        using(var gate=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){
            var data=Read();action(data);string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{
                System.IO.File.WriteAllText(temporary,J.Json().Serialize(data),new UTF8Encoding(false));
                if(System.IO.File.Exists(path))System.IO.File.Replace(temporary,path,path+".bak");else System.IO.File.Move(temporary,path);
            }finally{if(System.IO.File.Exists(temporary))System.IO.File.Delete(temporary);}
        }
        if(Changed!=null)Changed(this,EventArgs.Empty);
    }
    public string RemoveDistro(string id){
        if(String.IsNullOrEmpty(id)||id=="auto"||id=="generic")throw new InvalidOperationException("Системные режимы оформления удалить нельзя.");
        string customFile=null;
        Mutate(data=>{
            bool builtin=builtins.Any(row=>row.Id==id);var custom=J.A(J.V(data,"custom"));
            var entry=custom.FirstOrDefault(row=>J.S(row,"id")==id);
            if(!builtin&&entry==null)throw new InvalidOperationException("Дистрибутив уже удалён из каталога.");
            if(builtin){var hidden=J.A(J.V(data,"hidden")).Select(Convert.ToString).ToList();if(!hidden.Contains(id))hidden.Add(id);data["hidden"]=hidden.ToArray();}
            else{
                string file=J.S(entry,"file");
                if(!Regex.IsMatch(file,@"^custom-[a-f0-9]{32}\.png$"))throw new InvalidOperationException("Не удалось безопасно определить файл пользовательского значка.");
                customFile=Path.Combine(assets,"custom",file);data["custom"]=custom.Where(row=>J.S(row,"id")!=id).ToArray();
            }
            var machines=new Dictionary<string,object>(J.D(J.V(data,"machines")));
            foreach(string name in machines.Keys.ToArray())if(Convert.ToString(machines[name])==id)machines[name]="generic";
            data["machines"]=machines;
        });
        if(customFile!=null)try{System.IO.File.Delete(customFile);}
            catch(IOException){return "Запись удалена. PNG сейчас занят: "+customFile;}
            catch(UnauthorizedAccessException){return "Запись удалена, но нет прав удалить PNG: "+customFile;}
        return null;
    }
    public Distro AddCustom(string name,string png){
        name=(name??"").Trim();if(name.Length==0||name.Length>80)throw new InvalidOperationException("Введи название от 1 до 80 символов.");
        if(Choices().Any(row=>String.Equals(row.Name,name,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Такое название уже есть в каталоге.");
        using(var test=ReadPng(png)){}
        string id="custom-"+Guid.NewGuid().ToString("N"),file=id+".png",directory=Path.Combine(assets,"custom");Directory.CreateDirectory(directory);
        string target=Path.Combine(directory,file);System.IO.File.Copy(png,target,false);
        try{Mutate(data=>{var rows=J.A(J.V(data,"custom")).ToList();if(rows.Count>=256)throw new InvalidOperationException("В каталоге уже 256 пользовательских значков.");rows.Add(J.O("id",id,"name",name,"file",file));data["custom"]=rows.ToArray();});}
        catch{System.IO.File.Delete(target);throw;}
        return new Distro{Id=id,Name=name,File=target};
    }
    static Image ReadPng(string file){
        var info=new FileInfo(file);if(!info.Exists||info.Length>4194304)throw new InvalidOperationException("Выбери PNG размером до 4 МБ.");
        using(var stream=System.IO.File.OpenRead(file)){
            byte[] signature=new byte[8];if(stream.Read(signature,0,8)!=8||!signature.SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new InvalidOperationException("Файл должен быть настоящим PNG.");
            stream.Position=0;using(var image=Image.FromStream(stream,true,true)){
                if(image.Width>2048||image.Height>2048)throw new InvalidOperationException("Максимальный размер PNG — 2048 × 2048.");return new Bitmap(image);
            }
        }
    }
    public Image Logo(Distro distro){
        if(distro==null)return null;lock(images){Image image;if(images.TryGetValue(distro.File,out image))return image;
            try{image=ReadPng(distro.File);}catch(IOException){return null;}catch(ArgumentException){return null;}catch(InvalidOperationException){return null;}
            images[distro.File]=image;return image;
        }
    }
    Distro Match(string value){
        var match=builtins.Where(row=>!String.IsNullOrEmpty(row.Pattern)).OrderBy(row=>row.Id=="linux"?2:row.Id=="ubuntu"||row.Id=="windows"?1:0)
            .FirstOrDefault(row=>Regex.IsMatch(value??"",@"(?:^|[^a-z0-9])(?:"+row.Pattern+@")(?:[^a-z0-9]|$)",RegexOptions.IgnoreCase));
        return match!=null&&!J.A(J.V(Read(),"hidden")).Contains(match.Id)?match:null;
    }
    public Distro Resolve(string choice,object data,string name){
        if(choice=="generic")return null;if(choice!="auto")return Choices().FirstOrDefault(row=>row.Id==choice);
        string iso=J.S(data,"iso_path",J.S(data,"kind")=="iso"?J.S(data,"path"):"");Distro result=Match(Path.GetFileName(iso));
        if(result!=null)return result;
        if(!String.IsNullOrEmpty(iso)&&iso.EndsWith(".iso",StringComparison.OrdinalIgnoreCase)){
            result=Match(volumes.GetOrAdd(iso,ReadVolume));if(result!=null)return result;
        }
        return Match(name+" "+J.S(data,"label")+" "+Path.GetFileName(J.S(data,"disk_path")));
    }
    static string ReadVolume(string iso){
        // ISO9660 volume descriptors only: never mount a guest image or inspect its disk.
        try{using(var stream=new FileStream(iso,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)){
            byte[] sector=new byte[2048];for(int index=16;index<48;index++){
                stream.Position=index*2048L;if(stream.Read(sector,0,sector.Length)!=sector.Length)return "";
                if(Encoding.ASCII.GetString(sector,1,5)!="CD001")continue;
                if(sector[0]==1)return Encoding.ASCII.GetString(sector,40,32).Trim();if(sector[0]==255)break;
            }
        }}catch(IOException){}catch(UnauthorizedAccessException){}return "";
    }
    public void Dispose(){lock(images){foreach(var image in images.Values)image.Dispose();images.Clear();}}
}

sealed class AppearanceView : UserControl {
    readonly AppearanceStore store;readonly Func<object> context;readonly Func<string> vmName;
    internal readonly ThemedSelector selector=Theme.Combo();
    readonly MonitorEmblem preview=new MonitorEmblem();readonly Label summary=Theme.Label("",11,Theme.Muted);
    readonly Button delete;
    int version;
    public string Choice {get{var item=selector.SelectedItem as Distro;return item==null?"auto":item.Id;}}
    public AppearanceView(AppearanceStore store,Func<object> context,Func<string> vmName,string current="auto"){
        this.store=store;this.context=context;this.vmName=vmName;Dock=DockStyle.Fill;Padding=new Padding(24);BackColor=Theme.Bg;
        var flow=Theme.Flow(true);flow.Dock=DockStyle.Fill;
        flow.Controls.Add(Theme.Label("Значок системы",20,null,true));
        var note=Theme.Label("Только оформление. Выбор системы не меняет параметры запуска VM.\r\nАвтоматически: имя ISO, его метка тома, затем название машины.",10,Theme.Muted);note.MaximumSize=new Size(650,0);flow.Controls.Add(note);
        selector.Dock=DockStyle.None;selector.Width=480;selector.Margin=new Padding(0,12,0,20);flow.Controls.Add(selector);
        flow.Controls.Add(preview);summary.MaximumSize=new Size(650,0);summary.Margin=new Padding(0,14,0,18);flow.Controls.Add(summary);
        var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Добавить свой дистрибутив…",Add));
        delete=Theme.Button("Удалить из каталога",Remove,false,true);buttons.Controls.Add(delete);flow.Controls.Add(buttons);
        var source=Theme.Label("PNG: Dashboard Icons; Tails — Wikimedia Commons.\r\nЛоготипы доступны без интернета. Свои значки сохраняются в общем каталоге.",9,Theme.Muted);source.MaximumSize=new Size(650,0);source.Margin=new Padding(0,22,0,0);flow.Controls.Add(source);
        Controls.Add(new ScrollSurface(flow));Reload(current);selector.SelectedIndexChanged+=(sender,args)=>{UpdateDelete();RefreshPreview();};
        store.Changed+=CatalogChanged;UpdateDelete();
        HandleCreated+=(sender,args)=>RefreshPreview();VisibleChanged+=(sender,args)=>{if(Visible)RefreshPreview();};
    }
    void Reload(string id){selector.Items.Clear();selector.Items.Add(new Distro{Id="auto",Name="Определять автоматически"});selector.Items.Add(new Distro{Id="generic",Name="Обычный значок компьютера"});foreach(var item in store.Choices().OrderBy(row=>row.Name))selector.Items.Add(item);selector.SelectedIndex=Enumerable.Range(0,selector.Items.Count).FirstOrDefault(index=>((Distro)selector.Items[index]).Id==id);}
    public void RefreshPreview(){
        if(!IsHandleCreated||IsDisposed)return;int request=++version;object data=context();string name=vmName(),choice=Choice;
        Task.Run(()=>{Distro distro=null;string error=null;try{distro=store.Resolve(choice,data,name);}catch(Exception exception){error=exception.Message;}
            if(IsDisposed||!IsHandleCreated)return;
            try{BeginInvoke((Action)(()=>{if(IsDisposed||request!=version)return;preview.Logo=store.Logo(distro);summary.Text=error??(choice=="generic"?"Обычный значок компьютера.":distro==null?"Система не определена. Выбери её из списка или добавь свою.":"Значок: "+distro.Name);if(store.Error!=null)summary.Text+=Environment.NewLine+store.Error;}));}catch(InvalidOperationException){}
        });
    }
    void Add(){using(var dialog=new CustomDistroDialog(store)){if(dialog.ShowDialog(FindForm())==DialogResult.OK){Reload(dialog.Value.Id);RefreshPreview();}}}
    void UpdateDelete(){delete.Enabled=Choice!="auto"&&Choice!="generic";}
    void CatalogChanged(object sender,EventArgs args){string id=Choice;Reload(id);if(id!="auto"&&id!="generic"&&Choice=="auto")selector.SelectedIndex=1;UpdateDelete();RefreshPreview();}
    void Remove(){
        var item=selector.SelectedItem as Distro;if(item==null||!delete.Enabled)return;
        if(MessageBox.Show(FindForm(),"Удалить «"+item.Name+"» из общего каталога?\r\nVM, использующие этот значок, получат обычный значок компьютера. Параметры запуска не изменятся.\r\nУдаление применяется сразу ко всему каталогу.","Удаление дистрибутива",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        try{string warning=store.RemoveDistro(item.Id);if(warning!=null)MessageBox.Show(FindForm(),warning,"Каталог значков",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        catch(Exception error){MessageBox.Show(FindForm(),error.Message,"Каталог значков",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
    protected override void Dispose(bool disposing){if(disposing)store.Changed-=CatalogChanged;base.Dispose(disposing);}
}

sealed class CustomDistroDialog : Form {
    readonly AppearanceStore store;readonly FieldInput name=Theme.Input(),png=Theme.Input();
    public Distro Value {get;private set;}
    public CustomDistroDialog(AppearanceStore store){
        this.store=store;Text="Свой дистрибутив";Theme.Form(this,680,430);
        var fields=Theme.Fields();Theme.Row(fields,"Название в каталоге",name,true);Theme.Row(fields,"Логотип PNG",Theme.Browse(png,false,"PNG|*.png"),true);
        Theme.Row(fields,"",Theme.Label("До 4 МБ и 2048 × 2048 пикселей.\r\nPNG копируется в каталог менеджера и будет доступен для следующих VM.",10,Theme.Muted));
        var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Добавить",Save,true));buttons.Controls.Add(Theme.Button("Отмена",()=>{DialogResult=DialogResult.Cancel;Close();}));
        Theme.Dialog(this,fields,buttons,"Свой дистрибутив","Название и PNG используются только для оформления.");
    }
    void Save(){try{Value=store.AddCustom(name.Text,png.Text);DialogResult=DialogResult.OK;Close();}catch(Exception error){MessageBox.Show(this,error.Message,"Каталог значков",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
}
