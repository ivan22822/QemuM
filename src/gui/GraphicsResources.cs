using System;using System.Collections.Generic;using System.Drawing;using System.Drawing.Imaging;using System.Linq;using System.Runtime.InteropServices;using System.Threading;using System.Threading.Tasks;

// Accounting contains names and byte counts, never references to controls/bitmaps.
static class GraphicsResources {
 [ThreadStatic]static CancellationToken cancellation;
 internal static CancellationToken Cancellation{get{return cancellation;}}
 internal static T WithCancellation<T>(Func<T> work,CancellationToken token){var previous=cancellation;cancellation=token;try{token.ThrowIfCancellationRequested();return work();}finally{cancellation=previous;}}
 internal const long WorkingLimit=16*1024*1024,LogoLimit=4*1024*1024,DecorLimit=24*1024*1024;
 sealed class Entry{internal string Kind;internal long Bytes;}
 static readonly object gate=new object();static readonly Dictionary<string,Entry> values=new Dictionary<string,Entry>();static readonly Dictionary<string,long> peaks=new Dictionary<string,long>();static int next;
 internal static string Owner(string name){return name+"#"+Interlocked.Increment(ref next);}
 internal static void Set(string owner,string kind,long bytes){lock(gate){if(bytes==0)values.Remove(owner);else values[owner]=new Entry{Kind=kind,Bytes=bytes};long total=values.Values.Where(v=>v.Kind==kind).Sum(v=>v.Bytes),peak;peaks.TryGetValue(kind,out peak);peaks[kind]=Math.Max(total,peak);}}
 internal static long Bytes(string kind){lock(gate)return values.Values.Where(v=>v.Kind==kind).Sum(v=>v.Bytes);}
 internal static long ImageBytes(Image image){return image==null?0:(long)image.Width*image.Height*4;}
 internal static long PeakBytes(string kind){lock(gate){long value;return peaks.TryGetValue(kind,out value)?value:0;}}
 internal static bool TryDecor(string owner,long bytes){lock(gate){Entry old;long previous=values.TryGetValue(owner,out old)&&old.Kind=="decor"?old.Bytes:0;if(values.Values.Where(v=>v.Kind=="decor").Sum(v=>v.Bytes)-previous+bytes>DecorLimit)return false;Set(owner,"decor",bytes);return true;}}
}

