using System;using System.Drawing;using System.Diagnostics;using System.IO;using System.Linq;using System.Collections.Generic;using System.Reflection;using System.Windows.Forms;
static class EditLayoutTests {
 static int checks;static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
 static IEnumerable<Control> All(Control root){foreach(Control child in root.Controls){yield return child;foreach(var nested in All(child))yield return nested;}}
 [STAThread]static int Main(string[] args){try{Application.EnableVisualStyles();Program.TestMode=true;Dictionary<string,object> metrics=null;
  using(var backend=new Backend(args[0],args[2]))using(var main=new MainWindow(args[0],args[2],true,false)){
   backend.Open();main.Backend=backend;var watch=Stopwatch.StartNew();var original=backend.Call("get",J.O("name","test"));double read=watch.Elapsed.TotalMilliseconds;watch.Restart();
   using(var edit=new EditDialog(main,"test",original)){double construct=watch.Elapsed.TotalMilliseconds;watch.Restart();edit.Show();Application.DoEvents();double show=watch.Elapsed.TotalMilliseconds;
    var strip=All(edit).OfType<ChoiceStrip>().First();var choices=All(strip).OfType<Button>().ToArray();
    Check(choices.Select(b=>b.Height).Distinct().Count()==1&&choices.Max(b=>b.Width)-choices.Min(b=>b.Width)<=1,"video adapter buttons have equal dimensions across both rows");
    Check(All(edit).OfType<Button>().Single(b=>b.Text=="Настроить…").Height==choices[0].Height,"profile action and video choices align to one control height");
    Check(!All(edit).OfType<Label>().Any(l=>l.Text=="ПАРАМЕТРЫ МАШИНЫ"||l.Text.StartsWith("Изменения применятся")),"settings removes redundant footer and sidebar captions");
    Check(!All(edit).OfType<ExtraHelpView>().Any(),"opening settings does not construct the unvisited help page");
    using(var image=WindowFrame.Capture(edit))image.Save(Path.Combine(args[1],"edit-layout.png"));
    int initialControls=All(edit).Count();var deck=All(edit).OfType<PageDeck>().Single();deck.SelectedPage=deck.Pages.First(p=>p.Text=="Справка QEMU");Application.DoEvents();Check(All(edit).OfType<ExtraHelpView>().Count()==1,"help is available on its first selection");
    metrics=J.O("get_ms",read,"constructor_ms",construct,"shown_ms",show,"controls",initialControls);edit.Close();
   }
   main.Backend=null;
  }
  using(var form=new SmoothForm{ClientSize=new Size(920,400),StartPosition=FormStartPosition.Manual,Location=new Point(-2000,-2000)}){
   var grid=new UniformActionGrid();foreach(string title in new[]{"Запустить / продолжить","Выключить стенд","Остановить QEMU стенда","Просмотреть план запуска","План выключения"})grid.Controls.Add(Theme.Button(title,()=>{}));form.Controls.Add(grid);form.Show();
   foreach(int width in new[]{920,600,400,920}){form.ClientSize=new Size(width,400);Application.DoEvents();var buttons=grid.Controls.OfType<Button>().ToArray();
    Check(buttons.Select(b=>b.Height).Distinct().Count()==1&&buttons.Max(b=>b.Width)-buttons.Min(b=>b.Width)<=1,"stand launch grid keeps uniform buttons at width "+width+": "+String.Join(",",buttons.Select(b=>b.Size.ToString())));
    Check(buttons.All(b=>grid.ClientRectangle.Contains(b.Bounds)),"all launch actions remain inside the grid at width "+width);
    Check(buttons.All(b=>TextRenderer.MeasureText(b.Text,b.Font,new Size(b.ClientSize.Width-b.Padding.Horizontal,1000),TextFormatFlags.WordBreak).Height+b.Padding.Vertical<=b.Height),"launch labels fit without clipping at width "+width);
    using(var image=WindowFrame.Capture(form))image.Save(Path.Combine(args[1],"stand-actions-"+width+".png"));
   }
   grid.Visible=false;form.ClientSize=new Size(600,350);var actions=Theme.Flow();actions.Controls.Add(Theme.Button("Действие",()=>{}));var stage=new StateStage(Theme.Label("",10),actions){Dock=DockStyle.Fill};form.Controls.Add(stage);stage.Present("stopped",true);Application.DoEvents();
   Check(!All(stage).OfType<Label>().Any(l=>l.Visible&&(l.Text.StartsWith("СТЕНД /")||l.Text.StartsWith("СОСТОЯНИЕ")||l.Text=="Выключен")),"stand stage removes redundant header and footer strips");
   stage.Present("stopped",false);stage.Context("example","4096 MB","4");Application.DoEvents();
   Check(!All(stage).OfType<Label>().Any(l=>l.Visible&&(l.Text.StartsWith("QEMU /")||l.Text.StartsWith("СОСТОЯНИЕ")||l.Text=="Выключена")),"machine stage also removes redundant header and footer strips");
   Check(stage.Region!=null&&!stage.Region.IsVisible(0,0),"state stage clips square corners");
   var ready=Stopwatch.StartNew();while(!stage.BackgroundReady&&ready.ElapsedMilliseconds<5000){Application.DoEvents();System.Threading.Thread.Sleep(2);}using(var image=WindowFrame.Capture(form))image.Save(Path.Combine(args[1],"stand-clean-stage.png"));
   stage.Visible=false;var inspector=new ConfigurationInspector("Параметры",()=>{});foreach(string key in new[]{"Память","Процессор","Ускорение","Диск","ISO","Сеть"})inspector.Value(key,"Проверка");var disclosure=new ConfigurationDisclosure(inspector){Location=new Point(350,0)};form.Controls.Add(disclosure);disclosure.Expanded=true;Application.DoEvents();
   Check(All(inspector).OfType<RoundedZone>().Count()==6,"all parameter cells share rounded surfaces");
   using(var image=new Bitmap(inspector.Width,inspector.Height)){inspector.DrawToBitmap(image,inspector.ClientRectangle);image.Save(Path.Combine(args[1],"rounded-parameters.png"));}disclosure.Expanded=false;
   form.Close();
  }
  metrics["checks"]=checks;File.WriteAllText(Path.Combine(args[1],"edit-layout-results.json"),J.Json().Serialize(metrics));return 0;
 }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"edit-layout-error.txt"),error.ToString());return 1;}}
}
