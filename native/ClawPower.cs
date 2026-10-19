using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Power limits and CPU frequency cap for the Claw (Intel). Two roles in one file:
//   (no args)            parent-owned stdio bridge at the plugin's integrity level; it
//                        owns a named pipe and starts the same exe elevated.
//   --serve <pipe> <pid> elevated executor. It only accepts the pipe of that bridge
//                        process and exits as soon as the bridge goes away.
// The executor writes nothing by itself: every write is one explicit command. Only
// MSR 0x610 (package power limit) is ever written, through the signed PawnIO
// IntelMSR module, and only the PL1/PL2 power fields and their enable bits change.
class ClawPower {
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint process);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint process);

    public static int Main(string[] args) {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        try {
            if (args.Length == 0) return Bridge();
            if (args.Length == 3 && args[0] == "--serve") return Serve(args[1], int.Parse(args[2]));
            throw new ArgumentException("启动参数错误");
        } catch (Exception error) {
            Console.WriteLine(Json(new Dictionary<string, object> { { "id", 0 }, { "ok", false }, { "error", error.Message } }));
            Console.Out.Flush();
            return 1;
        }
    }

    static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }

    static int Bridge() {
        string name = "claw-power-" + Guid.NewGuid().ToString("N");
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        Process child = null;
        using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, acl)) {
            var pending = pipe.BeginWaitForConnection(null, null);
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            child = Process.Start(new ProcessStartInfo(exe, "--serve " + name + " " + Process.GetCurrentProcess().Id) {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exe)
            });
            if (child == null) throw new IOException("功耗后台启动失败");
            if (!pending.AsyncWaitHandle.WaitOne(30000)) throw new TimeoutException("功耗后台连接超时，可能未获得管理员授权");
            pipe.EndWaitForConnection(pending);
            uint client;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out client) || client != (uint)child.Id)
                throw new IOException("功耗后台连接身份不匹配");
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true }) {
                Console.WriteLine(Json(new Dictionary<string, object> { { "id", 0 }, { "ok", true }, { "ready", true }, { "pid", child.Id } }));
                Console.Out.Flush();
                var input = Task.Run(async () => { string line; while ((line = await Console.In.ReadLineAsync()) != null) await writer.WriteLineAsync(line); });
                var output = Task.Run(async () => { string line; while ((line = await reader.ReadLineAsync()) != null) { Console.WriteLine(line); Console.Out.Flush(); } });
                Task.WaitAny(input, output);
            }
        }
        if (child != null) { try { child.WaitForExit(5000); } catch { } child.Dispose(); }
        return 0;
    }

    static int Serve(string name, int bridgePid) {
        using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None)) {
            pipe.Connect(10000);
            uint server;
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out server) || server != (uint)bridgePid)
                throw new IOException("功耗管道身份不匹配");
            using (var bridge = Process.GetProcessById(bridgePid)) {
                if (!string.Equals(bridge.MainModule.FileName, Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("功耗管道来源不匹配");
            }
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true }) {
                var executor = new Executor();
                try {
                    string line;
                    while ((line = reader.ReadLine()) != null) writer.WriteLine(Json(executor.Handle(line)));
                } finally { executor.Dispose(); }
            }
        }
        return 0;
    }
}

class Executor : IDisposable {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_open(out IntPtr handle);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_execute(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name, ulong[] input, UIntPtr inSize, ulong[] output, UIntPtr outSize, out UIntPtr returned);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_close(IntPtr handle);
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

    const uint MSR_RAPL_POWER_UNIT = 0x606, MSR_PKG_POWER_LIMIT = 0x610, MSR_PKG_ENERGY_STATUS = 0x611, MSR_PKG_POWER_INFO = 0x614;
    const ulong PL1_MASK = 0x7FFFUL, PL2_MASK = 0x7FFFUL << 32, PL1_ENABLE = 1UL << 15, PL2_ENABLE = 1UL << 47, LOCK = 1UL << 63;
    static Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
    static Guid FreqMax = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
    static Guid FreqMax1 = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e101");
    IntPtr pawn = IntPtr.Zero;
    string pawnError;

    void OpenPawn() {
        if (pawn != IntPtr.Zero) return;
        if (pawnError != null) throw new InvalidOperationException(pawnError);
        try {
            string lib = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");
            if (!File.Exists(lib) || LoadLibraryW(lib) == IntPtr.Zero) throw new InvalidOperationException("未找到 PawnIO 驱动库，请确认 PawnIO 已安装");
            string module = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pawnio", "IntelMSR.bin");
            byte[] blob = File.ReadAllBytes(module);
            IntPtr handle;
            int hr = pawnio_open(out handle);
            if (hr < 0) throw new InvalidOperationException(string.Format("PawnIO 打开失败 0x{0:X8}", hr));
            hr = pawnio_load(handle, blob, (UIntPtr)blob.Length);
            if (hr < 0) { pawnio_close(handle); throw new InvalidOperationException(string.Format("PawnIO 模块加载失败 0x{0:X8}", hr)); }
            pawn = handle;
        } catch (Exception error) { pawnError = error.Message; throw; }
    }

    ulong ReadMsr(uint msr) {
        OpenPawn();
        var output = new ulong[1]; UIntPtr returned;
        int hr = pawnio_execute(pawn, "ioctl_read_msr", new ulong[] { msr }, (UIntPtr)1, output, (UIntPtr)1, out returned);
        if (hr < 0) throw new InvalidOperationException(string.Format("读取 MSR 0x{0:X} 失败 0x{1:X8}", msr, hr));
        return output[0];
    }

    void WriteMsr(uint msr, ulong value) {
        OpenPawn();
        UIntPtr returned;
        int hr = pawnio_execute(pawn, "ioctl_write_msr", new ulong[] { msr, value }, (UIntPtr)2, new ulong[0], UIntPtr.Zero, out returned);
        if (hr < 0) throw new InvalidOperationException(string.Format("写入 MSR 0x{0:X} 失败 0x{1:X8}", msr, hr));
    }

    public Dictionary<string, object> Handle(string line) {
        var reply = new Dictionary<string, object>();
        object id = null;
        try {
            var request = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(line.TrimStart('﻿'));
            request.TryGetValue("id", out id);
            string command = request.ContainsKey("command") ? request["command"] as string : null;
            if (command == "read") reply["power"] = Read(request.ContainsKey("sample") && true.Equals(request["sample"]));
            else if (command == "set_tdp") reply["power"] = SetTdp(Number(request, "pl1"), Number(request, "pl2"));
            else if (command == "restore_tdp") reply["power"] = RestoreTdp(Convert.ToString(request["raw"]));
            else if (command == "set_freq") reply["power"] = SetFreq(request);
            else if (command == "restore_freq") reply["power"] = SetFreq(request, true);
            else if (command == "frames") reply["frames"] = Frames(Convert.ToInt32(request["pid"]));
            else if (command == "thermal") reply["thermal"] = Thermal();
            else if (command == "osd") reply["osd"] = Osd.Show(request.ContainsKey("text") ? Convert.ToString(request["text"]) : "");
            else if (command == "gpu") reply["gpu"] = (gpu ?? (gpu = new GpuSysman())).Read();
            else if (command == "endurance_read") reply["endurance"] = IntelGpu.EnduranceRead();
            else if (command == "endurance_set") reply["endurance"] = IntelGpu.EnduranceSet(Convert.ToString(request["control"]), Convert.ToString(request["profile"]));
            else if (command == "frame_pacing_set") { IntelGpu.FramePacingSet((uint)Number(request, "value")); reply["endurance"] = IntelGpu.EnduranceRead(); }
            else if (command == "rtss_app") reply["rtss_app"] = Osd.App(Convert.ToInt32(request["pid"]));
            else if (command == "stats") reply["stats"] = (stats ?? (stats = new SystemStats())).Read();
            else if (command == "osd_zoom") reply["osd_zoom"] = Osd.Zoom(Convert.ToInt32(request["zoom"]));
            else if (command == "osd_height") reply["osd_height"] = Osd.FontHeight(Convert.ToInt32(request["height"]));
            else if (command == "osd_font") reply["osd_font"] = Osd.Font(Convert.ToString(request["face"]));
            else if (command == "fan_read") reply["fan"] = MsiFan.Read();
            else if (command == "fan_probe") reply["fanProbe"] = MsiFan.Probe();
            else if (command == "fan_test_curve") reply["fan"] = MsiFan.TestApply(Ints(request, "cpu"), Ints(request, "gpu"), true.Equals(request["custom"]), Convert.ToString(request["transport"]));
            else if (command == "fan_test_done") { MsiFan.EndTest(false); reply["fan"] = MsiFan.Read(); }
            else if (command == "fan_curve") reply["fan"] = MsiFan.Apply(Ints(request, "cpu"), Ints(request, "gpu"), true.Equals(request.ContainsKey("full") ? request["full"] : false));
            else if (command == "fan_restore") reply["fan"] = MsiFan.Restore(request.ContainsKey("cpu") ? Ints(request, "cpu") : null, request.ContainsKey("gpu") ? Ints(request, "gpu") : null);
            else if (command == "cpu_info") reply["cpu"] = CpuInfo();
            else if (command == "sys_read") reply["sys"] = SystemControls.Read();
            else if (command == "brightness_set") reply["sys"] = SystemControls.SetBrightness(Convert.ToInt32(Number(request, "value")));
            else if (command == "volume_set") reply["sys"] = SystemControls.SetVolume(Convert.ToInt32(Number(request, "value")), null);
            else if (command == "mute_set") reply["sys"] = SystemControls.SetVolume(-1, true.Equals(request.ContainsKey("muted") ? request["muted"] : false));
            else if (command == "refresh_set") reply["sys"] = SystemControls.SetRefresh(Convert.ToInt32(Number(request, "hz")));
            else if (command == "display_read") reply["display"] = DisplayAudio.Read();
            else if (command == "resolution_set") reply["display"] = DisplayAudio.SetResolution(Convert.ToInt32(Number(request, "width")), Convert.ToInt32(Number(request, "height")));
            else if (command == "vrr_set") reply["display"] = DisplayAudio.SetVrr(true.Equals(request.ContainsKey("enabled") ? request["enabled"] : false));
            else if (command == "hdr_set") reply["display"] = DisplayAudio.SetHdr(true.Equals(request.ContainsKey("enabled") ? request["enabled"] : false));
            else if (command == "audio_output_set") reply["display"] = DisplayAudio.SetOutput(Convert.ToString(request["output"]));
            else if (command == "mic_mute_set") reply["display"] = DisplayAudio.SetMicMute(true.Equals(request.ContainsKey("muted") ? request["muted"] : false));
            else if (command == "scale_set") reply["display"] = DisplayAudio.SetScale(Convert.ToInt32(Number(request, "percent")));
            else if (command == "auto_brightness_set") reply["display"] = DisplayAudio.SetAutoBrightness(true.Equals(request.ContainsKey("enabled") ? request["enabled"] : false));
            else if (command == "gpu_set") { IntelGpu.Set(Convert.ToString(request["name"]), request.ContainsKey("value") ? request["value"] : null); reply["display"] = DisplayAudio.Read(); }
            else if (command == "fps_limit_set") reply["sys"] = SystemControls.SetFpsLimit(Convert.ToInt32(Number(request, "fps")), request.ContainsKey("exe") ? Convert.ToString(request["exe"]) : null);
            else if (command == "kill") reply["sys"] = SystemControls.Kill(Convert.ToInt32(Number(request, "pid")));
            else if (command == "power_action") reply["sys"] = SystemControls.Power(Convert.ToString(request["action"]));
            else if (command == "keyboard") reply["sys"] = SystemControls.ToggleKeyboard();
            else if (command == "screenshot") reply["sys"] = SystemControls.Screenshot();
            else if (command == "ecore_set") reply["ecore"] = SmallCores.Set(true.Equals(request.ContainsKey("enabled") ? request["enabled"] : false));
            else throw new ArgumentException("未知命令");
            reply["ok"] = true;
        } catch (Exception error) {
            reply["ok"] = false; reply["error"] = error.Message;
        }
        reply["id"] = id;
        return reply;
    }

