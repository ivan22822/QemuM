using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

static class BootstrapTests {
    [STAThread]static int Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try{
            using(var setup=new SetupWindow()){
                setup.StartPosition=FormStartPosition.Manual;setup.Location=new Point(-2400,-1600);setup.Show();Application.DoEvents();
                Save(setup,Path.Combine(args[0],"setup-progress.png"));
                setup.Close();
            }
            using(var update=new UpdateWindow(args[1],false)){
                update.StartPosition=FormStartPosition.Manual;update.Location=new Point(-2400,-1600);update.Show();Application.DoEvents();
                Save(update,Path.Combine(args[0],"update-design.png"));update.Close();
            }
            using(var component=new ComponentProgress("Python")){
                component.Report("Загрузка",50,100);component.Report("Проверка");component.Report("Готово",1,1);
            }
            File.WriteAllText(Path.Combine(args[0],"bootstrap-results.json"),"{\"passed\":3}");return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[0],"bootstrap-error.txt"),error.ToString());return 1;}
    }
    static void Save(Form form,string path){using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(path);}}
}
