using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// Independently implemented from the observed MotionAssistant registry protocol.
// This changes a saved Windows setting; it does not change physical core types.
public static class SmallCores {
    const string Key = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel\Kgroups\00";
    const string Name = "SmallProcessorMask";
    [DllImport("kernel32.dll")] static extern ushort GetActiveProcessorGroupCount();
    [DllImport("kernel32.dll")] static extern uint GetActiveProcessorCount(ushort group);

    public static ulong Mask(int threads) {
        if (threads < 1 || threads > 64) throw new ArgumentOutOfRangeException("threads");
        return threads == 64 ? ulong.MaxValue : (1UL << threads) - 1;
    }

    public static RegistryValueKind MaskKind(int threads) {
        Mask(threads);
        // MotionAssistant chooses QWORD at 32 threads (BigInteger hex includes a sign nibble).
        return threads >= 32 ? RegistryValueKind.QWord : RegistryValueKind.DWord;
    }

    static int Threads() {
        if (GetActiveProcessorGroupCount() != 1)
            throw new InvalidOperationException("此功能仅支持单个处理器分组，未修改核心设置");
        int count = checked((int)GetActiveProcessorCount(0));
        Mask(count);
        return count;
    }

    public static Dictionary<string, object> Set(bool enabled) {
        int threads = Threads();
        return Write(RegistryHive.LocalMachine, Key, enabled, MaskKind(threads), Mask(threads));
    }

    public static Dictionary<string, object> Restore(Dictionary<string, object> original) {
        Threads();
        if (original == null || !original.ContainsKey("present") || !(original["present"] is bool))
            throw new ArgumentException("原始核心设置备份无效");
        bool present = (bool)original["present"];
        RegistryValueKind kind = RegistryValueKind.DWord;
        ulong mask = 0;
        if (present) {
            string type = original.ContainsKey("kind") ? original["kind"] as string : null;
            if (type != "dword" && type != "qword") throw new ArgumentException("原始核心设置类型无效");
            kind = type == "dword" ? RegistryValueKind.DWord : RegistryValueKind.QWord;
            if (!original.ContainsKey("mask") || !(original["mask"] is string) ||
                !ulong.TryParse((string)original["mask"], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out mask) ||
                (kind == RegistryValueKind.DWord && mask > uint.MaxValue))
                throw new ArgumentException("原始核心设置数值无效");
        }
        return Write(RegistryHive.LocalMachine, Key, present, kind, mask);
    }

    // Internal seam lets tests exercise real registry serialization in a private HKCU key.
    internal static Dictionary<string, object> Write(RegistryHive hive, string path, bool present, RegistryValueKind kind, ulong mask) {
        using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64)) {
            // Disabling an absent setting must not create a kernel configuration key.
            using (var key = present ? root.CreateSubKey(path) : root.OpenSubKey(path, true)) {
                if (key == null && present) throw new InvalidOperationException("无法打开处理器分组注册表项");
                if (key != null) {
                    if (!present) key.DeleteValue(Name, false);
                    else if (kind == RegistryValueKind.DWord) key.SetValue(Name, unchecked((int)(uint)mask), kind);
                    else if (kind == RegistryValueKind.QWord) key.SetValue(Name, unchecked((long)mask), kind);
                    else throw new ArgumentException("核心设置类型无效");
                    key.Flush();
                }
            }
            // Reopen after the write; access/write failures must never become a success response.
            using (var check = root.OpenSubKey(path)) {
                object value = check == null ? null : check.GetValue(Name);
                bool matches = !present ? value == null : value != null && check.GetValueKind(Name) == kind &&
                    (kind == RegistryValueKind.DWord ? unchecked((uint)(int)value) == mask : unchecked((ulong)(long)value) == mask);
                if (!matches) throw new InvalidOperationException("核心设置回读不一致，请重新读取状态");
            }
        }
        return new Dictionary<string, object> { { "present", present }, { "kind", present ? kind.ToString().ToLowerInvariant() : null },
            { "mask", present ? mask.ToString("x") : null }, { "restartRequired", true } };
    }
}