    static int[] Ints(Dictionary<string, object> request, string key) {
        object value;
        if (!request.TryGetValue(key, out value) || !(value is System.Collections.ArrayList)) throw new ArgumentException("缺少参数 " + key);
        var list = (System.Collections.ArrayList)value;
        var result = new int[list.Count];
        for (int i = 0; i < list.Count; i++) result[i] = Convert.ToInt32(list[i]);
        return result;
    }

    static double Number(Dictionary<string, object> request, string key) {
        object value;
        if (!request.TryGetValue(key, out value) || value == null) throw new ArgumentException("缺少参数 " + key);
        double number = Convert.ToDouble(value);
        if (double.IsNaN(number) || double.IsInfinity(number)) throw new ArgumentException("参数无效 " + key);
        return number;
    }

    double PowerUnit() { return 1.0 / (1UL << (int)(ReadMsr(MSR_RAPL_POWER_UNIT) & 0xF)); }

    Dictionary<string, object> Read(bool sample = false) {
        var result = new Dictionary<string, object>();
        try {
            ulong units = ReadMsr(MSR_RAPL_POWER_UNIT);
            double power = 1.0 / (1UL << (int)(units & 0xF));
            double energy = 1.0 / (1UL << (int)((units >> 8) & 0x1F));
            ulong limit = ReadMsr(MSR_PKG_POWER_LIMIT);
            ulong info = ReadMsr(MSR_PKG_POWER_INFO);
            result["raw"] = limit.ToString("X16");
            result["unit"] = power;
            result["pl1"] = Math.Round((limit & PL1_MASK) * power, 3);
            result["pl2"] = Math.Round(((limit & PL2_MASK) >> 32) * power, 3);
            result["pl1Enabled"] = (limit & PL1_ENABLE) != 0;
            result["pl2Enabled"] = (limit & PL2_ENABLE) != 0;
            result["locked"] = (limit & LOCK) != 0;
            result["tdpBase"] = Math.Round((info & 0x7FFF) * power, 3);
            result["tdpMin"] = Math.Round(((info >> 16) & 0x7FFF) * power, 3);
            result["tdpMax"] = Math.Round(((info >> 32) & 0x7FFF) * power, 3);
            // Package power over a short window, the actual draw rather than the limit.
            if (!sample) return Finish(result);
            uint first = (uint)ReadMsr(MSR_PKG_ENERGY_STATUS);
            var clock = Stopwatch.StartNew();
            Thread.Sleep(250);
            uint second = (uint)ReadMsr(MSR_PKG_ENERGY_STATUS);
            result["package"] = Math.Round(unchecked(second - first) * energy / clock.Elapsed.TotalSeconds, 2);
        } catch (Exception error) { result["msrError"] = error.Message; }
        return Finish(result);
    }

    static Dictionary<string, object> Finish(Dictionary<string, object> result) {
        try { result["freq"] = ReadFreq(); } catch (Exception error) { result["freqError"] = error.Message; }
        return result;
    }

    Dictionary<string, object> SetTdp(double pl1, double pl2) {
        if (pl1 < 3 || pl1 > 120 || pl2 < pl1 || pl2 > 150) throw new ArgumentException("功耗超出允许范围");
        ulong current = ReadMsr(MSR_PKG_POWER_LIMIT);
        if ((current & LOCK) != 0) throw new InvalidOperationException("固件已锁定功耗寄存器，无法调整");
        double unit = PowerUnit();
        ulong v1 = (ulong)Math.Round(pl1 / unit), v2 = (ulong)Math.Round(pl2 / unit);
        if (v1 > 0x7FFF || v2 > 0x7FFF) throw new ArgumentException("功耗超出寄存器范围");
        // Keep time windows, clamp bits and everything else exactly as firmware left them.
        ulong next = (current & ~(PL1_MASK | PL2_MASK)) | v1 | (v2 << 32) | PL1_ENABLE | PL2_ENABLE;
        if (next != current) WriteMsr(MSR_PKG_POWER_LIMIT, next);
        return Read();
    }

    Dictionary<string, object> RestoreTdp(string raw) {
        ulong value = Convert.ToUInt64(raw, 16);
        ulong current = ReadMsr(MSR_PKG_POWER_LIMIT);
        if ((current & LOCK) != 0) throw new InvalidOperationException("固件已锁定功耗寄存器，无法恢复");
        // Only the power fields and enable bits come from the backup.
        ulong fields = PL1_MASK | PL2_MASK | PL1_ENABLE | PL2_ENABLE;
        ulong next = (current & ~fields) | (value & fields);
        if (next != current) WriteMsr(MSR_PKG_POWER_LIMIT, next);
        return Read();
    }

    static Guid ActiveScheme() {
        IntPtr pointer;
        uint error = PowerGetActiveScheme(IntPtr.Zero, out pointer);
        if (error != 0) throw new InvalidOperationException("无法读取当前电源方案 " + error);
        try { return (Guid)Marshal.PtrToStructure(pointer, typeof(Guid)); } finally { LocalFree(pointer); }
    }

