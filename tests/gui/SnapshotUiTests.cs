using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

static class SnapshotUiTests {
    sealed class Service : ISnapshotUiService {
        public bool Future;public bool Fail;public bool ExecuteFail;public int Calls;public object Catalog;public TaskCompletionSource<object> Pending;
        public bool Supports(string feature){return Future||feature=="create_live"||feature=="restore_start"||feature=="refresh";}
        public Task<object> Load(){if(Fail)throw new Exception("test unavailable");return Task.FromResult(Catalog);}
        public Task<object> Execute(SnapshotUiRequest request){Calls++;if(ExecuteFail)throw new Exception("uncertain result");return Pending==null?Task.FromResult((object)J.O()):Pending.Task;}
        public event Action<SnapshotUiProgress> Progress;
        public void Report(SnapshotUiProgress value){if(Progress!=null)Progress(value);}
    }
    static T Field<T>(object value,string name){if(value is SnapshotsWindow&&value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)==null)value=((SnapshotsWindow)value).View;return (T)value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(value);}
    static IEnumerable<Control> All(Control root){foreach(Control child in root.Controls){yield return child;foreach(var nested in All(child))yield return nested;}}
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static void Capture(Form form,string path){form.Show();Application.DoEvents();using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,form.Width,form.Height));image.Save(path);} }
    [STAThread]static int Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Program.TestMode=true;
        try{
            var entry=new SnapshotUiEntry(J.O("id","one","name","Точка до обновления","date",1700000000));
            var catalog=J.O("online",false,"vm_state","stopped","rows",new object[]{entry.Raw,J.O("id","two","name","RAM · длинное название виртуальной машины для проверки переноса","kind","ram","date",1700000001)},"operations",new object[]{J.O("id","op1","phase","Проверка дисков")});
            var service=new Service{Catalog=catalog};
            Check(!SnapshotUiText.IsStopped(J.O()),"unknown state must not allow restore");
            foreach(string action in new[]{"create","edit","restore","delete","export","schedule","recover","protect","adopt"}){
                using(var dialog=new SnapshotOperationDialog(service,action,entry,catalog)){
                    var fields=Field<Dictionary<string,Control>>(dialog,"fields");
                    if(action=="create"){var protect=All(dialog).OfType<Button>().Single(b=>b.Text=="Защитить от удаления");Check(!protect.Enabled&&!String.IsNullOrEmpty(protect.AccessibleDescription)&&!fields.ContainsKey("protected"),"creation protection is explanatory only and never adds unsupported RPC options");}
                    if(fields.ContainsKey("confirm"))fields["confirm"].Text=entry.Name;
                    if(action=="recover")fields["operation_id"].Text="op1";
                    Check(action=="restore"?dialog.ValidateOptions()==null:dialog.ValidateOptions()!=null,"legacy capability gate: "+action);
                    Capture(dialog,Path.Combine(args[0],"snapshot-"+action+".png"));dialog.Close();
                }
            }
            Check(service.Calls==0,"opening and cancelling forms must never execute");
            using(var dialog=new SnapshotOperationDialog(service,"create",entry,catalog)){
                dialog.Show();Application.DoEvents();var fields=Field<Dictionary<string,Control>>(dialog,"fields");
                fields["label"].Focus();Control before=dialog.ActiveControl;dialog.SelectNextControl(before,true,true,true,true);Check(dialog.ActiveControl!=before,"Tab traversal moves from the input");
                typeof(Form).GetMethod("ProcessDialogKey",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(dialog,new object[]{Keys.Escape});
                Check(!dialog.Visible&&dialog.DialogResult!=DialogResult.OK,"Escape cancels without sending RPC");
            }
            foreach(string vmState in new[]{"stopped","running","paused","unknown"})using(var stateWindow=new SnapshotsWindow(service,"test",J.O("vm_state",vmState,"online",vmState=="running"||vmState=="paused","rows",new[]{entry.Raw}))){
                stateWindow.Show();Application.DoEvents();Check(Field<Button>(stateWindow,"restore").Enabled==(vmState=="stopped"),"disk restore gate: "+vmState);stateWindow.Close();
            }
            using(var host=new SmoothForm{ClientSize=new Size(1000,440)}){
                var compactEntry=J.O("id","compact","name","Снимок 2026-10-22 15:07","description","Перед обновлением системы. Можно вернуться к рабочей конфигурации.","date",1792670820);
                using(var view=new SnapshotCatalogView(service,"test",J.O("vm_state","stopped","rows",new[]{compactEntry}),false,true)){
                    host.Controls.Add(view);host.Show();Application.DoEvents();
                    var searchInput=Field<FieldInput>(view,"search");var type=Field<ThemedSelector>(view,"filter");
                    Check(searchInput.Top==type.Top&&searchInput.Height==type.Height,"snapshot search and filter share exact vertical bounds");Check(searchInput.CornerRadius==10&&type.CornerRadius==10,"snapshot toolbar uses matching rounded controls");
                    var details=Field<SnapshotDetailsCard>(view,"detail");var viewport=Field<ScrollSurface>(view,"detailScroll");
                    Check(details.Height<=viewport.ClientSize.Height,"snapshot details and normal description fit without vertical scrolling");
                    using(var image=WindowFrame.Capture(host))image.Save(Path.Combine(args[0],"snapshots-compact-layout.png"));
                    details.ShowEntry(new SnapshotUiEntry(J.O("id","short","name","Снимок 2026-10-22 15:07")));Application.DoEvents();
                    Check(details.Height==details.GetPreferredSize(new Size(details.Width,0)).Height,"selecting a snapshot without description recomputes card height without resizing the window");
                    using(var image=WindowFrame.Capture(host))image.Save(Path.Combine(args[0],"snapshots-card-spacing.png"));
                    details.ShowEntry(new SnapshotUiEntry(compactEntry));Application.DoEvents();Check(details.Height==details.GetPreferredSize(new Size(details.Width,0)).Height,"switching back restores space for description without clipping");
                    searchInput.Text="nothing matches";Application.DoEvents();var emptyPanel=Field<SnapshotUiList>(view,"list").Parent;Check(emptyPanel is RoundedZone&&emptyPanel.Margin.Right==0,"empty snapshot surface stays rounded and reaches the toolbar right edge");
                    using(var image=WindowFrame.Capture(host))image.Save(Path.Combine(args[0],"snapshots-empty-layout.png"));host.Close();
                }
            }
            var many=new List<object>();for(int index=0;index<200;index++)many.Add(J.O("id","long-"+index,"name",new String('Ж',120)+index,"description",new String('Д',4000),"state",index==199?"partial":"ready"));
            using(var window=new SnapshotsWindow(service,"test",J.O("vm_state","stopped","rows",many.ToArray()))){
                window.ClientSize=new Size(850,620);Capture(window,Path.Combine(args[0],"snapshots-long-catalog.png"));Check(Field<SnapshotUiList>(window,"list").Items.Count==200,"long catalogue remains navigable");window.Close();
            }
            using(var dialog=new SnapshotPlanDialog("Будут сохранены диски:\r\nДиск системы\r\nДиск данных\r\n\r\nISO не включается. Только диски, без RAM.")){
                Capture(dialog,Path.Combine(args[0],"snapshot-plan.png"));dialog.Close();Check(dialog.DialogResult!=DialogResult.OK,"plan close is cancellation");
            }
            service.Future=true;
            using(var export=new SnapshotOperationDialog(service,"export",null,J.O("vm_state","stopped"))){
                var fields=Field<Dictionary<string,Control>>(export,"fields");Check(((ThemedSelector)fields["export_scope"]).SelectedIndex==1,"empty catalogue defaults to exporting current disks");fields["path"].Text=Path.Combine(args[0],"not-created.zip");Check(export.ValidateOptions()==null,"current disk export requires no snapshot selection");
            }
            using(var dialog=new SnapshotOperationDialog(service,"create",entry,catalog)){
                var fields=Field<Dictionary<string,Control>>(dialog,"fields");fields["label"].Text="Мой снимок";fields["description"].Text="Первая строка\r\nВторая строка";((ThemedSelector)fields["kind"]).SelectedIndex=1;
                dialog.Show();Application.DoEvents();Field<Button>(dialog,"apply").PerformClick();
                Check(dialog.Request!=null&&!dialog.Request.Options.ContainsKey("protected")&&J.S(dialog.Request.Options,"kind")=="ram"&&J.S(dialog.Request.Options,"description").Contains("\n"),"future service receives semantic options and Unicode");
            }
            var protectedEntry=new SnapshotUiEntry(J.O("name","checkpoint","protected",true));
            using(var dialog=new SnapshotOperationDialog(service,"delete",protectedEntry,catalog))Check(dialog.ValidateOptions()!=null,"protected point cannot be deleted");
            using(var window=new SnapshotsWindow(service,"test",catalog)){
                Capture(window,Path.Combine(args[0],"snapshots-catalog.png"));
                var list=Field<SnapshotUiList>(window,"list");Check(list.Items.Count==2,"catalog rows");
                Field<FieldInput>(window,"search").Text="обновления";Check(list.Items.Count==1,"search works");
                Field<FieldInput>(window,"search").Text="";((ThemedSelector)Field<ThemedSelector>(window,"filter")).SelectedIndex=2;Check(list.Items.Count==1,"RAM filter works");
                service.Fail=true;window.Reload().GetAwaiter().GetResult();Check(!Field<Button>(window,"restore").Enabled,"read failure blocks mutation");
                Capture(window,Path.Combine(args[0],"snapshots-error.png"));service.Fail=false;window.Reload().GetAwaiter().GetResult();
                window.ClientSize=new Size(850,620);Capture(window,Path.Combine(args[0],"snapshots-narrow.png"));window.Close();
            }
            foreach(string mode in new[]{"details","history","config","help"})using(var dialog=new SnapshotInformationDialog(mode,entry,catalog)){Capture(dialog,Path.Combine(args[0],"snapshot-info-"+mode+".png"));dialog.Close();}
            Check(service.Calls==0,"read-only interactions do not execute");
            using(var window=new SnapshotsWindow(service,"test",catalog)){
                window.Show();Application.DoEvents();service.Pending=new TaskCompletionSource<object>();
                var operation=window.Perform(new SnapshotUiRequest("create"));
                service.Report(new SnapshotUiProgress{OperationId="op1",Phase="Проверка",Done=1,Total=4,CanCancel=true});
                Check(Field<Label>(window,"note").Text.Contains("25%")&&Field<Button>(window,"cancel").Visible,"real progress and capability-gated cancellation");
                var operationView=Field<ReferenceOperationWindow>(window,"operationWindow");var surface=Field<OperationSurface>(operationView,"surface");var phaseRows=Field<FlowLayoutPanel>(operationView,"phases");var phaseRow=phaseRows.Controls[0];Check(!phaseRow.Controls.OfType<PictureBox>().Single().Visible,"current phase index is not covered by an empty icon");Check(phaseRow.Controls.OfType<Label>().All(label=>label.Font.FontFamily.Name.StartsWith("Unbounded")),"operation phase typography matches the reference");Check(surface.Determinate&&Field<TrackedWord>(surface,"word").Word=="SAVING","real create progress remains pending until backend confirmation");var until=DateTime.UtcNow.AddMilliseconds(650);while(DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(10);}Check(Math.Abs(surface.DisplayProgress-.25)<.001,"progress reaches the backend value and never runs a simulated four-step clock");Capture(operationView,Path.Combine(args[0],"operation-real-progress.png"));
                window.Close();Check(!window.IsDisposed,"busy window cannot interrupt worker");
                service.Pending.SetResult(J.O());Application.DoEvents();Check(operation.IsCompleted,"async operation completes");Check(Field<TrackedWord>(surface,"word").Word=="SAVED"&&Field<Button>(operationView,"close").Enabled,"confirmed completion unlocks close and shows SAVED");service.Pending=null;
                service.ExecuteFail=true;window.Perform(new SnapshotUiRequest("create")).GetAwaiter().GetResult();
                Check(!window.IsDisposed&&!Field<Button>(window,"create").Enabled&&Field<Label>(window,"note").Text.Contains("не подтверждена"),"uncertain operation remains visible and requires refresh");
                service.ExecuteFail=false;window.Reload().GetAwaiter().GetResult();window.Close();
            }
            foreach(float scale in new[]{1.25F,1.5F,2F})using(var window=new SnapshotsWindow(service,"test",catalog)){
                window.Scale(new SizeF(scale,scale));Capture(window,Path.Combine(args[0],"snapshots-scale-"+scale.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"));window.Close();
            }
            if(!MotionPaint.Reduced){
                Program.TestMode=false;using(var animated=new ReferenceOperationWindow("motion test",()=>Task.FromResult(0))){
                    animated.StartPosition=FormStartPosition.Manual;animated.Location=new Point(-2200,-1400);Point target=animated.Location;double firstOpacity=-1;animated.Shown+=(sender,eventArgs)=>firstOpacity=animated.Opacity;animated.Show();Application.DoEvents();Check(firstOpacity==0&&animated.Opacity<1,"first visible frame is assembled before the 300ms entrance");var entranceUntil=DateTime.UtcNow.AddMilliseconds(400);while(DateTime.UtcNow<entranceUntil){Application.DoEvents();System.Threading.Thread.Sleep(5);}Check(animated.Opacity==1&&animated.Location==target,"300ms entrance ends at the exact requested native bounds");
                    var surface=Field<OperationSurface>(animated,"surface");surface.Report("one",1,4);var until=DateTime.UtcNow.AddMilliseconds(60);while(DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(5);}Check(surface.DisplayProgress>0&&surface.DisplayProgress<.25,"180ms operation transition has intermediate frames");
                    animated.Finish("completed","confirmed");animated.Close();
                }Program.TestMode=true;
            }
            File.WriteAllText(Path.Combine(args[0],"snapshot-ui-results.txt"),"Passed: capability gates, forms, semantic request, protection, search/filter, read failure/retry, read-only dialogs. Screenshots generated.");return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[0],"snapshot-ui-error.txt"),error.ToString());return 1;}
    }
}
