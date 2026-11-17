using System;using System.IO;using System.Drawing;using System.Drawing.Imaging;using System.Diagnostics;using System.Runtime.InteropServices;
static class Stage2GraphicsTests {
 static int checks;static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
 static Bitmap Fixture(int width,int height){var bitmap=new Bitmap(width,height,PixelFormat.Format32bppPArgb);var data=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppPArgb);try{var row=new byte[width*4];for(int y=0;y<height;y++){for(int x=0;x<width;x++){int at=x*4,alpha=(x*17+y*3)&255;row[at]=(byte)((x+y)%(alpha+1));row[at+1]=(byte)((x*3+y*5)%(alpha+1));row[at+2]=(byte)((x*7+y)%(alpha+1));row[at+3]=(byte)alpha;}Marshal.Copy(row,0,IntPtr.Add(data.Scan0,y*data.Stride),row.Length);}}finally{bitmap.UnlockBits(data);}return bitmap;}
 static void Equal(Bitmap before,Bitmap after,string label){var bounds=new Rectangle(0,0,before.Width,before.Height);var a=before.LockBits(bounds,ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);var b=after.LockBits(bounds,ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);try{var left=new byte[before.Width*4];var right=new byte[left.Length];for(int y=0;y<before.Height;y++){Marshal.Copy(IntPtr.Add(a.Scan0,y*a.Stride),left,0,left.Length);Marshal.Copy(IntPtr.Add(b.Scan0,y*b.Stride),right,0,right.Length);for(int i=0;i<left.Length;i++)if(left[i]!=right[i])throw new Exception(label+": pixel mismatch at "+i/4+","+y+" channel "+i%4+" "+left[i]+" != "+right[i]);}}finally{before.UnlockBits(a);after.UnlockBits(b);}checks++;}
 [STAThread]static int Main(string[] args){try{
  var timings=new System.Collections.Generic.List<object>();
  foreach(var size in new[]{new Size(1,1),new Size(7,3),new Size(321,179),new Size(1031,521),new Size(3840,2160)}){
   using(var source=Fixture(size.Width,size.Height)){
    float sigma=size.Width==7?20:size.Width==1031?3:10;var watch=Stopwatch.StartNew();
    using(var reference=GaussianReference.BlurBackdrop(source,sigma)){double old=watch.Elapsed.TotalMilliseconds;watch.Restart();using(var result=MotionPaint.BlurBackdrop(source,sigma)){double current=watch.Elapsed.TotalMilliseconds;Equal(reference,result,"backdrop "+size);timings.Add(J.O("width",size.Width,"height",size.Height,"reference_ms",old,"bounded_ms",current));}}
    using(var reference=GaussianReference.BlurMask(source,sigma,Color.OrangeRed))using(var result=MotionPaint.BlurMask(source,sigma,Color.OrangeRed))Equal(reference,result,"mask "+size);
    Check(BoundedBackdropBlur.WorkingBytes<=GraphicsResources.WorkingLimit,"working pool stays within 16 MiB at "+size);
   }
  }
  string root=args[0],output=args[1];
  using(var store=new AppearanceStore(root)){
   var choices=store.Choices();Check(choices.Count>=25,"logo fixture has the bundled catalogue");
   foreach(var distro in choices)using(var image=store.AcquireLogo(distro,256)){Check(image==null||image.Width<=256&&image.Height<=256,"thumbnail dimensions are bounded");}
   Check(GraphicsResources.Bytes("logos")<=GraphicsResources.LogoLimit,"thumbnail cache stays within 4 MiB");
   using(var owned=store.AcquireLogo(choices[0],256)){
    foreach(var distro in choices)using(var ignored=store.AcquireLogo(distro,256)){}
    using(var canvas=new Bitmap(80,80))using(var graphics=Graphics.FromImage(canvas))graphics.DrawImage(owned,new Rectangle(0,0,80,80));
    Check(owned.Width>0,"cache eviction does not invalidate a displayed owned copy");
   }
  }
  Check(GraphicsResources.Bytes("logos")==0,"disposing the store releases its cache");
  File.WriteAllText(Path.Combine(output,"stage2-graphics-results.json"),J.Json().Serialize(J.O("passed",checks,"pixel_exact",true,"working_bytes",BoundedBackdropBlur.WorkingBytes,"timings",timings)));return 0;
 }catch(Exception error){File.WriteAllText(Path.Combine(args[1],"stage2-graphics-error.txt"),error.ToString());return 1;}}
}