    static Dictionary<string, object> ReadFreq(Guid? savedScheme = null) {
        Guid scheme = savedScheme ?? ActiveScheme();
        uint ac, dc, ac1, dc1;
        PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref SubProcessor, ref FreqMax, out ac);
        PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref SubProcessor, ref FreqMax, out dc);
        uint e1 = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref SubProcessor, ref FreqMax1, out ac1);
        uint e2 = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref SubProcessor, ref FreqMax1, out dc1);
        var result = new Dictionary<string, object> { { "scheme", scheme.ToString() }, { "ac", (long)ac }, { "dc", (long)dc } };
        result["ac1"] = e1 == 0 ? (object)(long)ac1 : null;
        result["dc1"] = e2 == 0 ? (object)(long)dc1 : null;
        return result;
    }

    Dictionary<string, object> SetFreq(Dictionary<string, object> request, bool restoreSaved = false) {
        Guid active = ActiveScheme();
        Guid scheme = request.ContainsKey("scheme") && request["scheme"] != null ? new Guid(Convert.ToString(request["scheme"])) : active;
        if (!restoreSaved && scheme != active)
            throw new InvalidOperationException("电源方案已变化，请重新读取");
        // 0 means no cap. Both the P-core (FreqMax) and the E-core class (FreqMax1) settings
        // are written, the same pair the Windows UI hides and 体感助手 writes.
        foreach (string key in new[] { "ac", "dc", "ac1", "dc1" }) {
            if (!request.ContainsKey(key) || request[key] == null) continue;
            double value = Number(request, key);
            if (value < 0 || value > 9999 || (value > 0 && value < 400)) throw new ArgumentException("频率超出允许范围");
            uint mhz = (uint)Math.Round(value);
            Guid setting = key.EndsWith("1") ? FreqMax1 : FreqMax;
            uint error = key.StartsWith("ac")
                ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref SubProcessor, ref setting, mhz)
                : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref SubProcessor, ref setting, mhz);
            if (error != 0) throw new InvalidOperationException("写入频率上限失败 " + error);
        }
        // Restoring a saved, inactive plan must never activate it.
        if (ActiveScheme() == scheme) {
            uint applied = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (applied != 0) throw new InvalidOperationException("重新应用电源方案失败 " + applied);
        }
        var result = Read();
        if (restoreSaved) result["restoredFreq"] = ReadFreq(scheme);
        return result;
    }

    // Package temperature: TjMax (MSR 0x1A2 bits 16-23) minus the digital readout of
    // IA32_PACKAGE_THERM_STATUS (0x1B1 bits 16-22). Read only.
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);

    // Frequency limits from the CPU itself, so the frequency cap choices match this processor.
    // 0xCE: base and minimum ratio. 0x771 (HWP capabilities) per logical processor: its highest
    // performance level; on hybrid parts P- and E-cores report different levels.
    Dictionary<string, object> CpuInfo() {
        ulong platform = ReadMsr(0xCE);
        var highest = new List<object>();
        Thread.BeginThreadAffinity();
        try {
            for (int cpu = 0; cpu < Math.Min(Environment.ProcessorCount, 64); cpu++) {
                SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << cpu));
                Thread.Sleep(0);
                try { highest.Add((int)(ReadMsr(0x771) & 0xFF)); } catch (InvalidOperationException) { highest.Add(null); }
            }
        } finally {
            SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)((1UL << Math.Min(Environment.ProcessorCount, 63)) - 1));
            Thread.EndThreadAffinity();
        }
        ulong turbo = 0;
        try { turbo = ReadMsr(0x1AD); } catch (InvalidOperationException) { }
        return new Dictionary<string, object> {
            { "baseRatio", (int)((platform >> 8) & 0xFF) }, { "minRatio", (int)((platform >> 40) & 0xFF) },
            { "turboRatios", turbo.ToString("X16") }, { "hwpHighest", highest } };
    }

    Dictionary<string, object> Thermal() {
        ulong target = ReadMsr(0x1A2), status = ReadMsr(0x1B1);
        int tjMax = (int)((target >> 16) & 0xFF);
        return new Dictionary<string, object> { { "tjMax", tjMax }, { "package", tjMax - (int)((status >> 16) & 0x7F) },
            { "throttled", (status & 1) != 0 }, { "throttleLog", (status & 2) != 0 } };
    }

    // Frame statistics for the automatic power mode, plus package power since the last
    // call (no sleep, so it can run every second).
    FrameStats frames;
    SystemStats stats;
    GpuSysman gpu;
    uint lastEnergy, lastCores; long lastEnergyAt;

    Dictionary<string, object> Frames(int pid) {
        var result = new Dictionary<string, object>();
        result["pid"] = pid;
        result["sampleWindowMs"] = FrameStats.QueryWindowMs;
        result["sampleOffsetMs"] = FrameStats.QueryOffsetMs;
        try {
            ulong units = ReadMsr(MSR_RAPL_POWER_UNIT);
            double energy = 1.0 / (1UL << (int)((units >> 8) & 0x1F));
            uint now = (uint)ReadMsr(MSR_PKG_ENERGY_STATUS);
            uint cores = (uint)ReadMsr(0x639);  // MSR_PP0_ENERGY_STATUS: the CPU cores power plane
            long at = Stopwatch.GetTimestamp();
            if (lastEnergyAt != 0 && at > lastEnergyAt) {
                double seconds = (at - lastEnergyAt) / (double)Stopwatch.Frequency;
                result["package"] = Math.Round(unchecked(now - lastEnergy) * energy / seconds, 2);
                result["cpuPower"] = Math.Round(unchecked(cores - lastCores) * energy / seconds, 2);
            }
            lastEnergy = now; lastCores = cores; lastEnergyAt = at;
            double power = 1.0 / (1UL << (int)(units & 0xF));
            ulong limit = ReadMsr(MSR_PKG_POWER_LIMIT);
            result["pl1"] = Math.Round((limit & PL1_MASK) * power, 3);
            result["pl2"] = Math.Round(((limit & PL2_MASK) >> 32) * power, 3);
        } catch (Exception error) { result["msrError"] = error.Message; }
        try {
            if (frames == null) frames = new FrameStats();
            result["swapChains"] = pid > 0 ? frames.Poll((uint)pid) : new List<Dictionary<string, object>>();
            if (pid > 0) foreach (var pair in frames.Extra((uint)pid)) result[pair.Key] = pair.Value;
        } catch (Exception error) { result["framesError"] = error.Message; frames = null; }
        return result;
    }

    public void Dispose() {
        try { MsiFan.EndTest(true); } catch { } // bridge loss during a test must release fixed-speed control
        Osd.Show("");
        if (frames != null) { frames.Dispose(); frames = null; }
        if (pawn != IntPtr.Zero) { pawnio_close(pawn); pawn = IntPtr.Zero; }
    }
}

// RTSS OSD client, the same way 体感助手 feeds RTSS (RTSS SDK RTSSSharedMemory.h):
// one slot of RTSSSharedMemoryV2 owned by "ClawToolbox"; an empty text frees the slot.
// Some games (鸣潮 3.7) crash when RTSS is injected but no client provides OSD text,
// so the toolbox keeps a line there while the OSD is on.
static class Osd {
    const string Owner = "ClawToolbox";
    static readonly Encoding Ansi = Encoding.GetEncoding(0);

