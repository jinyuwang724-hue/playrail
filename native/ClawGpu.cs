// 显示与声音页的「显卡」一组：Intel 显卡驱动的控制接口（IGCL，系统里的 ControlLib.dll），只用全局的 3D 设置（不按游戏）。
// 低延迟（关 / 开 / 加强）、帧同步（翻转模式：应用决定、垂直同步、关垂直同步、平滑同步、极速帧、限制到刷新率）、XeSS 插帧（应用决定 / 2 倍）。
// 结构布局照 Intel 公开的 igcl_api.h（x64）。驱动不认的项不给字段，原因写进 gpu.error；整个接口拿不到时 available = false。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

static class IntelGpu {
    const int FeatureFrameGeneration = 17, FeatureGamingFlipModes = 9, FeatureLowLatency = 16;
    const int ValueTypeEnum = 4;

    /// <summary>ctl_init_args_t（36 字节：ApplicationUID 的 Data4 是 8 个字节，按 1 对齐）。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 36)]
    struct InitArgs {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public uint AppVersion;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint SupportedVersion;
        // 20…35：ApplicationUID，全 0 = 默认
    }

    /// <summary>ctl_3d_feature_getset_t（x64 共 56 字节；Value 是 8 字节的联合体，枚举值在头 4 个字节）。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 56)]
    struct Feature3D {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public int FeatureType;
        [FieldOffset(16)] public IntPtr ApplicationName;
        [FieldOffset(24)] public sbyte ApplicationNameLength;
        [FieldOffset(25)] [MarshalAs(UnmanagedType.U1)] public bool Set;
        [FieldOffset(28)] public int ValueType;
        [FieldOffset(32)] public uint EnumValue;
        [FieldOffset(40)] public int CustomValueSize;
        [FieldOffset(48)] public IntPtr CustomValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FeatureCaps {
        public uint Size;
        public byte Version;
        public uint Count;
        public IntPtr Details;
    }

    [DllImport("ControlLib.dll")] static extern uint ctlInit(ref InitArgs args, out IntPtr api);
    [DllImport("ControlLib.dll")] static extern uint ctlClose(IntPtr api);
    [DllImport("ControlLib.dll")] static extern uint ctlEnumerateDevices(IntPtr api, ref uint count, [Out] IntPtr[] devices);
    [DllImport("ControlLib.dll")] static extern uint ctlGetSet3DFeature(IntPtr device, ref Feature3D feature);
    [DllImport("ControlLib.dll")] static extern uint ctlGetSupported3DCapabilities(IntPtr device, ref FeatureCaps caps);

    static IntPtr api, device, emptyName;
    static uint? gamingFlipMask;

    /// <summary>第一次用时初始化，之后一直用同一个句柄（ClawPower 常驻）。</summary>
    static void Open() {
        if (device != IntPtr.Zero) return;
        var args = new InitArgs { Size = (uint)Marshal.SizeOf(typeof(InitArgs)), AppVersion = (1u << 16) | 1u };
        uint r = ctlInit(ref args, out api);
        if (r != 0) throw new InvalidOperationException(string.Format("显卡控制接口初始化失败 0x{0:X8}", r));
        uint count = 0;
        r = ctlEnumerateDevices(api, ref count, null);
        if (r != 0 || count == 0) throw new InvalidOperationException("没有找到 Intel 显卡");
        var devices = new IntPtr[count];
        r = ctlEnumerateDevices(api, ref count, devices);
        if (r != 0) throw new InvalidOperationException(string.Format("列显卡失败 0x{0:X8}", r));
        device = devices[0];
        emptyName = Marshal.AllocHGlobal(1);
        Marshal.WriteByte(emptyName, 0);
    }

    static uint Call(int feature, bool set, uint value, out uint result) {
        var f = new Feature3D {
            Size = (uint)Marshal.SizeOf(typeof(Feature3D)), FeatureType = feature, ApplicationName = emptyName, ApplicationNameLength = 0,
            Set = set, ValueType = ValueTypeEnum, EnumValue = value,
        };
        uint r = ctlGetSet3DFeature(device, ref f);
        result = f.EnumValue;
        return r;
    }

    static readonly string[] LowLatency = { "off", "on", "boost" };
    static readonly string[] EnduranceProfiles = { "performance", "balanced", "battery" };

    static uint EnduranceCall(bool set, uint control, uint profile, out uint actualControl, out uint actualProfile) {
        IntPtr value = Marshal.AllocHGlobal(8);
        try {
            Marshal.WriteInt32(value, (int)control);
            Marshal.WriteInt32(value, 4, (int)profile);
            var f = new Feature3D { Size = 56, FeatureType = 1, ApplicationName = emptyName,
                Set = set, ValueType = 5, CustomValueSize = 8, CustomValue = value };
            uint r = ctlGetSet3DFeature(device, ref f);
            actualControl = unchecked((uint)Marshal.ReadInt32(value));
            actualProfile = unchecked((uint)Marshal.ReadInt32(value, 4));
            return r;
        } finally { Marshal.FreeHGlobal(value); }
    }

