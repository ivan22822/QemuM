using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Diagnostics;
using System.Text;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Web.Script.Serialization;

static class SwitchService {
    static string ReadCommand(Stream input){
        var bytes=new List<byte>();while(true){int c=input.ReadByte();if(c<0)throw new EndOfStreamException();if(c==10)return Encoding.UTF8.GetString(bytes.ToArray());if(bytes.Count>=4096)throw new IOException("command-too-long");bytes.Add((byte)c);}
    }
    static object Reply(LocalSwitch network,string run){using(var p=Process.GetCurrentProcess())return new{protocol=1,run_id=run,pid=p.Id,created=p.StartTime.ToUniversalTime().ToFileTimeUtc(),network=network.Status()};}
    public static int Main(string[] args){
        try{
            if(args.Length==1&&args[0]=="--probe"){Console.WriteLine("{\"protocol\":1,\"helper\":\"qemum-local-switch\"}");return 0;}
            Guid run; if(args.Length!=5||args[0]!="--serve"||!Guid.TryParseExact(args[1],"N",out run)||args[2]!="qemum-switch-"+args[1])return 2;
            int[] ports=args[3].Split(',').Select(Int32.Parse).ToArray();string marker=Path.GetFullPath(args[4]);
            foreach(var path in new[]{marker,Path.GetDirectoryName(marker)})if((File.Exists(path)||Directory.Exists(path))&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("marker-reparse");
            var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
            using(var lease=new FileStream(marker,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read))
            using(var network=new LocalSwitch(ports.Length,true,null,ports)){
                var json=new JavaScriptSerializer{MaxJsonLength=65536};int quit=0;
                try{
                    while(Volatile.Read(ref quit)==0){
                        using(var pipe=new NamedPipeServerStream(args[2],PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,65536,security)){
                            var connection=pipe.BeginWaitForConnection(null,null);
                            var ready=connection.AsyncWaitHandle;
                            try{
                                while(!ready.WaitOne(500)){
                                    if(network.HasOwners()&&network.CanStop()){Interlocked.Exchange(ref quit,1);break;}
                                }
                                if(Volatile.Read(ref quit)!=0){pipe.Dispose();try{pipe.EndWaitForConnection(connection);}catch(IOException){}catch(ObjectDisposedException){}break;}
                                pipe.EndWaitForConnection(connection);
                            }finally{ready.Close();}
                            using(var timer=new Timer(_=>{try{pipe.Dispose();}catch(ObjectDisposedException){}},null,5000,Timeout.Infinite)){
                                try{
                                    object reply;
                                    try{
                                        var command=json.Deserialize<Dictionary<string,object>>(ReadCommand(pipe));
                                        if(Convert.ToInt32(command["protocol"])!=1||(string)command["run_id"]!=args[1])throw new IOException("identity-mismatch");
                                        string action=(string)command["action"];
                                        if(action=="authorize")network.Authorize(Convert.ToInt32(command["index"]),Convert.ToInt32(command["pid"]),Convert.ToInt64(command["created"]));
                                        else if(action=="quit"){if(!network.CanStop())throw new IOException("guests-still-alive");Interlocked.Exchange(ref quit,1);}
                                        else if(action!="status")throw new IOException("action-invalid");
                                        reply=Reply(network,args[1]);
                                    }catch(Exception error){reply=new{protocol=1,run_id=args[1],category=error.Message=="guests-still-alive"||error.Message=="owner-invalid"?"identity":"protocol",error=error.Message};}
                                    byte[] data=Encoding.UTF8.GetBytes(json.Serialize(reply)+"\n");pipe.Write(data,0,data.Length);pipe.Flush();
                                }catch(IOException){}catch(ObjectDisposedException){}
                            }
                        }
                    }
                }finally{lease.Dispose();try{File.Delete(marker);}catch(IOException){}}
            }
            return 0;
        }catch(Exception error){Console.Error.WriteLine(new JavaScriptSerializer().Serialize(new{category=error is System.Net.Sockets.SocketException?"port-unavailable":error is UnauthorizedAccessException?"permission":"helper-start",error=error.Message}));return 1;}
    }
}
