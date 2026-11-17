using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;

// Original 24x24 inline SVG geometry from qemu-motion-dialogs.html.
static class ReferenceIcons {
    static readonly Dictionary<string,string> source=new Dictionary<string,string>{
        {"monitor","<rect x=\"3\" y=\"4\" width=\"18\" height=\"13\" rx=\"2\"/><path d=\"M8 21h8m-4-4v4\"/>"},
        {"play","<path d=\"m8 5 11 7-11 7Z\"/>"},
        {"pause","<path d=\"M8 5v14M16 5v14\"/>"},
        {"power","<path d=\"M12 3v9m-5-6a8 8 0 1 0 10 0\"/>"},
        {"save","<path d=\"M5 3h12l4 4v14H3V3Zm2 0v6h10V3M7 21v-8h10v8\"/>"},
        {"cpu","<rect x=\"6\" y=\"6\" width=\"12\" height=\"12\" rx=\"2\"/><rect x=\"9\" y=\"9\" width=\"6\" height=\"6\"/><path d=\"M9 2v4m6-4v4M9 18v4m6-4v4M2 9h4m-4 6h4m12-6h4m-4 6h4\"/>"},
        {"layers","<path d=\"m12 3 10 5-10 5L2 8Zm-10 9 10 5 10-5M2 16l10 5 10-5\"/>"},
        {"shield","<path d=\"M12 3 3 6v6c0 5 9 9 9 9s9-4 9-9V6Z\"/>"},
        {"sliders-horizontal","<path d=\"M3 6h7m4 0h7M3 12h12m4 0h2M3 18h3m4 0h11\"/><circle cx=\"12\" cy=\"6\" r=\"2\"/><circle cx=\"17\" cy=\"12\" r=\"2\"/><circle cx=\"8\" cy=\"18\" r=\"2\"/>"},
        {"triangle-alert","<path d=\"m12 3 10 18H2Zm0 5v6m0 3v1\"/>"},
        {"loader-circle","<path d=\"M21 12a9 9 0 1 1-5-8\"/>"},
        {"chevron-down","<path d=\"m6 9 6 6 6-6\"/>"},
        {"chevron-right","<path d=\"m9 6 6 6-6 6\"/>"},
        {"plus","<path d=\"M12 5v14M5 12h14\"/>"},
        {"dialog:close","<path d=\"m6 6 12 12M18 6 6 18\"/>"},
        {"dialog:layers","<path d=\"m12 3 10 5-10 5L2 8Zm-10 9 10 5 10-5M2 16l10 5 10-5\"/>"},
        {"dialog:network","<rect x=\"8\" y=\"2\" width=\"8\" height=\"5\" rx=\"1\"/><rect x=\"2\" y=\"17\" width=\"7\" height=\"5\" rx=\"1\"/><rect x=\"15\" y=\"17\" width=\"7\" height=\"5\" rx=\"1\"/><path d=\"M12 7v5H5v5m7-5h7v5\"/>"},
        {"dialog:settings","<path d=\"M3 6h18M3 12h18M3 18h18M8 3v6m8 0v6M8 15v6\"/>"},
        {"dialog:plus","<path d=\"M12 5v14M5 12h14\"/>"},
        {"dialog:monitor","<rect x=\"3\" y=\"3\" width=\"18\" height=\"13\" rx=\"2\"/><path d=\"M12 16v5m-5 0h10\"/>"},
        {"dialog:shield","<path d=\"m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6Z\"/><path d=\"m8 12 3 3 5-6\"/>"},
        {"dialog:arrow","<path d=\"M5 12h14m-5-5 5 5-5 5\"/>"},
        {"dialog:folder","<path d=\"M3 7V4h7l2 3h9v13H3Z\"/>"},
        {"dialog:alert","<path d=\"m12 3 10 18H2Zm0 5v6m0 3v1\"/>"},
        {"dialog:check","<path d=\"m5 12 5 5L20 6\"/>"}
    };
    public static void Draw(Graphics graphics,string name,RectangleF target,Color color){
        string svg;if(!source.TryGetValue(name,out svg))return;
        var saved=graphics.Save();graphics.TranslateTransform(target.X,target.Y);graphics.ScaleTransform(target.Width/24,target.Height/24);graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(var pen=new Pen(color,1.6F)){pen.StartCap=pen.EndCap=LineCap.Round;pen.LineJoin=LineJoin.Round;
            foreach(Match shape in Regex.Matches(svg,@"<(path|rect|circle)\b([^>]*)>")){
                var attributes=new Dictionary<string,string>();foreach(Match attr in Regex.Matches(shape.Groups[2].Value,"([a-z]+)=\"([^\"]*)\""))attributes[attr.Groups[1].Value]=attr.Groups[2].Value;
                using(var path=new GraphicsPath()){
                    if(shape.Groups[1].Value=="path")ParsePath(path,attributes["d"]);
                    else if(shape.Groups[1].Value=="circle"){float radius=Number(attributes,"r");path.AddEllipse(Number(attributes,"cx")-radius,Number(attributes,"cy")-radius,radius*2,radius*2);}
                    else{float x=Number(attributes,"x"),y=Number(attributes,"y"),w=Number(attributes,"width"),h=Number(attributes,"height"),r=Number(attributes,"rx");if(r<=0)path.AddRectangle(new RectangleF(x,y,w,h));else{path.AddArc(x,y,r*2,r*2,180,90);path.AddArc(x+w-r*2,y,r*2,r*2,270,90);path.AddArc(x+w-r*2,y+h-r*2,r*2,r*2,0,90);path.AddArc(x,y+h-r*2,r*2,r*2,90,90);path.CloseFigure();}}
                    graphics.DrawPath(pen,path);
                }
            }
        }graphics.Restore(saved);
    }
    static float Number(Dictionary<string,string> attributes,string key){string value;return attributes.TryGetValue(key,out value)?Single.Parse(value,CultureInfo.InvariantCulture):0;}
    static void ParsePath(GraphicsPath path,string data){
        var tokens=Regex.Matches(data,@"[a-zA-Z]|[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?");int index=0;char command='M';PointF point=PointF.Empty,start=PointF.Empty,last=PointF.Empty;char previous=' ';
        Func<float> read=()=>Single.Parse(tokens[index++].Value,CultureInfo.InvariantCulture);
        while(index<tokens.Count){if(Char.IsLetter(tokens[index].Value[0]))command=tokens[index++].Value[0];bool relative=Char.IsLower(command);char kind=Char.ToUpperInvariant(command);
            if(kind=='Z'){path.CloseFigure();point=start;previous=kind;continue;}
            float x=point.X,y=point.Y;
            if(kind=='H'||kind=='V'){float n=read();var end=kind=='H'?new PointF(n+(relative?x:0),y):new PointF(x,n+(relative?y:0));path.AddLine(point,end);point=end;}
            else if(kind=='M'||kind=='L'){var end=new PointF(read()+(relative?x:0),read()+(relative?y:0));if(kind=='M'){path.StartFigure();start=end;command=relative?'l':'L';}else path.AddLine(point,end);point=end;}
            else if(kind=='C'){var a=new PointF(read()+(relative?x:0),read()+(relative?y:0));var b=new PointF(read()+(relative?x:0),read()+(relative?y:0));var end=new PointF(read()+(relative?x:0),read()+(relative?y:0));path.AddBezier(point,a,b,end);point=end;last=b;}
            else if(kind=='S'){var a=previous=='C'||previous=='S'?new PointF(x*2-last.X,y*2-last.Y):point;var b=new PointF(read()+(relative?x:0),read()+(relative?y:0));var end=new PointF(read()+(relative?x:0),read()+(relative?y:0));path.AddBezier(point,a,b,end);point=end;last=b;}
            else if(kind=='A'){double rx=read(),ry=read(),angle=read();bool large=read()!=0,sweep=read()!=0;var end=new PointF(read()+(relative?x:0),read()+(relative?y:0));Arc(path,point,end,rx,ry,angle,large,sweep);point=end;}
            else throw new InvalidOperationException("Unsupported original SVG command: "+command);
            previous=kind;
        }
    }
    static void Arc(GraphicsPath path,PointF from,PointF to,double rx,double ry,double angle,bool large,bool sweep){
        rx=Math.Abs(rx);ry=Math.Abs(ry);if(rx==0||ry==0){path.AddLine(from,to);return;}if(from==to)return;
        double phi=angle*Math.PI/180,cos=Math.Cos(phi),sin=Math.Sin(phi),dx=(from.X-to.X)/2,dy=(from.Y-to.Y)/2,x=cos*dx+sin*dy,y=-sin*dx+cos*dy;
        double lambda=x*x/(rx*rx)+y*y/(ry*ry);if(lambda>1){rx*=Math.Sqrt(lambda);ry*=Math.Sqrt(lambda);}
        double coefficient=(large==sweep?-1:1)*Math.Sqrt(Math.Max(0,(rx*rx*ry*ry-rx*rx*y*y-ry*ry*x*x)/(rx*rx*y*y+ry*ry*x*x)));
        double cx1=coefficient*rx*y/ry,cy1=-coefficient*ry*x/rx,cx=cos*cx1-sin*cy1+(from.X+to.X)/2,cy=sin*cx1+cos*cy1+(from.Y+to.Y)/2;
        double a=Math.Atan2((y-cy1)/ry,(x-cx1)/rx),b=Math.Atan2((-y-cy1)/ry,(-x-cx1)/rx),delta=b-a;if(!sweep&&delta>0)delta-=Math.PI*2;if(sweep&&delta<0)delta+=Math.PI*2;
        int segments=(int)Math.Ceiling(Math.Abs(delta)/(Math.PI/2));double step=delta/segments;
        Func<double,PointF> point=t=>new PointF((float)(cx+rx*cos*Math.Cos(t)-ry*sin*Math.Sin(t)),(float)(cy+rx*sin*Math.Cos(t)+ry*cos*Math.Sin(t)));
        Func<double,PointF> derivative=t=>new PointF((float)(-rx*cos*Math.Sin(t)-ry*sin*Math.Cos(t)),(float)(-rx*sin*Math.Sin(t)+ry*cos*Math.Cos(t)));
        for(int i=0;i<segments;i++){double end=a+step,k=4.0/3*Math.Tan(step/4);var p0=point(a);var p1=point(end);var d0=derivative(a);var d1=derivative(end);path.AddBezier(p0,new PointF(p0.X+(float)k*d0.X,p0.Y+(float)k*d0.Y),new PointF(p1.X-(float)k*d1.X,p1.Y-(float)k*d1.Y),p1);a=end;}
    }
    public static Bitmap Image(string name,int size,Color color){var image=new Bitmap(size,size);using(var g=Graphics.FromImage(image))Draw(g,name,new RectangleF(0,0,size,size),color);return image;}
}
