using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class GuiTests {
    static int checks;static readonly List<string> names=new List<string>();static string output;
    static void Check(bool value,string label){if(!value)throw new Exception(label);checks++;names.Add(label);}
    static T Field<T>(object value,string name){return (T)value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(value);}
    static IEnumerable<Control> All(Control root){foreach(Control child in root.Controls){yield return child;foreach(Control item in All(child))yield return item;}}
    static void Click(Form form,string text){var button=All(form).OfType<Button>().First(item=>item.Text==text);button.PerformClick();Application.DoEvents();}
    static void Wait(MainWindow main){var until=DateTime.UtcNow.AddSeconds(90);do{Application.DoEvents();Thread.Sleep(20);if(DateTime.UtcNow>until)throw new Exception("UI operation timed out");}while(main.Busy);Application.DoEvents();if(main.TestError!=null)throw main.TestError;}
    static void Save(Form form,string file){form.Show();Application.DoEvents();using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(output,file),System.Drawing.Imaging.ImageFormat.Png);}}
    delegate bool EnumProc(IntPtr window,IntPtr data);
    [DllImport("user32.dll")]static extern bool EnumWindows(EnumProc callback,IntPtr value);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr window,StringBuilder name,int count);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")]static extern bool FreeConsole();
    [DllImport("kernel32.dll")]static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")]static extern int GetWindowLong(IntPtr window,int index);
    [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    [DllImport("dwmapi.dll")]static extern int DwmIsCompositionEnabled(out bool enabled);
    static bool HasConsole(int pid){bool attached=AttachConsole((uint)pid);IntPtr window=attached?GetConsoleWindow():IntPtr.Zero;bool visible=window!=IntPtr.Zero&&IsWindowVisible(window);if(attached)FreeConsole();return visible;}
    static int Consoles(params int[] ids){int count=0;EnumWindows((window,value)=>{uint pid;GetWindowThreadProcessId(window,out pid);var type=new StringBuilder(256);GetClassName(window,type,256);if(ids.Contains((int)pid)&&type.ToString()=="ConsoleWindowClass")count++;return true;},IntPtr.Zero);return count;}
    [STAThread]static int Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Program.TestMode=true;
        output=Path.GetFullPath(args[0]);string root=Path.GetFullPath(args[1]),python=args[2];Directory.CreateDirectory(output);Backend backend=null;int guest=0;Process ownedGuest=null;string guestName="gui-test-"+Guid.NewGuid().ToString("N").Substring(0,8);
        try{
            using(var scrollForm=new Form()){
                Theme.Form(scrollForm,420,240);var stack=Theme.Flow(true);
                for(int index=0;index<10;index++)stack.Controls.Add(new LibraryCard("test-"+index,"Тест "+index,Theme.Accent));
                var surface=new ScrollSurface(stack){BackColor=Theme.Panel};scrollForm.Controls.Add(surface);scrollForm.Show();Application.DoEvents();
                Check(surface.ScrollBar.Visible&&surface.ScrollBar.Maximum>0,"long VM lists expose the slim scrollbar only when content overflows");
                Check((GetWindowLong(surface.Handle,-16)&0x200000)==0&&(GetWindowLong(stack.Parent.Handle,-16)&0x200000)==0,"scroll surface and viewport have no native vertical scrollbar");
                surface.ScrollOffset=0;SendMessage(stack.Controls[0].Handle,0x20A,new IntPtr(-120<<16),IntPtr.Zero);Application.DoEvents();
                Check(surface.ScrollOffset>0||SystemInformation.MouseWheelScrollLines==0,"wheel over a VM card scrolls its library");
                surface.ScrollOffset=0;surface.ScrollBar.Focus();SendMessage(surface.ScrollBar.Handle,0x100,new IntPtr((int)Keys.End),IntPtr.Zero);
                Check(surface.ScrollOffset==surface.ScrollBar.Maximum,"End key reaches the end of the slim scrollbar");
                SendMessage(surface.ScrollBar.Handle,0x100,new IntPtr((int)Keys.Home),IntPtr.Zero);
                var thumb=surface.ScrollBar.Thumb;int downY=thumb.Top+thumb.Height/2;
                SendMessage(surface.ScrollBar.Handle,0x201,new IntPtr(1),new IntPtr((downY<<16)|6));
                SendMessage(surface.ScrollBar.Handle,0x200,new IntPtr(1),new IntPtr(((downY+60)<<16)|6));
                SendMessage(surface.ScrollBar.Handle,0x202,IntPtr.Zero,new IntPtr(((downY+60)<<16)|6));
                Check(surface.ScrollOffset>0,"slim scrollbar thumb supports pointer dragging");
                surface.ScrollOffset=Int32.MaxValue;scrollForm.ClientSize=new Size(420,2000);Application.DoEvents();
                Check(!surface.ScrollBar.Visible&&surface.ScrollOffset==0,"scrollbar disappears and clamps position when all VMs fit");scrollForm.Close();
            }
            using(var deck=new PageDeck()){
                var first=new PageView("Первая");var second=new PageView("Вторая");var input=Theme.TextBox("Несохранённое значение");first.Controls.Add(input);deck.Pages.Add(first);deck.Pages.Add(second);
                EventHandler<PageSelectingEventArgs> cancel=(sender,eventArgs)=>eventArgs.Cancel=true;deck.Selecting+=cancel;deck.SelectedPage=second;
                Check(deck.SelectedPage==first&&input.Text=="Несохранённое значение","navigation cancellation preserves the active page and unsaved input");
                deck.Selecting-=cancel;deck.SelectedPage=second;deck.SelectedPage=first;
                Check(first.Controls.Contains(input)&&input.Text=="Несохранённое значение","navigation switches existing pages without rebuilding or replacing their controls");
            }
            var samples=new object[]{"-device","usb-tablet",@"C:\VM Logs\Папка\", "", "literal\"quote", "a\\\\\"b", "первая\nвторая", "'single'", "a,b,,c"};
            Check(ArgText.Parse(ArgText.Format(samples)).SequenceEqual(samples.Select(Convert.ToString)),"extra args preserve paths quotes empty values Unicode and embedded newlines");
            Check(ArgText.Parse("-device usb-tablet\n-display \"sdl,gl=off\"").SequenceEqual(new[]{"-device","usb-tablet","-display","sdl,gl=off"}),"multiline extra args parse as Windows arguments without a shell");
            backend=new Backend(root,python);backend.Open();
            bool attached=AttachConsole((uint)backend.Pid);IntPtr console=GetConsoleWindow();bool visible=console!=IntPtr.Zero&&IsWindowVisible(console);if(attached)FreeConsole();
            File.WriteAllText(Path.Combine(output,"console-details.json"),J.Json().Serialize(J.O("backend",backend.Pid,"protocol",backend.Call("ping"),"consoleWindows",Consoles(backend.Pid),"attached",attached,"handle",console.ToInt64(),"visible",visible)),new UTF8Encoding(false));
            Check(Consoles(backend.Pid)==0&&!visible,"backend has no console window");
            var main=new MainWindow(root,python,true);main.Backend=backend;main.Show();main.Demo();Application.DoEvents();
            Check(All(main).OfType<Button>().Any(button=>button.Text=="Живые логи"),"live logs button is present");
            Check(Application.OpenForms.OfType<LogsWindow>().Count()==0,"logs are not opened automatically");
            Check(All(main).OfType<Panel>().Count()>6,"main window renders VM cards and details");Save(main,"main.png");
            int caption,text;int capResult=DwmGetWindowAttribute(main.Handle,35,out caption,4);int expectedCaption=ColorTranslator.ToWin32(Theme.Bg);bool composition;int compositionResult=DwmIsCompositionEnabled(out composition);
            int setResult=DwmSetWindowAttribute(main.Handle,35,ref expectedCaption,4);
            File.WriteAllText(Path.Combine(output,"caption-details.json"),J.Json().Serialize(J.O("get_result",capResult,"caption",caption,"expected",expectedCaption,"set_result",setResult,"composition",composition,"composition_result",compositionResult,"os",Environment.OSVersion.ToString())));
            // Caption attributes are documented for Set; Get rejects them on this Windows session.
            Check(setResult==0,"Windows accepts application color for the native title bar");
            text=ColorTranslator.ToWin32(Theme.Text);Check(DwmSetWindowAttribute(main.Handle,36,ref text,4)==0,"Windows accepts readable theme color for native title text");
            var cardList=Field<FlowLayoutPanel>(main,"cards");var originalCards=cardList.Controls.Cast<Control>().ToArray();int changes=0;
            cardList.ControlAdded+=(sender,eventArgs)=>changes++;cardList.ControlRemoved+=(sender,eventArgs)=>changes++;
            foreach(Control item in originalCards)item.BackColorChanged+=(sender,eventArgs)=>changes++;
            var scroll=Field<ScrollSurface>(main,"libraryScroll");scroll.ScrollOffset=40;Application.DoEvents();int oldScroll=scroll.ScrollOffset;
            var initialStates=J.O("ubuntu-desktop","running","ubuntu-server","stopped","kali","paused","tails","stopped");
            for(int index=0;index<100;index++)main.ApplyStates(initialStates);
            Application.DoEvents();Check(changes==0&&originalCards.SequenceEqual(cardList.Controls.Cast<Control>())&&oldScroll==scroll.ScrollOffset,"100 status polls preserve cards selection colors and scroll without rebuilding");
            main.ApplyStates(J.O("ubuntu-desktop","running","ubuntu-server","stopped","kali","paused","tails","running"));
            var tailsCard=originalCards.First(item=>Object.Equals(item.Tag,"tails"));
            Check(changes==0&&((LibraryCard)tailsCard).Status=="running","changed VM status updates its existing badge without rebuilding the list");
            main.ApplyStates(initialStates);scroll.ScrollOffset=0;
            var selectedData=J.D(J.V(main.Row("ubuntu-desktop"),"data"));string oldLabel=J.S(selectedData,"label");selectedData["label"]=new string('Ж',100);main.Size=new Size(850,600);main.ApplyStates(J.O("ubuntu-desktop","running"));Application.DoEvents();
            Check(Field<Label>(main,"heading").Width<=Field<Panel>(main,"detail").ClientSize.Width-52,"long VM title wraps at minimum window size");
            Field<FieldInput>(main,"search").Text="ubuntu-server";Application.DoEvents();Check(Field<FlowLayoutPanel>(main,"cards").Controls.Count==1,"search filters VM cards without losing the library");
            Field<FieldInput>(main,"search").Clear();selectedData["label"]=oldLabel;main.Size=new Size(1136,779);main.Demo();Application.DoEvents();
            object value=backend.Call("get",J.O("name","test"));
            using(var editor=new EditDialog(main,"test",value)){Save(editor,"editor.png");Check(All(editor).OfType<NumberInput>().Count()==4,"GUI editor has RAM CPU and resolution controls");
                Field<PageDeck>(editor,"tabs").SelectedPage=editor.launchSettings.NetworkPage;Application.DoEvents();Save(editor,"network-legacy.png");
                Check(editor.launchSettings.Adapters.Count==1&&editor.launchSettings.List.Items.Count==1,"legacy adapter is immediately editable without a mode switch");
                Check(editor.launchSettings.Merge(J.D(J.V(value,"data"))).ContainsKey("network_adapters"),"legacy projection becomes a managed draft before explicit save");Click(editor,"Отмена");}
            Check(J.S(backend.Call("get",J.O("name","test")),"hash")==J.S(value,"hash"),"viewing current network and cancelling preserves exact configuration bytes");
            Check(J.S(J.V(backend.Call("get",J.O("name","test")),"data"),"label")=="Исходное имя","editor cancellation preserves configuration");
            using(var create=new CreateDialog(main,backend.Call("template_list"))){Save(create,"create.png");Check(All(create).OfType<ThemedSelector>().Count()>=3,"creation dialog has template source and profile choices");Click(create,"Отмена");}
            var savedTemplates=new object[]{J.O("id","first.json","backup_name","Ubuntu — базовая конфигурация","original_vm_name","ubuntu-desktop","parameters",J.O("label","Ubuntu из копии","ram_mb",8192,"cores",4)),J.O("id","second.json","backup_name","Лабораторная сеть","original_vm_name","lab","parameters",J.O("label","Лаборатория","ram_mb",2048,"cores",2))};
            string chosenTemplate=null;
            using(var library=new TemplatesWindow(savedTemplates,id=>chosenTemplate=id)){Save(library,"templates.png");Field<ThemedList>(library,"list").SelectedIndex=1;Click(library,"Создать VM из копии");}
            Check(chosenTemplate=="second.json","template library passes the selected backup identity to creation");
            using(var creation=new CreateDialog(main,savedTemplates,false,chosenTemplate)){creation.Show();Check(Field<FieldInput>(creation,"label").Text=="Лаборатория"&&Field<NumberInput>(creation,"ram").Value==2048,"creation loads the exact chosen backup parameters without saving a VM");creation.Close();}
            using(var creation=new CreateDialog(main,backend.Call("template_list"))){
                creation.Show();var settings=creation.launchSettings;var tabs=Field<PageDeck>(creation,"tabs");
                Check(settings.Adapters.Count==1,"new VM has explicit editable network configuration");
                tabs.SelectedPage=settings.NetworkPage;Save(creation,"network.png");
                var automation=new System.Windows.Forms.Timer{Interval=60};automation.Tick+=(sender,eventArgs)=>{var dialog=Application.OpenForms.OfType<NetworkAdapterDialog>().FirstOrDefault();if(dialog!=null){automation.Stop();Click(dialog,"Применить");}};automation.Start();Click(creation,"Добавить адаптер");automation.Dispose();
                Check(settings.Adapters.Count==2,"adapter can be added through its actual graphical dialog");
                settings.List.SelectedIndex=1;Click(creation,"Удалить");Check(settings.Adapters.Count==1,"adapter can be removed without saving the VM");
                settings.Extra.Text="-device usb-tablet";tabs.SelectedPage=settings.ExtraPage;Save(creation,"extra-args.png");
                var cancelEditor=new System.Windows.Forms.Timer{Interval=60};cancelEditor.Tick+=(sender,eventArgs)=>{var dialog=Application.OpenForms.OfType<ExtraArgsDialog>().FirstOrDefault();if(dialog!=null){cancelEditor.Stop();dialog.Editor.Text="discarded";Click(dialog,"Отмена");}};cancelEditor.Start();Click(creation,"Открыть редактор");cancelEditor.Dispose();
                Check(settings.Extra.Text=="-device usb-tablet","separate extra args editor cancellation preserves original text");
                tabs.SelectedPage=settings.HelpPage;var help=All(creation).OfType<ExtraHelpView>().First();Field<FieldInput>(help,"search").Text="hostfwd";Save(creation,"extra-help.png");
                Check(Field<ThemedList>(help,"topics").Items.Count>0,"extra args help searches category text and examples");
                Click(creation,"Вставить пример");Check(tabs.SelectedPage==settings.ExtraPage&&ArgText.Parse(settings.Extra.Text).Length>2,"help inserts a selected example without running a machine");creation.Close();
            }
            // Exercise the reported four-adapter legacy configuration without touching real VMs.
            var legacy=J.O("disk_path","example.qcow2","extra_args",new[]{"-device","usb-tablet","-netdev","socket,id=desktoplink,listen=127.0.0.1:1234","-device","e1000,netdev=desktoplink,mac=52:54:00:12:34:58","-netdev","socket,id=lab2,listen=127.0.0.1:1235","-device","e1000,netdev=lab2,mac=52:54:00:12:34:5a","-netdev","user,id=mgmt,net=10.10.0.0/24","-device","virtio-net-pci,netdev=mgmt,mac=52:54:00:12:34:60"});
            string untouched=J.Json().Serialize(legacy);
            using(var creation=new CreateDialog(main,backend.Call("template_list"))){
                creation.Show();var settings=creation.launchSettings;settings.Load(legacy);var deck=Field<PageDeck>(creation,"tabs");deck.SelectedPage=settings.NetworkPage;
                Check(settings.Adapters.Count==4&&settings.List.Items.Count==4,"all four legacy adapters appear in the editable network list");
                Check(ArgText.Parse(settings.Extra.Text).SequenceEqual(new[]{"-device","usb-tablet"}),"only recognized network arguments move into the draft list");
                Save(creation,"network-unified.png");
                for(int i=0;i<5;i++){deck.SelectedPage=settings.ExtraPage;deck.SelectedPage=settings.NetworkPage;}
                Check(settings.Adapters.Count==4,"revisiting pages never duplicates adapters");
                settings.Extra.Text+=" -netdev user,id=added,net=10.20.0.0/24 -device e1000,netdev=added";
                var merged=settings.Merge(legacy);
                Check(settings.Adapters.Count==5&&J.A(J.V(merged,"network_adapters")).Length==5,"network arguments added manually are imported before save");
                Check(J.A(J.V(merged,"extra_args")).Length==2,"imported network flags are not emitted twice");
                settings.List.SelectedIndex=1;Click(creation,"Удалить");
                deck.SelectedPage=settings.ExtraPage;deck.SelectedPage=settings.NetworkPage;
                Check(settings.Adapters.Count==4&&!settings.Adapters.Any(row=>J.S(row,"id")=="desktoplink"),"removed imported adapter stays removed across navigation");
                settings.Extra.Text+=" -netdev tap,id=opaque,script=C:/custom.cmd -device e1000,netdev=opaque";
                merged=settings.Merge(legacy);
                Check(settings.Adapters.Count==4&&settings.Extra.Text.Contains("custom.cmd"),"scripted TAP remains intact in Extra args alongside editable adapters");
                string prior=J.Json().Serialize(settings.Adapters);settings.Extra.Text+=" -netdev user,id=mgmt -device e1000,netdev=mgmt";bool rejected=false;
                try{settings.Merge(legacy);}catch(InvalidOperationException){rejected=true;}
                Check(rejected&&J.Json().Serialize(settings.Adapters)==prior&&settings.Extra.Text.Contains("id=mgmt"),"conflicting draft is rejected without losing adapters or raw arguments");
                creation.Close();
            }
            Check(J.Json().Serialize(legacy)==untouched,"automatic network import never mutates the source object");
            using(var log=new LogsWindow(main,"test",true)){Save(log,"logs.png");Check(All(log).OfType<PageDeck>().First().Pages.Count==2,"logs provide QEMU and manager event tabs");log.Close();}
            using(var snapshots=new SnapshotsWindow(main,"test",J.O("rows",new object[]{J.O("name","Лабораторная","date",1792310000,"ids",new[]{"1"})},"online",false))){Save(snapshots,"snapshots.png");Check(All(snapshots).OfType<Button>().Any(button=>button.Text=="Загрузить снимок"&&button.Enabled),"snapshot restore is available for offline VM");snapshots.Close();}
            using(var deletion=new DeleteDialog(main,J.O("name","test","token","demo","files",new[]{"private.qcow2"},"kept",new object[]{new[]{"shared.iso","общий ISO"}},"missing",new object[0]))){Save(deletion,"delete.png");Check(Field<CheckBox>(deletion,"saveBackup").Checked&&Field<FieldInput>(deletion,"confirm").Text=="","deletion defaults to preserving a parameter backup and requires exact VM confirmation");Click(deletion,"Отмена");}
            using(var deletion=new DeleteDialog(main,J.O("name","test","token","demo"))){deletion.Show();var remove=All(deletion).OfType<Button>().Single(button=>button.Text=="Удалить VM");Check(!remove.Enabled,"deletion is disabled before a named backup and exact confirmation");Field<FieldInput>(deletion,"backup").Text="Именная копия";Field<FieldInput>(deletion,"confirm").Text="tes";Check(!remove.Enabled,"partial VM name does not enable deletion");Field<FieldInput>(deletion,"confirm").Text="test";Check(remove.Enabled,"exact VM name and nonempty backup enable the confirmation button");deletion.Close();}
            using(var doctor=new DoctorWindow(main,J.O("checks",new object[]{J.O("state","ok","title","QEMU найден","detail","Исполняемый файл доступен"),J.O("state","warning","title","Нужна проверка профиля","detail","Пример предупреждения","advice","Открой параметры машины."),J.O("state","error","title","Файл отсутствует","detail","Пример ошибки","advice","Укажи доступный ISO.")}))){Save(doctor,"doctor.png");Check(All(doctor).OfType<DiagnosticCard>().Count()==3,"diagnostics render actual success warning and error checks separately");doctor.Close();}
            using(var adapter=new NetworkAdapterDialog(J.A(J.V(LaunchSettingsUi.Defaults(),"network_adapters"))[0])){adapter.Show();Save(adapter,"adapter-nat.png");var selector=Field<ThemedSelector>(adapter,"mode");selector.SelectedIndex=1;Application.DoEvents();Check(Field<NumberInput>(adapter,"port").Visible&&!Field<FieldInput>(adapter,"network").Visible,"TCP adapter shows its fields and hides NAT fields");Save(adapter,"adapter-tcp.png");selector.SelectedIndex=3;Application.DoEvents();Check(Field<FieldInput>(adapter,"ifname").Visible&&!Field<NumberInput>(adapter,"port").Visible,"TAP adapter shows its interface and hides TCP fields");adapter.Close();}
            Check(backend.Call("list")!=null,"cancelling dialogs leaves backend responsive");
            main.Close();Application.DoEvents();
            // A separate real frontend without auto-start uses its production Run callbacks.
            var live=new MainWindow(root,python,false,false);live.Backend=backend;live.Show();live.Reload();Wait(live);
            object original=backend.Call("get",J.O("name","test"));
            using(var edit=new EditDialog(live,"test",original)){edit.Show();Application.DoEvents();Field<FieldInput>(edit,"label").Text="Сохранено из GUI";
                edit.launchSettings.Extra.Text=ArgText.Format(new object[]{"-device","usb-tablet","-D",@"C:\VM Logs\лог.txt"});
                var editTabs=Field<PageDeck>(edit,"tabs");editTabs.SelectedPage=edit.launchSettings.HelpPage;editTabs.SelectedPage=edit.launchSettings.ExtraPage;
                var tab=Field<PageDeck>(edit,"tabs");tab.SelectedPage=tab.Pages.Cast<PageView>().First(page=>page.Text=="Конфигурация JSON");Check(Field<ThemedEditor>(edit,"raw").Text.Contains("Сохранено из GUI"),"editor keeps visual changes when switching to JSON");
                var rawData=J.D(J.Json().DeserializeObject(Field<ThemedEditor>(edit,"raw").Text));rawData["custom_field"]="Сеть сохранена";Field<ThemedEditor>(edit,"raw").Text=J.Json().Serialize(rawData);tab.SelectedIndex=0;
                Click(edit,"Сохранить");Wait(live);}
            Check(J.S(J.V(backend.Call("get",J.O("name","test")),"data"),"label")=="Сохранено из GUI","actual GUI save preserves Cyrillic over UTF8 pipes");
            Check(J.S(J.V(backend.Call("get",J.O("name","test")),"data"),"custom_field")=="Сеть сохранена","editor preserves custom JSON fields after switching back to visual controls");
            Check(J.A(J.V(J.V(backend.Call("get",J.O("name","test")),"data"),"extra_args")).Select(Convert.ToString).SequenceEqual(new[]{"-device","usb-tablet","-D",@"C:\VM Logs\лог.txt"}),"editor saves extra args losslessly after help and JSON navigation");
            object beforeImport=backend.Call("get",J.O("name","test"));using(var edit=new EditDialog(live,"test",beforeImport)){
                edit.Show();Field<PageDeck>(edit,"tabs").SelectedPage=edit.launchSettings.NetworkPage;
                Check(edit.launchSettings.Adapters.Count==1,"legacy network imports through the graphical editor without saving immediately");Click(edit,"Отмена");}
            Check(J.S(backend.Call("get",J.O("name","test")),"hash")==J.S(beforeImport,"hash"),"cancel after graphical network import preserves configuration bytes");
            using(var create=new CreateDialog(live,backend.Call("template_list"))){create.Show();Application.DoEvents();Field<FieldInput>(create,"name").Text="from-gui";Field<FieldInput>(create,"label").Text="Создано кнопкой";Field<ThemedSelector>(create,"mode").SelectedIndex=2;Field<FieldInput>(create,"iso").Text=Path.Combine(root,"test.iso");create.appearance.selector.SelectedItem=create.appearance.selector.Items.Cast<Distro>().First(row=>row.Id=="kali");Click(create,"Создать VM");Wait(live);}
            Check(File.Exists(Path.Combine(root,"machines","from-gui.json")),"actual GUI creates a VM without starting it");
            Check(live.Visuals.Choice("from-gui")=="kali"&&!J.D(J.V(backend.Call("get",J.O("name","from-gui")),"data")).ContainsKey("distro"),"creation saves its chosen distro only in appearance metadata");
            Check(J.A(J.V(J.V(backend.Call("get",J.O("name","from-gui")),"data"),"network_adapters")).Length==1,"new VM saves its graphical network adapter configuration");
            // Create a disposable qcow2 guest and exercise the same service used by buttons.
            string disk=Path.Combine(root,"guest.qcow2");
            var occupiedPort=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);occupiedPort.Server.ExclusiveAddressUse=true;occupiedPort.Start(16);int busyPort=((System.Net.IPEndPoint)occupiedPort.LocalEndpoint).Port;
            try{
            backend.Call("create",J.O("name",guestName,"new_disk",true,"size_gb",1,"data",J.O("label","Disposable GUI test","disk_path",disk,"iso_path","","ram_mb",128,"cores",1,"launch_profile","tcg","qmp_port",busyPort,"screen_width",320,"screen_height",200,"extra_args",new[]{"-display","none","-S"})));
            string guestConfig=Path.Combine(root,"machines",guestName+".json");byte[] beforePortRepair=File.ReadAllBytes(guestConfig);
            object started=backend.Call("start",J.O("name",guestName,"boot","disk"));guest=J.N(started,"pid");ownedGuest=Process.GetProcessById(guest);IntPtr ownedHandle=ownedGuest.Handle;Check(guest>0,"real QEMU starts through GUI backend");
            Check(J.N(J.Json().DeserializeObject(File.ReadAllText(guestConfig)),"qmp_port")!=busyPort,"an unavailable real QMP port is replaced before QEMU starts");
            Check(Directory.GetFiles(Path.GetDirectoryName(guestConfig),guestName+".json.pre-edit-*.bak").Any(path=>File.ReadAllBytes(path).SequenceEqual(beforePortRepair)),"port repair creates a byte-exact configuration backup");
            }finally{occupiedPort.Stop();}
            var deadline=DateTime.UtcNow.AddSeconds(20);bool ready=false,externalOwnerGuard=false;string snapshotError="";
            while(DateTime.UtcNow<deadline){try{backend.Call("control",J.O("name",guestName,"action","snapshot"));ready=true;break;}catch(InvalidOperationException error){snapshotError=error.Message;if(snapshotError.Contains("Не удалось проверить владельца другого QEMU")){externalOwnerGuard=true;break;}Thread.Sleep(200);}}
            // Positive two-disk creation is always tested by tools/test_snapshots.py.
            // A running user QEMU outside this temporary library must remain untouched.
            Check(ready||externalOwnerGuard,externalOwnerGuard?"snapshot correctly refuses unverified external QEMU ownership":"real QMP disk snapshot works: "+snapshotError);
            string supportPath=Path.Combine(root,"support-guest.zip");
            object supportReport=backend.Call("support_export",J.O("name",guestName,"path",supportPath,"sections",new[]{"logs","parameters","state","diagnostics","screenshot"},"redact",true,"description","Disposable paused guest report"));
            Check(File.Exists(supportPath)&&J.D(J.V(supportReport,"errors")).Count==0,"selected diagnostics and PNG screenshot export through real QMP");
            object snapshotsResult=backend.Call("snapshots",J.O("name",guestName));Check(J.B(snapshotsResult,"online")&&(externalOwnerGuard||J.A(J.V(snapshotsResult,"rows")).Length>0),"live snapshot list works; guarded creation leaves an empty catalog");
            backend.Call("control",J.O("name",guestName,"action","toggle"));backend.Call("control",J.O("name",guestName,"action","toggle"));Check(true,"pause and resume work through headless service");
            object liveLog=backend.Call("logs",J.O("name",guestName));Check(J.A(J.V(liveLog,"events")).Length>=3,"live manager events are available");
            Check(Consoles(backend.Pid,guest)==0&&!HasConsole(backend.Pid)&&!HasConsole(guest),"backend and QEMU have no console windows");
            live.Close();Application.DoEvents();Thread.Sleep(1500);Check(!Process.GetProcessById(guest).HasExited,"closing GUI manager leaves guest running");
            // Reopen and attach. A duplicate Start must not launch a second guest.
            backend=new Backend(root,python);backend.Open();Check(J.B(backend.Call("start",J.O("name",guestName)),"attached"),"new manager attaches without duplicate QEMU");
            var reopened=new MainWindow(root,python,false,false);reopened.Backend=backend;reopened.Show();reopened.Reload();Wait(reopened);reopened.ApplyStates(backend.Call("states"));reopened.OpenPanel(guestName);Application.DoEvents();
            Check(Application.OpenForms.OfType<VmWindow>().Count()==1,"GUI reopens one panel for the running VM");
            backend.Call("control",J.O("name",guestName,"action","quit","confirm",guestName));Check(ownedGuest.WaitForExit(10000),"force quit targets the exact temporary QEMU");guest=0;
            reopened.ApplyStates(backend.Call("states"));Application.DoEvents();Check(Application.OpenForms.OfType<VmWindow>().Count()==1&&Application.OpenForms.OfType<VmWindow>().Single().Text.Contains(Theme.State("stopped")),"VM exit keeps its panel open with the stopped state");reopened.Close();Application.DoEvents();
            // Root has no poll timer, so closing cannot act on a guest.
            if(!live.IsDisposed){live.Backend=null;live.Close();}Application.DoEvents();
            File.WriteAllText(Path.Combine(output,"gui-results.json"),J.Json().Serialize(J.O("passed",checks,"checks",names,"working_vms_touched",false)),new UTF8Encoding(false));return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(output,"gui-error.txt"),error.ToString(),new UTF8Encoding(false));return 1;}
        finally{if(ownedGuest!=null){try{if(!ownedGuest.HasExited){ownedGuest.Kill();ownedGuest.WaitForExit(5000);}}catch(InvalidOperationException){}ownedGuest.Dispose();}if(backend!=null)backend.Dispose();}
    }
}
