using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

static class AlphaIntegration {
    public const string Version="0.1.0-alpha.4";
    public const string Issues="https://github.com/ivan22822/QemuM/issues/new/choose";
    public static string AppRoot(string fallback) { return Environment.GetEnvironmentVariable("QEMUM_APP_DIR") ?? fallback; }
    public static void OpenIssues() { Process.Start(new ProcessStartInfo(Issues){UseShellExecute=true}); }
    public static void Updates(IWin32Window owner) {
        string root=Environment.GetEnvironmentVariable("QEMUM_INSTALL_DIR");
        string launcher=root==null?null:Path.Combine(root,"QemuM.exe");
        if(launcher==null||!File.Exists(launcher)){
            MessageBox.Show(owner,"Это сборка из исходников. Обновления доступны в установленной альфа-версии.\nВерсия: "+Version,"Обновления");return;
        }
        string bundled=Path.Combine(AppRoot(AppDomain.CurrentDomain.BaseDirectory),"QemuM.Update.exe");
        Process.Start(new ProcessStartInfo(File.Exists(bundled)?bundled:launcher,
            File.Exists(bundled)?"--check-updates --install-root "+J.Quote(root):"--check-updates"){UseShellExecute=false,CreateNoWindow=true});
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
    public SupportWindow(MainWindow main,string selected,bool snapshotJournalSupported=false) {
        this.main=main;Text="Сообщить о проблеме · QemuM";Theme.Form(this,800,770);MinimumSize=new Size(720,650);
        var body=Theme.Flow(true);body.Dock=DockStyle.Top;body.Padding=new Padding(24);body.WrapContents=false;
        var title=Theme.Label("Помочь улучшить QemuM",22,null,true);body.Controls.Add(title);
        var instruction=Theme.Label("1. Опиши, что делал и что ожидал.\n2. Выбери данные и сохрани отчёт.\n3. Проверь файлы и приложи ZIP к GitHub Issue.",11,Theme.Muted);
        instruction.Margin=new Padding(0,12,0,18);body.Controls.Add(instruction);
        body.Controls.Add(Theme.Button("FAQ · как пользоваться QemuM",()=>{using(var faq=new FaqWindow())faq.ShowDialog(this);}));
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
        AddSection(body,"snapshot_history","Журнал операций снимков",snapshotJournalSupported);
        if(!snapshotJournalSupported){var pending=Theme.Label("Журнал снимков станет доступен после обновления службы отчётов.",9,Theme.Muted);body.Controls.Add(pending);}
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
    void AddSection(Control parent,string key,string text,bool available=true){
        var box=new CheckBox{Text=text,AutoSize=true,Checked=false,Enabled=available,Margin=new Padding(0,5,0,5)};
        sections.Add(key,box);parent.Controls.Add(box);
    }
    async void Export(){
        if(collecting||main.Busy||main.Backend==null){result.Text="Дождись завершения текущей операции менеджера.";return;}
        string[] selected=sections.Where(pair=>pair.Value.Enabled&&pair.Value.Checked).Select(pair=>pair.Key).ToArray();
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

sealed class FaqEntry {
    public readonly string Category,Question,Answer;
    public FaqEntry(string category,string question,string answer){Category=category;Question=question;Answer=answer;}
    public override string ToString(){return Question;}
}

sealed class FaqWindow : Form {
    internal static readonly FaqEntry[] Entries={
        new FaqEntry("Стенды","Как создать стенд и что хранит его папка?","Переключи библиотеку в режим «Стенды», создай папку и выбери существующие VM. Слева остаётся список, справа — параметры. Стенд хранит ссылки, связи и порядок, а не копии дисков. Ширину библиотеки можно менять разделителем."),
        new FaqEntry("Стенды","Как соединить VM и сохранить интернет?","Открой «Соединения и порядок запуска», выбери две VM и адаптер каждой. Выбранный адаптер на время запуска работает как внутренняя связь вместо прежней сети. Для интернета и проброса портов оставь отдельный NAT-адаптер. После остановки стенда обычный запуск использует исходную сеть VM."),
        new FaqEntry("Стенды","Почему слушатель должен запускаться первым?","Одна машина сначала открывает соединение, другая подключается к ней. QemuM проверяет, что соединение открыл именно нужный процесс QEMU. Программа не подменяет эту проверку фиксированной паузой. Если порядок неверен, сохранение отклоняется с объяснением; скрытой перестановки нет."),
        new FaqEntry("Стенды","Назначаются ли IP и DHCP автоматически?","Нет. Связь соединяет сетевые адаптеры, но адреса, DHCP, маршруты и правила гостевого firewall настраиваются внутри ОС. Несколько пар связей не являются одним общим коммутатором. Если QEMU запущен, это ещё не доказывает IP-связность гостей."),
        new FaqEntry("Стенды","Что означают готовность и задержка?","Задержка — пауза перед запуском, а не гарантия загрузки ОС. «Процесс создан» дополнительно проверяет владельца QMP. «QMP готов» подтверждает QEMU, но не рабочий стол или вход в ОС. TCP-проверка ждёт доступный сервис на 127.0.0.1, например явный проброс SSH-порта. Guest Agent для неё не нужен; пароли и команды гостя не сохраняются."),
        new FaqEntry("Стенды","Как отменить, продолжить или восстановиться после сбоя?","В окне операции нажми «Отменить будущие этапы». Работающие VM остаются. После сбоя открой «Проверить после сбоя»: программа сверит QMP, PID и время создания процессов без автоматического запуска. Затем сформируй новый план. Уже подтверждённые участники не запускаются повторно. Неизвестный процесс не присваивается стенду по одному имени или PID."),
        new FaqEntry("Стенды","Чем отличаются выключение и удаление папки?","«Выключить стенд» отправляет гостям штатный запрос в обратном порядке и ждёт завершения до тайм-аута. Принудительная остановка QEMU — отдельное подтверждаемое действие. Ранее работавшие VM по умолчанию исключены из остановки. Удаление папки убирает только определение; VM, диски и журнал сохраняются, процессы продолжают работать."),
        new FaqEntry("Стенды","Копия стенда копирует диски и снимки?","Нет. Дублирование копирует только ссылки и настройки стенда. Обе папки используют те же VM; одновременная работа с разной сетью отклоняется. Снимок отдельной VM не является согласованным снимком всего стенда. Одновременное сохранение нескольких гостей требует отдельного механизма."),
        new FaqEntry("Стенды","Как передать отчёт и обновить приложение?","«Экспорт отчёта» показывает JSON перед сохранением. Имена заменяются, диски, RAM, дампы, конфигурации и гостевые документы не собираются. Проверь текст перед ручной передачей: автоматической отправки нет. Отказ QEMU, kernel panic и ожидание сети внутри ОС различаются; один статус QMP не определяет причину. Перед обновлением заверши или отмени операции стендов и закрой менеджер. Определения и журнал находятся в пользовательских данных отдельно от кода."),
        new FaqEntry("Начало работы","Что делает QemuM?","QemuM управляет виртуальными машинами через QEMU: создаёт конфигурации, запускает гостевую ОС и предоставляет параметры, сеть, снимки и диагностику. Саму операционную систему нужно установить отдельно или подключить готовый диск."),
        new FaqEntry("Начало работы","Чем отличаются ISO, виртуальный диск и конфигурация?","ISO — установочный или live-образ ОС. Виртуальный диск хранит систему и файлы гостя. JSON-конфигурация хранит параметры запуска и пути к носителям. Копия JSON не является копией содержимого диска."),
        new FaqEntry("Начало работы","Как установить ОС на новый диск?","Нажми «Новая машина», выбери «Установка ISO на новый диск», укажи папку диска, ISO, размер диска, память и CPU. Создай VM и запусти установщик. После установки используй загрузку с диска; ISO можно отключить в параметрах."),
        new FaqEntry("Начало работы","Как подключить существующую VM или запустить live ISO?","При создании выбери «Существующий диск» и укажи образ либо «Только live ISO». Параметры оборудования могут отличаться от VirtualBox: проверь прошивку, интерфейс диска и драйверы гостевой ОС. Live ISO без записываемого диска не подходит для снимков дисков."),
        new FaqEntry("Диски","Какие форматы поддерживаются?","Основной диск в интерфейсе — qcow2 или VDI. VDI автоматически не конвертируется в qcow2. Внутренние снимки требуют qcow2; для одного VDI/raw/qcow2 предусмотрена отдельная внешняя цепочка с приватной базой. Основной селектор запуска не принимает raw; дополнительный диск raw можно описать через Extra args."),
        new FaqEntry("Диски","Почему VDI из VirtualBox может не загружаться?","Проверь загрузку BIOS/UEFI, выбранный контроллер диска и драйверы гостя. В QemuM основной диск подключается через VirtIO. Надпись «Guest has not initialized the display» сама по себе не доказывает неисправность VDI: проверь вывод QEMU, ускорение и параметры экрана."),
        new FaqEntry("Запуск и управление","Что выбрать: WHPX, совместимый WHPX, Auto или TCG?","WHPX использует аппаратную виртуализацию Windows. Совместимый профиль задаёт kernel-irqchip=off для отдельных проблем совместимости. Auto проверяет WHPX и при необходимости использует TCG. TCG эмулирует CPU программно и может быть заметно медленнее. Причину отказа ускорения смотри в VM Doctor и живых логах."),
        new FaqEntry("Запуск и управление","Можно ли создать свой профиль запуска?","В параметрах VM рядом с профилем нажми «Настроить…». Можно сохранить собственные параметры ускорителя, его опций и CPU. Пользовательские профили можно создавать и удалять. Удаление профиля из списка не должно подменять уже сохранённые настройки VM. Изменения оборудования обычно применяются после перезапуска."),
        new FaqEntry("Запуск и управление","Сколько выделить RAM и ядер CPU?","Оставь ресурсы Windows и другим приложениям. Выделение всех ядер и всей памяти не гарантирует ускорения и может вызвать задержки на хосте. Сравни загрузку CPU, свободную память и скорость диска в Windows и гостевой ОС. Проверяй изменения по одному."),
        new FaqEntry("Запуск и управление","Чем отличаются пауза, выключение, перезагрузка и остановка QEMU?","Пауза останавливает выполнение гостя, но сохраняет процесс и занятую RAM. «Выключить» просит ОС завершить работу штатно. Перезагрузка выполняет аппаратный сброс. «Остановить QEMU» завершает виртуальную машину принудительно; несохранённые данные могут потеряться. Для обычного завершения используй выключение внутри гостя."),
        new FaqEntry("Запуск и управление","Что произойдёт, если закрыть панель VM или менеджер?","Закрытие панели оставляет VM работать. После выключения гостя панель остаётся открытой. Менеджер также может закрыться с сохранением процессов QEMU; при повторном открытии он проверяет подключение. Во время дисковой операции нужно дождаться её завершения."),
        new FaqEntry("Экран и устройства","Почему низкий FPS или задержка ввода?","Проверь фактический ускоритель: TCG может упираться в эмуляцию CPU. Влияют также RAM, диск, видеокарта гостя, драйверы, разрешение и рабочий стол. Аппаратное ускорение CPU WHPX не означает ускорение 3D-графики. Сначала сохрани лог и точные параметры; меняй по одному параметру, сравнивая результат."),
        new FaqEntry("Экран и устройства","Что делают VGA, разрешение и USB tablet?","VGA выбирает виртуальный видеоадаптер; для некоторых вариантов гостю нужны драйверы. Разрешение задаёт параметры экрана, но гостевая ОС может выбирать режим самостоятельно. usb-tablet передаёт абсолютные координаты указателя и часто удобнее относительной USB-мыши. Это виртуальное устройство, не проброс физической мыши."),
        new FaqEntry("Сеть","Что такое NAT и проброс портов?","NAT даёт гостю выход в сеть через хост. Для доступа с хоста к службе гостя настрой правило hostfwd, например tcp:127.0.0.1:2222-:22 для SSH. В госте должна работать соответствующая служба. Адрес 127.0.0.1 ограничивает вход локальным компьютером; открытие на других адресах меняет доступность службы."),
        new FaqEntry("Сеть","Можно ли использовать несколько адаптеров, TCP и TAP?","В разделе «Сеть» можно задать несколько адаптеров, их ID, модель, MAC и backend. TCP связывает сетевые сегменты VM через указанный адрес/порт. Для TAP нужен заранее настроенный адаптер и подходящий драйвер Windows. Пустой MAC позволяет автоматический выбор. Совпадающие ID и конфликтующие параметры не допускаются."),
        new FaqEntry("Extra args","Для чего нужны Extra args?","Это дополнительные аргументы командной строки QEMU. Встроенная справка содержит примеры. Сохраняй границы аргументов и кавычки в путях с пробелами. Не дублируй устройства и сетевые ID, уже заданные интерфейсом. Произвольные устройства могут ограничить доступность снимков или полного состояния."),
        new FaqEntry("Extra args","Как настроить общий буфер обмена и указатель?","Пример для поддерживающей его сборки QEMU:\r\n\r\n-usb -device usb-tablet -device virtio-serial-pci -chardev qemu-vdagent,id=vdagent,clipboard=on,mouse=off -device virtserialport,chardev=vdagent,name=com.redhat.spice.0 -display gtk,clipboard=on\r\n\r\nВ госте нужен совместимый spice-vdagent. Не создавай второй контроллер или канал с тем же ID. Поддержка GTK/clipboard зависит от сборки QEMU. Захват сочетаний клавиш регулируется окном отображения; этот пример не гарантирует передачу всех системных сочетаний Windows."),
        new FaqEntry("Guest Agent","Что такое QEMU Guest Agent и зачем он нужен?","Это служба внутри гостевой ОС и отдельный канал связи с QEMU. В поддерживаемых сценариях она позволяет получать сведения о госте, запрашивать выключение и подготавливать файловые системы перед снимком. Для обычного запуска VM агент не обязателен. QemuM не устанавливает его внутрь гостя автоматически."),
        new FaqEntry("Guest Agent","Что агент делает перед снимком дисков?","Гостевая ОС сбрасывает накопленные записи и временно замораживает запись в поддерживаемые файловые системы (freeze). QEMU создаёт снимок, после чего агент возобновляет запись (thaw). Это повышает согласованность файловой системы, но не сохраняет RAM и открытые программы. Базам данных может потребоваться отдельная подготовка самим приложением."),
        new FaqEntry("Guest Agent","Как агент подключается и что делать при ошибке thaw?","Нужны установленная служба qemu-guest-agent внутри ОС и явно настроенный отдельный канал Guest Agent в конфигурации QemuM. Общий буфер обмена через spice-vdagent — другой механизм. В QemuM независимый защитник ограничивает время freeze и пытается выполнить thaw при ошибке. Если thaw не подтверждён, проверь журнал и состояние гостя: продолжение блокируется. Проверки с тестовым агентом не означают аттестацию всех гостевых файловых систем."),
        new FaqEntry("Снимки","Чем снимок дисков отличается от полного состояния и backup?","Снимок дисков сохраняет содержимое дисков; при откате ОС загружается заново. Полное состояние дополнительно сохраняет RAM, CPU и устройства, но требует совместимой конфигурации. Независимый backup — отдельная копия, пригодная при потере исходного образа. Внутренний снимок внутри того же qcow2 не защищает от потери этого файла."),
        new FaqEntry("Снимки","Как создать и восстановить снимок?","Открой «Снимки дисков» → «Создать снимок», задай название, тип и необходимость сохранения параметров, проверь план и подтверди. Для обычного отката выключи VM, выбери точку и нажми «Загрузить снимок». Выбери диски либо диски с параметрами и нужен ли последующий запуск. Перед откатом создаётся страховочная точка."),
        new FaqEntry("Снимки","Зачем нужны группы дисков и страховочная точка?","Если VM пишет на несколько дисков, их нужно возвращать к одному моменту. QemuM проверяет состав всей группы, файловые ID и точные ID снимков. Страховочная точка сохраняет состояние перед откатом. Неполная группа не считается готовой; отдельные совпавшие названия не доказывают принадлежность дисков одной группе."),
        new FaqEntry("Снимки","Почему RAM-снимки недоступны с WHPX?","У проверенного стандартного QEMU 11.1.1 WHPX блокирует сохранение полного состояния из-за отсутствующей поддержки части необходимого состояния. kernel-irqchip=off не устраняет эту блокировку. Поэтому в QemuM RAM поддержана только для проверенных конфигураций TCG на паузе. Поиск другой кнопки savevm не устраняет ограничение самого QEMU."),
        new FaqEntry("Снимки","Какие ограничения у полного состояния с RAM?","Нужны та же сборка QEMU и совместимые CPU, устройства и параметры. Проверенная матрица QemuM ограничена TCG, CPU max/qemu64/qemu32 и разрешёнными устройствами. WHPX, host CPU, непроверенные каналы и устройства отклоняются. RAM может содержать пароли и документы. Не отправляй такой образ в GitHub Issues."),
        new FaqEntry("Снимки","Как работают внешние снимки VDI и raw?","Для одного записываемого диска создаётся приватная копия базы и рабочая ветвь qcow2. Исходный пользовательский образ не становится изменяемой базой этой цепочки. Ветви зависят от зарегистрированных родителей: не перемещай и не удаляй слои вручную. Commit/rebase/merge пока предоставляют только план, без исполнения."),
        new FaqEntry("Снимки","Почему требуется много свободного места?","Офлайн-операции готовят и проверяют полные рабочие копии, затем публикуют результат. Прежние образы сохраняются для восстановления. Внутренние снимки также растут по мере изменения данных. Виртуальный размер диска не равен занятому месту. Служебные .qemum-stage-папки нельзя бездумно удалять: они могут содержать аварийные копии."),
        new FaqEntry("Снимки","Можно ли отменить операцию и что делать после сбоя?","Отмена доступна только на безопасных этапах до публикации. Запрос во время qemu-img ожидает безопасной границы; после начала замены образов нужно дождаться результата. При сбое открой «Незавершённые операции…», сохрани ID и выполни сверку. Не удаляй журналы и не повторяй запись вслепую. При неопределённом результате запуск блокируется."),
        new FaqEntry("Снимки","Как удалить или защитить точку?","Выбери снимок и используй «Удалить» либо «Ещё…» → «Защита снимка…». Защищённые и страховочные точки требуют осознанного снятия защиты. Внешние слои с зависимостями не удаляются как независимые файлы. Удаление снимка не означает удаление всей VM; точное освобождение места заранее не гарантируется."),
        new FaqEntry("Снимки","Как настроить расписание?","Открой «Ещё…» → «Расписание и хранение…». По умолчанию расписание выключено и работает только при открытом менеджере, без скрытой службы. После пропущенного времени не создаётся очередь десятков снимков. Автоочистка выполняется офлайн и не удаляет ручные, защищённые, неполные, RAM и аварийные точки либо зависимости внешних цепочек."),
        new FaqEntry("Резервные копии","Как сделать независимую копию и открыть её отдельно?","В окне снимков выбери «Ещё…» → «Независимая копия…». Можно экспортировать выбранную точку или текущее состояние выключенной VM в новый ZIP. Внутри — самостоятельные qcow2/raw, manifest и по выбору параметры, без RAM. Распакуй ZIP в новую папку и создай отдельную VM с этими дисками. Не подменяй исходную VM. Существующий ZIP не перезаписывается."),
        new FaqEntry("Резервные копии","Что сохраняет копия параметров?","Она переносит настройки запуска, включая поддерживаемые сетевые параметры, но не содержимое диска или ISO. При создании VM из копии носители выбираются отдельно. Сохранение параметров внутри снимка также добровольное; откат настроек не должен возвращать старые PID, QMP-порт и оформление."),
        new FaqEntry("Библиотека и оформление","Как убрать VM из списка, сохранив диск?","В удалении выбери удаление только регистрации из интерфейса. Удаляется конфигурация VM, файлы дисков и ISO остаются. Сохранение копии параметров можно отключить, но тогда повторное подключение потребует настройки заново. Перед подтверждением проверь выбранный способ и перечень файлов."),
        new FaqEntry("Библиотека и оформление","Как менять оформление и находить машины?","Используй поиск библиотеки и настройки оформления VM: название, цвет, значок или доступный вариант оформления. Визуальные настройки отделены от параметров запуска. Удаление пользовательского элемента оформления не является удалением виртуального диска. Точный перечень доступных действий зависит от выбранного элемента."),
        new FaqEntry("Обновления","Как обновиться и не потерять VM?","Открой «Обновления», проверь наличие новой версии или сборки и следуй инструкции. Исправления могут приходить без нового релиза: например, alpha.4, сборка 2. Программа хранит код отдельно от пользовательских данных; прежняя сборка остаётся для возврата при неудачном старте. Не удаляй data и образы. Обновление приложения не обновляет автоматически Python, QEMU или гостевую ОС. Для перехода с первоначальной alpha.4 выбери переустановку текущей версии; если такой кнопки нет, один раз запусти QemuM-Refresh.exe из существующего релиза GitHub. После перехода новые сборки видны через обычную кнопку «Обновления». Перед обновлением дождись завершения операций с дисками и закрой менеджер. Фоновая автоматическая установка не выполняется."),
        new FaqEntry("Обновления","Можно ли обновляться с работающими VM?","Обновлятор требует закрытого менеджера и не завершает процессы QEMU принудительно. Прежде дождись окончания операций снимков и других записей дисков; для наиболее предсказуемого обновления штатно выключи гостей. Закрытие менеджера само по себе не выключает VM. Не запускай установку заново поверх пользовательских данных как способ обновления."),
        new FaqEntry("Обновления","Почему установка Python или QEMU долго не завершается?","Отдельные этапы — скачивание, проверка, распаковка и установка. Индикатор активности не всегда означает измеримый процент. Проверь соединение, свободное место и диагностические сообщения; сохрани журнал до повторной попытки. Не удаляй папки действующих VM. Если окно не отвечает, приложи сведения об этапе и отчёт диагностики к Issue."),
        new FaqEntry("Диагностика","Зачем нужны VM Doctor и живые логи?","VM Doctor проверяет окружение и конфигурацию; результаты показывают, что подтверждено и что требует внимания. Проверка ускорителя не равна проверке загрузки всей ОС. «Живые логи» разделяют события менеджера и вывод QEMU; пауза просмотра останавливает прокрутку/просмотр, а не виртуальную машину."),
        new FaqEntry("Диагностика","Как сообщить об ошибке и какие данные отправляются?","В «Помощь / отчёт» опиши шаги и ожидаемый результат, отметь необходимые категории и сохрани новый ZIP. Для ошибки запуска обычно полезны лог и параметры; для снимков — ID операции и журнал снимков. Ничего не отправляется автоматически. Проверь архив и прикрепи его вручную через «Открыть GitHub Issues». Диски, ISO и RAM в обычный отчёт не включаются; скриншот может содержать личные данные, а маскирование текста не гарантирует полной анонимности.")
    };
    readonly FieldInput search=Theme.Input();
    readonly ThemedSelector category=new ThemedSelector();
    readonly ThemedList questions=Theme.ListBox();
    readonly ThemedEditor answer=Theme.Editor();
    readonly Label count=Theme.Label("",10,Theme.Muted);
    public FaqWindow(){
        Text="FAQ · QemuM";Theme.Form(this,1080,760);MinimumSize=new Size(800,580);
        search.AccessibleName="Поиск по вопросам и ответам FAQ";category.AccessibleName="Тема FAQ";questions.AccessibleName="Вопросы FAQ";answer.AccessibleName="Ответ на выбранный вопрос";
        var content=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(24)};
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));content.RowStyles.Add(new RowStyle(SizeType.Absolute,54));content.RowStyles.Add(new RowStyle(SizeType.AutoSize));content.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var toolbar=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,65));toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,35));search.Margin=new Padding(0,0,12,8);toolbar.Controls.Add(search,0,0);toolbar.Controls.Add(category,1,0);content.Controls.Add(toolbar,0,0);content.Controls.Add(count,0,1);
        category.Items.Add("Все темы");foreach(string item in Entries.Select(entry=>entry.Category).Distinct())category.Items.Add(item);category.SelectedIndex=0;
        var body=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=new Padding(0,12,0,0)};body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,40));body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,60));questions.Dock=DockStyle.Fill;questions.Margin=new Padding(0,0,16,0);answer.ReadOnly=true;body.Controls.Add(questions,0,0);body.Controls.Add(answer,1,0);content.Controls.Add(body,0,2);
        var buttons=Theme.Flow();var close=Theme.Button("Закрыть",Close);buttons.Controls.Add(close);CancelButton=close;
        Theme.Dialog(this,content,buttons,"Ответы на вопросы","Поиск по вопросам и ответам · работает без интернета");
        search.TextChanged+=(sender,args)=>Filter();category.SelectedIndexChanged+=(sender,args)=>Filter();questions.SelectedIndexChanged+=(sender,args)=>ShowAnswer();
        KeyPreview=true;KeyDown+=(sender,args)=>{if(args.Control&&args.KeyCode==Keys.F){search.Focus();args.Handled=true;}};Shown+=(sender,args)=>search.Focus();Filter();
    }
    void Filter(){
        var previous=questions.SelectedItem as FaqEntry;string query=search.Text.Trim(),topic=Convert.ToString(category.SelectedItem);
        questions.Items.Clear();foreach(var entry in Entries)if((category.SelectedIndex<=0||entry.Category==topic)&&(query.Length==0||(entry.Category+" "+entry.Question+" "+entry.Answer).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0))questions.Items.Add(entry);
        count.Text="Найдено: "+questions.Items.Count+" из "+Entries.Length+" · Ctrl+F — поиск";
        if(questions.Items.Count>0)questions.SelectedIndex=previous!=null&&questions.Items.Contains(previous)?questions.Items.IndexOf(previous):0;ShowAnswer();
    }
    void ShowAnswer(){var entry=questions.SelectedItem as FaqEntry;answer.Text=entry==null?"Ничего не найдено. Попробуй другое слово или выбери «Все темы».":entry.Category+"\r\n\r\n"+entry.Question+"\r\n\r\n"+entry.Answer;answer.Input.Select(0,0);}
}
