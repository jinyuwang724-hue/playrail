// Intel's driver frame limiter. No RTSS hooks, injection, or private graphics settings.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

static class IntelFrameLimit {
    internal struct Setting {
        public bool Enabled;
        public int Value;
        public Setting(bool enabled, int value) { Enabled = enabled; Value = value; }
        public bool Same(Setting other) { return Enabled == other.Enabled && Value == other.Value; }
    }
    internal sealed class Capabilities {
        public int Min, Max, Step, Default, ApiFlags;
        public bool LiveChange;
        public void Validate(int fps) {
            if (fps != 0 && (fps < Min || fps > Max || (fps - Min) % Math.Max(1, Step) != 0))
                throw new ArgumentException("显卡驱动支持的帧率上限为 " + Min + "–" + Max + " FPS，步长 " + Step + "；0 表示不限");
        }
    }
    internal interface IBackend {
        Capabilities Caps { get; }
        Setting Get();
        void Put(Setting setting);
    }
    internal sealed class Controller {
        readonly IBackend driver;
        public Controller(IBackend backend) { driver = backend; }
        public Dictionary<string, object> Read() {
            var caps = driver.Caps;
            var value = driver.Get();
            return new Dictionary<string, object> {
                { "fpsLimit", value.Enabled ? value.Value : 0 }, { "fpsLimitProvider", "intel" },
                { "fpsLimitMin", caps.Min }, { "fpsLimitMax", caps.Max }, { "fpsLimitStep", caps.Step },
                { "fpsLimitLiveChange", caps.LiveChange }, { "fpsLimitApiFlags", caps.ApiFlags },
                { "fpsLimitEnabled", value.Enabled }, { "fpsLimitValue", value.Value }
            };
        }
        void WriteChecked(Setting wanted, Setting before) {
            if (wanted.Same(before)) return;
            try {
                driver.Put(wanted);
                if (!driver.Get().Same(wanted)) throw new InvalidOperationException("显卡驱动限帧写入后回读不一致");
            } catch {
                // A failed write must not leave an unconfirmed limit behind.
                try { if (!driver.Get().Same(before)) driver.Put(before); } catch { }
                throw;
            }
        }
        public Dictionary<string, object> Set(int fps) {
            var caps = driver.Caps; caps.Validate(fps);
            var before = driver.Get();
            WriteChecked(new Setting(fps != 0, fps == 0 ? before.Value : fps), before);
            return Read();
        }
        public Dictionary<string, object> Session(string command, Dictionary<string, object> request) {
            var caps = driver.Caps;
            var current = driver.Get();
            if (command == "driver_game_read") return new Dictionary<string, object> {
                { "enabled", current.Enabled }, { "value", current.Value }, { "fps", current.Enabled ? current.Value : 0 }
            };
            if (command != "driver_game_set" && command != "driver_game_restore") throw new ArgumentException("限帧操作无效");
            int fps = Convert.ToInt32(request["written"]); caps.Validate(fps);
            var previous = new Setting(Convert.ToBoolean(request["previousEnabled"]), Convert.ToInt32(request["previousValue"]));
            var written = new Setting(fps != 0, fps == 0 ? previous.Value : fps);
            bool restore = command == "driver_game_restore";
            var expected = restore ? written : previous;
            var wanted = restore ? previous : written;
            if (!current.Same(wanted)) {
                if (!current.Same(expected)) {
                    if (restore) return new Dictionary<string, object> { { "conflict", true } };
                    throw new InvalidOperationException("显卡限帧已被其他操作修改，请重试");
                }
                WriteChecked(wanted, current);
            }
            return new Dictionary<string, object> { { "fps", wanted.Enabled ? wanted.Value : 0 } };
        }
    }
    static readonly object Gate = new object();
    static readonly Controller Control = new Controller(new NativeBackend());
    public static Dictionary<string, object> Read() { lock (Gate) return Control.Read(); }
    public static Dictionary<string, object> Set(int fps) { lock (Gate) return Control.Set(fps); }
    public static Dictionary<string, object> Session(string command, Dictionary<string, object> request) {
        lock (Gate) return Control.Session(command, request);
    }

