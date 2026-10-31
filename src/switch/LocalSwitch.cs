using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Web.Script.Serialization;
using System.Diagnostics;
using System.Text;

// Wire-compatible Ethernet switch core. No guest configuration or OS network changes.
// Production entrypoint is SwitchService; the stdio entrypoint below is test-only.
sealed class LocalSwitch : IDisposable {
    const int MaxFrame=65535,MaxMacs=4096,QueueFrames=64;
    readonly object gate=new object();
    readonly Dictionary<string,Learned> macs=new Dictionary<string,Learned>();
    readonly List<Port> ports=new List<Port>();
    readonly Queue<object> events=new Queue<object>();
    string lastEvent="";long lastOverflow=-1000;
    volatile bool closed;
    readonly bool guarded;
    readonly Func<long> clock;
    sealed class Learned { public Port Port;public long Seen; }
    sealed class Port {
        public TcpListener Listener;public TcpClient Client;public Thread AcceptThread;
        public BlockingCollection<byte[]> Queue;public long Generation;
        public int OwnerPid;public long OwnerCreated;public int Index;
    }
    public int[] Addresses { get { return ports.Select(p=>((IPEndPoint)p.Listener.LocalEndpoint).Port).ToArray(); } }
    public long Received,Sent,Dropped,Rejected;
    public LocalSwitch(int count,bool guarded=false,Func<long> clock=null,int[] requested=null){
        this.guarded=guarded;this.clock=clock??(()=>Environment.TickCount&0xffffffffL);
        if(count<2||count>64)throw new ArgumentOutOfRangeException("count");
        if(requested!=null&&(requested.Length!=count||requested.Any(p=>p<1024||p>65535)||requested.Distinct().Count()!=count))throw new ArgumentException("ports-invalid");
        try{
            for(int i=0;i<count;i++){
                var p=new Port{Index=i,Listener=new TcpListener(IPAddress.Loopback,requested==null?0:requested[i])};
                p.Listener.Server.ExclusiveAddressUse=true;p.Listener.Start(1);ports.Add(p);
            }
            Note("helper-ready",-1);
            foreach(var p in ports){p.AcceptThread=new Thread(()=>Accept(p)){IsBackground=true};p.AcceptThread.Start();}
        }catch{Dispose();throw;}
    }
    void Note(string category,int index){lock(gate){lastEvent=DateTime.UtcNow.ToString("o");if(events.Count>=32)events.Dequeue();events.Enqueue(new{time=lastEvent,category=category,port_index=index});}}
    void Accept(Port port){
        while(!closed){
            TcpClient client;
            try{client=port.Listener.AcceptTcpClient();}catch(SocketException){if(closed)return;continue;}catch(ObjectDisposedException){return;}
            client.NoDelay=true;client.SendTimeout=3000;
            int acceptedPid=0;long acceptedCreated=0;
            if(guarded){
                bool allowed=false;
                var deadline=Stopwatch.StartNew();
                try{
                    while(!closed&&deadline.ElapsedMilliseconds<8000){
                        int pid;long created;lock(gate){pid=port.OwnerPid;created=port.OwnerCreated;}
                        if(pid>0){allowed=WindowsPeer.Match(client,pid,created);acceptedPid=pid;acceptedCreated=created;break;}
                        Thread.Sleep(20);
                    }
                }
                catch(SocketException){}catch(ObjectDisposedException){}catch(System.ComponentModel.Win32Exception){}
                if(!allowed){Interlocked.Increment(ref Rejected);Note("owner-rejected",port.Index);client.Close();Thread.Sleep(20);continue;}
            }
            BlockingCollection<byte[]> queue;long generation;
            lock(gate){
                if(closed||port.Client!=null||(guarded&&(port.OwnerPid!=acceptedPid||port.OwnerCreated!=acceptedCreated))){Interlocked.Increment(ref Rejected);Note("port-busy",port.Index);client.Close();continue;}
                port.Client=client;queue=new BlockingCollection<byte[]>(QueueFrames);port.Queue=queue;generation=++port.Generation;
                Note("port-connected",port.Index);
            }
            new Thread(()=>Write(port,client,queue,generation)){IsBackground=true}.Start();
            new Thread(()=>Read(port,client,generation)){IsBackground=true}.Start();
        }
    }
    static byte[] Exact(Stream stream,int count){
        byte[] data=new byte[count];int offset=0;
        var elapsed=Stopwatch.StartNew();
        while(offset<count){if(elapsed.ElapsedMilliseconds>=5000)throw new IOException("frame-timeout");if(stream.CanTimeout)stream.ReadTimeout=Math.Max(1,5000-(int)elapsed.ElapsedMilliseconds);int n=stream.Read(data,offset,count-offset);if(n==0)throw new EndOfStreamException();offset+=n;}
        return data;
    }
    static string Mac(byte[] data,int offset){return BitConverter.ToString(data,offset,6);}
    void Read(Port port,TcpClient client,long generation){
        try{
            var stream=client.GetStream();
            long window=clock();int frames=0;long bytes=0;
            while(!closed){
                // Idle links are allowed; a started header/body has a finite deadline.
                stream.ReadTimeout=Timeout.Infinite;int first=stream.ReadByte();if(first<0)break;
                stream.ReadTimeout=5000;byte[] rest=Exact(stream,3);
                uint n=((uint)first<<24)|((uint)rest[0]<<16)|((uint)rest[1]<<8)|rest[2];
                if(n<14||n>MaxFrame){Interlocked.Increment(ref Dropped);Note("malformed-frame",port.Index);break;}
                long tick=clock();if(((tick-window)&0xffffffffL)>=1000){window=tick;frames=0;bytes=0;}
                if(++frames>10000||(bytes+=n)>33554432){Interlocked.Increment(ref Dropped);Note("rate-limit",port.Index);break;}
                byte[] frame=Exact(stream,(int)n);Interlocked.Increment(ref Received);
                lock(gate){
                    if(port.Client!=client||port.Generation!=generation)return;
                    if(guarded&&!WindowsPeer.Alive(port.OwnerPid,port.OwnerCreated))return;
                    long now=clock();
                    foreach(var key in macs.Where(pair=>((now-pair.Value.Seen)&0xffffffffL)>300000).Select(pair=>pair.Key).ToArray())macs.Remove(key);
                    if((frame[6]&1)==0&&frame.Skip(6).Take(6).Any(b=>b!=0)){
                        string source=Mac(frame,6);
                        if(macs.Count>=MaxMacs&&!macs.ContainsKey(source))macs.Remove(macs.OrderBy(pair=>pair.Value.Seen).First().Key);
                        macs[source]=new Learned{Port=port,Seen=now};
                    }
                    Learned destination;
                    var targets=(frame[0]&1)==0&&macs.TryGetValue(Mac(frame,0),out destination)?new[]{destination.Port}:ports.ToArray();
                    foreach(var target in targets){
                        if(target==port||target.Client==null)continue;
                        if(!target.Queue.TryAdd(frame)){Interlocked.Increment(ref Dropped);if(((now-lastOverflow)&0xffffffffL)>=1000){lastOverflow=now;Note("queue-overflow",target.Index);}}
                    }
                }
            }
        }catch(EndOfStreamException){Note("partial-frame",port.Index);}catch(IOException){Note("read-error-or-timeout",port.Index);}catch(SocketException){Note("connection-error",port.Index);}catch(ObjectDisposedException){}
        finally{Disconnect(port,client,generation);}
    }
    public void Authorize(int index,int pid,long created){
        if(index<0||index>=ports.Count||!WindowsPeer.Alive(pid,created))throw new ArgumentException("owner-invalid");
        lock(gate){var p=ports[index];if(p.Client!=null){if(p.OwnerPid==pid&&p.OwnerCreated==created)return;throw new InvalidOperationException("port-connected");}p.OwnerPid=pid;p.OwnerCreated=created;}
    }
    public bool CanStop(){lock(gate)return !ports.Any(p=>p.OwnerPid>0&&WindowsPeer.MayBeAlive(p.OwnerPid,p.OwnerCreated));}
    public bool HasOwners(){lock(gate)return ports.Any(p=>p.OwnerPid>0);}
    public object Status(){lock(gate)return new{protocol=1,received=Interlocked.Read(ref Received),sent=Interlocked.Read(ref Sent),dropped=Interlocked.Read(ref Dropped),rejected=Interlocked.Read(ref Rejected),learned=macs.Count,last_event_utc=lastEvent,events=events.ToArray(),ports=ports.Select((p,i)=>new{index=i,port=((IPEndPoint)p.Listener.LocalEndpoint).Port,connected=p.Client!=null,authorized=p.OwnerPid>0}).ToArray()};}
    void Write(Port port,TcpClient client,BlockingCollection<byte[]> queue,long generation){
        try{
            var stream=client.GetStream();
            foreach(byte[] frame in queue.GetConsumingEnumerable()){
                int n=frame.Length;byte[] header={(byte)(n>>24),(byte)(n>>16),(byte)(n>>8),(byte)n};
                stream.Write(header,0,4);stream.Write(frame,0,n);Interlocked.Increment(ref Sent);
            }
        }catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}
        finally{Disconnect(port,client,generation);}
    }
    void Disconnect(Port port,TcpClient client,long generation){
        lock(gate){
            if(port.Client!=client||port.Generation!=generation)return;
            port.Client=null;port.Queue.CompleteAdding();
            Note("port-disconnected",port.Index);
            foreach(var key in macs.Where(pair=>pair.Value.Port==port).Select(pair=>pair.Key).ToArray())macs.Remove(key);
        }
        client.Close();
    }
    public void Dispose(){
        lock(gate){
            if(closed)return;closed=true;
            foreach(var p in ports){p.Listener.Stop();if(p.Client!=null){p.Queue.CompleteAdding();p.Client.Close();p.Client=null;}}
            macs.Clear();
        }
    }
    static string CommandLine(){
        var text=new StringBuilder();
        while(true){int c=Console.In.Read();if(c<0)return text.Length==0?null:text.ToString();if(c==10)return text.ToString();if(text.Length>=4096)throw new ArgumentException("command-too-long");if(c!=13)text.Append((char)c);}
    }
    static int Main(string[] args){
        try{
            int count;if(args.Length!=2||(args[0]!="--test-ports"&&args[0]!="--guarded-ports")||!Int32.TryParse(args[1],out count))return 2;
            bool guarded=args[0]=="--guarded-ports";
            using(var network=new LocalSwitch(count,guarded)){
                if(!guarded){Console.WriteLine(String.Join(",",network.Addresses));Console.Out.Flush();Console.ReadLine();return 0;}
                var json=new JavaScriptSerializer{MaxJsonLength=4096};Console.WriteLine(json.Serialize(network.Status()));Console.Out.Flush();
                while(true){
                    string line=CommandLine();if(line==null)return 0;
                    try{
                        var command=json.Deserialize<Dictionary<string,object>>(line);
                        if(Convert.ToInt32(command["protocol"])!=1)throw new ArgumentException("protocol-mismatch");
                        string action=(string)command["action"];
                        if(action=="quit")return 0;
                        if(action=="authorize")network.Authorize(Convert.ToInt32(command["index"]),Convert.ToInt32(command["pid"]),Convert.ToInt64(command["created"]));
                        else if(action!="status")throw new ArgumentException("action-invalid");
                        Console.WriteLine(json.Serialize(network.Status()));
                    }catch(Exception error){Console.WriteLine(json.Serialize(new{protocol=1,error=error.Message}));}
                    Console.Out.Flush();
                }
            }
        }catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
}
