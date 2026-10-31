using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
static class SwitchCoreTests {
    static byte[] Frame(int source,int target){byte[] f=new byte[60];f[0]=0x52;f[5]=(byte)target;f[6]=0x52;f[10]=(byte)(source>>8);f[11]=(byte)source;f[12]=8;return f;}
    static void Send(TcpClient c,byte[] f){byte[] h={0,0,0,(byte)f.Length};c.GetStream().Write(h,0,4);c.GetStream().Write(f,0,f.Length);}
    static void Receive(TcpClient c){byte[] f=new byte[64];int n=0;while(n<f.Length){int read=c.GetStream().Read(f,n,f.Length-n);if(read==0)throw new Exception("EOF");n+=read;}}
    static void Quiet(TcpClient c){try{c.GetStream().ReadByte();throw new Exception("unexpected frame");}catch(IOException){}}
    static int Main(){
        try{
            long now=0;
            using(var s=new LocalSwitch(3,false,()=>Interlocked.Read(ref now)))using(var a=new TcpClient())using(var b=new TcpClient())using(var c=new TcpClient()){
                int[] ports=s.Addresses;a.Connect(IPAddress.Loopback,ports[0]);b.Connect(IPAddress.Loopback,ports[1]);c.Connect(IPAddress.Loopback,ports[2]);
                a.ReceiveTimeout=b.ReceiveTimeout=c.ReceiveTimeout=300;Thread.Sleep(100);
                Send(a,Frame(1,2));Receive(b);Receive(c);
                Send(b,Frame(2,1));Receive(a);Quiet(c);
                Interlocked.Exchange(ref now,300001);Send(b,Frame(2,1));Receive(a);Receive(c);
                Send(c,Frame(1,2));Receive(b);Quiet(a);
                Send(b,Frame(2,1));Receive(c);Quiet(a);
                for(int i=3;i<4200;i++){Send(a,Frame(i,2));Receive(b);}
                string status=new JavaScriptSerializer().Serialize(s.Status());if(!status.Contains("\"learned\":4096"))throw new Exception(status);
                c.Close();Thread.Sleep(100);Send(b,Frame(2,1));Receive(a);
                Interlocked.Exchange(ref now,301001);
                try{for(int i=0;i<10002;i++)Send(a,Frame(7,2));}catch(IOException){}
                a.ReceiveTimeout=5000;
                try{if(a.GetStream().ReadByte()!=-1)throw new Exception("rate limit did not disconnect sender");}catch(IOException error){var socket=error.InnerException as SocketException;if(socket==null||(socket.SocketErrorCode!=SocketError.ConnectionReset&&socket.SocketErrorCode!=SocketError.ConnectionAborted))throw;}
            }
            using(var network=new LocalSwitch(2)){
                for(int i=0;i<40;i++)using(var client=new TcpClient()){
                    client.Connect(IPAddress.Loopback,network.Addresses[0]);client.ReceiveTimeout=3000;client.GetStream().Write(new byte[4],0,4);if(client.GetStream().ReadByte()!=-1)throw new Exception("malformed sender not closed");
                }
                var snapshot=(System.Collections.Generic.Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(new JavaScriptSerializer().Serialize(network.Status()));
                if(((object[])snapshot["events"]).Length!=32||String.IsNullOrEmpty((string)snapshot["last_event_utc"]))throw new Exception("event ring not bounded or timestamp missing");
                string json=new JavaScriptSerializer().Serialize(snapshot);if(!json.Contains("malformed-frame")||json.Contains("payload"))throw new Exception("unsafe or missing diagnostic event");
            }
            Console.WriteLine("MAC aging, move, capacity, disconnect, rate limit and bounded private event ring: passed");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