    sealed class NativeBackend : IBackend {
        [StructLayout(LayoutKind.Explicit, Size = 36)] struct Init {
            [FieldOffset(0)] public uint Size;
            [FieldOffset(12)] public uint Flags;
            [FieldOffset(8)] public uint AppVersion;
        }
        [StructLayout(LayoutKind.Sequential)] struct Features {
            public uint Size; public byte Version; public uint Count; public IntPtr Details;
        }
        [StructLayout(LayoutKind.Explicit, Size = 56)] struct Feature {
            [FieldOffset(0)] public uint Size;
            [FieldOffset(8)] public int Type;
            [FieldOffset(25)] [MarshalAs(UnmanagedType.U1)] public bool Set;
            [FieldOffset(28)] public int ValueType;
            [FieldOffset(32)] [MarshalAs(UnmanagedType.U1)] public bool Enabled;
            [FieldOffset(36)] public int Value;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("ControlLib.dll")] static extern uint ctlInit(ref Init args, out IntPtr api);
        [DllImport("ControlLib.dll")] static extern uint ctlClose(IntPtr api);
        [DllImport("ControlLib.dll")] static extern uint ctlEnumerateDevices(IntPtr api, ref uint count, [Out] IntPtr[] devices);
        [DllImport("ControlLib.dll")] static extern uint ctlGetSupported3DCapabilities(IntPtr device, ref Features caps);
        [DllImport("ControlLib.dll")] static extern uint ctlGetSet3DFeature(IntPtr device, ref Feature value);
        IntPtr api, device;
        Capabilities capabilities;
        static void Check(uint code, string action) {
            if (code != 0) throw new InvalidOperationException(action + " 0x" + code.ToString("X8"));
        }
        void Open() {
            if (device != IntPtr.Zero) return;
            if (LoadLibraryW(Path.Combine(Environment.SystemDirectory, "ControlLib.dll")) == IntPtr.Zero)
                throw new InvalidOperationException("未找到 Intel 显卡控制接口，请安装兼容的显卡驱动");
            var init = new Init { Size = 36, AppVersion = 0x10001 };
            Check(ctlInit(ref init, out api), "显卡限帧接口初始化失败");
            try {
                uint count = 0; Check(ctlEnumerateDevices(api, ref count, null), "无法枚举显卡");
                if (count == 0 || count > 64) throw new InvalidOperationException("没有可用的 Intel 显卡");
                var devices = new IntPtr[count]; Check(ctlEnumerateDevices(api, ref count, devices), "无法枚举显卡");
                foreach (var adapter in devices) {
                    var caps = new Features { Size = (uint)Marshal.SizeOf(typeof(Features)) };
                    if (ctlGetSupported3DCapabilities(adapter, ref caps) != 0 || caps.Count == 0 || caps.Count > 256) continue;
                    IntPtr data = Marshal.AllocHGlobal((int)caps.Count * 72);
                    try {
                        for (int offset = 0; offset < caps.Count * 72; offset += 8) Marshal.WriteInt64(data, offset, 0);
                        caps.Details = data;
                        if (ctlGetSupported3DCapabilities(adapter, ref caps) != 0) continue;
                        for (int i = 0; i < caps.Count; i++) {
                            IntPtr item = IntPtr.Add(data, i * 72);
                            if (Marshal.ReadInt32(item) != 2 || Marshal.ReadInt32(item, 4) != 2) continue;
                            var supported = new Capabilities {
                                Min = Marshal.ReadInt32(item, 12), Max = Marshal.ReadInt32(item, 16),
                                Step = Marshal.ReadInt32(item, 20), Default = Marshal.ReadInt32(item, 24),
                                LiveChange = (Marshal.ReadInt16(item, 64) & 16) != 0,
                                ApiFlags = Marshal.ReadInt16(item, 64) & 15
                            };
                            if (supported.Min <= 0 || supported.Max < supported.Min || supported.Step < 1)
                                throw new InvalidOperationException("显卡驱动返回的限帧范围无效");
                            capabilities = supported; device = adapter; return;
                        }
                    } finally { Marshal.FreeHGlobal(data); }
                }
                throw new InvalidOperationException("当前显卡驱动未提供独立限帧功能");
            } catch { ctlClose(api); api = IntPtr.Zero; throw; }
        }
        public Capabilities Caps { get { Open(); return capabilities; } }
        public Setting Get() {
            Open(); var value = new Feature { Size = 56, Type = 2, ValueType = 2 };
            Check(ctlGetSet3DFeature(device, ref value), "读取显卡驱动限帧失败");
            return new Setting(value.Enabled, value.Value);
        }
        public void Put(Setting setting) {
            Open(); var value = new Feature { Size = 56, Type = 2, ValueType = 2, Set = true, Enabled = setting.Enabled, Value = setting.Value };
            Check(ctlGetSet3DFeature(device, ref value), "显卡驱动未接受限帧设置");
        }
    }
}