    public static Dictionary<string, object> Show(string text) {
        var result = new Dictionary<string, object> { { "rtss", false } };
        System.IO.MemoryMappedFiles.MemoryMappedFile map;
        try { map = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", System.IO.MemoryMappedFiles.MemoryMappedFileRights.ReadWrite); }
        catch (FileNotFoundException) { return result; }  // RTSS is not running
        using (map)
        using (var view = map.CreateViewAccessor()) {
            uint sig = view.ReadUInt32(0), ver = view.ReadUInt32(4);
            uint size = view.ReadUInt32(20), offset = view.ReadUInt32(24), count = view.ReadUInt32(28);
            if (sig != 0x52545353 || ver < 0x00020000 || size < 512) return result;
            result["rtss"] = true;
            long slot = -1, free = -1;
            for (uint i = 1; i < count; i++) {  // slot 0 belongs to RTSS itself
                long entry = offset + (long)i * size;
                string owner = ReadString(view, entry + 256, 256);
                if (owner == Owner) { slot = entry; continue; }
                // 体感助手 leaves its OSD line behind when it exits, so a second, frozen line stays on
                // screen. Clear its slot only while it is not running; a running copy keeps its line.
                if (owner == "MotionAssistant" && Process.GetProcessesByName("MotionAssistant").Length == 0) {
                    Clear(view, entry, size); Bump(view); owner = "";
                }
                if (owner.Length == 0 && free < 0) free = entry;
            }
            if (text.Length == 0) {
                if (slot >= 0) { Clear(view, slot, size); Bump(view); }
                return result;
            }
            if (slot < 0) {
                if (free < 0) { result["error"] = "RTSS 没有空闲的 OSD 位置"; return result; }
                slot = free; WriteString(view, slot + 256, Owner, 256);
            }
            // "{GRAPH}" becomes RTSS's own frame time graph, embedded at the start of the slot buffer.
            if (text.Contains("{GRAPH}")) {
                if (ver >= 0x0002000C && size >= 512 + 4096 + 64) {
                    long buffer = slot + 512 + 4096;
                    view.Write(buffer, 0x47523030u);       // 'GR00'
                    view.Write(buffer + 4, 36u);           // header + graph fields, no own samples
                    view.Write(buffer + 8, -12);           // width: 12 characters
                    view.Write(buffer + 12, -1);           // height: one text row, so the graph and the text are equally tall
                    view.Write(buffer + 16, 0);            // no margin
                    view.Write(buffer + 20, 4u | 1u);      // FRAMETIME | FILLED, no background box
                    view.Write(buffer + 24, 0f);
                    view.Write(buffer + 28, 50000f);       // 0-50 ms
                    view.Write(buffer + 32, 0u);
                    text = text.Replace("{GRAPH}", "<OBJ=00000000>");
                } else text = text.Replace("{GRAPH}", "");
            }
            if (ver >= 0x00020007 && size >= 512 + 4096) { WriteString(view, slot + 512, text, 4096); WriteString(view, slot, "", 256); }
            else WriteString(view, slot, text, 256);
            Bump(view);
            result["shown"] = true;
        }
        return result;
    }

    // Frame rate and frame time RTSS itself measured for a process (its app entry), read
    // passively. 体感助手 and the plain test client never used RTSS's <FR>/<FT> macros; with
    // them in the text 鸣潮 3.7 crashed on start, so the OSD shows these numbers instead.
    public static Dictionary<string, object> App(int pid) {
        var result = new Dictionary<string, object>();
        if (pid <= 0) return result;
        System.IO.MemoryMappedFiles.MemoryMappedFile map;
        try { map = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read); }
        catch (FileNotFoundException) { return result; }
        using (map)
        using (var view = map.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read)) {
            if (view.ReadUInt32(0) != 0x52545353) return result;
            uint size = view.ReadUInt32(8), offset = view.ReadUInt32(12), count = view.ReadUInt32(16);
            for (uint i = 0; i < count; i++) {
                long entry = offset + (long)i * size;
                if (view.ReadUInt32(entry) != (uint)pid) continue;
                uint t0 = view.ReadUInt32(entry + 268), t1 = view.ReadUInt32(entry + 272), frames = view.ReadUInt32(entry + 276), frameTime = view.ReadUInt32(entry + 280);
                if (t1 > t0) result["fps"] = Math.Round(1000.0 * frames / (t1 - t0), 1);
                if (frameTime > 0) result["frameTime"] = Math.Round(frameTime / 1000.0, 1);
                break;
            }
        }
        return result;
    }

    // OSD zoom (RTSS ZoomRatio 1..8) through RTSSHooks64's profile interface, in the global
    // profile and in every per-game profile that already exists (they override it).
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint EnumProfilesFn(byte[] list, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet=CharSet.Ansi)] delegate void ProfileFn(string profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet=CharSet.Ansi)] delegate bool PropertyFn(string name, byte[] data, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void UpdateFn();

    public static Dictionary<string, object> Zoom(int zoom) {
        if (zoom < 1 || zoom > 8) throw new ArgumentException("OSD 字号超出范围");
        return SetInt("ZoomRatio", zoom);
    }

    // Font height (negative = character height in pixels, RTSS "FontHeight") gives finer
    // steps than the integer zoom.
    public static Dictionary<string, object> FontHeight(int height) {
        if (height > -4 || height < -40) throw new ArgumentException("OSD 字高超出范围");
        return SetInt("FontHeight", height);
    }

    internal static Dictionary<string, object> SetInt(string property, int zoom) {
        string dir = null;
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Unwinder\RTSS"))
            if (key != null) dir = key.GetValue("InstallDir") as string;
        if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("未找到 RTSS 安装目录");
        IntPtr hooks = LoadLibraryW(Path.Combine(dir, "RTSSHooks64.dll"));
        if (hooks == IntPtr.Zero) throw new InvalidOperationException("无法加载 RTSS 配置接口");
        var enumProfiles = Get<EnumProfilesFn>(hooks, "EnumProfiles");
        var load = Get<ProfileFn>(hooks, "LoadProfile");
        var save = Get<ProfileFn>(hooks, "SaveProfile");
        var getProp = Get<PropertyFn>(hooks, "GetProfileProperty");
        var setProp = Get<PropertyFn>(hooks, "SetProfileProperty");
        var update = Get<UpdateFn>(hooks, "UpdateProfiles");
        var list = new byte[1 << 16];
        enumProfiles(list, (uint)list.Length);
        int end = Array.IndexOf(list, (byte)0);
        var names = new List<string> { "" };
        foreach (string name in Encoding.GetEncoding(0).GetString(list, 0, end < 0 ? list.Length : end).Split(','))
            if (name.Trim().Length > 0) names.Add(name.Trim());
        var previous = new Dictionary<string, object>();
        foreach (string name in names) {
            load(name);
            var data = new byte[4];
            if (getProp(property, data, 4)) previous[name] = BitConverter.ToInt32(data, 0);
            if (previous.ContainsKey(name) && (int)previous[name] == zoom) continue;
            setProp(property, BitConverter.GetBytes(zoom), 4);
            save(name);
        }
        update();
        return new Dictionary<string, object> { { "zoom", zoom }, { "profiles", names.Count }, { "previous", previous } };
    }

    // Font face in every RTSS profile, same interface as Zoom. Cascadia Mono (the profiles'
    // default here) has no ℃ or CJK glyphs; Microsoft YaHei UI has both.
    public static Dictionary<string, object> Font(string face) {
        if (string.IsNullOrEmpty(face) || face.Length > 60) throw new ArgumentException("字体名无效");
        var hooks = Hooks();
        var enumProfiles = Get<EnumProfilesFn>(hooks, "EnumProfiles");
        var load = Get<ProfileFn>(hooks, "LoadProfile");
        var save = Get<ProfileFn>(hooks, "SaveProfile");
        var getProp = Get<PropertyFn>(hooks, "GetProfileProperty");
        var setProp = Get<PropertyFn>(hooks, "SetProfileProperty");
        var update = Get<UpdateFn>(hooks, "UpdateProfiles");
        var previous = new Dictionary<string, object>();
        byte[] wanted = Encoding.GetEncoding(0).GetBytes(face + "\0");
        foreach (string name in ProfileNames(enumProfiles)) {
            load(name);
            var data = new byte[256];
            if (getProp("FontFace", data, 256)) { int n = Array.IndexOf(data, (byte)0); previous[name] = Encoding.GetEncoding(0).GetString(data, 0, n < 0 ? 256 : n); }
            if (previous.ContainsKey(name) && (string)previous[name] == face) continue;
            setProp("FontFace", wanted, (uint)wanted.Length);
            save(name);
        }
        update();
        return new Dictionary<string, object> { { "face", face }, { "previous", previous } };
    }

    // One integer property in the given profiles only ("" = global); a missing game profile is skipped.
    internal static void SetIntIn(string property, int value, string[] profiles) {
        var hooks = Hooks();
        var enumProfiles = Get<EnumProfilesFn>(hooks, "EnumProfiles");
        var load = Get<ProfileFn>(hooks, "LoadProfile");
        var save = Get<ProfileFn>(hooks, "SaveProfile");
        var setProp = Get<PropertyFn>(hooks, "SetProfileProperty");
        var update = Get<UpdateFn>(hooks, "UpdateProfiles");
        var existing = new HashSet<string>(ProfileNames(enumProfiles), StringComparer.OrdinalIgnoreCase);
        foreach (string name in profiles) {
            if (!existing.Contains(name)) continue;
            load(name);
            setProp(property, BitConverter.GetBytes(value), 4);
            save(name);
        }
        update();
    }

    // Frame limit (RTSS "FramerateLimit", 0 = off) of the global profile, for the widget.
    internal static int HooksForWidget() {
        var hooks = Hooks();
        var load = Get<ProfileFn>(hooks, "LoadProfile");
        var getProp = Get<PropertyFn>(hooks, "GetProfileProperty");
        load("");
        var data = new byte[4];
        return getProp("FramerateLimit", data, 4) ? BitConverter.ToInt32(data, 0) : 0;
    }

    static IntPtr Hooks() {
        string dir = null;
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Unwinder\RTSS"))
            if (key != null) dir = key.GetValue("InstallDir") as string;
        if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("未找到 RTSS 安装目录");
        IntPtr hooks = LoadLibraryW(Path.Combine(dir, "RTSSHooks64.dll"));
        if (hooks == IntPtr.Zero) throw new InvalidOperationException("无法加载 RTSS 配置接口");
        return hooks;
    }

    static List<string> ProfileNames(EnumProfilesFn enumProfiles) {
        var list = new byte[1 << 16];
        enumProfiles(list, (uint)list.Length);
        int end = Array.IndexOf(list, (byte)0);
        var names = new List<string> { "" };
        foreach (string name in Encoding.GetEncoding(0).GetString(list, 0, end < 0 ? list.Length : end).Split(','))
            if (name.Trim().Length > 0) names.Add(name.Trim());
        return names;
    }

    static T Get<T>(IntPtr module, string name) where T : class {
        IntPtr fn = GetProcAddress(module, name);
        if (fn == IntPtr.Zero) throw new InvalidOperationException("RTSS 配置接口缺少 " + name);
        return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
    }

    static void Bump(System.IO.MemoryMappedFiles.MemoryMappedViewAccessor view) { view.Write(32, unchecked(view.ReadUInt32(32) + 1)); }

    static void Clear(System.IO.MemoryMappedFiles.MemoryMappedViewAccessor view, long slot, uint size) {
        var zero = new byte[Math.Min(size, 512 + 4096)];
        view.WriteArray(slot, zero, 0, zero.Length);
    }

    static string ReadString(System.IO.MemoryMappedFiles.MemoryMappedViewAccessor view, long at, int max) {
        var bytes = new byte[max];
        view.ReadArray(at, bytes, 0, max);
        int n = Array.IndexOf(bytes, (byte)0);
        return Ansi.GetString(bytes, 0, n < 0 ? max : n);
    }

    static void WriteString(System.IO.MemoryMappedFiles.MemoryMappedViewAccessor view, long at, string text, int max) {
        byte[] bytes = Ansi.GetBytes(text);
        int n = Math.Min(bytes.Length, max - 1);
        view.WriteArray(at, bytes, 0, n);
        view.Write(at + n, (byte)0);
    }
}

