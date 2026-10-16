using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// 显示与声音页（ClawLibrary 的 WallStage.Display.cs）要的系统设置，在 ClawPower 的提权执行端里跑：
//   分辨率（当前刷新率下能选的档）、Windows「可变刷新率」开关、HDR（屏幕不支持就报 unsupported）、
//   音频输出设备（列出 / 设为默认）、麦克风静音。亮度、音量、静音、刷新率仍在 SystemControls。
// 编译器是 .NET Framework 4 自带的 csc（C# 5）：不用字符串内插、out var、表达式体成员。
// 接口说明见 docs/display-sound-page.md。
static class DisplayAudio {
    // ---------------- 读 ----------------

    /// <summary>显示与声音页的整份状态：SystemControls.Read() 的那几项 + 这里的。拿不到的项不给字段，硬件没有的写进 unsupported。</summary>
    public static Dictionary<string, object> Read() {
        var result = SystemControls.Read();
        var unsupported = new List<string>();
        try {
            var current = Current();
            result["resolution"] = new[] { current.dmPelsWidth, current.dmPelsHeight };
            result["resolutions"] = Resolutions(current);
        } catch (Exception) { }
        try { result["vrr"] = VrrEnabled(); } catch (Exception) { }
        try {
            bool supported, enabled;
            if (AdvancedColor(out supported, out enabled)) {
                if (supported) result["hdr"] = enabled; else unsupported.Add("hdr");
            }
        } catch (Exception) { }
        try {
            string current;
            var outputs = Outputs(out current);
            result["outputs"] = outputs;
            if (current != null) result["output"] = current;
            if (outputs.Count == 0) unsupported.Add("outputs");
        } catch (Exception) { }
        try { result["micMuted"] = MicMuted(); }
        catch (NoDeviceException) { unsupported.Add("micMuted"); }
        catch (Exception) { }
        result["unsupported"] = unsupported;
        return result;
    }

    class NoDeviceException : Exception { public NoDeviceException(string message) : base(message) { } }

    // ---------------- 分辨率 ----------------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsExW(string device, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr param);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    const int ENUM_CURRENT_SETTINGS = -1, DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;

