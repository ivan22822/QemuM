using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// Diagram placement is local UI state. It never changes the stand definition,
// guest configuration or the backend's revision and runtime ownership rules.
static class StandDiagramLayout {
    static readonly Regex Key=new Regex(@"^(vm:[A-Za-z0-9_-]{1,120}|(switch|mcast|tcp):[0-9a-f]{32})$",RegexOptions.CultureInvariant);
    static string PathFor(string root,string stand){
        Guid parsed;if(!Guid.TryParseExact(stand,"N",out parsed))throw new ArgumentException("Некорректный ID стенда.");
        return Path.Combine(root,"stand-diagrams",stand+".json");
    }
    static void Safe(string path){
        for(string part=Path.GetFullPath(path);!String.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))
            if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Настройки схемы через ссылки запрещены.");
    }
    public static Dictionary<string,Point> Read(string root,string stand){
        var result=new Dictionary<string,Point>(StringComparer.Ordinal);string path=PathFor(root,stand);Safe(path);
        if(!File.Exists(path))return result;
        if(new FileInfo(path).Length>131072)throw new IOException("Файл расположения схемы слишком большой.");
        object data=J.Json().DeserializeObject(File.ReadAllText(path));if(J.N(data,"schema")!=1)throw new IOException("Неизвестная версия расположения схемы.");
        foreach(var item in J.D(J.V(data,"nodes"))){
            if(result.Count>=256||!Key.IsMatch(item.Key))throw new IOException("Некорректный узел схемы.");
            var pair=J.A(item.Value);if(pair.Length!=2)throw new IOException("Некорректные координаты схемы.");
            int x=Convert.ToInt32(pair[0]),y=Convert.ToInt32(pair[1]);if(x<0||x>1000||y<0||y>8000)throw new IOException("Координаты схемы выходят за пределы.");
            result.Add(item.Key,new Point(x,y));
        }
        return result;
    }
    public static void Save(string root,string stand,Dictionary<string,Point> positions){
        string path=PathFor(root,stand);Safe(path);if(positions.Count>256)throw new IOException("Слишком много узлов схемы.");
        var nodes=new Dictionary<string,object>(StringComparer.Ordinal);
        foreach(var item in positions){if(!Key.IsMatch(item.Key)||item.Value.X<0||item.Value.X>1000||item.Value.Y<0||item.Value.Y>8000)throw new IOException("Некорректные координаты узла.");nodes[item.Key]=new[]{item.Value.X,item.Value.Y};}
        string folder=Path.GetDirectoryName(path);Directory.CreateDirectory(folder);Safe(path);
        string temp=Path.Combine(folder,"."+Guid.NewGuid().ToString("N")+".tmp");
        try{File.WriteAllText(temp,J.Json().Serialize(J.O("schema",1,"nodes",nodes)));Safe(path);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public static void Delete(string root,string stand){string path=PathFor(root,stand);Safe(path);if(File.Exists(path))File.Delete(path);}
}