// MSI Claw fans through the firmware's own WMI interface (MSI_ACPI, the same calls
// ClawTweaks uses), never raw EC ports. Layout seen on the Claw 8 EX (2026-10-12):
//   Get_Fan(0)          payload[0..3]: RPM counters, rpm = 480000 / |b0-b1| (fan 2: b2,b3)
//   Get_Fan(1|2)        CPU|GPU table: [0] current duty, [1..6] six duty points (%), [7] limit
//   Get_Temperature(1|2) temperature axis: [0], [3..7] -> six points (°C)
//   Get_AP(1)[0] bit 7  software curve in control (Set_Data 0xD4); clear = firmware's own curve
//   Get_Data(0x98) bit 7 full speed
// Every Get_* reply starts with a status byte (1 = ok) that is stripped here.
// "All cores as E-cores" (体感助手's 模拟全核心E核): SmallProcessorMask marks every logical
// processor of group 0 as a small core, so the scheduler stops preferring P-cores. Windows reads
// it at boot only. Writing the same value 体感助手 writes keeps the two interchangeable.
// System controls for the Game Bar widget: brightness, volume, refresh rate, RTSS frame limit,
// closing the game, power actions, touch keyboard and screenshot. All run in this elevated
// executor because the games (and Steam) run elevated here.
static class SystemControls {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsExW(string device, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);

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
    const int ENUM_CURRENT_SETTINGS = -1, DM_DISPLAYFREQUENCY = 0x400000;

    public static Dictionary<string, object> Read() {
        var result = new Dictionary<string, object>();
        try { result["brightness"] = Brightness(); } catch (Exception) { result["brightness"] = null; }
        try { var audio = Audio(); float level; bool muted; audio.GetMasterVolumeLevelScalar(out level); audio.GetMute(out muted);
              result["volume"] = (int)Math.Round(level * 100); result["muted"] = muted; } catch (Exception) { result["volume"] = null; }
        var current = Current();
        result["refresh"] = current.dmDisplayFrequency;
        result["refreshRates"] = RefreshRates(current);
        try {
            var hooks = Osd.HooksForWidget();
            result["fpsLimit"] = hooks;
        } catch (Exception) { result["fpsLimit"] = null; }
        return result;
    }

    // Brightness of the built-in panel through WMI (the only interface Windows exposes for it).
    static int Brightness() {
        using (var searcher = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
            foreach (System.Management.ManagementObject item in searcher.Get()) return Convert.ToInt32(item["CurrentBrightness"]);
        throw new InvalidOperationException("没有可调亮度的屏幕");
    }

    public static Dictionary<string, object> SetBrightness(int value) {
        value = Math.Max(0, Math.Min(100, value));
        using (var searcher = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
            foreach (System.Management.ManagementObject item in searcher.Get()) { item.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)value }); break; }
        return Read();
    }

    // Core Audio: default render endpoint volume.
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator { int NotImpl1(); [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice { [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint); }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume {
        int RegisterControlChangeNotify(IntPtr notify); int UnregisterControlChangeNotify(IntPtr notify); int GetChannelCount(out int count);
        int SetMasterVolumeLevel(float level, ref Guid context); int SetMasterVolumeLevelScalar(float level, ref Guid context);
        int GetMasterVolumeLevel(out float level); int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float level, ref Guid context); int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        int GetChannelVolumeLevel(uint channel, out float level); int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context); int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static IAudioEndpointVolume Audio() {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice device;
        if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0) throw new InvalidOperationException("没有默认音频输出");
        var iid = typeof(IAudioEndpointVolume).GUID; object endpoint;
        if (device.Activate(ref iid, 23, IntPtr.Zero, out endpoint) != 0) throw new InvalidOperationException("无法控制音量");
        return (IAudioEndpointVolume)endpoint;
    }

    public static Dictionary<string, object> SetVolume(int value, bool? muted) {
        var audio = Audio(); var context = Guid.Empty;
        if (value >= 0) { audio.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(100, value)) / 100f, ref context); audio.SetMute(false, ref context); }
        if (muted.HasValue) audio.SetMute(muted.Value, ref context);
        return Read();
    }

    static DEVMODE Current() {
        var mode = new DEVMODE(); mode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref mode)) throw new InvalidOperationException("无法读取显示模式");
        return mode;
    }

    // Refresh rates the panel offers at the current resolution.
    static List<int> RefreshRates(DEVMODE current) {
        var rates = new SortedSet<int>();
        var mode = new DEVMODE(); mode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        for (int i = 0; EnumDisplaySettingsW(null, i, ref mode); i++)
            if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight && mode.dmDisplayFrequency > 1)
                rates.Add(mode.dmDisplayFrequency);
        return new List<int>(rates);
    }

    public static Dictionary<string, object> SetRefresh(int hz) {
        var mode = Current();
        if (!RefreshRates(mode).Contains(hz)) throw new ArgumentException("屏幕不支持 " + hz + " Hz");
        mode.dmDisplayFrequency = hz; mode.dmFields = DM_DISPLAYFREQUENCY;
        int code = ChangeDisplaySettingsExW(null, ref mode, IntPtr.Zero, 0x1 /* CDS_UPDATEREGISTRY */, IntPtr.Zero);
        if (code != 0) throw new InvalidOperationException("切换刷新率失败（" + code + "）");
        return Read();
    }

    // Global profile plus the running game's own profile (it overrides the global one). Touching every
    // profile like the OSD settings do took ~10 s with the ~60 profiles here, far too slow for a tile.
    public static Dictionary<string, object> SetFpsLimit(int fps, string exe) {
        if (fps < 0 || fps > 360) throw new ArgumentException("帧率限制超出范围");
        Osd.SetIntIn("FramerateLimit", fps, string.IsNullOrEmpty(exe) ? new[] { "" } : new[] { "", exe });
        return new Dictionary<string, object> { { "fpsLimit", fps } };
    }

    // Only ever called with the foreground game the agent detected.
    public static Dictionary<string, object> Kill(int pid) {
        using (var process = Process.GetProcessById(pid)) {
            string name = process.ProcessName;
            process.Kill();
            process.WaitForExit(5000);
            return new Dictionary<string, object> { { "closed", name } };
        }
    }

    public static Dictionary<string, object> Power(string action) {
        if (action == "lock") { if (!LockWorkStation()) throw new InvalidOperationException("无法锁定"); }
        else if (action == "sleep") SetSuspendState(false, false, false);
        else if (action == "shutdown" || action == "restart")
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), (action == "shutdown" ? "/s" : "/r") + " /t 0") { UseShellExecute = false, CreateNoWindow = true });
        else throw new ArgumentException("不支持的电源操作");
        return new Dictionary<string, object> { { "action", action } };
    }

    // Touch keyboard: TabTip's own toggle interface (starting TabTip.exe alone does not show it).
    [ComImport, Guid("4ce576fa-83dc-4F88-951c-9d0782b4e376")] class UIHostNoLaunch { }
    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITipInvocation { void Toggle(IntPtr hwnd); }

    public static Dictionary<string, object> ToggleKeyboard() {
        try { ((ITipInvocation)new UIHostNoLaunch()).Toggle(GetDesktopWindow()); }
        catch (COMException) {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), @"microsoft shared\ink\TabTip.exe")) { UseShellExecute = true });
        }
        return new Dictionary<string, object> { { "keyboard", true } };
    }

    // Game Bar's own screenshot shortcut (Win+Alt+PrtScn), saved to Videos\Captures.
    public static Dictionary<string, object> Screenshot() {
        const byte WIN = 0x5B, ALT = 0x12, PRINT = 0x2C; const uint UP = 2;
        keybd_event(WIN, 0, 0, UIntPtr.Zero); keybd_event(ALT, 0, 0, UIntPtr.Zero); keybd_event(PRINT, 0, 0, UIntPtr.Zero);
        keybd_event(PRINT, 0, UP, UIntPtr.Zero); keybd_event(ALT, 0, UP, UIntPtr.Zero); keybd_event(WIN, 0, UP, UIntPtr.Zero);
        return new Dictionary<string, object> { { "screenshot", true } };
    }
}

static class SmallCores {
    const string Key = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel\Kgroups\00";
    const string Name = "SmallProcessorMask";

    public static Dictionary<string, object> Set(bool enabled) {
        using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(Key)) {
            if (key == null) throw new InvalidOperationException("无法打开处理器分组注册表项");
            if (enabled) {
                int threads = Math.Min(Environment.ProcessorCount, 64);
                ulong mask = threads == 64 ? ulong.MaxValue : (1UL << threads) - 1;
                if (mask <= uint.MaxValue) key.SetValue(Name, unchecked((int)(uint)mask), Microsoft.Win32.RegistryValueKind.DWord);
                else key.SetValue(Name, unchecked((long)mask), Microsoft.Win32.RegistryValueKind.QWord);
            } else key.DeleteValue(Name, false);
            return new Dictionary<string, object> { { "enabled", key.GetValue(Name) != null } };
        }
    }
}

static class MsiFan {
    static bool testActive;
    const string Instance = @"MSI_ACPI.InstanceName='ACPI\PNP0C14\0_0'";

