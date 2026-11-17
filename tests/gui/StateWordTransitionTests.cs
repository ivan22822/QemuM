using System;using System.Drawing;using System.IO;using System.Reflection;using System.Threading;using System.Windows.Forms;
static class StateWordTransitionTests {
 [STAThread]static int Main(string[] args){try{Application.EnableVisualStyles();Program.TestMode=false;var render=typeof(TrackedWord).GetMethod("RenderWord",BindingFlags.Instance|BindingFlags.NonPublic);int frames=0;
 using(var word=new TrackedWord{StageColors=true,Size=new Size(1560,112)})using(var image=new Bitmap(1560,112))using(var g=Graphics.FromImage(image)){
  foreach(string state in new[]{"STOPPED","STARTING","NO QMP","RUNNING","PAUSED","STOPPING","NO QMP","RUNNING"}){word.Word=state;word.Running=state=="RUNNING";word.Replay();for(int i=0;i<30;i++){g.Clear(Color.Beige);try{render.Invoke(word,new object[]{g});}catch(TargetInvocationException error){throw new Exception(state+" frame "+i,error.InnerException);}frames++;Thread.Sleep(8);}}
 }
 File.WriteAllText(Path.Combine(args[1],"state-word-transition-results.txt"),"PASS "+frames+" animated frames");return 0;
 }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"state-word-transition-error.txt"),error.ToString());return 1;}}
}
