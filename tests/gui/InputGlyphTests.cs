using System;using System.Drawing;using System.Drawing.Imaging;using System.IO;using System.Linq;using System.Windows.Forms;
static class InputGlyphTests {
 static int checks;static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
 static Rectangle Ink(Bitmap image){int left=image.Width,top=image.Height,right=-1,bottom=-1;for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++){var c=image.GetPixel(x,y);if(c.R<100&&c.G<100&&c.B<100){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}}return right<left?Rectangle.Empty:Rectangle.FromLTRB(left,top,right+1,bottom+1);}
 [STAThread]static int Main(string[] args){try{Application.EnableVisualStyles();Program.TestMode=true;
  using(var form=new SmoothForm{ClientSize=new Size(650,280),StartPosition=FormStartPosition.Manual,Location=new Point(-2000,-2000),BackColor=Theme.Bg})
  using(var field=new FieldInput{Dock=DockStyle.None,Bounds=new Rectangle(20,20,570,52)})
  using(var reference=new TextBox{AutoSize=false,BorderStyle=BorderStyle.None,BackColor=field.BackColor,ForeColor=Theme.Text,Bounds=new Rectangle(20,100,570,90)}){
   form.Controls.Add(field);form.Controls.Add(reference);form.Show();
   foreach(int pixels in new[]{10,11,12,15,18,24})foreach(string text in new[]{"привет как дела","руфцщду gyjpq","Джщц — Подпись VM"}){
    field.Font=UiTypography.Make(pixels);reference.Font=field.Input.Font;field.Text=reference.Text=text;form.ActiveControl=null;field.PerformLayout();WindowFrame.PaintLive(form);
    using(var whole=WindowFrame.Capture(form))using(var actual=whole.Clone(form.RectangleToClient(field.Input.RectangleToScreen(field.Input.ClientRectangle)),PixelFormat.Format32bppRgb))using(var expected=whole.Clone(form.RectangleToClient(reference.RectangleToScreen(reference.ClientRectangle)),PixelFormat.Format32bppRgb)){
     var a=Ink(actual);var b=Ink(expected);actual.Save(Path.Combine(args[1],"input-glyph-"+pixels+".png"));
     Check(!a.IsEmpty&&a.Size==b.Size,"all native glyph extents fit at "+pixels+"px: actual="+a+" reference="+b+" text="+text);
     int different=0;for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++){var ca=actual.GetPixel(a.Left+x,a.Top+y);var cb=expected.GetPixel(b.Left+x,b.Top+y);if((ca.R<100&&ca.G<100&&ca.B<100)!=(cb.R<100&&cb.G<100&&cb.B<100))different++;}
     Check(different==0,"descenders match the unclipped native reference at "+pixels+"px: "+different);
    }
   }
   field.Height=40;field.Font=UiTypography.Make(11);field.Text="Привет";form.ActiveControl=reference;field.PerformLayout();WindowFrame.PaintLive(form);
   using(var whole=WindowFrame.Capture(form))using(var preview=whole.Clone(field.Bounds,PixelFormat.Format32bppRgb)){
    preview.Save(Path.Combine(args[1],"input-centered-preview.png"));var ink=Ink(preview);
    Check(Math.Abs(ink.Top+ink.Height/2.0-field.Height/2.0)<=2,"visible glyphs are optically centered in a 40px field: "+ink);
   }
   form.Close();
  }
  File.WriteAllText(Path.Combine(args[1],"input-glyph-results.json"),"{\"passed\":"+checks+"}");return 0;
 }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"input-glyph-error.txt"),error.ToString());return 1;}}
}
