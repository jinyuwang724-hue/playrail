using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
namespace ClawRouter {
    // A separate raw-input queue identifies the firmware keyboard before a Tab
    // chord is classified. Ordinary external keyboards retain native Win+Tab.
    public sealed class RawKeyboardSource : NativeWindow,IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct Device {public ushort page,usage;public uint flags;public IntPtr target;}
        [StructLayout(LayoutKind.Sequential)] struct Header {public uint type,size;public IntPtr device,param;}
        [StructLayout(LayoutKind.Sequential)] struct Keyboard {public ushort make,flags,reserved,vkey;public uint message,extra;}
        [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(Device[] devices,uint count,uint size);
        [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr raw,uint command,IntPtr data,ref uint size,uint headerSize);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr device,uint command,StringBuilder data,ref uint size);
        [DllImport("user32.dll")] static extern int GetMessageTime();
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint msg,IntPtr w,IntPtr l);
        readonly object gate=new object();readonly AutoResetEvent classified=new AutoResetEvent(false);readonly ManualResetEvent ready=new ManualResetEvent(false);
        readonly Dictionary<IntPtr,bool> devices=new Dictionary<IntPtr,bool>();readonly Thread thread;
        uint stroke;int source;volatile bool disposed;
        public int FirmwareWindows {get;private set;}public bool Ready {get;private set;}
        public RawKeyboardSource(){thread=new Thread(Run){IsBackground=true,Name="firmware-keyboard-source"};thread.SetApartmentState(ApartmentState.STA);thread.Start();ready.WaitOne(1500);}
        void Run(){try{CreateHandle(new CreateParams{Parent=new IntPtr(-3)});Ready=RegisterRawInputDevices(new[]{new Device{page=1,usage=6,flags=0x100,target=Handle}},1,(uint)Marshal.SizeOf(typeof(Device)));ready.Set();Application.Run();}finally{ready.Set();DestroyHandle();}}
        public void Begin(uint time){lock(gate){stroke=time;source=0;classified.Reset();}}
        public bool Firmware(uint time){for(int n=0;n<2;n++){lock(gate){if(source!=0&&unchecked(time-stroke)<2000)return source==1;}if(n==0)classified.WaitOne(8);}return false;}
        bool IsFirmware(IntPtr device){bool yes;if(devices.TryGetValue(device,out yes))return yes;uint size=0;GetRawInputDeviceInfo(device,0x20000007,null,ref size);var name=new StringBuilder((int)size+1);GetRawInputDeviceInfo(device,0x20000007,name,ref size);string s=name.ToString().ToUpperInvariant();yes=s.Contains("MSNB1001")||s.Contains("VID_0DB0");devices[device]=yes;return yes;}
        protected override void WndProc(ref Message m){if(m.Msg==0x8001){Application.ExitThread();return;}if(m.Msg==0xff){uint size=0,hs=(uint)Marshal.SizeOf(typeof(Header));GetRawInputData(m.LParam,0x10000003,IntPtr.Zero,ref size,hs);if(size>=hs+Marshal.SizeOf(typeof(Keyboard))){var data=Marshal.AllocHGlobal((int)size);try{if(GetRawInputData(m.LParam,0x10000003,data,ref size,hs)==size){var h=(Header)Marshal.PtrToStructure(data,typeof(Header));var key=(Keyboard)Marshal.PtrToStructure(IntPtr.Add(data,(int)hs),typeof(Keyboard));if(h.type==1&&(key.vkey==91||key.vkey==92)&&(key.flags&1)==0){bool fw=IsFirmware(h.device);lock(gate){source=fw?1:2;stroke=(uint)GetMessageTime();if(fw)FirmwareWindows++;classified.Set();}}}}finally{Marshal.FreeHGlobal(data);}}}base.WndProc(ref m);}
        public void Dispose(){if(disposed)return;disposed=true;if(Handle!=IntPtr.Zero)PostMessage(Handle,0x8001,IntPtr.Zero,IntPtr.Zero);thread.Join(1500);}
    }
}