    static byte[] Call(string method, byte[] data) {
        using (var obj = new System.Management.ManagementObject(@"root\WMI", Instance, null)) {
            using (var pkgClass = new System.Management.ManagementClass(@"root\WMI", "Package_32", null))
            using (var pkg = pkgClass.CreateInstance()) {
                var bytes = new byte[32];
                Array.Copy(data, bytes, Math.Min(32, data.Length));
                pkg["Bytes"] = bytes;
                var input = obj.GetMethodParameters(method);
                input["Data"] = pkg;
                var output = obj.InvokeMethod(method, input, null);
                var result = output == null ? null : output["Data"] as System.Management.ManagementBaseObject;
                var raw = result == null ? null : result["Bytes"] as byte[];
                if (raw == null || raw.Length < 2) throw new InvalidOperationException("MSI 风扇接口没有返回数据：" + method);
                if (raw[0] != 1) throw new InvalidOperationException("MSI 风扇接口返回失败：" + method);
                var payload = new byte[raw.Length - 1];
                Array.Copy(raw, 1, payload, 0, payload.Length);
                return payload;
            }
        }
    }

    static byte[] Get(string method, byte index) { return Call(method, new[] { index }); }

    static int[] Duties(byte[] table) { var d = new int[6]; for (int i = 0; i < 6; i++) d[i] = table[1 + i]; return d; }

    static int[] Temps(byte[] axis) { return new[] { (int)axis[0], axis[3], axis[4], axis[5], axis[6], axis[7] }; }

    static int Rpm(byte a, byte b) { int d = Math.Abs(a - b); return d == 0 ? 0 : (int)Math.Round(480000.0 / d); }

    public static Dictionary<string, object> Probe() {
        var result = new Dictionary<string, object>();
        foreach (string method in new[] { "Get_Temperature", "Get_Thermal", "Get_Fan" }) {
            for (byte index = 0; index <= 2; index++) {
                try { result[method + "_" + index] = Array.ConvertAll(Get(method, index), b => (int)b); }
                catch (Exception error) { result[method + "_" + index] = error.Message; }
            }
        }
        result["AP1"] = Array.ConvertAll(Get("Get_AP", 1), b => (int)b);
        result["scenario"] = Array.ConvertAll(Get("Get_Data", 0xD2), b => (int)b);
        result["model"] = Model();
        return result;
    }

