using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class SupportTests {
    static IEnumerable<Control> All(Control root){foreach(Control child in root.Controls){yield return child;foreach(var item in All(child))yield return item;}}
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    [STAThread]static int Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Program.TestMode=true;
        try{
            using(var main=new MainWindow(args[1],null,true,false)){
                main.Show();main.Demo();Application.DoEvents();
                var update=All(main).OfType<Button>().Single(button=>button.Text=="Обновления");
                using(var faq=new FaqWindow()){
                    faq.Show();Application.DoEvents();
                    Check(FaqWindow.Entries.Length>=35,"FAQ covers application features");
                    var search=All(faq).OfType<FieldInput>().First();var list=All(faq).OfType<ThemedList>().First();
                    search.Text="thaw";Application.DoEvents();Check(list.Items.Count>0&&list.Items.Count<FaqWindow.Entries.Length,"FAQ searches answers");
                    search.Text="no-match-unique-123456";Application.DoEvents();Check(list.Items.Count==0&&All(faq).OfType<ThemedEditor>().First().Text.Contains("Ничего не найдено"),"empty FAQ search is explicit");
                    search.Text="";var category=All(faq).OfType<ThemedSelector>().First();category.SelectedIndex=1;Application.DoEvents();Check(list.Items.Count>0&&list.Items.Count<FaqWindow.Entries.Length,"FAQ category filters results");
                    faq.ClientSize=new Size(800,580);Application.DoEvents();using(var image=new Bitmap(faq.Width,faq.Height)){faq.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(args[0],"faq.png"));}faq.Close();
                }
                Check(update.Visible&&update.Bottom<=update.Parent.ClientSize.Height&&update.Right<=update.Parent.ClientSize.Width,"update action fits header");
                using(var support=new SupportWindow(main,null)){
                    support.Show();Application.DoEvents();
                    var boxes=All(support).OfType<CheckBox>().ToArray();
                    Check(boxes.Length==7&&boxes.Count(box=>box.Checked)==1,"sensitive selections default off");
                    Check(boxes.Any(box=>box.Text=="Журнал операций снимков"&&!box.Enabled&&!box.Checked),"future snapshot report section remains gated");
                    Check(All(support).OfType<Button>().Any(button=>button.Text=="Открыть GitHub Issues"),"explicit Issues action");
                    Check(All(support).OfType<Button>().Any(button=>button.Text=="FAQ · как пользоваться QemuM"),"help opens built-in FAQ");
                    using(var image=new Bitmap(support.Width,support.Height)){support.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(args[0],"support.png"));}
                    support.ClientSize=new Size(720,650);Application.DoEvents();
                    Check(All(support).OfType<Button>().Any(button=>button.Text=="Сохранить отчёт…"&&button.Enabled),"export remains available at minimum size");
                    support.Close();
                }
                using(var image=new Bitmap(main.Width,main.Height)){main.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(args[0],"alpha-main.png"));}
                main.Close();
            }
            File.WriteAllText(Path.Combine(args[0],"support-results.json"),"{\"passed\":3}");return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[0],"support-error.txt"),error.ToString());return 1;}
    }
}
