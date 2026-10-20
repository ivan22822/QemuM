using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

static class ArgText {
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CommandLineToArgvW(string command,out int count);
    [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr memory);
    public static string Format(IEnumerable<object> args){return String.Join(" ",args.Select(value=>{string text=Convert.ToString(value);return text.Length==0||text.Any(Char.IsWhiteSpace)||text.Contains("\"")?J.Quote(text):text;}));}
    public static string[] Parse(string command){
        if(command.IndexOf('\0')>=0)throw new InvalidOperationException("Extra args содержит нулевой символ.");
        var normalized=new StringBuilder();bool quoted=false;int slashes=0;
        foreach(char value in command){if(value=='"'&&slashes%2==0)quoted=!quoted;normalized.Append(!quoted&&(value=='\r'||value=='\n')?' ':value);slashes=value=='\\'?slashes+1:0;}
        if(quoted)throw new InvalidOperationException("В Extra args не закрыты двойные кавычки.");
        int count;IntPtr memory=CommandLineToArgvW("qemu.exe "+normalized,out count);
        if(memory==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try{return Enumerable.Range(1,count-1).Select(index=>Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory,index*IntPtr.Size))).ToArray();}finally{LocalFree(memory);}
    }
}

sealed class ExtraArgsDialog : Form {
    internal readonly ThemedEditor Editor;public string Value {get{return Editor.Text;}}
    public ExtraArgsDialog(string value){
        Text="Расширенный редактор Extra args";Theme.Form(this,1000,730);
        Editor=Theme.Editor(value);Editor.Font=new Font("Consolas",11F);Editor.AcceptsTab=true;
        var buttons=Theme.Flow();buttons.Dock=DockStyle.Bottom;buttons.Padding=new Padding(20,10,10,0);
        buttons.Controls.Add(Theme.Button("Применить",()=>{try{ArgText.Parse(Editor.Text);DialogResult=DialogResult.OK;Close();}catch(Exception error){MessageBox.Show(this,error.Message,"Extra args");}},true));
        buttons.Controls.Add(Theme.Button("Отмена",()=>{DialogResult=DialogResult.Cancel;Close();}));Theme.Dialog(this,Theme.EditorLayout(Editor,"Аргументы запуска","Без имени программы. Пути с пробелами — в двойных кавычках.\r\nПереносы строк разделяют аргументы; команды PowerShell здесь не выполняются.",null),buttons,"Extra args","Дополнительные аргументы запуска QEMU.","РЕДАКТОР ПАРАМЕТРОВ");
    }
}