// Shared bounded scratch space. Tile halos preserve the Gaussian kernel and edge rules.
// No full-image byte/float arrays are retained or allocated here.
static class BoundedBackdropBlur {
 static readonly object gate=new object();static readonly string owner=GraphicsResources.Owner("blur-workspace");
 static float[] horizontal=new float[0];static byte[] output=new byte[0];static int[] columns=new int[0],rows=new int[0];
 static readonly byte[][] inputs=new byte[4][]{new byte[0],new byte[0],new byte[0],new byte[0]};
 static long Bytes{get{return (long)horizontal.Length*4+output.Length+(long)(columns.Length+rows.Length)*4+inputs.Sum(v=>(long)v.Length);}}
 internal static long WorkingBytes{get{lock(gate)return Bytes;}}
 static void Ensure(int samples,int pixels,int columnCount,int rowCount,int inputBytes){
  long retained=(long)Math.Max(horizontal.Length,samples)*4+Math.Max(output.Length,pixels)+(long)(Math.Max(columns.Length,columnCount)+Math.Max(rows.Length,rowCount))*4+inputs.Sum(v=>(long)Math.Max(v.Length,inputBytes));
  if(retained>GraphicsResources.WorkingLimit){horizontal=new float[0];output=new byte[0];columns=new int[0];rows=new int[0];for(int i=0;i<4;i++)inputs[i]=new byte[0];}
  if(horizontal.Length<samples)horizontal=new float[samples];if(output.Length<pixels)output=new byte[pixels];if(columns.Length<columnCount)columns=new int[columnCount];if(rows.Length<rowCount)rows=new int[rowCount];
  for(int i=0;i<4;i++)if(inputs[i].Length<inputBytes)inputs[i]=new byte[inputBytes];
  if(Bytes>GraphicsResources.WorkingLimit)throw new InvalidOperationException("Превышен рабочий бюджет размытия.");GraphicsResources.Set(owner,"working",Bytes);
 }
 static long Required(int width,int height,int radius,int tile,int band){int length=radius*2+1;return (long)Math.Min(height,band+radius*2)*tile*12+(long)band*tile*4+(long)(tile+band)*length*4+(long)Math.Min(width,tile+radius*2)*16;}
 internal static Bitmap Render(Bitmap source,float sigma){return Render(source,sigma,false,Color.Empty);}
 internal static Bitmap Mask(Bitmap source,float sigma,Color tint){return Render(source,sigma,true,tint);}
 static Bitmap Render(Bitmap source,float sigma,bool alpha,Color tint){
  if(!(sigma>0)||Single.IsInfinity(sigma))throw new ArgumentOutOfRangeException("sigma");
  GraphicsResources.Cancellation.ThrowIfCancellationRequested();
  lock(gate){
   GraphicsResources.Cancellation.ThrowIfCancellationRequested();
   int width=source.Width,height=source.Height,radius=(int)Math.Ceiling(sigma*3),length=checked(radius*2+1);
   var kernel=new double[length];double sum=0;for(int k=-radius;k<=radius;k++){kernel[k+radius]=Math.Exp(-k*k/(2*sigma*sigma));sum+=kernel[k+radius];}for(int k=0;k<length;k++)kernel[k]/=sum;
   int tile=Math.Min(1024,width),band=Math.Min(512,height);
   while(Required(width,height,radius,tile,band)>GraphicsResources.WorkingLimit){if(band>1)band=Math.Max(1,band/2);else if(tile>1)tile=Math.Max(1,tile/2);else throw new InvalidOperationException("Размер ядра размытия превышает бюджет.");}
   Ensure(checked(Math.Min(height,band+radius*2)*tile*3),checked(band*tile*4),checked(tile*length),checked(band*length),checked(Math.Min(width,tile+radius*2)*4));
   int workers=Math.Max(1,Math.Min(4,Environment.ProcessorCount-1)),channels=alpha?1:3;var options=new ParallelOptions{MaxDegreeOfParallelism=workers};
   var result=new Bitmap(width,height,PixelFormat.Format32bppPArgb);var rect=new Rectangle(0,0,width,height);BitmapData sourceData=null,targetData=null;bool complete=false;
   try{
    sourceData=source.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);targetData=result.LockBits(rect,ImageLockMode.WriteOnly,PixelFormat.Format32bppPArgb);
    for(int left=0;left<width;left+=tile){
     int count=Math.Min(tile,width-left),sourceLeft=Math.Max(0,left-radius),sourceRight=Math.Min(width-1,left+count-1+radius),inputWidth=sourceRight-sourceLeft+1;
     for(int x=0;x<count;x++)for(int k=-radius;k<=radius;k++){int at=left+x+k;columns[x*length+k+radius]=alpha&&(at<0||at>=width)?-1:(Math.Max(0,Math.Min(width-1,at))-sourceLeft)*4;}
     for(int top=0;top<height;top+=band){
      GraphicsResources.Cancellation.ThrowIfCancellationRequested();
      int lines=Math.Min(band,height-top),first=Math.Max(0,top-radius),last=Math.Min(height-1,top+lines-1+radius),inputRows=last-first+1;
      for(int y=0;y<lines;y++)for(int k=-radius;k<=radius;k++){int at=top+y+k;rows[y*length+k+radius]=alpha&&(at<0||at>=height)?-1:(Math.Max(0,Math.Min(height-1,at))-first)*count*channels;}
      Parallel.For(0,workers,options,worker=>{
       var input=inputs[worker];for(int y=worker;y<inputRows;y+=workers){Marshal.Copy(IntPtr.Add(sourceData.Scan0,(first+y)*sourceData.Stride+sourceLeft*4),input,0,inputWidth*4);
        for(int x=0;x<count;x++){int offset=x*length,index=(y*count+x)*channels;
         if(alpha){double value=0;int start=Math.Max(-radius,-(left+x))+radius,end=Math.Min(radius,width-1-(left+x))+radius;for(int k=start;k<=end;k++)value+=input[columns[offset+k]+3]*kernel[k];horizontal[index]=(float)value;}
         else{double blue=0,green=0,red=0;for(int k=0;k<length;k++){int at=columns[offset+k];double weight=kernel[k];blue+=input[at]*weight;green+=input[at+1]*weight;red+=input[at+2]*weight;}horizontal[index]=(float)blue;horizontal[index+1]=(float)green;horizontal[index+2]=(float)red;}
        }
       }
      });
      Parallel.For(0,workers,options,worker=>{
       for(int y=worker;y<lines;y+=workers)for(int x=0;x<count;x++){int offset=y*length,column=x*channels,index=(y*count+x)*4;
        if(alpha){double value=0;int start=Math.Max(-radius,-(top+y))+radius,end=Math.Min(radius,height-1-(top+y))+radius;for(int k=start;k<=end;k++)value+=horizontal[rows[offset+k]+column]*kernel[k];output[index]=(byte)Math.Round(tint.B*value/255);output[index+1]=(byte)Math.Round(tint.G*value/255);output[index+2]=(byte)Math.Round(tint.R*value/255);output[index+3]=(byte)Math.Round(value);}
        else{double blue=0,green=0,red=0;for(int k=0;k<length;k++){int at=rows[offset+k]+column;double weight=kernel[k];blue+=horizontal[at]*weight;green+=horizontal[at+1]*weight;red+=horizontal[at+2]*weight;}output[index]=(byte)Math.Max(0,Math.Min(255,Math.Round(blue)));output[index+1]=(byte)Math.Max(0,Math.Min(255,Math.Round(green)));output[index+2]=(byte)Math.Max(0,Math.Min(255,Math.Round(red)));output[index+3]=255;}
       }
      });
      for(int y=0;y<lines;y++)Marshal.Copy(output,y*count*4,IntPtr.Add(targetData.Scan0,(top+y)*targetData.Stride+left*4),count*4);
     }
    }
    complete=true;
   }finally{try{if(sourceData!=null)source.UnlockBits(sourceData);}finally{if(targetData!=null)result.UnlockBits(targetData);if(!complete)result.Dispose();}}
   if(!alpha)using(var graphics=Graphics.FromImage(result))using(var shade=new SolidBrush(Color.FromArgb(0x49,0x1c,0x18,0x13)))graphics.FillRectangle(shade,rect);
   return result;
  }
 }
}
