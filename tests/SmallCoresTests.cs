using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Win32;

class SmallCoresTests {
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Main() {
        foreach (int threads in new[] { 1, 8, 14, 31, 32, 33, 63, 64 }) {
            BigInteger reference = (BigInteger.One << threads) - 1;
            Check(SmallCores.Mask(threads) == (ulong)reference, "mask " + threads);
            Check(SmallCores.MaskKind(threads) == (reference.ToString("x").Length > 8 ? RegistryValueKind.QWord : RegistryValueKind.DWord), "MotionAssistant type " + threads);
        }
        foreach (int invalid in new[] { -1, 0, 65 }) {
            try { SmallCores.Mask(invalid); throw new Exception("invalid thread count accepted"); }
            catch (ArgumentOutOfRangeException) { }
        }
        string path = @"Software\ClawToolbox.Tests\SmallCores-" + Guid.NewGuid().ToString("N");
        using (var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)) {
            try {
                SmallCores.Write(RegistryHive.CurrentUser, path, false, RegistryValueKind.DWord, 0);
                using (var absent = root.OpenSubKey(path)) Check(absent == null, "disable created key");
                foreach (int threads in new[] { 14, 31, 32, 64 }) {
                    ulong mask = SmallCores.Mask(threads);
                    var kind = SmallCores.MaskKind(threads);
                    var result = SmallCores.Write(RegistryHive.CurrentUser, path, true, kind, mask);
                    using (var key = root.OpenSubKey(path)) {
                        object value = key.GetValue("SmallProcessorMask");
                        Check(key.GetValueKind("SmallProcessorMask") == kind, "kind roundtrip");
                        Check((kind == RegistryValueKind.DWord ? unchecked((uint)(int)value) : unchecked((ulong)(long)value)) == mask, "unsigned mask roundtrip");
                    }
                    Check((string)result["mask"] == mask.ToString("x"), "reported mask");
                }
                // Restore a pre-existing partial QWORD without converting it to DWORD.
                SmallCores.Write(RegistryHive.CurrentUser, path, true, RegistryValueKind.QWord, 3);
                using (var key = root.OpenSubKey(path)) Check(key.GetValueKind("SmallProcessorMask") == RegistryValueKind.QWord && (long)key.GetValue("SmallProcessorMask") == 3, "original kind and partial mask");
                SmallCores.Write(RegistryHive.CurrentUser, path, false, RegistryValueKind.DWord, 0);
                using (var key = root.OpenSubKey(path)) Check(key.GetValue("SmallProcessorMask") == null, "disable must delete value");
                try { SmallCores.Restore(new Dictionary<string, object> { { "present", true }, { "kind", "dword" }, { "mask", "100000000" } }); throw new Exception("oversize restore accepted"); }
                catch (ArgumentException) { }
            } finally { root.DeleteSubKeyTree(path, false); }
        }
        Console.WriteLine("Small-core masks, registry types, readback and removal checks passed (private HKCU fixture only).");
    }
}
