using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

// Fail closed: match the reverse TCP tuple and native process creation time.
static class WindowsPeer {
    public static bool MayBeAlive(int pid,long created){
        try{using(var p=Process.GetProcessById(pid))return !p.HasExited&&p.StartTime.ToUniversalTime().ToFileTimeUtc()==created;}
        catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}catch(System.ComponentModel.Win32Exception){return true;}
    }
    [DllImport("iphlpapi.dll")]static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool sort,int family,int tableClass,uint reserved);
    public static bool Alive(int pid,long created){
        try{using(var p=Process.GetProcessById(pid))return !p.HasExited&&p.StartTime.ToUniversalTime().ToFileTimeUtc()==created;}
        catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}catch(System.ComponentModel.Win32Exception){return false;}
    }
    static int Port(int value){return (int)(ushort)IPAddress.NetworkToHostOrder((short)(value&65535));}
    public static bool Match(TcpClient client,int pid,long created){
        if(!Alive(pid,created))return false;
        var local=(IPEndPoint)client.Client.LocalEndPoint;var remote=(IPEndPoint)client.Client.RemoteEndPoint;
        if(!IPAddress.IsLoopback(remote.Address)||!IPAddress.IsLoopback(local.Address))return false;
        int size=0;uint result=GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,5,0);
        if(result!=122||size<4||size>16777216)return false;
        IntPtr buffer=Marshal.AllocHGlobal(size);
        try{
            result=GetExtendedTcpTable(buffer,ref size,false,2,5,0);if(result!=0)return false;
            int count=Marshal.ReadInt32(buffer);if(count<0||4L+count*24L>size)return false;
            int matches=0;
            for(int i=0;i<count;i++){
                IntPtr row=IntPtr.Add(buffer,4+i*24);
                if(Marshal.ReadInt32(row)!=5||Marshal.ReadInt32(row,20)!=pid)continue;
                if(Port(Marshal.ReadInt32(row,8))==remote.Port&&Port(Marshal.ReadInt32(row,16))==local.Port&&
                   new IPAddress((uint)Marshal.ReadInt32(row,4)).Equals(remote.Address)&&new IPAddress((uint)Marshal.ReadInt32(row,12)).Equals(local.Address))matches++;
            }
            return matches==1&&Alive(pid,created);
        }finally{Marshal.FreeHGlobal(buffer);}
    }
}
