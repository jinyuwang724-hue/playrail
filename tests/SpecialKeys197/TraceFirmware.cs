using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
class TraceFirmware {
    [StructLayout(LayoutKind.Sequential)] struct Key { public uint key, scan, flags, time; public UIntPtr extra; }
    delegate IntPtr Hook(int code,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id,Hook cb,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h,int code,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    static StreamWriter log; static readonly object gate=new object();static readonly Stopwatch clock=Stopwatch.StartNew();static Hook callback;static IntPtr hook;static bool win;
    static void Write(string value){lock(gate){log.WriteLine(clock.ElapsedMilliseconds+" "+value);log.Flush();}}
    static IntPtr OnKey(int code,IntPtr wp,IntPtr lp){if(code>=0){var k=(Key)Marshal.PtrToStructure(lp,typeof(Key));bool up=(k.flags&128)!=0;
        if(k.key==91||k.key==92){win=!up;Write("Win "+(up?"up":"down"));}
        else if(win&&(k.key==71||k.key==9))Write("chord key="+k.key+" "+(up?"up":"down")+" injected="+((k.flags&16)!=0));
    }return CallNextHookEx(hook,code,wp,lp);}
    [STAThread] static void Main(string[] args){log=new StreamWriter(args[0],false);callback=OnKey;hook=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);
        using(var watcher=new ManagementEventWatcher(new ManagementScope(@"\\.\root\WMI"),new WqlEventQuery("SELECT * FROM MSI_Event"))){watcher.EventArrived+=(s,e)=>{Write("MSI "+e.NewEvent.Properties["MSIEvt"].Value);};watcher.Start();
        var timer=new Timer{Interval=180000};timer.Tick+=(s,e)=>Application.ExitThread();timer.Start();Write("ready");Application.Run();watcher.Stop();}
        UnhookWindowsHookEx(hook);log.Dispose();}
}
