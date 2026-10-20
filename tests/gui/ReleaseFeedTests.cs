using System;
using System.IO;
using QemuM.Update;

static class ReleaseFeedTests {
    static int Main(string[] args){
        string work=Path.Combine(args[0],"feed-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
        try{
            var feed=Core.Latest(work);
            if(args[1]=="-"){
                File.WriteAllText(Path.Combine(args[0],"feed-results.json"),"{\"connection\":\"ok\"}");return 0;
            }
            if(feed==null||feed.version!=args[1])throw new Exception("Published version was not discovered.");
            string zip=Path.Combine(work,"published.zip");Core.Download(feed.url,zip,128*1024*1024);
            var package=Core.Stage(zip,feed.sha256,work);
            if(package.version!=feed.version)throw new Exception("Published package version mismatch.");
            File.WriteAllText(Path.Combine(args[0],"feed-results.json"),"{\"version\":\""+feed.version+"\",\"download_and_hashes\":\"ok\"}");
            return 0;
        }catch(Exception error){File.WriteAllText(Path.Combine(args[0],"feed-error.txt"),error.ToString());return 1;}
    }
}
