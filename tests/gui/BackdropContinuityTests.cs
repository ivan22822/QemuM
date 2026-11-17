using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class BackdropContinuityTests {
    static int checks;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static void Wait(Func<bool> ready){var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<10000){Application.DoEvents();Thread.Sleep(2);}Check(ready(),"operation completed");}
    static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);}
    [DllImport("user32.dll")]static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")]static extern bool BitBlt(IntPtr target,int x,int y,int width,int height,IntPtr source,int sx,int sy,uint operation);
    static Bitmap VisibleClient(Form form){
        var bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height,PixelFormat.Format32bppPArgb);
        using(var graphics=Graphics.FromImage(bitmap)){
            var target=graphics.GetHdc();var source=GetDC(form.Handle);
            try{Check(BitBlt(target,0,0,bitmap.Width,bitmap.Height,source,0,0,0x00CC0020),"capture this test owner's visible client");}
            finally{ReleaseDC(form.Handle,source);graphics.ReleaseHdc(target);}
        }
        return bitmap;
    }
    static void OwnerPixels(string output){
        using(var owner=new Form{ClientSize=new Size(640,420),StartPosition=FormStartPosition.Manual,Location=new Point(30,30),BackColor=Color.White}){
            var back=new Panel{Bounds=new Rectangle(40,60,300,220),BackColor=Color.Crimson};
            var front=new Panel{Bounds=new Rectangle(120,100,280,220),BackColor=Color.CornflowerBlue};
            owner.Controls.Add(back);owner.Controls.Add(front);front.BringToFront();
            owner.Show();WindowFrame.PaintLive(owner);
            using(var visible=VisibleClient(owner))using(var captured=WindowFrame.Capture(owner)){
                visible.Save(Path.Combine(output,"owner-visible.png"));captured.Save(Path.Combine(output,"owner-captured.png"));
                Check(captured.GetPixel(180,160).ToArgb()==visible.GetPixel(180,160).ToArgb(),"background snapshot preserves overlapping native control order");
                Check(captured.GetPixel(70,80).ToArgb()==visible.GetPixel(70,80).ToArgb(),"background snapshot preserves client coordinates");
            }
            using(var dialog=new Form{ClientSize=new Size(170,120),StartPosition=FormStartPosition.Manual,Location=new Point(740,50)})
            using(var backdrop=new ReferenceDialogBackdrop(dialog,(source,sigma)=>{
                Check(source.GetPixel(180,160).ToArgb()==Color.CornflowerBlue.ToArgb(),"blur worker always receives the original owner scene");
                var effect=new Bitmap(source.Width,source.Height);using(var graphics=Graphics.FromImage(effect))graphics.Clear(Color.Lime);return effect;
            }))using(var entrance=new DialogEntrance(dialog)){
                dialog.Show(owner);var ready=ReferenceDialogBackdrop.WaitFor(dialog);Wait(()=>ready.IsCompleted);
                var layer=owner.Controls.OfType<ReferenceBackdropSurface>().Single();
                Check(layer.Blend==1&&dialog.Opacity==0,"background transition finishes before the first dialog frame");
                Wait(()=>dialog.Opacity==1);
                int visibilityChanges=0;layer.VisibleChanged+=(s,e)=>visibilityChanges++;int generation=layer.Generation;
                owner.ClientSize=new Size(650,430);Wait(()=>layer.Generation>generation);
                Check(layer.Visible&&visibilityChanges==0,"resize refresh never hides the painted background layer");
                dialog.Close();
            }
            owner.Close();
        }
    }
    static void RealOwnerScene(string root,string output){
        Program.TestMode=false;
        using(var main=new MainWindow(root,null,true,false)){
            main.Demo();main.Show();Wait(()=>main.Opacity==1&&Field<StateStage>(main,"machineStage").BackgroundReady);WindowFrame.PaintLive(main);
            using(var visible=VisibleClient(main))using(var captured=WindowFrame.Capture(main)){
                visible.Save(Path.Combine(output,"workspace-visible.png"));captured.Save(Path.Combine(output,"workspace-captured.png"));
                int total=0,different=0;for(int y=4;y<visible.Height-4;y+=8)for(int x=4;x<visible.Width-4;x+=8){total++;if(visible.GetPixel(x,y).ToArgb()!=captured.GetPixel(x,y).ToArgb())different++;}
                Check(different<=total/100,"native capture preserves the visible workspace: "+different+"/"+total+" changed samples");
            }
            using(var dialog=new Form{ClientSize=new Size(170,120),StartPosition=FormStartPosition.Manual,Location=new Point(-2000,-2000)})
            using(var backdrop=new ReferenceDialogBackdrop(dialog,(source,sigma)=>(Bitmap)source.Clone()))using(var entrance=new DialogEntrance(dialog)){
                dialog.Show(main);Wait(()=>ReferenceDialogBackdrop.WaitFor(dialog).IsCompleted);
                var layer=main.Controls.OfType<ReferenceBackdropSurface>().Single();
                Check(layer.Bounds==main.ClientRectangle,"backdrop covers the full owner, including its docked header and footer: "+layer.Bounds+" vs "+main.ClientRectangle);
                using(var displayed=new Bitmap(layer.Width,layer.Height)){
                    layer.DrawToBitmap(displayed,layer.ClientRectangle);displayed.Save(Path.Combine(output,"workspace-overlay.png"));
                    int different=0;for(int y=8;y<displayed.Height-8;y+=13)for(int x=8;x<displayed.Width-8;x+=13)if(displayed.GetPixel(x,y).ToArgb()!=layer.Snapshot.GetPixel(x,y).ToArgb())different++;
                    Check(different==0,"displayed backdrop preserves all client coordinates without squeezing or repeating the header");
                }
                main.ClientSize=new Size(main.ClientSize.Width-20,main.ClientSize.Height-20);Application.DoEvents();
                Check(layer.Bounds==main.ClientRectangle,"overlay still covers the entire owner after resizing docked chrome");
                dialog.Close();
            }
            main.Close();
        }
    }
    static void StableStatus(string root){
        Program.TestMode=true;
        using(var main=new MainWindow(root,null,true,false))using(var release=new ManualResetEventSlim()){
            main.Demo();main.Show();Application.DoEvents();
            var footer=Field<Label>(main,"footer");var progress=Field<ProgressBar>(main,"progress");
            var content=main.Controls.OfType<SplitContainer>().Single();var bounds=content.Bounds;
            bool done=false;
            main.Read("Reading fixture...",()=>{release.Wait();return new object();},value=>done=true);
            Wait(()=>footer.Text=="Reading fixture...");
            Check(content.Bounds==bounds,"delayed read status never shifts the workspace");
            release.Set();Wait(()=>done);
            Check(content.Bounds==bounds,"finishing the read preserves workspace geometry");
            foreach(string message in new[]{"Loading...","Saved",""}){footer.Text=message;progress.Visible=message.Length>0;Application.DoEvents();Check(content.Bounds==bounds,"write and empty status preserve workspace geometry");}
            main.Close();
        }
    }
    [STAThread]static int Main(string[] args){
        try{Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Program.TestMode=false;OwnerPixels(args[1]);RealOwnerScene(args[0],args[1]);StableStatus(args[0]);
            File.WriteAllText(Path.Combine(args[1],"backdrop-continuity-results.json"),"{\"passed\":"+checks+"}");return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"backdrop-continuity-error.txt"),error.ToString());return 1;}
    }
}
