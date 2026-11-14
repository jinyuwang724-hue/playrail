using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Devices of the input layer. Everything talks to the drivers directly (no ViGEmClient.dll or
// HidHide client DLL): the IOCTLs below are the ones ViGEmBus 1.17+ and HidHide 1.2+ document in
// their shared headers (ViGEmBusShared.h, HidHide's Logic.h).
namespace ClawInput {
    static class Win {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        public static readonly IntPtr Invalid = new IntPtr(-1);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(IntPtr device, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetOverlappedResult(IntPtr device, IntPtr overlapped, out int transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateEventW(IntPtr security, bool manual, bool initial, string name);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CancelIoEx(IntPtr device, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern uint QueryDosDeviceW(string device, StringBuilder target, int size);
        [DllImport("kernel32.dll")] public static extern bool QueryUnbiasedInterruptTime(out ulong time);
        public static long UnbiasedMs() { ulong t; QueryUnbiasedInterruptTime(out t); return (long)(t / 10000); }
        public static uint Ctl(uint type, uint function, uint access) { return (type << 16) | (access << 14) | (function << 2); }
    }

    // Overlapped DeviceIoControl with its own event and unmanaged buffer (the buffer outlives the call).
    sealed class Io : IDisposable {
        public readonly IntPtr Buffer, Overlapped, Event;
        public readonly int Size;
        public Io(int size) {
            Size = size; Buffer = Marshal.AllocHGlobal(size); Overlapped = Marshal.AllocHGlobal(32);
            Event = Win.CreateEventW(IntPtr.Zero, true, false, null);
        }
        public bool Call(IntPtr device, uint code, int inSize, int outSize, out int returned, int timeoutMs) {
            for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(Overlapped, i, 0);
            Marshal.WriteIntPtr(Overlapped, 24, Event);
            if (!Win.DeviceIoControl(device, code, Buffer, inSize, Buffer, outSize, out returned, Overlapped)) {
                int error = Marshal.GetLastWin32Error();
                if (error != 997) { returned = 0; return false; }
                if (timeoutMs >= 0 && WaitForSingleObject(Event, (uint)timeoutMs) != 0) { Win.CancelIoEx(device, Overlapped); Win.GetOverlappedResult(device, Overlapped, out returned, true); return false; }
                return Win.GetOverlappedResult(device, Overlapped, out returned, true);
            }
            return true;
        }
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        public void Dispose() { Marshal.FreeHGlobal(Buffer); Marshal.FreeHGlobal(Overlapped); Win.CloseHandle(Event); }
    }

    public static class XInput {
        [StructLayout(LayoutKind.Sequential)] public struct Gamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
        [StructLayout(LayoutKind.Sequential)] public struct State { public uint Packet; public Gamepad Pad; }
        [StructLayout(LayoutKind.Sequential)] public struct Vibration { public ushort Left, Right; }
        [StructLayout(LayoutKind.Sequential)] public struct CapsEx {
            public byte Type, SubType; public ushort Flags; public Gamepad Pad; public Vibration Vibration;
            public ushort Vendor, Product, Version, Unknown; public uint Unknown2;
        }
        // Ordinal 100 also reports the guide button; 108 reports the hardware vendor / product id.
        [DllImport("xinput1_4.dll", EntryPoint = "#100")] public static extern uint GetStateEx(uint index, out State state);
        [DllImport("xinput1_4.dll", EntryPoint = "#108")] public static extern uint GetCapabilitiesEx(uint one, uint index, uint flags, out CapsEx caps);
        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")] public static extern uint SetState(uint index, ref Vibration vibration);

        public static bool Identity(uint index, out ushort vendor, out ushort product) {
            CapsEx caps; vendor = product = 0;
            if (GetCapabilitiesEx(1, index, 0, out caps) != 0) return false;
            vendor = caps.Vendor; product = caps.Product; return true;
        }
    }

    // ViGEmBus: one virtual Xbox 360 pad, its reports and the rumble the game asks of it.
    public sealed class VirtualPad : IDisposable {
        static readonly Guid Interface = new Guid("96E42B22-F5E9-42F8-B043-ED0F932F014F");
        const uint Bus = 0x2A, W = 2, RW = 3, Base = 0x801;
        static readonly uint Plugin = Win.Ctl(Bus, Base + 0x000, W), Unplug = Win.Ctl(Bus, Base + 0x001, W),
            CheckVersion = Win.Ctl(Bus, Base + 0x002, W), WaitReady = Win.Ctl(Bus, Base + 0x003, W),
            Notification = Win.Ctl(Bus, Base + 0x200, RW), Submit = Win.Ctl(Bus, Base + 0x201, W), UserIndex = Win.Ctl(Bus, Base + 0x206, RW);
        IntPtr device = Win.Invalid;
        readonly Io submit = new Io(20), control = new Io(16), notify = new Io(12);
        Thread rumbleThread;
        volatile bool closing;
        public uint Serial { get; private set; }
        public long Submitted, Failed, RumbleRequests;
        public Action<byte, byte> Rumble;

        public VirtualPad() {
            string path = DevicePath();
            device = Win.CreateFileW(path, Win.GENERIC_READ | Win.GENERIC_WRITE, 3, IntPtr.Zero, Win.OPEN_EXISTING, Win.FILE_FLAG_OVERLAPPED | 0x80, IntPtr.Zero);
            if (device == Win.Invalid) throw new InvalidOperationException("打不开 ViGEm 虚拟手柄总线（" + Marshal.GetLastWin32Error() + "）");
            int n;
            Marshal.WriteInt32(control.Buffer, 0, 8); Marshal.WriteInt32(control.Buffer, 4, 1);
            if (!control.Call(device, CheckVersion, 8, 0, out n, 2000)) { Dispose(); throw new InvalidOperationException("ViGEm 总线版本不兼容"); }
            for (uint serial = 1; serial <= 16 && Serial == 0; serial++) {
                Marshal.WriteInt32(control.Buffer, 0, 16); Marshal.WriteInt32(control.Buffer, 4, (int)serial);
                Marshal.WriteInt32(control.Buffer, 8, 0); // Xbox360Wired
                Marshal.WriteInt16(control.Buffer, 12, unchecked((short)0x045E)); Marshal.WriteInt16(control.Buffer, 14, unchecked((short)0x028E));
                if (control.Call(device, Plugin, 16, 0, out n, 5000)) Serial = serial;
            }
            if (Serial == 0) { Dispose(); throw new InvalidOperationException("没能建立虚拟手柄（ViGEm 序号都被占用）"); }
            Marshal.WriteInt32(control.Buffer, 0, 8); Marshal.WriteInt32(control.Buffer, 4, (int)Serial);
            if (!control.Call(device, WaitReady, 8, 0, out n, 5000)) Thread.Sleep(300); // older bus: no wait call
            Marshal.WriteInt32(submit.Buffer, 0, 20); Marshal.WriteInt32(submit.Buffer, 4, (int)Serial);
            rumbleThread = new Thread(RumbleLoop) { IsBackground = true, Name = "vigem-rumble" };
            rumbleThread.Start();
        }

        // XInput user index Windows gave the virtual pad, or -1.
        public int Index() {
            int n;
            Marshal.WriteInt32(control.Buffer, 0, 12); Marshal.WriteInt32(control.Buffer, 4, (int)Serial); Marshal.WriteInt32(control.Buffer, 8, -1);
            lock (control) return control.Call(device, UserIndex, 12, 12, out n, 1000) ? Marshal.ReadInt32(control.Buffer, 8) : -1;
        }

        public bool Send(PadReport r) {
            Marshal.WriteInt16(submit.Buffer, 8, unchecked((short)r.Buttons));
            Marshal.WriteByte(submit.Buffer, 10, r.LT); Marshal.WriteByte(submit.Buffer, 11, r.RT);
            Marshal.WriteInt16(submit.Buffer, 12, r.LX); Marshal.WriteInt16(submit.Buffer, 14, r.LY);
            Marshal.WriteInt16(submit.Buffer, 16, r.RX); Marshal.WriteInt16(submit.Buffer, 18, r.RY);
            int n;
            bool ok = submit.Call(device, Submit, 20, 0, out n, 100);
            if (ok) Submitted++; else Failed++;
            return ok;
        }

        void RumbleLoop() {
            while (!closing) {
                Marshal.WriteInt32(notify.Buffer, 0, 12); Marshal.WriteInt32(notify.Buffer, 4, (int)Serial);
                Marshal.WriteInt32(notify.Buffer, 8, 0);
                int n;
                if (!notify.Call(device, Notification, 12, 12, out n, -1)) { if (closing) return; Thread.Sleep(50); continue; }
                RumbleRequests++;
                var handler = Rumble;
                if (handler != null) try { handler(Marshal.ReadByte(notify.Buffer, 8), Marshal.ReadByte(notify.Buffer, 9)); } catch { }
            }
        }

        static string DevicePath() {
            var guid = Interface;
            IntPtr set = Setup.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            if (set == Win.Invalid) throw new InvalidOperationException("找不到 ViGEm 虚拟手柄总线");
            try {
                var data = new Setup.InterfaceData { Size = Marshal.SizeOf(typeof(Setup.InterfaceData)) };
                if (!Setup.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, 0, ref data)) throw new InvalidOperationException("没有装 ViGEm 虚拟手柄总线");
                int size;
                Setup.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out size, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal(size);
                try {
                    Marshal.WriteInt32(detail, 8); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
                    if (!Setup.SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, size, out size, IntPtr.Zero)) throw new Win32Exception();
                    return Marshal.PtrToStringUni(detail + 4);
                } finally { Marshal.FreeHGlobal(detail); }
            } finally { Setup.SetupDiDestroyDeviceInfoList(set); }
        }

        public void Dispose() {
            closing = true;
            if (device != Win.Invalid) {
                Win.CancelIoEx(device, notify.Overlapped);
                if (rumbleThread != null) rumbleThread.Join(1000);
                if (Serial != 0) {
                    int n;
                    Marshal.WriteInt32(control.Buffer, 0, 8); Marshal.WriteInt32(control.Buffer, 4, (int)Serial);
                    control.Call(device, Unplug, 8, 0, out n, 2000);
                }
                Win.CloseHandle(device); device = Win.Invalid; // the bus also unplugs our pad when this handle closes
            }
        }
    }

    static class Setup {
        [StructLayout(LayoutKind.Sequential)] public struct InterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] public struct DevInfo { public int Size; public Guid Class; public int Inst; public IntPtr Reserved; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")] public static extern IntPtr SetupDiGetAllClassDevs(IntPtr guid, string enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, int index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceData data, IntPtr detail, int size, out int required, IntPtr info);
        [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref DevInfo info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DevInfo info, StringBuilder id, int size, out int required);
        [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        // Present device instances of the real pad that games read it through: the XInput (xusb22)
        // function and the HID game-controller view of it. The vendor (firmware) and keyboard
        // collections stay visible: the toolbox writes the firmware through one, M1 / M2 arrive on the other.
        public static List<string> PadInstances(string hardware) {
            var result = new List<string>();
            var xna = new Guid("d61ca365-5af4-4486-998b-9db4734c6ca3");
            IntPtr set = SetupDiGetAllClassDevs(IntPtr.Zero, null, IntPtr.Zero, 0x6); // ALLCLASSES | PRESENT
            if (set == Win.Invalid) return result;
            try {
                var info = new DevInfo { Size = Marshal.SizeOf(typeof(DevInfo)) };
                var id = new StringBuilder(512);
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++) {
                    int n;
                    if (!SetupDiGetDeviceInstanceIdW(set, ref info, id, id.Capacity, out n)) continue;
                    string value = id.ToString();
                    if (value.IndexOf(hardware, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool xinput = info.Class == xna;
                    bool hidGamepad = value.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) && value.IndexOf("&IG_", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (xinput || hidGamepad) result.Add(value);
                }
            } finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }
    }

    // HidHide control device: lists of hidden device instances and of applications that still see them.
    public interface IHidHide {
        List<string> Blacklist { get; set; }
        List<string> Whitelist { get; set; }
        bool Active { get; set; }
    }

    public sealed class HidHide : IHidHide, IDisposable {
        const uint Type = 32769, ReadData = 1;
        static readonly uint GetWhite = Win.Ctl(Type, 2048, ReadData), SetWhite = Win.Ctl(Type, 2049, ReadData),
            GetBlack = Win.Ctl(Type, 2050, ReadData), SetBlack = Win.Ctl(Type, 2051, ReadData),
            GetActive = Win.Ctl(Type, 2052, ReadData), SetActive = Win.Ctl(Type, 2053, ReadData);
        IntPtr device;
        public HidHide() {
            device = Win.CreateFileW("\\\\.\\HidHide", Win.GENERIC_READ | Win.GENERIC_WRITE, 3, IntPtr.Zero, Win.OPEN_EXISTING, 0, IntPtr.Zero);
            if (device == Win.Invalid) throw new InvalidOperationException("打不开 HidHide（" + Marshal.GetLastWin32Error() + "），是否已安装？");
        }
        public List<string> Blacklist { get { return GetList(GetBlack); } set { SetList(SetBlack, value); } }
        public List<string> Whitelist { get { return GetList(GetWhite); } set { SetList(SetWhite, value); } }
        public bool Active {
            get {
                IntPtr b = Marshal.AllocHGlobal(1);
                try { int n; Marshal.WriteByte(b, 0); if (!Win.DeviceIoControl(device, GetActive, IntPtr.Zero, 0, b, 1, out n, IntPtr.Zero)) throw new Win32Exception(); return Marshal.ReadByte(b) != 0; }
                finally { Marshal.FreeHGlobal(b); }
            }
            set {
                IntPtr b = Marshal.AllocHGlobal(1);
                try { int n; Marshal.WriteByte(b, (byte)(value ? 1 : 0)); if (!Win.DeviceIoControl(device, SetActive, b, 1, IntPtr.Zero, 0, out n, IntPtr.Zero)) throw new Win32Exception(); }
                finally { Marshal.FreeHGlobal(b); }
            }
        }
        List<string> GetList(uint code) {
            int size;
            if (!Win.DeviceIoControl(device, code, IntPtr.Zero, 0, IntPtr.Zero, 0, out size, IntPtr.Zero)) throw new Win32Exception();
            if (size <= 2) return new List<string>();
            IntPtr b = Marshal.AllocHGlobal(size);
            try {
                int n;
                // The driver wants the exact size it reported.
                if (!Win.DeviceIoControl(device, code, IntPtr.Zero, 0, b, size, out n, IntPtr.Zero)) throw new Win32Exception();
                return MultiSz.Decode(Marshal.PtrToStringUni(b, n / 2));
            } finally { Marshal.FreeHGlobal(b); }
        }
        void SetList(uint code, List<string> values) {
            string raw = MultiSz.Encode(values);
            IntPtr b = Marshal.StringToHGlobalUni(raw);
            try { int n; if (!Win.DeviceIoControl(device, code, b, raw.Length * 2, IntPtr.Zero, 0, out n, IntPtr.Zero)) throw new Win32Exception(); }
            finally { Marshal.FreeHGlobal(b); }
        }
        public void Dispose() { if (device != Win.Invalid) { Win.CloseHandle(device); device = Win.Invalid; } }

        // HidHide matches applications by their NT image path (\Device\HarddiskVolume3\...).
        public static string ImagePath(string dosPath) {
            if (dosPath == null || dosPath.Length < 3 || dosPath[1] != ':') return dosPath;
            var target = new StringBuilder(260);
            if (Win.QueryDosDeviceW(dosPath.Substring(0, 2), target, target.Capacity) == 0) return dosPath;
            return target.ToString() + dosPath.Substring(2);
        }
    }

    public static class MultiSz {
        public static List<string> Decode(string raw) {
            var result = new List<string>();
            foreach (var part in raw.Split('\0')) if (part.Length > 0) result.Add(part);
            return result;
        }
        public static string Encode(List<string> values) {
            var b = new StringBuilder();
            foreach (var v in values) if (!string.IsNullOrEmpty(v)) b.Append(v).Append('\0');
            if (b.Length == 0) b.Append('\0');
            b.Append('\0');
            return b.ToString();
        }
    }

    // Keyboard and mouse output (SendInput). Injected events carry Tag so our own hook ignores them.
    public static class Output {
        public static Action<string> SystemAction;
        public static readonly IntPtr Tag = new IntPtr(0x434C4157); // "CLAW"
        [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int Dx, Dy; public uint Data, Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct KeyInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Explicit, Size = 40)] struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseInput Mouse; [FieldOffset(8)] public KeyInput Key; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint code, uint type);
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        public static long Sent, Failed;

        public static void Send(List<OutputEvent> events) {
            var inputs = new List<Input>();
            foreach (var e in events) {
                switch (e.Kind) {
                    case OutputKind.KeyDown: case OutputKind.KeyUp: inputs.Add(Key(e.Key, e.Extended, e.Kind == OutputKind.KeyUp)); break;
                    case OutputKind.MouseDown: case OutputKind.MouseUp: inputs.Add(MouseButton(e.Value, e.Kind == OutputKind.MouseDown)); break;
                    case OutputKind.Wheel: inputs.Add(Wheel(e.Value)); break;
                    case OutputKind.MouseMove: inputs.Add(new Input { Type = 0, Mouse = new MouseInput { Dx = e.Value, Dy = e.Dy, Flags = 0x1, Extra = Tag } }); break;
                    case OutputKind.Action: Flush(inputs); RunAction(e.Action); break;
                }
            }
            Flush(inputs);
        }

        static void Flush(List<Input> inputs) {
            if (inputs.Count == 0) return;
            uint done = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Input)));
            Sent += done; Failed += inputs.Count - done;
            inputs.Clear();
        }

        static Input Key(ushort vk, bool extended, bool up) {
            // Media keys only work as virtual keys; everything else goes as a scan code too, which
            // DirectInput / raw-input games read.
            bool media = vk >= 0xA6 && vk <= 0xB7;
            uint scan = media ? 0 : MapVirtualKeyW(vk, 4) & 0xFF;
            uint flags = (up ? 2u : 0u) | (extended ? 1u : 0u) | (scan != 0 ? 8u : 0u);
            return new Input { Type = 1, Key = new KeyInput { Vk = vk, Scan = (ushort)scan, Flags = flags, Extra = Tag } };
        }

        static Input MouseButton(int button, bool down) {
            uint flags = 0, data = 0;
            switch (button) {
                case 1: flags = down ? 0x2u : 0x4u; break;
                case 2: flags = down ? 0x8u : 0x10u; break;
                case 3: flags = down ? 0x20u : 0x40u; break;
                case 4: flags = down ? 0x80u : 0x100u; data = 1; break;
                case 5: flags = down ? 0x80u : 0x100u; data = 2; break;
            }
            return new Input { Type = 0, Mouse = new MouseInput { Flags = flags, Data = data, Extra = Tag } };
        }

        static Input Wheel(int direction) {
            uint flags = direction <= 2 ? 0x800u : 0x1000u;
            int amount = direction == 1 || direction == 4 ? 120 : -120;
            return new Input { Type = 0, Mouse = new MouseInput { Flags = flags, Data = unchecked((uint)amount), Extra = Tag } };
        }

        // Touch keyboard: TabTip's toggle interface (same as the toolbox's power helper).
        [ComImport, Guid("4ce576fa-83dc-4F88-951c-9d0782b4e376")] class UIHostNoLaunch { }
        [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITipInvocation { void Toggle(IntPtr hwnd); }

        static void RunAction(string action) {
            if(action!="keyboard") { var handler=SystemAction;if(handler!=null)handler(action);return; }
            if (action == "keyboard") {
                var t = new Thread(delegate () {
                    try { ((ITipInvocation)new UIHostNoLaunch()).Toggle(GetDesktopWindow()); }
                    catch (Exception) {
                        try { System.Diagnostics.Process.Start(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), @"microsoft shared\ink\TabTip.exe")); } catch { }
                    }
                });
                t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
            }
        }
    }

    // M1 / M2: the firmware sends them as F22 / F23 on the pad's keyboard collection while the input
    // layer owns them (ClawTweaks does the same). The low-level hook swallows those keys before
    // any window sees them and turns them into button state.
    public sealed class BackKeyHook : IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct HookKey { public uint Vk, Scan, Flags, Time; public IntPtr Extra; }
        delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookExW(int type, HookProc hook, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern int GetMessageW(out Msg msg, IntPtr window, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint thread, uint message, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
        [StructLayout(LayoutKind.Sequential)] struct Msg { public IntPtr Window; public uint Message; public IntPtr W, L; public uint Time; public int X, Y; }
        public const uint F22 = 0x85, F23 = 0x86;
        readonly HookProc callback;
        readonly Thread thread;
        uint threadId;
        IntPtr hook;
        public volatile bool M1, M2;
        public long Swallowed;
        public string Error;

        public BackKeyHook() {
            callback = OnKey;
            var ready = new ManualResetEvent(false);
            thread = new Thread(delegate () {
                threadId = GetCurrentThreadId();
                hook = SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
                if (hook == IntPtr.Zero) Error = "背键钩子装不上（" + Marshal.GetLastWin32Error() + "）";
                ready.Set();
                Msg msg;
                while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0) { }
                if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            }) { IsBackground = true, Name = "backkey-hook", Priority = ThreadPriority.Highest };
            thread.Start();
            ready.WaitOne(3000);
        }

        IntPtr OnKey(int code, IntPtr message, IntPtr data) {
            if (code >= 0) {
                var key = (HookKey)Marshal.PtrToStructure(data, typeof(HookKey));
                if ((key.Flags & 0x10) == 0 && (key.Vk == F22 || key.Vk == F23)) {
                    bool down = message.ToInt32() == 0x100 || message.ToInt32() == 0x104;
                    if (key.Vk == F22) M1 = down; else M2 = down;
                    Interlocked.Increment(ref Swallowed);
                    return new IntPtr(1);
                }
            }
            return CallNextHookEx(hook, code, message, data);
        }

        public void Dispose() { if (threadId != 0) PostThreadMessageW(threadId, 0x12, IntPtr.Zero, IntPtr.Zero); thread.Join(2000); }
    }

    // Which program is in front, by lower-case image name (cached per process id).
    public sealed class Foreground {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);
        readonly Dictionary<uint, string> names = new Dictionary<uint, string>();
        long cleared = Environment.TickCount;
        public uint Pid;
        public string Name = "";

        public void Refresh() {
            if (Environment.TickCount - cleared > 10000) { names.Clear(); cleared = Environment.TickCount; }
            uint pid = 0;
            IntPtr window = GetForegroundWindow();
            if (window != IntPtr.Zero) GetWindowThreadProcessId(window, out pid);
            Pid = pid;
            string name;
            if (pid == 0) name = "";
            else if (!names.TryGetValue(pid, out name)) { name = System.IO.Path.GetFileName(ImagePath(pid) ?? "").ToLowerInvariant(); names[pid] = name; }
            Name = name;
        }

        public static string ImagePath(uint pid) {
            IntPtr process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return null;
            try { var b = new StringBuilder(1024); int size = b.Capacity; return QueryFullProcessImageNameW(process, 0, b, ref size) ? b.ToString() : null; }
            finally { Win.CloseHandle(process); }
        }
    }

    // 1 ms tick without spinning: a high-resolution waitable timer (Windows 10 1803+) aimed at fixed
    // deadlines (a plain 1 ms relative wait overshoots to ~675 Hz on this device), else Sleep(1) at 1 ms
    // timer resolution.
    public sealed class Ticker : IDisposable {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWaitableTimerExW(IntPtr security, string name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr routine, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
        readonly IntPtr timer;
        readonly long period;
        long next;
        bool paused;
        public readonly bool HighResolution;
        public Ticker(int hz) {
            period = System.Diagnostics.Stopwatch.Frequency / Math.Max(100, Math.Min(1000, hz));
            next = System.Diagnostics.Stopwatch.GetTimestamp() + period;
            timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x1F0003);
            HighResolution = timer != IntPtr.Zero;
            if (!HighResolution) timeBeginPeriod(1);
        }
        public void Pause() {
            if (paused) return;
            paused = true;
            if (!HighResolution) timeEndPeriod(1);
        }
        public void Wait() {
            if (paused) {
                paused = false;
                if (!HighResolution) timeBeginPeriod(1);
                next = System.Diagnostics.Stopwatch.GetTimestamp() + period;
            }
            long now = System.Diagnostics.Stopwatch.GetTimestamp(), wait = next - now;
            if (wait > 0) {
                if (HighResolution) {
                    long due = -Math.Max(1, wait * 10000000L / System.Diagnostics.Stopwatch.Frequency);
                    SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false); WaitForSingleObject(timer, 50);
                } else Thread.Sleep(1);
                now = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            next += period;
            if (now - next > 4 * period) next = now + period; // after a stall (sleep, debugger): no catch-up burst
        }
        public void Dispose() { if (HighResolution) Win.CloseHandle(timer); else if (!paused) timeEndPeriod(1); }
    }
}
