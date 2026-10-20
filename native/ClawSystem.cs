using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// The control centre's 系统 page: battery health, the charge limit as the EC has it (read-only),
// the power-button / hibernate / display-off settings of the active plan, which devices may wake
// the machine, and the firmware and driver versions. Everything here runs in the elevated helper
// because MSI's WMI reader and powercfg's wake commands refuse a normal user.
static class SystemPage {
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern bool GetPwrCapabilities(byte[] caps);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll")] static extern int GetOEMCP();

    static Guid SubButtons = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347"), PowerButton = new Guid("7648efa3-dd9c-4e3e-b566-50f929386280");
    static Guid SubSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20"), HibernateIdle = new Guid("9d7815a6-7ee4-497e-8888-515a05f02364");
    static Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99"), VideoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    // Power button action indices as Windows numbers them.
    static readonly string[] ButtonActions = { "nothing", "sleep", "hibernate", "shutdown", "display" };

    static Dictionary<string, object> device;   // never changes while the helper runs

    public static Dictionary<string, object> Read() {
        var r = new Dictionary<string, object>();
        try { r["battery"] = Battery(); } catch (Exception e) { r["batteryError"] = e.Message; }
        try { r["chargeLimit"] = ChargeLimit(); } catch (Exception e) { r["chargeLimitError"] = e.Message; }
        try { r["power"] = PowerSettings(); } catch (Exception e) { r["powerError"] = e.Message; }
        try { r["wakeArmed"] = Powercfg("/devicequery wake_armed"); } catch (Exception e) { r["wakeError"] = e.Message; }
        if (device == null) {
            var d = new Dictionary<string, object>();
            Wmi(d, "SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem", o => {
                d["model"] = Convert.ToString(o["Model"]).Trim();
                d["memoryGB"] = Math.Round(Convert.ToDouble(o["TotalPhysicalMemory"]) / (1L << 30));
            });
            Wmi(d, "SELECT Name FROM Win32_Processor", o => d["cpu"] = Convert.ToString(o["Name"]).Trim());
            Wmi(d, "SELECT SMBIOSBIOSVersion FROM Win32_BIOS", o => d["bios"] = Convert.ToString(o["SMBIOSBIOSVersion"]).Trim());
            Wmi(d, "SELECT Name, DriverVersion FROM Win32_VideoController", o => {
                string name = Convert.ToString(o["Name"]);
                if (name.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) < 0) return;  // skip virtual displays
                d["gpu"] = name.Replace("(R)", "").Replace("(TM)", "").Trim();
                d["gpuDriver"] = Convert.ToString(o["DriverVersion"]);
            });
            device = d;
        }
        r["device"] = device;
        return r;
    }

    static void Wmi(Dictionary<string, object> d, string query, Action<System.Management.ManagementBaseObject> take) {
        try {
            using (var s = new System.Management.ManagementObjectSearcher(query))
                foreach (var o in s.Get()) take(o);
        } catch (Exception e) { d["error"] = e.Message; }
    }

    // ---------------- battery ----------------

    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid cls, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid cls, uint index, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref uint input, int inSize, out uint output, int outSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref BatteryQuery input, int inSize, out BatteryInformation output, int outSize, out int returned, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)] struct DeviceInterfaceData { public int Size; public Guid Class; public uint Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct BatteryQuery { public uint Tag; public int Level; public int AtRate; }
    [StructLayout(LayoutKind.Sequential)] struct BatteryInformation {
        public uint Capabilities; public byte Technology, R1, R2, R3; public uint Chemistry;
        public uint DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;
    }

    /// <summary>Design and full-charge capacity (mWh) and cycle count straight from the battery driver
    /// (the WMI BatteryStaticData class fails on this Claw).</summary>
    static Dictionary<string, object> Battery() {
        var cls = new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
        IntPtr set = SetupDiGetClassDevs(ref cls, IntPtr.Zero, IntPtr.Zero, 0x12);  // PRESENT | DEVICEINTERFACE
        if (set == new IntPtr(-1)) throw new InvalidOperationException("找不到电池");
        try {
            var data = new DeviceInterfaceData { Size = Marshal.SizeOf(typeof(DeviceInterfaceData)) };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref cls, 0, ref data)) throw new InvalidOperationException("找不到电池");
            uint size;
            SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out size, IntPtr.Zero);
            IntPtr detail = Marshal.AllocHGlobal((int)size);
            string path;
            try {
                Marshal.WriteInt32(detail, 8);  // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out size, IntPtr.Zero)) throw new InvalidOperationException("读不到电池路径");
                path = Marshal.PtrToStringUni(detail + 4);
            } finally { Marshal.FreeHGlobal(detail); }
            using (var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                if (h.IsInvalid) throw new InvalidOperationException("打不开电池设备");
                uint wait = 0, tag; int got;
                if (!DeviceIoControl(h, 0x294040, ref wait, 4, out tag, 4, out got, IntPtr.Zero) || tag == 0) throw new InvalidOperationException("电池没有应答");
                var q = new BatteryQuery { Tag = tag, Level = 0 };
                BatteryInformation info;
                if (!DeviceIoControl(h, 0x294044, ref q, Marshal.SizeOf(q), out info, Marshal.SizeOf(typeof(BatteryInformation)), out got, IntPtr.Zero))
                    throw new InvalidOperationException("读不到电池信息");
                var r = new Dictionary<string, object>();
                r["design"] = (long)info.DesignedCapacity;
                r["full"] = (long)info.FullChargedCapacity;
                r["cycles"] = (long)info.CycleCount;
                return r;
            }
        } finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>
    /// MSI keeps the charge limit in one EC byte: bit 7 = on, the low bits = the percentage
    /// (0xD7 on recent models, 0xEF on older ones). Which one this Claw uses is not confirmed,
    /// so both raw bytes go back and the page only shows a value that reads as a sane limit.
    /// </summary>
    static Dictionary<string, object> ChargeLimit() {
        var r = new Dictionary<string, object>();
        foreach (byte address in new byte[] { 0xD7, 0xEF }) {
            int value = MsiFan.EcRead(address);
            r[address.ToString("x2")] = value;
            int percent = value & 0x7F;
            if (!r.ContainsKey("percent") && (value & 0x80) != 0 && percent >= 50 && percent <= 100) {
                r["address"] = address.ToString("x2");
                r["percent"] = percent;
            }
        }
        return r;
    }

    // ---------------- power plan ----------------

    static Guid ActiveScheme() {
        IntPtr pointer;
        uint error = PowerGetActiveScheme(IntPtr.Zero, out pointer);
        if (error != 0) throw new InvalidOperationException("无法读取当前电源方案 " + error);
        try { return (Guid)Marshal.PtrToStructure(pointer, typeof(Guid)); } finally { LocalFree(pointer); }
    }

    static long ReadIndex(Guid scheme, Guid sub, Guid setting, bool ac) {
        uint value;
        uint error = ac ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value)
                        : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value);
        if (error != 0) throw new InvalidOperationException("读取电源设置失败 " + error);
        return value;
    }

    static Dictionary<string, object> PowerSettings() {
        Guid scheme = ActiveScheme();
        var caps = new byte[76];
        bool hibernate = GetPwrCapabilities(caps) && caps[6] != 0 && caps[8] != 0;  // SystemS4, HiberFilePresent
        return new Dictionary<string, object> {
            { "scheme", scheme.ToString() }, { "hibernateAvailable", hibernate },
            { "buttonAc", ButtonName(ReadIndex(scheme, SubButtons, PowerButton, true)) },
            { "buttonDc", ButtonName(ReadIndex(scheme, SubButtons, PowerButton, false)) },
            { "hibernateAc", ReadIndex(scheme, SubSleep, HibernateIdle, true) },
            { "hibernateDc", ReadIndex(scheme, SubSleep, HibernateIdle, false) },
            { "displayAc", ReadIndex(scheme, SubVideo, VideoIdle, true) },
            { "displayDc", ReadIndex(scheme, SubVideo, VideoIdle, false) },
        };
    }

    static string ButtonName(long i) { return i >= 0 && i < ButtonActions.Length ? ButtonActions[i] : "other"; }

    static void Write(Guid sub, Guid setting, uint value, bool ac, bool dc) {
        Guid scheme = ActiveScheme();
        if (ac) { uint e = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value); if (e != 0) throw new InvalidOperationException("写入电源设置失败 " + e); }
        if (dc) { uint e = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value); if (e != 0) throw new InvalidOperationException("写入电源设置失败 " + e); }
        PowerSetActiveScheme(IntPtr.Zero, ref scheme);  // takes effect now, as the Settings app does
    }

    public static Dictionary<string, object> SetButton(string action) {
        int index = Array.IndexOf(ButtonActions, action);
        if (index < 1 || index > 3) throw new ArgumentException("不支持的电源键动作");
        if (action == "hibernate" && !(bool)PowerSettings()["hibernateAvailable"]) throw new InvalidOperationException("这台机器没有开启休眠");
        Write(SubButtons, PowerButton, (uint)index, true, true);
        return Read();
    }

    public static Dictionary<string, object> SetHibernateAfter(int seconds) {
        if (seconds < 0 || seconds > 86400) throw new ArgumentException("时间超出范围");
        Write(SubSleep, HibernateIdle, (uint)seconds, true, true);
        return Read();
    }

    public static Dictionary<string, object> SetDisplayOff(int seconds) {
        if (seconds < 0 || seconds > 86400) throw new ArgumentException("时间超出范围");
        Write(SubVideo, VideoIdle, (uint)seconds, false, true);  // on battery only; plugged-in stays as it is
        return Read();
    }

    // ---------------- wake ----------------

    /// <summary>
    /// Wake only from the power button: take the wake permission away from every device that has it
    /// and answer with their names, so the agent can give exactly those back later.
    /// </summary>
    public static Dictionary<string, object> SetWake(bool lockDown, List<string> restore) {
        var changed = new List<string>();
        if (lockDown) {
            foreach (string name in Powercfg("/devicequery wake_armed")) { Powercfg("/devicedisablewake \"" + name + "\""); changed.Add(name); }
        } else {
            foreach (string name in restore ?? new List<string>()) {
                try { Powercfg("/deviceenablewake \"" + name + "\""); changed.Add(name); } catch (Exception) { }  // the device may be gone
            }
        }
        var r = Read();
        r["changed"] = changed;
        return r;
    }

    static List<string> Powercfg(string args) {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), args) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.GetEncoding(GetOEMCP()),
        };
        using (var p = Process.Start(start)) {
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            if (p.ExitCode != 0) throw new InvalidOperationException(output.Trim().Length > 0 ? output.Trim() : "powercfg 失败");
            var lines = new List<string>();
            foreach (string line in output.Split('\n')) {
                string t = line.Trim();
                // "NONE" / "无" when nothing is armed
                if (t.Length == 0 || t == "NONE" || t == "无") continue;
                lines.Add(t);
            }
            return lines;
        }
    }
}
