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
                foreach(var button in update.Controls.OfType<FlowLayoutPanel>().SelectMany(flow=>flow.Controls.OfType<Button>())){
                    if(!button.Parent.ClientRectangle.Contains(button.Bounds))throw new Exception("Updater footer buttons must fit the window.");
                }
                Save(update,Path.Combine(args[0],"update-design.png"));
                var action=update.Controls.OfType<FlowLayoutPanel>().SelectMany(flow=>flow.Controls.OfType<Button>()).Single(button=>button.Text=="Установить обновление");
                if(action.Enabled)throw new Exception("Launch must not be offered before an update succeeds.");
                update.UpdateComplete(new QemuM.Update.Feed{version="0.1.0-alpha.7",build=3});
                Application.DoEvents();
                if(action.Text!="Запустить менеджер"||!action.Enabled||update.AcceptButton!=action)throw new Exception("Successful update must offer launch as the default action.");
                if(!action.Parent.ClientRectangle.Contains(action.Bounds))throw new Exception("Launch button must fit the updater footer.");
                bool missing=false;try{update.ManagerStartInfo();}catch(IOException){missing=true;}
                if(!missing)throw new Exception("Missing installation loader must be rejected.");
                action.PerformClick();
                if(update.IsDisposed||!action.Enabled)throw new Exception("Failed launch must keep the updater open for retry.");
                Save(update,Path.Combine(args[0],"update-complete-design.png"));update.Close();
            }
            using(var component=new ComponentProgress("Python")){
                component.Report("Загрузка",50,100);component.Report("Проверка");component.Report("Готово",1,1);
            }
            using(var parent=new Panel{BackColor=BootstrapUI.Bg})using(var button=BootstrapUI.Button("Установить обновление",true)){
                parent.Controls.Add(button);button.Size=new Size(220,44);
                foreach(bool enabled in new[]{true,false}){
                    button.Enabled=enabled;using(var image=new Bitmap(button.Width,button.Height)){
                        button.DrawToBitmap(image,button.ClientRectangle);
                        if(image.GetPixel(0,button.Height-1).ToArgb()!=parent.BackColor.ToArgb())throw new Exception("Updater button corners must match the surrounding background.");
                    }
                }
            }
            File.WriteAllText(Path.Combine(args[0],"bootstrap-results.json"),"{\"passed\":9}");return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[0],"bootstrap-error.txt"),error.ToString());return 1;}
    }
    static void Save(Form form,string path){using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(path);}}
}
