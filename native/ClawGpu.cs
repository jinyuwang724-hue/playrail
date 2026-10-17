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

    [DllImport("ControlLib.dll")] static extern uint ctlInit(ref InitArgs args, out IntPtr api);
    [DllImport("ControlLib.dll")] static extern uint ctlClose(IntPtr api);
    [DllImport("ControlLib.dll")] static extern uint ctlEnumerateDevices(IntPtr api, ref uint count, [Out] IntPtr[] devices);
    [DllImport("ControlLib.dll")] static extern uint ctlGetSet3DFeature(IntPtr device, ref Feature3D feature);

    static IntPtr api, device, emptyName;

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
    /// <summary>帧同步：id → 驱动的翻转模式位。</summary>
    static readonly string[] FlipIds = { "app", "vsyncOff", "vsync", "smooth", "speed", "capped" };
    static readonly string[] FlipNames = { "应用决定", "关闭垂直同步", "垂直同步", "平滑同步", "极速帧", "限制到刷新率" };

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
            gpu["frameSync"] = FlipIds[bit];
            var options = new List<Dictionary<string, object>>();
            for (int i = 0; i < FlipIds.Length; i++) options.Add(new Dictionary<string, object> { { "id", FlipIds[i] }, { "name", FlipNames[i] } });
            gpu["frameSyncOptions"] = options;
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
