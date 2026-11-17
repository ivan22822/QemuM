using System;using System.Drawing;using System.IO;using System.Linq;using System.Reflection;using System.Windows.Forms;
static class RoundedLaunchTests {
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static T Field<T>(object o,string key){return (T)o.GetType().GetField(key,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);}
 static void Save(Form f,string path){using(var b=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(path);}}
 [STAThread]static int Main(string[] args){try{Application.EnableVisualStyles();Program.TestMode=true;string output=args[0];int checks=0;
 foreach(float scale in new[]{1F,1.25F,1.5F,2F})foreach(int width in new[]{360,540,940})using(var f=new Form{StartPosition=FormStartPosition.Manual,Location=new Point(-3000,-2000),Size=new Size(width+20,600)}){
 var actions=Theme.Flow(true);var primary=Theme.Flow();primary.Controls.Add(Theme.Button("Запустить",()=>{},true));primary.Controls.Add(Theme.Button("Ещё…",()=>{}));var mode=new LaunchModeSelector{Margin=new Padding(8,0,0,0)};primary.Controls.Add(mode);actions.Controls.Add(primary);
 var stage=new StateStage(Theme.Label("",10),actions){Dock=DockStyle.Fill};f.Controls.Add(stage);f.Scale(new SizeF(scale,scale));stage.Present("checking");f.Show();Application.DoEvents();stage.PerformLayout();
 Check(Field<TrackedWord>(stage,"word").Word=="CHECKING","animated state uses English");
 Check(mode.Controls.Count==0&&mode is Button,"single native toggle button");int events=0;mode.SelectedIndexChanged+=(s,e)=>events++;mode.PerformClick();Check(mode.SelectedIndex==1&&mode.Text=="Запуск из снимка","one click enables snapshot mode");Check(mode.Width>=mode.GetPreferredSize(new Size(1000,0)).Width,"snapshot mode text fits the toggle width");mode.PerformClick();Check(mode.SelectedIndex==0&&events==2,"second click restores normal mode");
 var dock=Field<BufferedPanel>(stage,"dock");foreach(Button button in primary.Controls){Check(button.Region==null,"anti-aliased button corners are not clipped by native regions");Point point=dock.PointToClient(button.Parent.PointToScreen(button.Location));Check(point.X>=0&&point.Y>=0&&point.X+button.Width<=dock.Width&&point.Y+button.Height<=dock.Height,"action remains inside dock at width="+width+" scale="+scale);}

 if(scale==1F&&width==540)Save(f,Path.Combine(output,"launch-one-toggle.png"));checks++;f.Close();}
 using(var button=new PolarButton{Size=new Size(160,40)}){button.CornerRadius=0;Check(button.Region==null,"explicit square style does not add native clipping");button.CornerRadius=12;Check(button.Region==null,"round paint does not add binary clipping");}
 Check(ReferenceHeader.EnglishGhost("Снимки")=="SNAPSHOT"&&ReferenceHeader.EnglishGhost("Параметры машины")=="SETTINGS","decorative animated headings use English");
 File.WriteAllText(Path.Combine(output,"result.txt"),"PASS: "+checks+" layout/DPI cases, one toggle, English animation, rounded regions, no clipped controls or native corner masks");return 0;
 }catch(Exception e){File.WriteAllText(Path.Combine(args[0],"error.txt"),e.ToString());return 1;}}
}