sealed class LaunchSettingsUi {
    readonly MainWindow main;readonly Form owner;readonly PageDeck tabs;readonly Func<Dictionary<string,object>> context;readonly Action ensureVisual;
    internal readonly ThemedEditor Extra=Theme.Editor();
    internal readonly AdapterList List=new AdapterList();
    internal readonly PageView NetworkPage,ExtraPage,HelpPage;
    internal readonly List<object> Adapters=new List<object>();readonly Label hint=Theme.Label("",10,Theme.Muted);readonly FlowLayoutPanel networkButtons=Theme.Flow();
    Dictionary<string,object> source;string normalizedExtra;bool initialized;string[] warnings=new string[0];
    public LaunchSettingsUi(MainWindow main,Form owner,PageDeck tabs,Func<Dictionary<string,object>> context,Action ensureVisual=null){
        this.main=main;this.owner=owner;this.tabs=tabs;this.context=context;this.ensureVisual=ensureVisual;
        NetworkPage=new PageView("Сеть");ExtraPage=new PageView("Extra args");HelpPage=new PageView("Справка Extra args");
        var netPanel=new Panel{Dock=DockStyle.Fill,Padding=new Padding(20)};
        var netTop=Theme.Flow(true);netTop.Dock=DockStyle.Top;netTop.Controls.Add(Theme.Label("Сетевые адаптеры",20,null,true));hint.MaximumSize=new Size(700,0);hint.Margin=new Padding(0,12,0,15);netTop.Controls.Add(hint);
        networkButtons.Dock=DockStyle.Bottom;networkButtons.Padding=new Padding(0,15,0,0);
        networkButtons.Controls.Add(Theme.Button("Добавить адаптер",()=>Safe(Add),true));networkButtons.Controls.Add(Theme.Button("Изменить",()=>Safe(Edit)));networkButtons.Controls.Add(Theme.Button("Удалить",()=>Safe(Remove)));
        var preview=Theme.Button("Проверить сеть",()=>Safe(Preview));
        var extraActions=Theme.Flow();extraActions.Dock=DockStyle.Bottom;extraActions.Controls.Add(preview);
        netPanel.Controls.Add(List);netPanel.Controls.Add(netTop);netPanel.Controls.Add(networkButtons);netPanel.Controls.Add(extraActions);NetworkPage.Controls.Add(netPanel);
        List.DoubleClick+=(sender,args)=>Safe(Edit);
        Extra.Font=new Font("Consolas",11F);Extra.AcceptsTab=true;
        var extraButtons=Theme.Flow();
        extraButtons.Controls.Add(Theme.Button("Открыть редактор",()=>Safe(()=>{using(var editor=new ExtraArgsDialog(Extra.Text)){if(editor.ShowDialog(owner)==DialogResult.OK)Extra.Text=editor.Value;}}),true));
        extraButtons.Controls.Add(Theme.Button("Справка и примеры",()=>tabs.SelectedPage=HelpPage));extraButtons.Controls.Add(Theme.Button("Разобрать аргументы",()=>Safe(()=>{var args=ArgText.Parse(Extra.Text);using(var view=new ExtraArgsDialog(String.Join(Environment.NewLine,args.Select((value,index)=>(index+1)+": "+J.Json().Serialize(value))))){view.Text="Аргументы после разбора · только просмотр";view.Editor.ReadOnly=true;view.ShowDialog(owner);}})));
        ExtraPage.Controls.Add(Theme.EditorLayout(Extra,"Дополнительные аргументы","Укажи только параметры QEMU, без имени программы.\r\nНапример: -device usb-tablet. Путь с пробелами: -D \"C:\\VM Logs\\qemu.log\"",extraButtons));
        HelpPage.Controls.Add(new ExtraHelpView(sample=>Safe(()=>{if(ensureVisual!=null)ensureVisual();var current=ArgText.Parse(Extra.Text);Extra.Text=ArgText.Format(current.Cast<object>().Concat(sample.Args));tabs.SelectedPage=ExtraPage;})));
        tabs.Pages.Add(NetworkPage);tabs.Pages.Add(ExtraPage);tabs.Pages.Add(HelpPage);
        tabs.Selecting+=(sender,args)=>{if(args.Page==NetworkPage)try{if(ensureVisual!=null)ensureVisual();context();}catch(Exception error){args.Cancel=true;MessageBox.Show(owner,error.Message,"Сетевые параметры",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
    }
    void Safe(Action action){try{action();}catch(Exception error){MessageBox.Show(owner,error.Message,"Настройка запуска",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
    public static Dictionary<string,object> Defaults(){byte[] bytes=Guid.NewGuid().ToByteArray();return J.O("extra_args",new object[0],"network_adapters",new object[]{J.O("id","net0","type","user","model","e1000e","mac","52:54:00:"+String.Join(":",bytes.Take(3).Select(value=>value.ToString("x2"))),"net","10.0.2.0/24")});}
    // The projection is a draft only. No configuration is written until Save.
    public void Load(object data,object preview=null){
        source=new Dictionary<string,object>(J.D(data));initialized=false;normalizedExtra=null;Adapters.Clear();
        Extra.Text=ArgText.Format(J.A(J.V(data,"extra_args")));
        try{if(preview!=null&&!J.D(preview).ContainsKey("error"))ApplyProjection(preview);else Normalize(source);}
        catch(Exception error){warnings=new[]{error.Message};Render();}
    }
    void ApplyProjection(object result){
        Adapters.Clear();foreach(object item in J.A(J.V(result,"adapters")))Adapters.Add(J.Json().DeserializeObject(J.Json().Serialize(item)));
        Extra.Text=ArgText.Format(J.A(J.V(result,"extra_args")));normalizedExtra=Extra.Text;initialized=true;
        warnings=J.A(J.V(result,"warnings")).Select(Convert.ToString).ToArray();Render();
    }
    void Normalize(Dictionary<string,object> data){
        if(initialized&&Extra.Text==normalizedExtra)return;
        if(main.Backend==null)throw new InvalidOperationException("Сервис менеджера ещё не подключён.");
        var candidate=new Dictionary<string,object>(data);candidate["extra_args"]=ArgText.Parse(Extra.Text);
        if(initialized)candidate["network_adapters"]=Adapters.ToArray();
        else if(source.ContainsKey("network_adapters"))candidate["network_adapters"]=source["network_adapters"];
        else candidate.Remove("network_adapters");
        // Pure local parsing over the existing RPC pipe; never launches QEMU.
        ApplyProjection(main.Backend.Call("network_import",J.O("data",candidate)));
    }
    public Dictionary<string,object> Merge(Dictionary<string,object> data){
        Normalize(data);var result=new Dictionary<string,object>(data);
        result["extra_args"]=ArgText.Parse(Extra.Text);result["network_adapters"]=Adapters.ToArray();return result;
    }
    void Render(){
        hint.Text="Добавляй и изменяй адаптеры здесь — сеть из Extra args подхватывается автоматически.\r\nПустой список отключает автоматическую сеть. Применится при следующем запуске.";
        if(warnings.Length>0)hint.Text+=Environment.NewLine+String.Join(Environment.NewLine,warnings);
        List.Items.Clear();var displayed=Adapters.ToArray();
        foreach(object item in displayed){string type=J.S(item,"type","user");string mode=type=="user"?"NAT":type=="socket"?"TCP "+J.S(item,"mode","ручной"):type.ToUpperInvariant();var row=new AdapterRow(new[]{J.S(item,"id"),mode,J.S(item,"model"),J.S(item,"mac","Авто"),type=="user"?J.S(item,"net","10.0.2.0/24"):type=="tap"?J.S(item,"ifname"):J.S(item,"address")+":"+J.N(item,"port")});List.Items.Add(row);}
    }
    void Add(){
        if(ensureVisual!=null)ensureVisual();context();
        string id;int index=0;var extra=ArgText.Parse(Extra.Text);do{id="net"+index++;}while(Adapters.Any(row=>J.S(row,"id")==id)||extra.Any(arg=>Regex.IsMatch(arg,@"(?:^|,)id="+Regex.Escape(id)+@"(?:,|$)")));
        byte[] bytes=Guid.NewGuid().ToByteArray();var data=J.O("id",id,"type","user","model","virtio-net-pci","mac","52:54:00:"+String.Join(":",bytes.Take(3).Select(value=>value.ToString("x2"))),"net","10.0."+(index+1)+".0/24");
        using(var dialog=new NetworkAdapterDialog(data)){if(dialog.ShowDialog(owner)==DialogResult.OK){Adapters.Add(dialog.Value);Render();}}
    }
    void Edit(){if(List.SelectedIndex<0)return;int index=List.SelectedIndex;using(var dialog=new NetworkAdapterDialog(Adapters[index])){if(dialog.ShowDialog(owner)==DialogResult.OK){Adapters[index]=dialog.Value;Render();}}}
    void Remove(){if(List.SelectedIndex<0)return;Adapters.RemoveAt(List.SelectedIndex);Render();}
    void Preview(){
        if(ensureVisual!=null)ensureVisual();var data=context();if(main.Backend==null)return;
        main.Run("Проверяю сетевые параметры…",()=>main.Backend.Call("network_validate",J.O("data",data)),result=>{using(var view=new ExtraArgsDialog(ArgText.Format(J.A(J.V(result,"args"))))){view.Text="Сеть и Extra args · только просмотр";view.Editor.ReadOnly=true;view.ShowDialog(owner);}});
    }
}

sealed class NetworkAdapterDialog : Form {
    readonly Dictionary<string,object> original;readonly FieldInput identifier,mac,network,dhcp,address,ifname,backend,backendOptions,deviceOptions;readonly ThemedEditor forwards;readonly ThemedSelector model;
    readonly ThemedSelector mode=Theme.Combo("NAT · Интернет и проброс портов","TCP · слушать соединение VM","TCP · подключиться к VM","TAP · существующий адаптер Windows","Другой backend · вручную");
    readonly CheckBox restrict=new CheckBox{Text="Изоляция NAT: ограничить исходящие соединения",AutoSize=true,ForeColor=Theme.Text};readonly NumberInput port;
    public object Value {get;private set;}
    public NetworkAdapterDialog(object data){
        original=new Dictionary<string,object>(J.D(data));Text="Сетевой адаптер";Theme.Form(this,940,830);
        identifier=Theme.Input(J.S(data,"id"));model=Theme.Combo("e1000e","virtio-net-pci","e1000","rtl8139");model.DropDownStyle=ComboBoxStyle.DropDown;model.Text=J.S(data,"model","virtio-net-pci");mac=Theme.Input(J.S(data,"mac"));network=Theme.Input(J.S(data,"net"));dhcp=Theme.Input(J.S(data,"dhcpstart"));
        address=Theme.Input(J.S(data,"address","127.0.0.1"));port=Theme.Number(J.N(data,"port",1234),1,65535);ifname=Theme.Input(J.S(data,"ifname"));backend=Theme.Input(J.S(data,"type","user"));
        backendOptions=Theme.Input(J.S(data,"backend_options"));deviceOptions=Theme.Input(J.S(data,"device_options"));forwards=Theme.Editor(String.Join(Environment.NewLine,J.A(J.V(data,"hostfwd")).Select(Convert.ToString)));forwards.Height=90;forwards.Font=new Font("Consolas",10F);restrict.Checked=J.B(data,"restrict");
        var fields=Theme.Fields();Theme.Row(fields,"ID сети",identifier);Theme.Row(fields,"Подключение",mode);Theme.Row(fields,"Модель NIC",model);Theme.Row(fields,"MAC (пусто — авто)",mac);
        var natFields=new[]{Theme.Row(fields,"Подсеть NAT / CIDR",network),Theme.Row(fields,"DHCP начинается с",dhcp),Theme.Row(fields,"",restrict),Theme.Row(fields,"Проброс портов NAT",forwards,true)};
        var tcpFields=new[]{Theme.Row(fields,"Узел TCP",address),Theme.Row(fields,"Порт TCP",port)};var tapField=Theme.Row(fields,"Имя TAP Windows",ifname,true);var customField=Theme.Row(fields,"Тип ручного backend",backend,true);
        Theme.Row(fields,"Доп. опции netdev",backendOptions);Theme.Row(fields,"Доп. свойства NIC",deviceOptions);
        Theme.Row(fields,"",Theme.Label("Модели: virtio-net-pci, e1000, e1000e, rtl8139.\r\nVirtio требует драйвер гостевой ОС.\r\nПроброс: tcp:127.0.0.1:2222-:22 — по одному правилу в строке.\r\nTAP требует уже установленного драйвера и адаптера.",9,Theme.Muted));
        string type=J.S(data,"type","user");mode.SelectedIndex=type=="user"?0:type=="socket"&&J.S(data,"mode")=="listen"?1:type=="socket"&&J.S(data,"mode")=="connect"?2:type=="tap"?3:4;
        Action selectFields=()=>{bool user=mode.SelectedIndex==0,tcp=mode.SelectedIndex==1||mode.SelectedIndex==2;fields.SuspendLayout();foreach(var field in natFields)field.Visible=user;foreach(var field in tcpFields)field.Visible=tcp;tapField.Visible=mode.SelectedIndex==3;customField.Visible=mode.SelectedIndex==4;fields.ResumeLayout();};mode.SelectedIndexChanged+=(sender,args)=>selectFields();selectFields();
        var buttons=Theme.Flow();buttons.Controls.Add(Theme.Button("Применить",Save,true));buttons.Controls.Add(Theme.Button("Отмена",()=>{DialogResult=DialogResult.Cancel;Close();}));Theme.Dialog(this,fields,buttons,"Сетевой адаптер",J.S(data,"id")+" · подключение, адресация и проброс портов");
    }
    void Save(){
        if(!Regex.IsMatch(identifier.Text.Trim(),@"^[A-Za-z_][A-Za-z0-9_.-]{0,63}$")||!Regex.IsMatch(model.Text.Trim(),@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,79}$")){MessageBox.Show(this,"Проверь ID и модель адаптера.");return;}
        var data=new Dictionary<string,object>(original);data["id"]=identifier.Text.Trim();data["model"]=model.Text.Trim();data["mac"]=mac.Text.Trim();data["type"]=mode.SelectedIndex==0?"user":mode.SelectedIndex==1||mode.SelectedIndex==2?"socket":mode.SelectedIndex==3?"tap":backend.Text.Trim();
        data["mode"]=mode.SelectedIndex==1?"listen":mode.SelectedIndex==2?"connect":"custom";data["net"]=network.Text.Trim();data["dhcpstart"]=dhcp.Text.Trim();data["restrict"]=restrict.Checked;data["hostfwd"]=forwards.Lines.Where(line=>!String.IsNullOrWhiteSpace(line)).Select(line=>line.Trim()).ToArray();
        data["address"]=address.Text.Trim();data["port"]=(int)port.Value;data["ifname"]=ifname.Text.Trim();data["backend_options"]=backendOptions.Text.Trim();data["device_options"]=deviceOptions.Text.Trim();Value=data;DialogResult=DialogResult.OK;Close();
    }
}

sealed class ExtraExample {public string Name;public object[] Args;public override string ToString(){return Name;}}
sealed class ExtraTopic {public string Title,Body;public ExtraExample[] Examples;public override string ToString(){return Title;}}

sealed class ExtraHelpView : UserControl {
    readonly ThemedList topics=Theme.ListBox(9);
    readonly FieldInput search=Theme.Input();readonly ThemedEditor body=Theme.Editor();
    readonly ThemedSelector examples=Theme.Combo();readonly List<ExtraTopic> all=Content();readonly Action<ExtraExample> insert;
    readonly Label articleTitle=Theme.Label("",19,null,true),exampleCaption=Theme.Label("Пример для вставки",9,Theme.Muted);
    readonly Button insertButton,copyButton;
    public ExtraHelpView(Action<ExtraExample> insert){
        this.insert=insert;Dock=DockStyle.Fill;BackColor=Theme.Bg;Padding=new Padding(24);topics.ItemHeight=44;
        body.BackColor=Theme.Bg;body.BorderVisible=false;body.ReadOnly=true;body.Font=new Font("Segoe UI",10F);body.Padding=new Padding(0,12,10,8);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,214));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        var left=new Panel{Dock=DockStyle.Fill,Padding=new Padding(0,0,24,0),Margin=Padding.Empty};
        var find=new TableLayoutPanel{Dock=DockStyle.Top,ColumnCount=1,RowCount=2,Height=86,Padding=Padding.Empty};find.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        var hint=Theme.Label("Поиск по справке",9,Theme.Muted);hint.Margin=new Padding(0,0,0,8);find.Controls.Add(hint,0,0);
        search.SearchIcon=true;search.Placeholder="Параметр или тема…";search.Dock=DockStyle.Top;search.Margin=Padding.Empty;find.Controls.Add(search,0,1);left.Controls.Add(topics);left.Controls.Add(find);
        var right=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=Padding.Empty,Margin=Padding.Empty};right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));right.RowStyles.Add(new RowStyle(SizeType.Percent,100));right.RowStyles.Add(new RowStyle(SizeType.Absolute,180));
        articleTitle.Dock=DockStyle.Fill;articleTitle.Margin=new Padding(0,0,0,4);right.Controls.Add(articleTitle,0,0);body.Margin=Padding.Empty;right.Controls.Add(body,0,1);
        var sample=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,AutoSize=false,Padding=new Padding(16),BackColor=Theme.Panel,Margin=new Padding(0,18,0,0)};sample.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));sample.RowStyles.Add(new RowStyle(SizeType.AutoSize));sample.RowStyles.Add(new RowStyle(SizeType.Absolute,56));sample.RowStyles.Add(new RowStyle(SizeType.Absolute,55));
        exampleCaption.Margin=new Padding(0,0,0,8);sample.Controls.Add(exampleCaption,0,0);
        examples.Dock=DockStyle.Top;examples.Margin=new Padding(0,0,0,16);sample.Controls.Add(examples,0,1);
        var buttons=Theme.Flow();buttons.AutoSizeMode=AutoSizeMode.GrowAndShrink;buttons.Dock=DockStyle.Top;buttons.Margin=Padding.Empty;
        insertButton=Theme.Button("Вставить пример",()=>{var example=examples.SelectedItem as ExtraExample;if(example!=null)this.insert(example);},true);buttons.Controls.Add(insertButton);
        copyButton=Theme.Button("Копировать",()=>{var example=examples.SelectedItem as ExtraExample;if(example!=null)Clipboard.SetText(ArgText.Format(example.Args));});buttons.Controls.Add(copyButton);
        buttons.Controls.Add(Theme.Button("Документация QEMU",()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.qemu.org/docs/master/system/invocation.html"){UseShellExecute=true})));sample.Controls.Add(buttons,0,2);right.Controls.Add(sample,0,2);
        layout.Controls.Add(left,0,0);layout.Controls.Add(right,1,0);Controls.Add(layout);
        right.Resize+=(sender,args)=>{
            int width=Math.Max(1,right.ClientSize.Width),inner=Math.Max(1,width-32);
            buttons.MaximumSize=new Size(inner,0);exampleCaption.MaximumSize=new Size(inner,0);
            int actionHeight=buttons.GetPreferredSize(new Size(inner,0)).Height;
            sample.RowStyles[2].Height=actionHeight;
            right.RowStyles[2].Height=18+32+exampleCaption.GetPreferredSize(new Size(inner,0)).Height+8+56+actionHeight;
        };
        search.TextChanged+=(sender,args)=>Filter();topics.SelectedIndexChanged+=(sender,args)=>ShowTopic();Filter();
    }
    void Filter(){topics.Items.Clear();foreach(var item in all)if((item.Title+" "+item.Body+" "+String.Join(" ",item.Examples.Select(example=>ArgText.Format(example.Args)))).IndexOf(search.Text,StringComparison.OrdinalIgnoreCase)>=0)topics.Items.Add(item);if(topics.Items.Count>0)topics.SelectedIndex=0;else{articleTitle.Text="Ничего не найдено";body.Text="Попробуй имя параметра, например hostfwd или usb.";examples.Items.Clear();UpdateExamples();}}
    void UpdateExamples(){bool available=examples.Items.Count>0;examples.Enabled=insertButton.Enabled=copyButton.Enabled=available;exampleCaption.Text=available?"Пример для вставки":"В этой теме нет готовых примеров";}
    void ShowTopic(){var topic=topics.SelectedItem as ExtraTopic;if(topic==null)return;articleTitle.Text=topic.Title;body.Text=topic.Body.Replace("\r\n","\n").Replace("\n",Environment.NewLine);examples.Items.Clear();foreach(var example in topic.Examples)examples.Items.Add(example);if(examples.Items.Count>0)examples.SelectedIndex=0;UpdateExamples();}
    static ExtraExample E(string name,params object[] args){return new ExtraExample{Name=name,Args=args};}
    static ExtraTopic T(string title,string body,params ExtraExample[] examples){return new ExtraTopic{Title=title,Body=body,Examples=examples};}
    static List<ExtraTopic> Content(){return new List<ExtraTopic>{
        T("Начало и формат",@"Extra args — список дополнительных аргументов, передаваемых прямо в QEMU. Это не PowerShell и не CMD: команды, переменные и перенаправления здесь не исполняются. Не вставляй qemu-system-x86_64.exe в начало.

Пиши флаг и его значение через пробел. Можно переносить строку между аргументами. Путь с пробелами заключай в двойные кавычки. Одинарные кавычки не группируют текст. Кнопка «Разобрать аргументы» показывает точные границы значений. Пустой аргумент записывается как """". Кавычка внутри значения экранируется обратной косой чертой; для сложных случаев доступна вкладка JSON.

«Вставить пример» добавляет аргументы в поле и ничего не запускает. Перед вставкой проверь повторяющиеся устройства, пути и ID. Изменения применяются при следующем запуске VM; редактирование работающей машины не перенастраивает её на лету.

Источники: локальная справка QEMU 11.1.1 и документация QEMU. Набор доступных возможностей зависит от сборки и гостевой ОС.",E("USB-планшет","-device","usb-tablet")),
        T("Что уже задаёт менеджер",@"Имя VM и QMP, ускорение и CPU, RAM, ядра, основной диск/ISO, источник загрузки, видеокарта и разрешение уже формируются менеджером. Меняй их в основных полях.

Свой -qmp запрещён: менеджер проверяет имя машины перед командами управления. -accel, -cpu, -readconfig и accel= в -machine конфликтуют с профилем. -name, -m, -smp, -boot, -cdrom и повторная видеокарта способны переопределить поля или создать конфликт: не дублируй их без понимания итоговой команды.

Параметры -daemonize, -pidfile, -incoming, -loadvm, -nographic, -serial stdio и -monitor stdio не подходят обычному запуску из GUI: они могут нарушить наблюдение за процессом, изменить способ загрузки либо потребовать терминал. Для снимков используй окно снимков; для вывода — живые логи и файловые журналы.

Чтобы убрать автоматическую сеть, удали все адаптеры на вкладке «Сеть». Сохраняй с резервной копией и проверяй журнал QEMU после запуска."),
        T("NAT и несколько NIC",@"Проще настроить NAT на вкладке «Сеть»: тип user, модель, MAC, подсеть и DHCP. Для новых VM создаётся один адаптер; можно добавить ещё или удалить все. Каждому backend нужен уникальный ID. Устройство NIC подключается к нему через netdev=тот_же_ID.

Две VM с отдельными user-backend не становятся одной общей локальной сетью только потому, что используют одинаковую подсеть. Для лабораторной связи между процессами QEMU выбери TCP-сеть. Если добавляешь несколько NAT в одну VM, используй разные подсети, чтобы упростить маршрутизацию гостя.

При открытии параметров и после изменения Extra args распознанные пары -netdev/-device автоматически попадают в редактируемый список на вкладке «Сеть». Непонятные параметры остаются в Extra args с пояснением; они не удаляются. До сохранения всё можно отменить. Сетевые аргументы из справки используй либо вручную с уникальными ID, либо через форму адаптеров — не дублируй одну сеть в обоих местах.",E("Дополнительный NAT вручную","-netdev","user,id=extra_nat,net=10.40.0.0/24","-device","e1000,netdev=extra_nat")),
        T("Проброс портов hostfwd",@"В форме NAT добавляй по одному правилу в строке. Пример tcp:127.0.0.1:2222-:22 направляет TCP-подключение к localhost:2222 на SSH-порт 22 гостя. Сервис должен работать внутри гостевой ОС, а её firewall должен разрешать соединение.

127.0.0.1 ограничивает доступ текущим компьютером. Адрес 0.0.0.0 привязывает порт ко всем IPv4-интерфейсам хоста — это уже доступ из внешней сети при разрешении firewall. Порты 1–65535; один и тот же локальный порт нельзя занять двумя одинаковыми правилами или процессами. Адрес гостя после дефиса можно указать явно, иначе используется стандартный адрес QEMU.

Если получаешь bind/permission denied, проверь другой локальный порт: Windows может резервировать диапазоны даже без видимого слушающего процесса. Автовыбор QMP не меняет твои hostfwd-правила.",E("NAT с SSH и HTTP","-netdev","user,id=forwarded,hostfwd=tcp:127.0.0.1:2222-:22,hostfwd=tcp:127.0.0.1:8088-:80","-device","virtio-net-pci,netdev=forwarded")),
        T("TCP-сеть между VM",@"Одна VM слушает выбранный адрес/порт, другая подключается к нему. Сначала запускай слушающую машину. В форме адаптера используй одинаковый порт на обеих сторонах и разные ID внутри каждой VM. ID не обязан совпадать между разными процессами.

Socket-backend передаёт Ethernet-кадры, а не предоставляет NAT или DHCP. Назначь гостям адреса одной лабораторной подсети самостоятельно либо подними DHCP внутри лаборатории. Для машин на одном компьютере подходит 127.0.0.1. Для разных хостов нужны доступный адрес, firewall и доверенная сеть; сам socket-backend не добавляет шифрование.

Если подключающаяся VM завершается с ошибкой соединения, проверь, что первая уже слушает и что порт свободен. Это отдельный порт лаборатории, а не QMP.",E("Сторона, которая слушает","-netdev","socket,id=lab_a,listen=127.0.0.1:1234","-device","e1000,netdev=lab_a"),E("Подключающаяся сторона","-netdev","socket,id=lab_b,connect=127.0.0.1:1234","-device","e1000,netdev=lab_b")),
        T("TAP и ручной backend",@"TAP подключает гостя к существующему виртуальному интерфейсу хоста. На Windows нужны подходящий установленный драйвер, созданный адаптер и иногда дополнительные права. Менеджер не устанавливает драйвер и не создаёт мост Windows. Укажи имя существующего TAP в форме; IP, bridge и маршруты хоста настраиваются отдельно.

Для необычных backend выбери ручной тип и дополнительные опции. Поддержка bridge, vhost-user, multicast, stream и других вариантов зависит от ОС и сборки QEMU. Linux-примеры с /dev/net/tun, br0, vhost=on или sudo не переносить на Windows буквально.

Дополнительные опции записываются через запятую в формате ключ=значение. Не дублируй id, netdev, mac и поля, уже заполненные в форме. Запятая внутри значения экранируется удвоением. Распознанные старые TAP со сценариями не импортируются автоматически и остаются в Extra args.",E("Существующий TAP","-netdev","tap,id=tap_lab,ifname=MyTap,script=no,downscript=no","-device","e1000,netdev=tap_lab")),
        T("Модель NIC и MAC",@"virtio-net-pci — паравиртуальный сетевой адаптер; гостевой Windows обычно нужен отдельный драйвер virtio. e1000/e1000e эмулируют Intel и часто удобны при установке ОС. rtl8139 полезен для некоторых старых гостей. Поддержка модели зависит от гостя и машины QEMU.

MAC задаётся шестью шестнадцатеричными октетами через двоеточие. Новые адаптеры получают отдельный MAC; поле можно очистить для выбора QEMU. Не повторяй MAC внутри одной VM и избегай дублей в общей лабораторной сети. Менеджер не меняет MAC других VM автоматически.

Дополнительные свойства NIC, например bootindex или адрес PCI, доступны в форме адаптера. Не задавай вручную bus/addr, пока не знаешь топологию: занятый адрес PCI остановит запуск.",E("Ручной NIC с MAC","-netdev","user,id=manual_mac","-device","e1000,netdev=manual_mac,mac=52:54:00:21:43:65")),
        T("USB и указатель",@"USB-контроллер и устройство — разные сущности. usb-tablet обычно удобнее относительной USB-мыши: гостевая ОС получает абсолютные координаты. Для usb-kbd нужен поддерживаемый контроллер USB. Не добавляй второй контроллер с уже занятым ID.

Проброс настоящего USB-устройства зависит от Windows, драйверов, сборки и доступа к устройству. Пример с виртуальным планшетом не пробрасывает физическую мышь. Если QEMU сообщает, что шина usb.0 отсутствует, сначала добавь виртуальный контроллер.",E("Контроллер xHCI и планшет","-device","qemu-xhci,id=usb_extra","-device","usb-tablet,bus=usb_extra.0")),
        T("Звук",@"Аудиоконтроллер виден гостевой ОС; audiodev выбирает backend вывода на хосте. Возможные backend зависят от сборки. Для обычной Windows-среды сначала проверь доступные варианты через справку самой QEMU, а не копируй Linux-специфичный alsa/pa.

intel-hda вместе с hda-duplex — распространённая комбинация контроллера и кодека. В госте нужен драйвер. Существующий звук в Extra args сначала проверь: повторный контроллер или кодек не заменяет старый автоматически.",E("Виртуальный HDA-контроллер","-device","intel-hda,id=hda_extra","-device","hda-duplex,bus=hda_extra.0")),
        T("Экран и буфер обмена",@"Модель видео и разрешение настраиваются в основных параметрах. -display выбирает окно хоста, а не драйвер видеокарты гостя. Наличие gtk/sdl, OpenGL и clipboard зависит от сборки QEMU. Если добавляешь -display в Extra args, проверь, что твоя сборка принимает значение; не добавляй второй -vga.

Буфер обмена через qemu-vdagent требует устройства virtio-serial и работающего агента в гостевой системе. Не все режимы экрана поддерживают одинаковые функции. Изменение аргументов само по себе агент в гостя не устанавливает. Поддержка 3D также зависит от backend, драйвера и гостя.",E("Окно SDL без OpenGL","-display","sdl,gl=off")),
        T("Serial и текстовый вывод",@"Живые логи показывают stdout/stderr QEMU и события менеджера. Чтобы увидеть текст гостевого ядра через serial, настрой соответствующую консоль внутри гостя и направь виртуальный serial в файл. Без настройки гостя файл может оставаться пустым.

stdio не является удобным выбором в GUI: отдельного терминала нет. Используй файл и заранее существующую папку. Путь из примера замени своим. Serial-консоль может содержать команды и сведения из гостевой ОС; выбирай место хранения осознанно.",E("Serial в файл","-serial",@"file:C:\VM Logs\serial.log")),
        T("Диагностика QEMU",@"-D задаёт файл диагностического журнала, а -d выбирает категории. Это другой поток по отношению к обычным живым логам. Некоторые режимы, например in_asm или cpu, создают очень большой объём данных и замедляют эмуляцию — включай их для конкретной проверки.

guest_errors и unimp помогают искать неподдержанные обращения гостя. Доступные категории определяются сборкой. Папка для файла должна существовать; используй отдельный журнал каждой VM, чтобы не смешивать записи. -D не перенаправляет экран гостя в текст.",E("Журнал ошибок гостя","-d","guest_errors,unimp","-D",@"C:\VM Logs\debug.log")),
        T("Дополнительные диски",@"Основной диск и ISO задаются в полях менеджера. Для второго диска можно использовать -drive с явным format=qcow2/raw и отдельным ID. Укажи правильный формат существующего файла. При записи один образ не должен одновременно открываться несколькими VM.

Менеджер учитывает распознаваемые дополнительные носители в проверках снимков и удаления; их shared/backing-зависимости проверяются прежней логикой. Не добавляй один и тот же основной диск ещё раз. Снимки дисков не включают RAM. Параметр readonly=on предназначен для подключения существующего образа только для чтения.

Внутри значения -drive запятые разделяют опции, поэтому запятую в имени файла нужно удвоить. Двойные кавычки вокруг всего аргумента сохраняют пробелы при разборе Windows, но сами по себе не экранируют запятую QEMU.",E("Второй диск только для чтения","-drive",@"file=C:\VM Disks\reference.qcow2,format=qcow2,if=virtio,readonly=on")),
        T("UEFI и прошивки",@"Некоторые гостевые ОС требуют UEFI/OVMF и отдельного файла переменных NVRAM. Эти файлы зависят от установленной сборки и архитектуры. Нельзя подставить произвольный ISO вместо прошивки.

Файл кода прошивки обычно подключают только для чтения, а файл переменных делают отдельным для каждой VM. Не используй одну записываемую NVRAM несколькими машинами. Изменение схемы BIOS/UEFI может сделать уже установленную ОС незагружаемой: сначала проверь её текущий режим и резервные копии.

Параметры -bios и -drive if=pflash доступны через Extra args; конкретные пути, формат и совместимость нужно сверять с твоей сборкой. Готового примера с вымышленными файлами здесь нет."),
        T("Память, CPU и профиль",@"Количество RAM и ядер меняй в основных полях. WHPX/Авто/TCG управляют -accel и моделью CPU. Перед реальной загрузкой менеджер проверяет выбранный ускоритель на отдельном процессе без твоих дисков.

NUMA, huge pages, hotplug RAM, -object memory-backend-* и нестандартные machine-параметры требуют согласованной конфигурации. Linux-флаги KVM и /dev/kvm не подходят WHPX в Windows. Справка описывает возможности QEMU, а не гарантирует, что любая комбинация работает с текущим ускорением.

Если нужно изменить поддерживаемую модель CPU, сначала расширь профиль осознанно; простая вставка -cpu в Extra args блокируется, чтобы два источника не задавали противоречащие значения."),
        T("Общие каталоги и сервисы",@"NAT с hostfwd удобен для доступа к SSH/HTTP гостя. Это не общий каталог, а сетевой сервис: сервер и права доступа настраиваются в самой гостевой ОС.

Примеры -virtfs, 9p, virtio-fs и SMB из Linux-документации могут требовать отсутствующих backend, отдельного демона или Samba на хосте. Менеджер не устанавливает эти службы. Начинай с сервисов гостевой ОС и проверенного перенаправления localhost.

Если подключаешь каталог хоста через поддерживаемый backend, учитывай, что guest получает доступ к указанным данным. Выбирай отдельную папку для лаборатории и режим чтения, когда он подходит задаче."),
        T("Порядок, ID и ошибки",@"ID связывают backend и устройства: netdev, audiodev, chardev, drive, bus. Имя должно быть уникальным среди объектов соответствующего типа. Порядок некоторых определений важен: обычно сначала backend/контроллер, затем использующее его устройство.

Ошибка «Duplicate ID» — ищи повтор в форме сети и Extra args. «No bus found» — не хватает контроллера или неверное имя шины. «Property not found» — опция не поддерживается выбранной моделью. «Could not open» — проверь путь, права и использование файла. «Failed to bind» — проверь порт и адрес хоста.

Если новый аргумент мешает старту, открой параметры, убери последнее изменение и сохрани. Перед сохранением менеджер делает копию конфигурации. Отмена формы ничего не записывает. При работе с незнакомой опцией используй временную VM или копию диска; не пытайся исправить занятый диск удалением его файлов."),
        T("Ссылки и версии",@"Эта справка встроена и работает без интернета. Кнопка документации открывает первичный источник QEMU в браузере только по твоему нажатию.

QEMU: https://www.qemu.org/docs/master/system/invocation.html
Сеть: https://www.qemu.org/docs/master/system/devices/net.html
Правила кавычек Windows: https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-commandlinetoargvw

Состав параметров проверен по локальной справке QEMU 11.1.1. Документация master может описывать более новую версию. Установка менеджера использует существующий QEMU либо закреплённую сборку; автоматического обновления зависимостей нет. Если пример не принимается, смотри журнал, версию QEMU и доступные устройства/backend.")
    };}
}