    public static Dictionary<string, object> EnduranceRead() {
        var view = new Dictionary<string, object> { { "available", false } };
        try {
            Open();
            var caps = new FeatureCaps { Size = (uint)Marshal.SizeOf(typeof(FeatureCaps)) };
            uint r = ctlGetSupported3DCapabilities(device, ref caps);
            if (r != 0 || caps.Count == 0 || caps.Count > 256) throw new InvalidOperationException("读不到 Intel 智能功耗支持列表");
            IntPtr details = Marshal.AllocHGlobal((int)caps.Count * 72);
            IntPtr custom = Marshal.AllocHGlobal(32);
            try {
                for (int i = 0; i < caps.Count * 72; i += 8) Marshal.WriteInt64(details, i, 0);
                for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(custom, i, 0);
                caps.Details = details;
                r = ctlGetSupported3DCapabilities(device, ref caps);
                if (r != 0) throw new InvalidOperationException("读取 Intel 智能功耗能力失败");
                IntPtr eg = IntPtr.Zero;
                for (int i = 0; i < caps.Count; i++) {
                    IntPtr item = IntPtr.Add(details, i * 72);
                    if (Marshal.ReadInt32(item) == 1) { eg = item; break; }
                }
                if (eg == IntPtr.Zero || Marshal.ReadInt32(eg, 4) != 5) throw new InvalidOperationException("当前驱动不支持 Intel 智能功耗");
                Marshal.WriteInt32(eg, 32, 32);
                Marshal.WriteIntPtr(eg, 40, custom);
                r = ctlGetSupported3DCapabilities(device, ref caps);
                if (r != 0) throw new InvalidOperationException("读取 Intel 智能功耗档位失败");
                uint controls = unchecked((uint)Marshal.ReadInt64(custom));
                uint modes = unchecked((uint)Marshal.ReadInt64(custom, 16));
                if ((controls & 5) != 5 || (modes & 7) == 0) throw new InvalidOperationException("当前驱动没有提供自动功耗调节");
                uint control, profile;
                r = EnduranceCall(false, 0, 0, out control, out profile);
                if (r != 0 || control > 2 || profile >= EnduranceProfiles.Length)
                    throw new InvalidOperationException(string.Format("读取 Intel 智能功耗配置失败 0x{0:X8}", r));
                var profiles = new List<Dictionary<string, object>>();
                string[] names = { "性能", "均衡", "续航" };
                for (int i = 0; i < 3; i++) if ((modes & (1u << i)) != 0)
                    profiles.Add(new Dictionary<string, object> { { "id", EnduranceProfiles[i] }, { "name", names[i] } });
                view["available"] = true;
                view["control"] = control == 0 ? "off" : control == 2 ? "auto" : "on";
                view["profile"] = EnduranceProfiles[profile];
                view["profiles"] = profiles;
                view["perApplication"] = Marshal.ReadByte(eg, 48) != 0;
                view["liveChange"] = (Marshal.ReadInt16(eg, 64) & 16) != 0;
                view["conflicts"] = Marshal.ReadInt64(eg, 56);
                uint pacing;
                if (Call(0, false, 0, out pacing) == 0) view["framePacing"] = pacing;
            } finally { Marshal.FreeHGlobal(details); Marshal.FreeHGlobal(custom); }
        } catch (Exception e) { view["error"] = e.Message; }
        return view;
    }

    public static Dictionary<string, object> EnduranceSet(string control, string profile) {
        var before = EnduranceRead();
        if (!true.Equals(before["available"])) throw new InvalidOperationException(Convert.ToString(before["error"]));
        int mode = Array.IndexOf(EnduranceProfiles, profile);
        if ((control != "auto" && control != "off") || mode < 0) throw new ArgumentException("Intel 智能功耗选项无效");
        bool supported = false;
        foreach (var item in (List<Dictionary<string, object>>)before["profiles"])
            if (Convert.ToString(item["id"]) == profile) supported = true;
        if (!supported) throw new InvalidOperationException("驱动不支持这个 Intel 智能功耗档位");
        uint actualControl, actualProfile;
        uint r = EnduranceCall(true, control == "auto" ? 2u : 0u, (uint)mode, out actualControl, out actualProfile);
        if (r != 0) throw new InvalidOperationException(string.Format("Intel 智能功耗写入失败 0x{0:X8}", r));
        var after = EnduranceRead();
        if (!true.Equals(after["available"]) || Convert.ToString(after["control"]) != control || Convert.ToString(after["profile"]) != profile)
            throw new InvalidOperationException("Intel 智能功耗写入后回读不一致");
        return after;
    }

