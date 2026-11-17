using System;using System.IO;using System.Drawing;using System.Reflection;using System.Threading;using System.Windows.Forms;
static class Stage2VisualProbe {
 [STAThread]static int Main(string[] args){try{Application.EnableVisualStyles();Program.TestMode=true;
  using(var window=new Form{ClientSize=new Size(900,500),StartPosition=FormStartPosition.Manual,Location=new Point(-6000,-5000)}){
   var actions=Theme.Flow();actions.Controls.Add(Theme.Button("Запустить",()=>{}));var stage=new StateStage(Theme.Label("",10),actions){Dock=DockStyle.Fill};window.Controls.Add(stage);stage.Present("stopped");window.Show();Application.DoEvents();
   var ready=typeof(StateStage).GetProperty("BackgroundReady",BindingFlags.Instance|BindingFlags.NonPublic);var until=DateTime.UtcNow.AddSeconds(20);while(ready!=null&&!(bool)ready.GetValue(stage,null)&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(5);}if(ready!=null&&!(bool)ready.GetValue(stage,null))throw new Exception("state background did not complete");
   using(var bitmap=new Bitmap(stage.Width,stage.Height)){stage.DrawToBitmap(bitmap,stage.ClientRectangle);bitmap.Save(args[0]);}window.Close();
  }return 0;
 }catch(Exception error){File.WriteAllText(args[0]+".error.txt",error.ToString());return 1;}}
}
