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
        var errors = new Dictionary<string, object>();   // 读失败的项和原因（字段不给，原因写这里，界面和日志都看得到）
        try {
            var current = Current();
            result["resolution"] = new[] { current.dmPelsWidth, current.dmPelsHeight };
            result["resolutions"] = Resolutions(current);
        } catch (Exception e) { errors["resolution"] = e.Message; }
        try { result["vrr"] = VrrEnabled(); } catch (Exception e) { errors["vrr"] = e.Message; }
        try {
            bool supported, enabled;
            if (AdvancedColor(out supported, out enabled)) {
                if (supported) result["hdr"] = enabled; else unsupported.Add("hdr");
            }
        } catch (Exception e) { errors["hdr"] = e.Message; }
        try {
            string current;
            var outputs = Outputs(out current);
            result["outputs"] = outputs;
            if (current != null) result["output"] = current;
            if (outputs.Count == 0) unsupported.Add("outputs");
        } catch (Exception e) { errors["outputs"] = e.GetType().Name + ": " + e.Message; }
        try { result["micMuted"] = MicMuted(); }
        catch (NoDeviceException) { unsupported.Add("micMuted"); }
        catch (Exception e) { errors["micMuted"] = e.GetType().Name + ": " + e.Message; }
        try {
            int recommended; List<int> options;
            result["scale"] = Scale(out recommended, out options);
            result["scales"] = options;
            result["scaleRecommended"] = recommended;
        } catch (Exception e) { errors["scale"] = e.Message; }
        try {
            if (HasLightSensor()) result["autoBrightness"] = AutoBrightness();
            else unsupported.Add("autoBrightness");
        } catch (Exception e) { errors["autoBrightness"] = e.Message; }
        result["unsupported"] = unsupported;
        if (errors.Count > 0) result["errors"] = errors;
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

    // ---------------- 屏幕缩放（设置 → 显示 → 缩放；DisplayConfig 没公开的 DPI 缩放项，设置页自己用的就是它） ----------------
    // 值是相对「推荐」的档位偏移：推荐档 = DpiSteps[-min]，当前 = DpiSteps[-min + cur]，能选 min…max。

    static readonly int[] DpiSteps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_SOURCE_DPI_SCALE_GET { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public int minScaleRel, curScaleRel, maxScaleRel; }
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_SOURCE_DPI_SCALE_SET { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public int scaleRel; }
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DPI_SCALE_GET info);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DPI_SCALE_SET info);

    /// <summary>第一块活动屏的源（缩放挂在源上，不是目标）。</summary>
    static bool FirstSource(out LUID adapter, out uint source) {
        adapter = new LUID(); source = 0;
        uint pathCount, modeCount;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount) != 0 || pathCount == 0) return false;
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0 || pathCount == 0) return false;
        adapter = paths[0].sourceAdapterId; source = paths[0].sourceId;
        return true;
    }

    /// <summary>当前缩放（%）、推荐缩放、可选的几档；读不到抛错。</summary>
    static int Scale(out int recommended, out List<int> options) {
        LUID adapter; uint source;
        if (!FirstSource(out adapter, out source)) throw new InvalidOperationException("找不到屏幕");
        var info = new DISPLAYCONFIG_SOURCE_DPI_SCALE_GET();
        info.header.type = -3;   // DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE
        info.header.size = Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DPI_SCALE_GET));
        info.header.adapterId = adapter; info.header.id = source;
        int code = DisplayConfigGetDeviceInfo(ref info);
        if (code != 0) throw new InvalidOperationException("读不到缩放（" + code + "）");
        int baseIdx = -info.minScaleRel;
        if (baseIdx < 0 || baseIdx >= DpiSteps.Length) throw new InvalidOperationException("缩放档位超出范围");
        recommended = DpiSteps[baseIdx];
        options = new List<int>();
        for (int rel = info.minScaleRel; rel <= info.maxScaleRel; rel++)
            if (baseIdx + rel >= 0 && baseIdx + rel < DpiSteps.Length) options.Add(DpiSteps[baseIdx + rel]);
        int cur = baseIdx + info.curScaleRel;
        return cur >= 0 && cur < DpiSteps.Length ? DpiSteps[cur] : recommended;
    }

    public static Dictionary<string, object> SetScale(int percent) {
        int recommended; List<int> options;
        Scale(out recommended, out options);
        if (!options.Contains(percent)) throw new ArgumentException("这块屏幕没有 " + percent + "% 这一档");
        LUID adapter; uint source;
        FirstSource(out adapter, out source);
        var set = new DISPLAYCONFIG_SOURCE_DPI_SCALE_SET();
        set.header.type = -4;   // DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE
        set.header.size = Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DPI_SCALE_SET));
        set.header.adapterId = adapter; set.header.id = source;
        set.scaleRel = Array.IndexOf(DpiSteps, percent) - Array.IndexOf(DpiSteps, recommended);
        int code = DisplayConfigSetDeviceInfo(ref set);
        if (code != 0) throw new InvalidOperationException("改缩放失败（" + code + "）");
        return Read();
    }

    // ---------------- 自动亮度（电源设置 ADAPTBRIGHT；没有光线传感器的机器写进 unsupported） ----------------

    static bool HasLightSensor() {
        // 光线传感器的设备接口类 GUID_DEVINTERFACE_SENSOR + ALS 类别：直接问传感器 API 太重，这里看注册的环境光传感器设备
        using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Sensor'"))
            foreach (System.Management.ManagementObject o in searcher.Get()) {
                string name = Convert.ToString(o["Name"]) ?? "";
                if (name.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("光", StringComparison.Ordinal) >= 0) return true;
            }
        return false;
    }

    static readonly Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99"), AdaptBright = new Guid("fbd9aa66-9553-4097-ba44-ed6e9d65eab8");
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);

    static Guid ActiveScheme() {
        IntPtr p;
        if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) throw new InvalidOperationException("读不到电源方案");
        try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); } finally { LocalFree(p); }
    }

    static bool AutoBrightness() {
        Guid scheme = ActiveScheme(), sub = SubVideo, setting = AdaptBright; uint value;
        if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value) != 0) throw new InvalidOperationException("读不到自动亮度");
        return value != 0;
    }

    /// <summary>插电、电池两边一起改，再重新激活当前方案让它生效。</summary>
    public static Dictionary<string, object> SetAutoBrightness(bool enabled) {
        if (!HasLightSensor()) throw new InvalidOperationException("这台机器没有光线传感器");
        Guid scheme = ActiveScheme(), sub = SubVideo, setting = AdaptBright;
        uint v = enabled ? 1u : 0u;
        if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, v) != 0 || PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, v) != 0)
            throw new InvalidOperationException("改自动亮度失败");
        PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        return Read();
    }

    // ---------------- 音频设备（Core Audio） ----------------

    // 设备枚举器按 CLSID 建、直接转成接口：它是 COM 单例，SystemControls 已经用自己的 ComImport 类包过一次，
    // 再 new 一个同 CLSID 的 ComImport 类会撞上已有的包装（InvalidCastException MMDeviceEnumerator → MMDeviceEnumeratorCo）。
    static IMMDeviceEnumerator Enumerator() { return (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))); }
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
        var enumerator = Enumerator();
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
        var enumerator = Enumerator();
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