    public static void FramePacingSet(uint value) {
        Open();
        if (value > 3) throw new ArgumentException("帧节奏设置无效");
        uint actual;
        uint r = Call(0, true, value, out actual);
        if (r != 0) throw new InvalidOperationException(string.Format("帧节奏写入失败 0x{0:X8}", r));
    }
    /// <summary>帧同步：id → 驱动的翻转模式位。</summary>
    static readonly string[] FlipIds = { "app", "vsyncOff", "vsync", "smooth", "speed", "capped" };
    static readonly string[] FlipNames = { "应用决定", "关闭垂直同步", "垂直同步", "平滑同步", "极速帧", "限制到刷新率" };

    static uint GamingFlipMask() {
        if (gamingFlipMask.HasValue) return gamingFlipMask.Value;
        var caps = new FeatureCaps { Size = (uint)Marshal.SizeOf(typeof(FeatureCaps)) };
        uint r = ctlGetSupported3DCapabilities(device, ref caps);
        if (r != 0 || caps.Count == 0 || caps.Count > 256) throw new InvalidOperationException("读不到帧同步支持列表");
        // igcl_api.h: ctl_3d_feature_details_t is 72 bytes on x64; EnumType.SupportedTypes is at byte 8.
        IntPtr details = Marshal.AllocHGlobal((int)caps.Count * 72);
        try {
            for (int i = 0; i < caps.Count * 72; i += 8) Marshal.WriteInt64(details, i, 0);
            caps.Details = details;
            r = ctlGetSupported3DCapabilities(device, ref caps);
            if (r != 0) throw new InvalidOperationException("读不到帧同步支持列表");
            for (int i = 0; i < caps.Count; i++) {
                IntPtr item = IntPtr.Add(details, i * 72);
                if (Marshal.ReadInt32(item) == FeatureGamingFlipModes && Marshal.ReadInt32(item, 4) == ValueTypeEnum) {
                    gamingFlipMask = unchecked((uint)Marshal.ReadInt64(item, 8));
                    return gamingFlipMask.Value;
                }
            }
            throw new InvalidOperationException("驱动没有提供帧同步能力");
        } finally { Marshal.FreeHGlobal(details); }
    }

    internal static List<Dictionary<string, object>> FrameSyncOptions(uint supported) {
        var options = new List<Dictionary<string, object>>();
        for (int i = 0; i < FlipIds.Length; i++)
            if ((supported & (1u << i)) != 0)
                options.Add(new Dictionary<string, object> { { "id", FlipIds[i] }, { "name", FlipNames[i] } });
        return options;
    }

    public static Dictionary<string, object> Read() {
        var gpu = new Dictionary<string, object>();
        try { Open(); }
        catch (Exception e) { gpu["available"] = false; gpu["error"] = e.Message; return gpu; }
        gpu["available"] = true;
        var errors = new List<string>();
        uint v;
        uint r = Call(FeatureLowLatency, false, 0, out v);
        if (r == 0 && v < LowLatency.Length) gpu["lowLatency"] = LowLatency[v]; else errors.Add(string.Format("低延迟 0x{0:X8}", r));
        r = Call(FeatureGamingFlipModes, false, 0, out v);
        if (r == 0) {
            int bit = 0;
            for (int i = 0; i < FlipIds.Length; i++) if ((v & (1u << i)) != 0) { bit = i; break; }
            try {
                var options = FrameSyncOptions(GamingFlipMask());
                if (options.Count > 0) {
                    gpu["frameSync"] = FlipIds[bit];
                    gpu["frameSyncOptions"] = options;
                }
            } catch (Exception e) { errors.Add(e.Message); }
        } else errors.Add(string.Format("帧同步 0x{0:X8}", r));
        r = Call(FeatureFrameGeneration, false, 0, out v);
        if (r == 0) gpu["xessFg"] = v != 0; else errors.Add(string.Format("XeSS 插帧 0x{0:X8}", r));
        if (errors.Count > 0) gpu["error"] = "驱动不认：" + string.Join("、", errors);
        return gpu;
    }

    /// <summary>改一项：lowLatency = off / on / boost；frameSync = 上面的 id；xessFg = true / false。</summary>
    public static void Set(string name, object value) {
        Open();
        uint result, r;
        switch (name) {
            case "lowLatency": {
                int i = Array.IndexOf(LowLatency, Convert.ToString(value));
                if (i < 0) throw new ArgumentException("低延迟只能是 off / on / boost");
                r = Call(FeatureLowLatency, true, (uint)i, out result);
                break;
            }
            case "frameSync": {
                int i = Array.IndexOf(FlipIds, Convert.ToString(value));
                if (i < 0) throw new ArgumentException("没有这个帧同步模式");
                if ((GamingFlipMask() & (1u << i)) == 0) throw new InvalidOperationException("当前显卡驱动不支持这个帧同步模式");
                r = Call(FeatureGamingFlipModes, true, 1u << i, out result);
                break;
            }
            case "xessFg":
                r = Call(FeatureFrameGeneration, true, true.Equals(value) ? 1u : 0u, out result);
                break;
            default:
                throw new ArgumentException("显卡选项里没有 " + name);
        }
        if (r != 0) throw new InvalidOperationException(string.Format("显卡驱动没有接受这个改动 0x{0:X8}", r));
    }
}