    static DEVMODE Current() {
        var mode = new DEVMODE(); mode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref mode)) throw new InvalidOperationException("无法读取显示模式");
        return mode;
    }

    /// <summary>当前色深下屏幕给的分辨率，去重、按面积从大到小；竖屏方向的（宽 &lt; 高）和太小的（宽 &lt; 800）不列。</summary>
    static List<int[]> Resolutions(DEVMODE current) {
        var seen = new HashSet<long>();
        var list = new List<int[]>();
        var mode = new DEVMODE(); mode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        for (int i = 0; EnumDisplaySettingsW(null, i, ref mode); i++) {
            if (mode.dmBitsPerPel != current.dmBitsPerPel || mode.dmPelsWidth < 800) continue;
            if ((current.dmPelsWidth >= current.dmPelsHeight) != (mode.dmPelsWidth >= mode.dmPelsHeight)) continue;
            long key = ((long)mode.dmPelsWidth << 32) | (uint)mode.dmPelsHeight;
            if (seen.Add(key)) list.Add(new[] { mode.dmPelsWidth, mode.dmPelsHeight });
        }
        list.Sort((a, b) => ((long)b[0] * b[1]).CompareTo((long)a[0] * a[1]));
        return list;
    }

    /// <summary>换分辨率：优先保持现在的刷新率，那个分辨率下没有就用它最高的。</summary>
    public static Dictionary<string, object> SetResolution(int width, int height) {
        var current = Current();
        int best = -1;
        var mode = new DEVMODE(); mode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        for (int i = 0; EnumDisplaySettingsW(null, i, ref mode); i++) {
            if (mode.dmPelsWidth != width || mode.dmPelsHeight != height || mode.dmBitsPerPel != current.dmBitsPerPel) continue;
            if (mode.dmDisplayFrequency == current.dmDisplayFrequency) { best = mode.dmDisplayFrequency; break; }
            best = Math.Max(best, mode.dmDisplayFrequency);
        }
        if (best <= 0) throw new ArgumentException("屏幕不支持 " + width + " × " + height);
        var target = current;
        target.dmPelsWidth = width; target.dmPelsHeight = height; target.dmDisplayFrequency = best;
        target.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL;
        int code = ChangeDisplaySettingsExW(null, ref target, IntPtr.Zero, 0x1 /* CDS_UPDATEREGISTRY */, IntPtr.Zero);
        if (code != 0) throw new InvalidOperationException("切换分辨率失败（" + code + "）");
        return Read();
    }

    // ---------------- 可变刷新率 ----------------
    // 设置 → 显示 → 图形 →「可变刷新率」写的是 HKCU\Software\Microsoft\DirectX\UserGpuPreferences 的
    // DirectXUserGlobalSettings（形如 "VRROptimizeEnable=1;SwapEffectUpgradeEnable=1;"）。新启动的游戏才读到。

    const string GpuPrefsKey = @"Software\Microsoft\DirectX\UserGpuPreferences", GpuPrefsValue = "DirectXUserGlobalSettings";

    static bool VrrEnabled() {
        using (var key = Registry.CurrentUser.OpenSubKey(GpuPrefsKey)) {
            string text = key == null ? null : key.GetValue(GpuPrefsValue) as string;
            string value = Token(text, "VRROptimizeEnable");
            return value == "1";
        }
    }

    public static Dictionary<string, object> SetVrr(bool enabled) {
        using (var key = Registry.CurrentUser.CreateSubKey(GpuPrefsKey)) {
            string text = key.GetValue(GpuPrefsValue) as string ?? "";
            key.SetValue(GpuPrefsValue, WithToken(text, "VRROptimizeEnable", enabled ? "1" : "0"), RegistryValueKind.String);
        }
        return Read();
    }

    static string Token(string text, string name) {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var part in text.Split(';')) {
            int eq = part.IndexOf('=');
            if (eq > 0 && part.Substring(0, eq).Trim() == name) return part.Substring(eq + 1).Trim();
        }
        return null;
    }

    static string WithToken(string text, string name, string value) {
        var parts = new List<string>();
        bool found = false;
        foreach (var part in (text ?? "").Split(';')) {
            if (part.Trim().Length == 0) continue;
            int eq = part.IndexOf('=');
            if (eq > 0 && part.Substring(0, eq).Trim() == name) { parts.Add(name + "=" + value); found = true; }
            else parts.Add(part);
        }
        if (!found) parts.Add(name + "=" + value);
        return string.Join(";", parts) + ";";
    }

    // ---------------- HDR（DisplayConfig 的 advanced color） ----------------

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_INFO {
        public LUID sourceAdapterId; public uint sourceId, sourceModeInfoIdx, sourceStatusFlags;
        public LUID targetAdapterId; public uint targetId, targetModeInfoIdx; public int outputTechnology, rotation, scaling;
        public uint refreshNumerator, refreshDenominator; public int scanLineOrdering; public bool targetAvailable; public uint targetStatusFlags;
        public uint pathFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_MODE_INFO { public int infoType; public uint id; public LUID adapterId; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] union; }
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type; public int size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint value; public int colorEncoding; public int bitsPerColorChannel; }
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint value; }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint modes, [Out] DISPLAYCONFIG_MODE_INFO[] modeArray, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO info);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE info);
    const uint QDC_ONLY_ACTIVE_PATHS = 2;

    /// <summary>第一块活动屏（掌机自己的屏）。读不到返回 false。</summary>
    static bool FirstTarget(out LUID adapter, out uint target) {
        adapter = new LUID(); target = 0;
        uint pathCount, modeCount;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount) != 0 || pathCount == 0) return false;
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0 || pathCount == 0) return false;
        adapter = paths[0].targetAdapterId; target = paths[0].targetId;
        return true;
    }

    static bool AdvancedColor(out bool supported, out bool enabled) {
        supported = enabled = false;
        LUID adapter; uint target;
        if (!FirstTarget(out adapter, out target)) return false;
        var info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
        info.header.type = 9;   // DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO
        info.header.size = Marshal.SizeOf(typeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO));
        info.header.adapterId = adapter; info.header.id = target;
        if (DisplayConfigGetDeviceInfo(ref info) != 0) return false;
        supported = (info.value & 0x1) != 0;   // advancedColorSupported
        enabled = (info.value & 0x2) != 0;     // advancedColorEnabled
        return true;
    }

    public static Dictionary<string, object> SetHdr(bool enabled) {
        bool supported, current;
        if (!AdvancedColor(out supported, out current) || !supported) throw new InvalidOperationException("这块屏幕不支持 HDR");
        LUID adapter; uint target;
        FirstTarget(out adapter, out target);
        var state = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE();
        state.header.type = 10;   // DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE
        state.header.size = Marshal.SizeOf(typeof(DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE));
        state.header.adapterId = adapter; state.header.id = target;
        state.value = enabled ? 1u : 0u;
        int code = DisplayConfigSetDeviceInfo(ref state);
        if (code != 0) throw new InvalidOperationException("切换 HDR 失败（" + code + "）");
        return Read();
    }

    // ---------------- 音频设备（Core Audio） ----------------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT value);

    // 设默认设备用的是系统里没公开的 IPolicyConfig（声音设置面板自己用的那个，Win7 起没变过）
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigCo { }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig {
        [PreserveSig] int GetMixFormat(); [PreserveSig] int GetDeviceFormat(); [PreserveSig] int ResetDeviceFormat(); [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod(); [PreserveSig] int SetProcessingPeriod(); [PreserveSig] int GetShareMode(); [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue(); [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }

    const int eRender = 0, eCapture = 1, eConsole = 0, eMultimedia = 1, eCommunications = 2, DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23;
    static readonly Guid FriendlyNameFmt = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    static List<Dictionary<string, object>> Outputs(out string current) {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        current = null;
        IMMDevice def;
        if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out def) == 0 && def != null) def.GetId(out current);
        var list = new List<Dictionary<string, object>>();
        IMMDeviceCollection devices;
        if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out devices) != 0) return list;
        uint count;
        devices.GetCount(out count);
        for (uint i = 0; i < count; i++) {
            IMMDevice device;
            if (devices.Item(i, out device) != 0) continue;
            string id;
            if (device.GetId(out id) != 0) continue;
            list.Add(new Dictionary<string, object> { { "id", id }, { "name", FriendlyName(device) ?? "音频设备" } });
        }
        return list;
    }

    static string FriendlyName(IMMDevice device) {
        IPropertyStore store;
        if (device.OpenPropertyStore(0 /* STGM_READ */, out store) != 0) return null;
        var key = new PROPERTYKEY { fmtid = FriendlyNameFmt, pid = 14 };
        PROPVARIANT value;
        if (store.GetValue(ref key, out value) != 0) return null;
        try { return value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.p) : null; }
        finally { PropVariantClear(ref value); }
    }

    public static Dictionary<string, object> SetOutput(string id) {
        string current;
        bool known = false;
        foreach (var o in Outputs(out current)) if ((string)o["id"] == id) known = true;
        if (!known) throw new ArgumentException("没有这个输出设备，可能已经拔掉了");
        var policy = (IPolicyConfig)new PolicyConfigCo();
        foreach (int role in new[] { eConsole, eMultimedia, eCommunications }) {
            int hr = policy.SetDefaultEndpoint(id, role);
            if (hr != 0) throw new InvalidOperationException(string.Format("切换输出设备失败 0x{0:X8}", hr));
        }
        return Read();
    }

    static SystemControls.IAudioEndpointVolume Mic() {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        IMMDevice device;
        if (enumerator.GetDefaultAudioEndpoint(eCapture, eConsole, out device) != 0 || device == null) throw new NoDeviceException("没有麦克风");
        var iid = typeof(SystemControls.IAudioEndpointVolume).GUID; object endpoint;
        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out endpoint) != 0) throw new InvalidOperationException("无法控制麦克风");
        return (SystemControls.IAudioEndpointVolume)endpoint;
    }

    static bool MicMuted() { bool muted; Mic().GetMute(out muted); return muted; }

    public static Dictionary<string, object> SetMicMute(bool muted) {
        var context = Guid.Empty;
        Mic().SetMute(muted, ref context);
        return Read();
    }
}
