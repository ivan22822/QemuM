using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
static class SwitchStressTests {
    static int Main(){
        try{
            using(var s=new LocalSwitch(3))using(var source=new TcpClient())using(var fast=new TcpClient())using(var slow=new TcpClient()){
                source.SendTimeout=10000;fast.ReceiveTimeout=1000;slow.ReceiveBufferSize=1024;
                var ports=s.Addresses;source.Connect(IPAddress.Loopback,ports[0]);fast.Connect(IPAddress.Loopback,ports[1]);slow.Connect(IPAddress.Loopback,ports[2]);Thread.Sleep(150);
                bool done=false;int received=0;
                var reader=new Thread(()=>{byte[] buffer=new byte[65536];while(!Volatile.Read(ref done)){try{int n=fast.GetStream().Read(buffer,0,buffer.Length);if(n==0)break;Interlocked.Add(ref received,n);}catch(IOException){}}}){IsBackground=true};reader.Start();
                byte[] frame=new byte[1518];frame[2]=5;frame[3]=234; // 1514-byte Ethernet frame
                for(int i=4;i<10;i++)frame[i]=255;frame[10]=0x52;frame[15]=1;
                for(int i=0;i<7000;i++)source.GetStream().Write(frame,0,frame.Length);
                var deadline=DateTime.UtcNow.AddSeconds(8);
                while(Interlocked.Read(ref s.Dropped)==0&&DateTime.UtcNow<deadline)Thread.Sleep(10);
                if(Interlocked.Read(ref s.Dropped)==0||Volatile.Read(ref received)==0)throw new Exception("backpressure not exercised");
                slow.Close();int before=Volatile.Read(ref received);
                for(int i=0;i<100;i++)source.GetStream().Write(frame,0,frame.Length);
                deadline=DateTime.UtcNow.AddSeconds(3);while(Volatile.Read(ref received)<=before&&DateTime.UtcNow<deadline)Thread.Sleep(10);
                if(Volatile.Read(ref received)<=before)throw new Exception("fast peer stalled");
                Volatile.Write(ref done,true);fast.Close();reader.Join(2000);
                Console.WriteLine("Slow peer: bounded queue drops; fast peer continues. received bytes="+received+", dropped="+s.Dropped);return 0;
            }
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
