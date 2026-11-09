using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Read-only: never changes a power policy, consumes a key, or requests suspend.
// Logs only firmware notifications, system-control HID reports and power events.
sealed class SleepButtonProbe : NativeWindow, IDisposable {
    [StructLayout(LayoutKind.Sequential)] struct RawDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct RawList { public IntPtr Device; public uint Type; }
    [StructLayout(LayoutKind.Sequential)] struct RawHeader { public uint Type, Size; public IntPtr Device, Param; }
    [DllImport("user32.dll", SetLastError=true)] static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError=true)] static extern uint GetRawInputDeviceList([Out] RawList[] list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, byte[] data, ref uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr raw, uint command, byte[] data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr RegisterPowerSettingNotification(IntPtr window, ref Guid setting, uint flags);
    [DllImport("user32.dll")] static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    readonly string path;
    readonly object gate = new object();
    readonly List<IntPtr> notifications = new List<IntPtr>();
    ManagementEventWatcher watcher;
    public SleepButtonProbe(string path) {
        this.path=Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(this.path));
        CreateHandle(new CreateParams { Caption="Claw sleep button observer" });
        Log("started", new { pid=Process.GetCurrentProcess().Id, readOnly=true });
        Inventory();
        var raw=new[] { new RawDevice { Page=1,Usage=0x80,Flags=0x100,Target=Handle }, new RawDevice { Page=1,Usage=0x0c,Flags=0x100,Target=Handle } };
        Log("raw-registration", new { success=RegisterRawInputDevices(raw, (uint)raw.Length,(uint)Marshal.SizeOf(typeof(RawDevice))), error=Marshal.GetLastWin32Error() });
        foreach(string value in new[] { "6fe69556-704a-47a0-8f24-c28d936fda47", "2b84c20e-ad23-4ddf-93db-05ffbd7efca5" }) {
            Guid setting=new Guid(value); IntPtr n=RegisterPowerSettingNotification(Handle,ref setting,0); if(n!=IntPtr.Zero)notifications.Add(n);
        }
        try {
            watcher=new ManagementEventWatcher(new ManagementScope(@"\\.\root\WMI"),new WqlEventQuery("SELECT * FROM MSI_Event"));
            watcher.EventArrived+=delegate(object sender, EventArrivedEventArgs e) {
                var values=new Dictionary<string,object>(); foreach(PropertyData p in e.NewEvent.Properties)values[p.Name]=p.Value;
                Log("msi-event",values);
            }; watcher.Start(); Log("msi-ready",null);
        } catch(Exception ex) { Log("msi-unavailable",ex.Message); }
        Log("ready",null);
    }
    void Log(string kind, object value) {
        lock(gate)File.AppendAllText(path,new JavaScriptSerializer().Serialize(new { at=DateTime.UtcNow.ToString("o"),qpcMs=Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency,kind=kind,data=value })+Environment.NewLine,new UTF8Encoding(false));
    }
    static string DeviceName(IntPtr device) {
        uint size=0;GetRawInputDeviceInfo(device,0x20000007,(StringBuilder)null,ref size);
        var text=new StringBuilder((int)size+1);GetRawInputDeviceInfo(device,0x20000007,text,ref size);return text.ToString();
    }
    void Inventory() {
        uint count=0,sz=(uint)Marshal.SizeOf(typeof(RawList));GetRawInputDeviceList(null,ref count,sz);
        var devices=new RawList[count];if(GetRawInputDeviceList(devices,ref count,sz)==uint.MaxValue)return;
        foreach(var d in devices) {
            if(d.Type!=2)continue;
            uint size=32;var info=new byte[size];BitConverter.GetBytes(size).CopyTo(info,0);
            GetRawInputDeviceInfo(d.Device,0x2000000b,info,ref size);
            Log("hid-device",new { name=DeviceName(d.Device),page=BitConverter.ToUInt16(info,20),usage=BitConverter.ToUInt16(info,22) });
        }
    }
    protected override void WndProc(ref Message message) {
        if(message.Msg==0x218) {
            long type=message.WParam.ToInt64();
            if(type==0x8013 && message.LParam!=IntPtr.Zero) {
                Guid setting=(Guid)Marshal.PtrToStructure(message.LParam,typeof(Guid));int length=Marshal.ReadInt32(message.LParam,16);
                Log("power-setting",new { guid=setting.ToString(),value=length==4?Marshal.ReadInt32(message.LParam,20):-1 });
            } else Log("power-broadcast",new { type=type });
        }
        if(message.Msg==0xff) {
            uint size=0,header=(uint)Marshal.SizeOf(typeof(RawHeader));GetRawInputData(message.LParam,0x10000003,null,ref size,header);
            if(size>header && size<65536) {
                var bytes=new byte[size];if(GetRawInputData(message.LParam,0x10000003,bytes,ref size,header)!=uint.MaxValue) {
                    IntPtr device=IntPtr.Size==8?new IntPtr(BitConverter.ToInt64(bytes,8)):new IntPtr(BitConverter.ToInt32(bytes,8));
                    Log("system-hid-input",new { device=DeviceName(device),report=BitConverter.ToString(bytes,(int)header) });
                }
            }
        }
        base.WndProc(ref message);
    }
    public void Dispose() {
        if(watcher!=null){try{watcher.Stop();}catch{}watcher.Dispose();}
        foreach(var n in notifications)UnregisterPowerSettingNotification(n);
        Log("stopped",null);DestroyHandle();
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length<1)return 2;
        using(var probe=new SleepButtonProbe(args[0])) {
            var timer=new Timer { Interval=1000 };timer.Tick+=delegate { if(File.Exists(args[0]+".stop"))Application.ExitThread(); };timer.Start();
            Application.Run();timer.Dispose();
        }return 0;
    }
}