    static string Model() {
        using (var search = new System.Management.ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
        using (var rows = search.Get()) {
            foreach (System.Management.ManagementObject row in rows) return Convert.ToString(row["Model"]);
        }
        return "";
    }

    public static Dictionary<string, object> Read() {
        var rpm = Get("Get_Fan", 0);
        byte[] cpu = Get("Get_Fan", 1), gpu = Get("Get_Fan", 2);
        byte[] cpuTemps = Get("Get_Temperature", 1), gpuTemps = Get("Get_Temperature", 2);
        byte ap = Get("Get_AP", 1)[0], full = Get("Get_Data", 0x98)[0];
        return new Dictionary<string, object> {
            { "rpm1", Rpm(rpm[0], rpm[1]) }, { "rpm2", Rpm(rpm[2], rpm[3]) },
            { "duty1", (int)cpu[0] }, { "duty2", (int)gpu[0] },
            { "cpu", Duties(cpu) }, { "gpu", Duties(gpu) },
            { "cpuTable", Array.ConvertAll(cpu, b => (int)b) }, { "gpuTable", Array.ConvertAll(gpu, b => (int)b) },
            { "ecTemperature", (int)Get("Get_Temperature", 0)[0] },
            { "apData", Array.ConvertAll(Get("Get_AP", 1), b => (int)b) },
            { "cpuTemps", Temps(cpuTemps) }, { "gpuTemps", Temps(gpuTemps) },
            { "cpuTemperatureData", Array.ConvertAll(cpuTemps, b => (int)b) },
            { "gpuTemperatureData", Array.ConvertAll(gpuTemps, b => (int)b) },
            { "software", (ap & 0x80) != 0 }, { "fullSpeed", (full & 0x80) != 0 },
            { "scenario", (int)Get("Get_Data", 0xD2)[0] },
        };
    }

    static void WriteTable(byte block, int[] duties) {
        if (duties == null || duties.Length != 6) throw new ArgumentException("风扇曲线需要 6 个点");
        var current = Get("Get_Fan", block);
        var payload = new byte[32];
        payload[0] = block;
        Array.Copy(current, 0, payload, 1, Math.Min(31, current.Length));  // keep [0] and [7] as the EC has them
        for (int i = 0; i < 6; i++) payload[2 + i] = (byte)Math.Max(0, Math.Min(150, duties[i]));
        Call("Set_Fan", payload);
        var check = Duties(Get("Get_Fan", block));
        for (int i = 0; i < 6; i++)
            if (check[i] != payload[2 + i]) throw new InvalidOperationException("风扇曲线写入后回读不一致");
    }

    static void SetBit(byte index, bool on, string getter) {
        byte cur = Get(getter, getter == "Get_AP" ? (byte)1 : index)[0];
        byte next = on ? (byte)(cur | 0x80) : (byte)(cur & 0x7F);
        if (next != cur) Call("Set_Data", new[] { index, next });
    }

    // Diagnostic only: compare the direct Set_AP selector with the old Set_Data alias,
    // and test the seven-duty interpretation without changing temperature registers.
    public static Dictionary<string, object> TestApply(int[] cpu, int[] gpu, bool custom, string transport) {
        if (Model().IndexOf("CG3EM", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("风扇固件测试仅支持已核对的 CG3EM 机型");
        if (transport != "set_ap" && transport != "legacy") throw new ArgumentException("未知风扇控制接口");
        foreach (var duties in new[] { cpu, gpu }) {
            if (duties == null || duties.Length != 7) throw new ArgumentException("测试表需要 7 个风量值");
            foreach (int duty in duties) if (duty < 0 || duty > 150) throw new ArgumentException("风量超出范围");
        }
        var before = Read();
        testActive = true;
        try {
            WriteSeven(1, cpu); WriteSeven(2, gpu);
            SetCustom(custom, transport);
            SetBit(0x98, false, "Get_Data");
            var state = Read();
            if ((bool)state["software"] != custom || (bool)state["fullSpeed"])
                throw new InvalidOperationException("测试控制位回读不一致");
            return state;
        } catch {
            WriteSeven(1, FirstSeven((int[])before["cpuTable"]));
            WriteSeven(2, FirstSeven((int[])before["gpuTable"]));
            SetCustom((bool)before["software"], transport);
            SetBit(0x98, (bool)before["fullSpeed"], "Get_Data");
            throw;
        }
    }

    public static void EndTest(bool release) {
        if (release && testActive) {
            SetCustom(false, "set_ap");
            SetBit(0x98, false, "Get_Data");
        }
        testActive = false;
    }

    static int[] FirstSeven(int[] table) { var values = new int[7]; Array.Copy(table, values, 7); return values; }

    static void WriteSeven(byte block, int[] values) {
        var current = Get("Get_Fan", block);
        var payload = new byte[32]; payload[0] = block;
        Array.Copy(current, 0, payload, 1, Math.Min(31, current.Length));
        for (int i = 0; i < 7; i++) payload[i + 1] = (byte)values[i];
        Call("Set_Fan", payload);
        var check = Get("Get_Fan", block);
        for (int i = 0; i < 7; i++) if (check[i] != values[i])
            throw new InvalidOperationException("测试曲线逐点回读不一致");
    }

    static void SetCustom(bool enabled, string transport) {
        if (transport == "legacy") { SetBit(0xD4, enabled, "Get_AP"); return; }
        var current = Get("Get_AP", 1);
        var payload = new byte[32]; payload[0] = 1;
        Array.Copy(current, 0, payload, 1, Math.Min(31, current.Length));
        payload[1] = enabled ? (byte)(payload[1] | 0x80) : (byte)(payload[1] & 0x7F);
        Call("Set_AP", payload);
    }

    public static Dictionary<string, object> Apply(int[] cpu, int[] gpu, bool full) {
        foreach (var duties in new[] { cpu, gpu }) {
            if (duties == null || duties.Length != 6) throw new ArgumentException("风扇曲线需要 6 个点");
            foreach (int duty in duties) if (duty < 0 || duty > 150) throw new ArgumentException("风扇风量超出范围");
        }
        var before = Read();
        try {
            WriteTable(1, cpu);
            WriteTable(2, gpu);
            SetBit(0xD4, true, "Get_AP"); // enable custom table control
            SetBit(0x98, full, "Get_Data");
            var state = Read();
            if (!(bool)state["software"] || (bool)state["fullSpeed"] != full)
                throw new InvalidOperationException("EC 风扇模式回读不一致");
            return state;
        } catch (Exception error) {
            try {
                WriteTable(1, (int[])before["cpu"]);
                WriteTable(2, (int[])before["gpu"]);
                SetBit(0xD4, (bool)before["software"], "Get_AP");
                SetBit(0x98, (bool)before["fullSpeed"], "Get_Data");
            } catch (Exception rollback) {
                throw new InvalidOperationException("风扇写入失败：" + error.Message + "；恢复失败：" + rollback.Message);
            }
            throw;
        }
    }

    public static Dictionary<string, object> Restore(int[] cpu, int[] gpu) {
        SetBit(0x98, false, "Get_Data");
        SetBit(0xD4, false, "Get_AP");  // hand back to the firmware curve
        if (cpu != null) WriteTable(1, cpu);
        if (gpu != null) WriteTable(2, gpu);
        return Read();
    }
}

// Intel GPU frequency, temperature and power from Level Zero Sysman (ze_loader.dll, part
// of the graphics driver). PresentMon's GPU telemetry returned nothing on this device.
class GpuSysman {
    [DllImport("ze_loader.dll")] static extern int zesInit(uint flags);
    [DllImport("ze_loader.dll")] static extern int zesDriverGet(ref uint count, [Out] IntPtr[] drivers);
    [DllImport("ze_loader.dll")] static extern int zesDeviceGet(IntPtr driver, ref uint count, [Out] IntPtr[] devices);
    [DllImport("ze_loader.dll")] static extern int zesDeviceEnumFrequencyDomains(IntPtr device, ref uint count, [Out] IntPtr[] handles);
    [DllImport("ze_loader.dll")] static extern int zesFrequencyGetProperties(IntPtr handle, byte[] props);
    [DllImport("ze_loader.dll")] static extern int zesFrequencyGetState(IntPtr handle, byte[] state);
    [DllImport("ze_loader.dll")] static extern int zesDeviceEnumTemperatureSensors(IntPtr device, ref uint count, [Out] IntPtr[] handles);
    [DllImport("ze_loader.dll")] static extern int zesTemperatureGetProperties(IntPtr handle, byte[] props);
    [DllImport("ze_loader.dll")] static extern int zesTemperatureGetState(IntPtr handle, out double celsius);
    [DllImport("ze_loader.dll")] static extern int zesDeviceEnumPowerDomains(IntPtr device, ref uint count, [Out] IntPtr[] handles);
    [DllImport("ze_loader.dll")] static extern int zesPowerGetEnergyCounter(IntPtr handle, byte[] counter);  // { uint64 energy µJ, uint64 timestamp µs }

    IntPtr freq, temp, power;
    ulong lastEnergy; long lastAt;
    string error;

    public GpuSysman() {
        try {
            if (zesInit(0) != 0) throw new InvalidOperationException("zesInit");
            uint n = 0; zesDriverGet(ref n, null); if (n == 0) throw new InvalidOperationException("no driver");
            var drivers = new IntPtr[n]; zesDriverGet(ref n, drivers);
            uint d = 0; zesDeviceGet(drivers[0], ref d, null); if (d == 0) throw new InvalidOperationException("no device");
            var devices = new IntPtr[d]; zesDeviceGet(drivers[0], ref d, devices);
            IntPtr device = devices[0];
            uint c = 0; zesDeviceEnumFrequencyDomains(device, ref c, null);
            var fs = new IntPtr[c]; if (c > 0) zesDeviceEnumFrequencyDomains(device, ref c, fs);
            foreach (var h in fs) {  // zes_freq_properties_t: stype, pNext, type (0 = GPU) at offset 16
                var p = new byte[64]; BitConverter.GetBytes(0x9).CopyTo(p, 0);
                if (zesFrequencyGetProperties(h, p) == 0 && BitConverter.ToInt32(p, 16) == 0) { freq = h; break; }
            }
            c = 0; zesDeviceEnumTemperatureSensors(device, ref c, null);
            var ts = new IntPtr[c]; if (c > 0) zesDeviceEnumTemperatureSensors(device, ref c, ts);
            foreach (var h in ts) {  // zes_temp_properties_t: type (1 = GPU) at offset 16
                var p = new byte[64]; BitConverter.GetBytes(0x14).CopyTo(p, 0);
                if (zesTemperatureGetProperties(h, p) == 0 && BitConverter.ToInt32(p, 16) == 1) { temp = h; break; }
            }
            if (temp == IntPtr.Zero && ts.Length > 0) temp = ts[0];
            c = 0; zesDeviceEnumPowerDomains(device, ref c, null);
            var ps = new IntPtr[c]; if (c > 0) zesDeviceEnumPowerDomains(device, ref c, ps);
            if (ps.Length > 0) power = ps[0];
        } catch (Exception e) { error = e.Message; }
    }

    public Dictionary<string, object> Read() {
        var r = new Dictionary<string, object>();
        if (error != null) { r["error"] = error; return r; }
        if (freq != IntPtr.Zero) {  // zes_freq_state_t: stype, pNext, voltage, request, tdp, efficient, actual
            var s = new byte[64]; BitConverter.GetBytes(0x1b).CopyTo(s, 0);
            if (zesFrequencyGetState(freq, s) == 0) { double actual = BitConverter.ToDouble(s, 48); if (actual > 0) r["gpuMhz"] = Math.Round(actual); }
        }
        double c;
        if (temp != IntPtr.Zero && zesTemperatureGetState(temp, out c) == 0 && c > 0 && c < 150) r["gpuTemp"] = Math.Round(c);
        var counter = new byte[16];
        if (power != IntPtr.Zero && zesPowerGetEnergyCounter(power, counter) == 0) {
            ulong e = BitConverter.ToUInt64(counter, 0);
            long at = Stopwatch.GetTimestamp();
            if (lastAt != 0 && e >= lastEnergy) r["gpuPower"] = Math.Round((e - lastEnergy) / 1e6 / ((at - lastAt) / (double)Stopwatch.Frequency), 2);
            lastEnergy = e; lastAt = at;
        }
        return r;
    }
}

// CPU, GPU, memory and battery figures for the detailed OSD, from Windows performance
// counters and the battery WMI classes. Instances are cached; GPU engine instances
// come and go with processes, so they are re-listed every few seconds.
class SystemStats {
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus { public uint length, load; public ulong totalPhys, availPhys, totalPage, availPage, totalVirtual, availVirtual, availExtended; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    PerformanceCounter cpuUtility, cpuPerformance;
    List<PerformanceCounter> gpu3d = new List<PerformanceCounter>(), gpuMemory = new List<PerformanceCounter>();
    DateTime listed = DateTime.MinValue;
    double baseMhz;

    public SystemStats() {
        cpuUtility = Counter("Processor Information", "% Processor Utility", "_Total");
        cpuPerformance = Counter("Processor Information", "% Processor Performance", "_Total");
        // "% Processor Performance" is relative to the nominal frequency, which this counter
        // reports (the registry ~MHz value is a boost reading on this chip).
        var nominal = Counter("Processor Information", "Processor Frequency", "_Total");
        baseMhz = nominal == null ? 0 : Next(nominal);
        if (nominal != null) nominal.Dispose();
    }

    static PerformanceCounter Counter(string category, string name, string instance) {
        try { var c = new PerformanceCounter(category, name, instance, true); c.NextValue(); return c; } catch { return null; }
    }

    static double Next(PerformanceCounter c) { try { return c == null ? double.NaN : c.NextValue(); } catch { return double.NaN; } }

    void List() {
        listed = DateTime.Now;
        foreach (var c in gpu3d) c.Dispose();
        foreach (var c in gpuMemory) c.Dispose();
        gpu3d.Clear(); gpuMemory.Clear();
        try {
            foreach (string instance in new PerformanceCounterCategory("GPU Engine").GetInstanceNames())
                if (instance.EndsWith("engtype_3D")) { var c = Counter("GPU Engine", "Utilization Percentage", instance); if (c != null) gpu3d.Add(c); }
        } catch { }
        try {
            foreach (string instance in new PerformanceCounterCategory("GPU Adapter Memory").GetInstanceNames()) {
                var shared = Counter("GPU Adapter Memory", "Shared Usage", instance);
                var dedicated = Counter("GPU Adapter Memory", "Dedicated Usage", instance);
                if (shared != null) gpuMemory.Add(shared);
                if (dedicated != null) gpuMemory.Add(dedicated);
            }
        } catch { }
    }

    public Dictionary<string, object> Read() {
        if ((DateTime.Now - listed).TotalSeconds > 5) List();
        var r = new Dictionary<string, object>();
        double utility = Next(cpuUtility), performance = Next(cpuPerformance);
        if (!double.IsNaN(utility)) r["cpuUsage"] = Math.Round(Math.Min(100, utility), 1);
        if (!double.IsNaN(performance) && baseMhz > 0) r["cpuMhz"] = Math.Round(baseMhz * performance / 100);
        double gpu = 0; bool any = false;
        foreach (var c in gpu3d) { double v = Next(c); if (!double.IsNaN(v)) { gpu += v; any = true; } }
        if (any) r["gpuUsage"] = Math.Round(Math.Min(100, gpu), 1);
        double memory = 0; any = false;
        foreach (var c in gpuMemory) { double v = Next(c); if (!double.IsNaN(v)) { memory += v; any = true; } }
        if (any) r["gpuMemMB"] = Math.Round(memory / 1048576);
        var status = new MemoryStatus { length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
        if (GlobalMemoryStatusEx(ref status)) {
            r["ramUsedGB"] = Math.Round((status.totalPhys - status.availPhys) / 1073741824.0, 1);
            r["ramTotalGB"] = Math.Round(status.totalPhys / 1073741824.0, 1);
        }
        try {
            using (var searcher = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT DischargeRate, ChargeRate, RemainingCapacity, PowerOnline, Discharging FROM BatteryStatus"))
                foreach (System.Management.ManagementObject b in searcher.Get()) {
                    double discharge = Convert.ToDouble(b["DischargeRate"] ?? 0), charge = Convert.ToDouble(b["ChargeRate"] ?? 0);
                    // This firmware often reports DischargeRate as 0x80000000 (unknown); the plugin
                    // then derives the rate from RemainingCapacity over time.
                    if (discharge < 0 || discharge > 200000) discharge = 0;
                    if (charge < 0 || charge > 200000) charge = 0;
                    r["discharging"] = Convert.ToBoolean(b["Discharging"] ?? false);
                    double remaining = Convert.ToDouble(b["RemainingCapacity"] ?? 0);
                    r["batteryW"] = Math.Round((discharge > 0 ? -discharge : charge) / 1000.0, 1);
                    if (discharge > 0 && remaining > 0) r["batteryMinutes"] = Math.Round(remaining / discharge * 60);
                    r["batteryRemaining"] = remaining;
                    break;
                }
            using (var full = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
                foreach (System.Management.ManagementObject b in full.Get()) {
                    double capacity = Convert.ToDouble(b["FullChargedCapacity"] ?? 0);
                    if (capacity > 0) r["batteryFull"] = capacity;
                    object rem; double charging = r.ContainsKey("batteryW") ? Convert.ToDouble(r["batteryW"]) : 0;
                    if (capacity > 0 && charging > 0.5 && r.TryGetValue("batteryRemaining", out rem))
                        r["chargeMinutes"] = Math.Round(Math.Max(0, capacity - Convert.ToDouble(rem)) / (charging * 1000) * 60);
                    break;
                }
        } catch (Exception error) { r["batteryError"] = error.Message; }
        return r;
    }
}

// Intel PresentMon 2.x service API (installed with Intel Graphics Software). Application
// FPS counts only frames the game rendered: XeSS frame generation (frame type
// INTEL_XEFG) and other generated or repeated frames are excluded, while Presented FPS
// counts everything the swap chain showed.
class FrameStats : IDisposable {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("PresentMonAPI2.dll")] static extern int pmOpenSession(out IntPtr session);
    [DllImport("PresentMonAPI2.dll")] static extern int pmCloseSession(IntPtr session);
    [DllImport("PresentMonAPI2.dll")] static extern int pmStartTrackingProcess(IntPtr session, uint pid);
    [DllImport("PresentMonAPI2.dll")] static extern int pmStopTrackingProcess(IntPtr session, uint pid);
    [DllImport("PresentMonAPI2.dll")] static extern int pmRegisterDynamicQuery(IntPtr session, out IntPtr query, [In, Out] Element[] elements, ulong count, double windowMs, double offsetMs);
    [DllImport("PresentMonAPI2.dll")] static extern int pmPollDynamicQuery(IntPtr query, uint pid, byte[] blob, ref uint swapChains);
    [DllImport("PresentMonAPI2.dll")] static extern int pmFreeDynamicQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    struct Element { public int metric, stat; public uint device, index; public ulong offset, size; }
    const int AVG = 1;
    public const double QueryWindowMs = 1000, QueryOffsetMs = 1000;
    static readonly string[] Names = { "appFps", "presentedFps", "displayedFps", "gpuBusy", "cpuBusy", "frameTime" };
    // PM_METRIC_APPLICATION_FPS, PRESENTED_FPS, DISPLAYED_FPS, GPU_BUSY, CPU_BUSY, CPU_FRAME_TIME
    static readonly int[] Metrics = { 62, 12, 11, 14, 9, 8 };
    const int MaxSwapChains = 4;
    IntPtr session, query, lowsQuery, gpuQuery;
    Element[] elements, lows, gpu;
    int stride, lowsStride, gpuStride;
    uint tracked;
    public string lastStatus;

    public FrameStats() {
        string lib = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMonSharedService", "PresentMonAPI2.dll");
        if (!File.Exists(lib) || LoadLibraryW(lib) == IntPtr.Zero) throw new InvalidOperationException("未找到 Intel PresentMon 服务，无法读取帧率");
        int status = pmOpenSession(out session);
        if (status != 0) throw new InvalidOperationException("无法连接 Intel PresentMon 服务 " + status);
        elements = new Element[Metrics.Length];
        for (int i = 0; i < Metrics.Length; i++) elements[i] = new Element { metric = Metrics[i], stat = AVG };
        status = pmRegisterDynamicQuery(session, out query, elements, (ulong)elements.Length, QueryWindowMs, QueryOffsetMs);
        if (status != 0) { pmCloseSession(session); session = IntPtr.Zero; throw new InvalidOperationException("帧率查询注册失败 " + status); }
        stride = Stride(elements);
        // 1% low over a longer window, and GPU telemetry (adapter 1 = the Arc iGPU). Both
        // are optional: a failed registration only leaves those OSD fields empty.
        lows = new[] { new Element { metric = 62, stat = 5 }, new Element { metric = 12, stat = 5 } };
        if (pmRegisterDynamicQuery(session, out lowsQuery, lows, (ulong)lows.Length, 5000, 1000) == 0) lowsStride = Stride(lows); else lowsQuery = IntPtr.Zero;
        gpu = new[] { new Element { metric = 29, stat = AVG, device = 1 }, new Element { metric = 30, stat = AVG, device = 1 }, new Element { metric = 27, stat = AVG, device = 1 } };
        if (pmRegisterDynamicQuery(session, out gpuQuery, gpu, (ulong)gpu.Length, 1000, 1000) == 0) gpuStride = Stride(gpu); else gpuQuery = IntPtr.Zero;
    }

    static int Stride(Element[] items) {
        ulong end = 0;
        foreach (var e in items) end = Math.Max(end, e.offset + e.size);
        return (int)end;
    }

    static double[] PollValues(IntPtr q, Element[] items, int itemStride, uint pid) {
        if (q == IntPtr.Zero) return null;
        var blob = new byte[itemStride * MaxSwapChains];
        uint count = MaxSwapChains;
        if (pmPollDynamicQuery(q, pid, blob, ref count) != 0 || count == 0) return null;
        // Highest value across swap chains: the game's main chain presents the most.
        var values = new double[items.Length];
        for (int chain = 0; chain < count && chain < MaxSwapChains; chain++)
            for (int i = 0; i < items.Length; i++) {
                double v = BitConverter.ToDouble(blob, chain * itemStride + (int)items[i].offset);
                if (!double.IsNaN(v) && !double.IsInfinity(v)) values[i] = Math.Max(values[i], v);
            }
        return values;
    }

    public Dictionary<string, object> Extra(uint pid) {
        var r = new Dictionary<string, object>();
        if (pid == 0 || pid != tracked) return r;
        var l = PollValues(lowsQuery, lows, lowsStride, pid);
        if (l != null) { r["appFps01"] = Math.Round(l[0], 1); r["presentedFps01"] = Math.Round(l[1], 1); }
        var g = PollValues(gpuQuery, gpu, gpuStride, pid);
        if (g != null) { r["gpuMhz"] = Math.Round(g[0]); r["gpuTemp"] = Math.Round(g[1]); r["gpuPower"] = Math.Round(g[2], 1); }
        return r;
    }

    public List<Dictionary<string, object>> Poll(uint pid) {
        var list = new List<Dictionary<string, object>>();
        if (pid != tracked) {
            if (tracked != 0) pmStopTrackingProcess(session, tracked);
            tracked = 0;
            int status = pmStartTrackingProcess(session, pid);
            lastStatus = "start=" + status;
            if (status != 0 && status != 7) throw new InvalidOperationException("无法跟踪游戏进程帧率 " + status);
            tracked = pid;
            return list;  // the first window has no samples yet
        }
        var blob = new byte[stride * MaxSwapChains];
        uint count = MaxSwapChains;
        int poll = pmPollDynamicQuery(query, pid, blob, ref count);
        lastStatus = "poll=" + poll + " count=" + count;
        if (poll != 0) { pmStopTrackingProcess(session, tracked); tracked = 0; return list; }
        for (int chain = 0; chain < count && chain < MaxSwapChains; chain++) {
            var item = new Dictionary<string, object>();
            for (int i = 0; i < elements.Length; i++) {
                double value = BitConverter.ToDouble(blob, chain * stride + (int)elements[i].offset);
                item[Names[i]] = double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Round(value, 3);
            }
            list.Add(item);
        }
        return list;
    }

    public void Dispose() {
        if (tracked != 0) pmStopTrackingProcess(session, tracked);
        if (query != IntPtr.Zero) pmFreeDynamicQuery(query);
        if (lowsQuery != IntPtr.Zero) pmFreeDynamicQuery(lowsQuery);
        if (gpuQuery != IntPtr.Zero) pmFreeDynamicQuery(gpuQuery);
        lowsQuery = gpuQuery = IntPtr.Zero;
        if (session != IntPtr.Zero) pmCloseSession(session);
        query = session = IntPtr.Zero; tracked = 0;
    }
}
