using System;
using System.Drawing;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Original Google Fonts static faces, embedded for offline GDI and GDI+ rendering.
static class UiTypography {
    static readonly Dictionary<int,System.Drawing.Text.PrivateFontCollection> collections=new Dictionary<int,System.Drawing.Text.PrivateFontCollection>();
    static readonly Dictionary<int,FontFamily> families=new Dictionary<int,FontFamily>();
    static readonly List<IntPtr> storage=new List<IntPtr>(),registrations=new List<IntPtr>();
    [DllImport("gdi32.dll")]static extern IntPtr AddFontMemResourceEx(IntPtr memory,uint length,IntPtr reserved,ref uint count);
    static UiTypography(){foreach(int weight in new[]{400,500,600,700,800,900}){using(var stream=typeof(UiTypography).Assembly.GetManifestResourceStream("QemuM.Unbounded."+weight)){if(stream==null)throw new InvalidOperationException("Embedded Unbounded face is missing: "+weight);var bytes=new byte[stream.Length];int position=0,read;while(position<bytes.Length&&(read=stream.Read(bytes,position,bytes.Length-position))>0)position+=read;IntPtr memory=Marshal.AllocCoTaskMem(bytes.Length);Marshal.Copy(bytes,0,memory,bytes.Length);storage.Add(memory);uint count=0;registrations.Add(AddFontMemResourceEx(memory,(uint)bytes.Length,IntPtr.Zero,ref count));var collection=new System.Drawing.Text.PrivateFontCollection();collection.AddMemoryFont(memory,bytes.Length);collections[weight]=collection;families[weight]=collection.Families[0];}}}
    public static FontFamily Family(int weight){return families[weight];}
    public static FontStyle Style(int weight){return families[weight].IsStyleAvailable(FontStyle.Regular)?FontStyle.Regular:FontStyle.Bold;}
    public static Font Make(float pixels,int weight=400){return new Font(Family(weight),pixels,Style(weight),GraphicsUnit.Pixel);}
    public static Font Points(float points,FontStyle style=FontStyle.Regular){return Make(points*96/72,(style&FontStyle.Bold)!=0?700:400);}
}
