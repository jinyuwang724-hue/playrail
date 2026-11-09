using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Read-only, finite observation of G / Windows keys and MSI front-button events.
// Other keyboard input is never logged. No input is swallowed or synthesized.
class TargetKeyboardObserver : NativeWindow {
    [StructLayout(LayoutKind.Sequential)] struct KeyInfo { public uint vk, scan, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct RawHeader { public uint type, size; public IntPtr device, param; }
    [StructLayout(LayoutKind.Sequential)] struct RawKey { public ushort scan, flags, reserved, vk; public uint message, extra; }
    [StructLayout(LayoutKind.Sequential)] struct RawDevice { public ushort page, usage; public uint flags; public IntPtr target; }
    delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SetWindowsHookEx(int id, Hook hook, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", SetLastError=true)] static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);
    readonly BlockingCollection<object> records = new BlockingCollection<object>();
    readonly Hook callback;
    IntPtr hookHandle;
    ManagementEventWatcher watcher;
    static bool Target(uint vk) { return vk == 0x47 || vk == 0x5b || vk == 0x5c; }
    void Record(string source, object detail) { records.Add(new { at=DateTime.UtcNow.ToString("o"), ticks=Stopwatch.GetTimestamp(), source, detail }); }
    IntPtr OnKey(int code, IntPtr message, IntPtr data) {
        if (code >= 0) {
            var key = (KeyInfo)Marshal.PtrToStructure(data, typeof(KeyInfo));
            if (Target(key.vk)) Record("keyboard-hook", new { vk=key.vk,scan=key.scan,flags=key.flags,time=key.time,extra=key.extra.ToUInt64(),message=message.ToInt64() });
        }
        return CallNextHookEx(hookHandle,code,message,data);
    }
    protected override void WndProc(ref Message message) {
        if (message.Msg == 0xff) {
            uint size=0, headerSize=(uint)Marshal.SizeOf(typeof(RawHeader));
            GetRawInputData(message.LParam,0x10000003,IntPtr.Zero,ref size,headerSize);
            if (size >= headerSize + Marshal.SizeOf(typeof(RawKey)) && size < 4096) {
                var data=Marshal.AllocHGlobal((int)size);
                try {
                    if (GetRawInputData(message.LParam,0x10000003,data,ref size,headerSize) != uint.MaxValue) {
                        var header=(RawHeader)Marshal.PtrToStructure(data,typeof(RawHeader));
                        var key=(RawKey)Marshal.PtrToStructure(IntPtr.Add(data,(int)headerSize),typeof(RawKey));
                        if (header.type == 1 && Target(key.vk)) {
                            uint chars=512; var name=new StringBuilder((int)chars);
                            GetRawInputDeviceInfo(header.device,0x20000007,name,ref chars);
                            Record("raw-keyboard",new {vk=key.vk,scan=key.scan,flags=key.flags,message=key.message,extra=key.extra,device=name.ToString()});
                        }
                    }
                } finally { Marshal.FreeHGlobal(data); }
            }
        }
        base.WndProc(ref message);
    }
    TargetKeyboardObserver() {
        callback=OnKey;
        CreateHandle(new CreateParams {Parent=new IntPtr(-3)});
        if (!RegisterRawInputDevices(new[]{new RawDevice{page=1,usage=6,flags=0x100,target=Handle}},1,(uint)Marshal.SizeOf(typeof(RawDevice))))
            throw new Exception("Raw keyboard observation failed: "+Marshal.GetLastWin32Error());
        hookHandle=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);
        if (hookHandle==IntPtr.Zero) throw new Exception("Keyboard observation failed: "+Marshal.GetLastWin32Error());
        watcher=new ManagementEventWatcher(new ManagementScope(@"\\.\root\WMI"),new WqlEventQuery("SELECT * FROM MSI_Event"));
        watcher.EventArrived += delegate(object sender,EventArrivedEventArgs e) {
            int raw=Convert.ToInt32(e.NewEvent.Properties["MSIEvt"].Value), code=raw&255;
            if (code==41||code==88) Record("MSI_Event",new {raw,code});
        };
        watcher.Start();
    }
    [STAThread] static int Main() {
        Console.OutputEncoding=new UTF8Encoding(false);
        var observer=new TargetKeyboardObserver();
        var writer=Task.Run(delegate {var json=new JavaScriptSerializer();foreach(var item in observer.records.GetConsumingEnumerable())Console.WriteLine(json.Serialize(item));});
        var timer=new Timer {Interval=120000}; timer.Tick += delegate {timer.Stop();Application.Exit();}; timer.Start();
        Console.WriteLine("READY: read-only target-key observation for 120 seconds");
        try {Application.Run();} finally {
            observer.watcher.Stop();observer.watcher.Dispose();
            UnhookWindowsHookEx(observer.hookHandle);observer.DestroyHandle();timer.Dispose();
            observer.records.CompleteAdding();writer.Wait();
        }
        return 0;
    }
}
