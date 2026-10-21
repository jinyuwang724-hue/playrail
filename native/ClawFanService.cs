using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Only fan settings cross this boundary. No paths, register addresses or executable
// commands are accepted from the unelevated UI. The service owns the hardware loop.
public sealed class FanSettings {
    public string mode { get; set; }
    public Dictionary<string, int[]> curves { get; set; }
    public bool full { get; set; }
    public string control { get; set; }
    public FanSettings Checked() {
        if (control == null) control = "custom";
        if (control != "custom" && control != "auto") throw new ArgumentException("风扇控制方式无效");
        if (mode != "quiet" && mode != "balanced" && mode != "performance") throw new ArgumentException("风扇档位无效");
        if (curves == null || curves.Count != 3) throw new ArgumentException("需要三个风扇档位");
        foreach (string key in new[] { "quiet", "balanced", "performance" }) {
            int[] points;
            if (!curves.TryGetValue(key, out points) || points == null || points.Length != 6)
                throw new ArgumentException("每档曲线需要六个风量值");
            if (points.Any(d => d < 1 || d > (key == "performance" ? 100 : 76)))
                throw new ArgumentException("风量超出范围，不能设置为零");
        }
        return this;
    }
    public FanSettings Copy() { return FanJson.Copy(this); }
    public bool SameActive(FanSettings other) {
        return mode == other.mode && full == other.full && control == other.control && curves[mode].SequenceEqual(other.curves[other.mode]);
    }
}

public sealed class FanConfig {
    public int schema { get; set; }
    public long revision { get; set; }
    public string ownerSid { get; set; }
    public FanSettings fan { get; set; }
    public int[] factoryCpu { get; set; }
    public int[] factoryGpu { get; set; }
    public FanConfig Checked() {
        if (schema != 1 || revision < 0) throw new ArgumentException("风扇服务配置版本无效");
        new SecurityIdentifier(ownerSid);
        if (fan == null) throw new ArgumentException("风扇服务配置缺失");
        fan.Checked();
        foreach (int[] table in new[] { factoryCpu, factoryGpu })
            if (table == null || table.Length != 6 || table.Any(d => d < 1 || d > 150))
                throw new ArgumentException("原厂风扇备份无效");
        return this;
    }
}

static class FanJson {
    public static string Encode(object value) { return new JavaScriptSerializer().Serialize(value); }
    public static T Decode<T>(string value) { return new JavaScriptSerializer().Deserialize<T>(value); }
    public static T Copy<T>(T value) { return Decode<T>(Encode(value)); }
}

interface IFanHardware {
    double Temperature();
    Dictionary<string, object> Read();
    Dictionary<string, object> Apply(int duty, bool full);
    Dictionary<string, object> Auto(int[] cpu, int[] gpu);
}
interface IFanStore { void Save(FanConfig config); }
sealed class FanFileStore : IFanStore {
    readonly string path;
    public FanFileStore(string path) { this.path = path; }
    public void Save(FanConfig config) {
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
            byte[] bytes = new UTF8Encoding(false).GetBytes(FanJson.Encode(config));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        // The existing file's ACL survives replacement. The directory is admin/SYSTEM writable.
        File.Replace(temporary, path, null);
    }
}

// The package temperature jumps 65→86 °C within a second even at 10 W. The curve follows a smoothed
// temperature (8 s time constant); raises wait 4 s, drops 10 s. Only a really hot reading (≥92 °C) goes
// straight up. (Before 2026-10-21 every reading ≥75 °C ratcheted the duty up and it never came down
// while the spikes kept coming, so the fan sat at the 4700 rpm cap at 10 W.)
sealed class FanGovernor {
    const double Smoothing = 8, Hot = 92;
    readonly List<KeyValuePair<double, int>> history = new List<KeyValuePair<double, int>>();
    double? smooth, smoothAt;
    public int? Duty { get; private set; }
    public double? Smoothed { get { return smooth; } }
    public void Reset() { history.Clear(); Duty = null; smooth = null; smoothAt = null; }
    public static int At(int[] curve, double temp) {
        int[] axis = { 50, 60, 70, 75, 80, 90 };
        if (temp <= axis[0]) return curve[0];
        for (int i = 1; i < axis.Length; i++) if (temp <= axis[i])
            return (int)Math.Round(curve[i - 1] + (curve[i] - curve[i - 1]) * (temp - axis[i - 1]) / (axis[i] - axis[i - 1]));
        return curve[5];
    }
    public int Update(int[] curve, double temp, double now) {
        double dt = smoothAt.HasValue ? Math.Max(0, now - smoothAt.Value) : 0;
        smooth = smooth.HasValue ? smooth.Value + (temp - smooth.Value) * (1 - Math.Exp(-dt / Smoothing)) : temp;
        smoothAt = now;
        int want = At(curve, smooth.Value);
        history.RemoveAll(p => now - p.Key >= 10);
        history.Add(new KeyValuePair<double, int>(now, want));
        if (!Duty.HasValue) Duty = want;
        else if (temp >= Hot) Duty = Math.Max(At(curve, temp), Duty.Value);
        else if (now - history[0].Key >= 3 && history.Where(p => now - p.Key < 4).Min(p => p.Value) > Duty.Value)
            Duty = history.Where(p => now - p.Key < 4).Min(p => p.Value);
        else if (now - history[0].Key >= 9 && history.Max(p => p.Value) < Duty.Value)
            Duty = history.Max(p => p.Value);
        return Duty.Value;
    }
}

sealed class FanEngine {
    readonly object gate = new object();
    readonly IFanHardware hardware;
    readonly IFanStore store;
    readonly Action<Dictionary<string, object>> record;
    readonly FanGovernor governor = new FanGovernor();
    FanConfig config;
    Dictionary<string, object> state = new Dictionary<string, object>();
    int? written;
    bool writtenFull, paused;
    long writes;
    double? temperature;
    double lastRecord = double.NegativeInfinity;
    long repairs, temperatureRetries;
    long thermalRecoveries;
    bool thermalBoost;
    double? cooledSince;
    double lastThermalRecovery = double.NegativeInfinity;
    Dictionary<string, object> lastIncident;
    string error = "", lastWrite;
    public FanEngine(FanConfig initial, IFanHardware hardware, IFanStore store, Action<Dictionary<string, object>> record = null) {
        config = FanJson.Copy(initial).Checked(); this.hardware = hardware; this.store = store; this.record = record;
    }
    double FreshTemperature() {
        for (int attempt = 0; ; attempt++) {
            try {
                double value = hardware.Temperature();
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > 125)
                    throw new InvalidOperationException("CPU 温度读取异常");
                temperature = value;
                return value;
            } catch { if (attempt >= 2) throw; temperatureRetries++; Thread.Sleep(25); }
        }
    }
    void Record(double now, string reason) {
        if (reason == "sample" && now - lastRecord < 2) return;
        var entry = new Dictionary<string, object>(state);
        entry["at"] = DateTime.UtcNow.ToString("o"); entry["reason"] = reason;
        entry["mode"] = config.fan.mode; entry["control"] = config.fan.control;
        entry["requestedFull"] = config.fan.full; entry["target"] = written;
        entry["temperature"] = temperature; entry["error"] = error;
        entry["repairs"] = repairs; entry["temperatureRetries"] = temperatureRetries;
        entry["thermalBoost"] = thermalBoost; entry["thermalRecoveries"] = thermalRecoveries;
        if (reason != "sample") lastIncident = entry;
        try { if (record != null) record(entry); } catch { /* Diagnostics must never stop cooling. */ }
        lastRecord = now;
    }
    static void Verify(Dictionary<string, object> check, int duty, bool full) {
        if (!true.Equals(check["software"]) || !full.Equals(check["fullSpeed"]) ||
            ((int[])check["cpu"]).Any(d => d != duty) || ((int[])check["gpu"]).Any(d => d != duty))
            throw new InvalidOperationException("风扇写入后回读不一致");
    }
    void Apply(FanSettings fan, bool immediate, double now) {
        if (fan.control == "auto") { Release(); return; }
        if (immediate) { governor.Reset(); cooledSince = null; }
        int duty = fan.full ? 100 : governor.Update(fan.curves[fan.mode], FreshTemperature(), now);
        if (immediate || written != duty || writtenFull != fan.full) {
            var next = hardware.Apply(duty, fan.full);
            Verify(next, duty, fan.full);
            state = next; written = duty; writtenFull = fan.full;
            writes++; lastWrite = DateTime.UtcNow.ToString("o");
        } else {
            var check = hardware.Read();
            state = check;
            try { Verify(check, duty, fan.full); state = check; }
            catch { repairs++; Record(now, "external_control_changed"); written = null; Apply(fan, false, now); }
        }
        RecoverCooledBoost(fan, duty, now);
    }
    void RecoverCooledBoost(FanSettings fan, int duty, double now) {
        // The EC may drive its live output to 94% while leaving our six table points intact.
        // Never change its ceiling/temperature thresholds or fight a hot machine. Refresh the
        // saved curve only after BOTH sensors remain <=80 C for ten seconds.
        thermalBoost = !fan.full && fan.mode != "performance" && new[] { "duty1", "duty2" }
            .Any(k => state.ContainsKey(k) && Convert.ToInt32(state[k]) > 76);
        if (!thermalBoost || !temperature.HasValue || temperature.Value > 80 ||
            !state.ContainsKey("ecTemperature") || Convert.ToInt32(state["ecTemperature"]) > 80) {
            cooledSince = null; return;
        }
        if (!cooledSince.HasValue) cooledSince = now;
        if (now - cooledSince.Value < 10 || now - lastThermalRecovery < 15) return;
        Record(now, "thermal_boost_cooldown");
        var next = hardware.Apply(duty, false);
        Verify(next, duty, false);
        state = next; written = duty; writtenFull = false;
        thermalRecoveries++; writes++; lastWrite = DateTime.UtcNow.ToString("o");
        lastThermalRecovery = now; cooledSince = null;
        thermalBoost = new[] { "duty1", "duty2" }.Any(k => state.ContainsKey(k) && Convert.ToInt32(state[k]) > 76);
    }
    void Release() {
        state = hardware.Auto(config.factoryCpu, config.factoryGpu);
        if (true.Equals(state["software"]) || true.Equals(state["fullSpeed"]))
            throw new InvalidOperationException("恢复原厂自动控制后回读不一致");
        written = null; governor.Reset(); thermalBoost = false; cooledSince = null;
    }
    public void Tick(double now) {
        lock (gate) {
            if (paused) return;
            try {
                if (config.fan.control == "auto") {
                    state = hardware.Read();
                    if (true.Equals(state["software"]) || true.Equals(state["fullSpeed"])) Release();
                } else Apply(config.fan, false, now);
                error = "";
                bool overspeed = config.fan.control == "custom" && !config.fan.full && config.fan.mode != "performance"
                    && new[] { "rpm1", "rpm2" }.Any(k => state.ContainsKey(k) && Convert.ToInt32(state[k]) > 5000);
                Record(now, overspeed ? "rpm_above_profile_expectation" : "sample");
            } catch (Exception failure) {
                error = failure.Message;
                Record(now, "factory_auto_fallback"); temperature = null;
                try { Release(); } catch (Exception recovery) { error += "；恢复自动控制失败：" + recovery.Message; }
            }
        }
    }
    public Dictionary<string, object> Update(FanSettings value, long expectedRevision, double now) {
        lock (gate) {
            if (paused) throw new InvalidOperationException("风扇服务正在休眠，请恢复后重试");
            if (expectedRevision != config.revision) throw new InvalidOperationException("风扇设置已更新，请刷新后重试");
            var candidate = FanJson.Copy(config);
            candidate.fan = value.Copy().Checked(); candidate.revision++;
            bool changed = !candidate.fan.SameActive(config.fan) || error.Length != 0;
            try {
                if (changed) Apply(candidate.fan, true, now);
                store.Save(candidate);
            } catch (Exception failure) {
                // A failed transaction never becomes the next boot's configuration.
                try { if (changed) Apply(config.fan, true, now); }
                catch (Exception rollback) {
                    error = failure.Message + "；旧档位恢复失败：" + rollback.Message;
                    try { Release(); } catch (Exception recovery) { error += "；自动控制恢复失败：" + recovery.Message; }
                }
                throw new InvalidOperationException(failure.Message + (error.Length == 0 ? "" : "；" + error));
            }
            config = candidate; error = ""; Record(now, "settings_applied");
            return Snapshot();
        }
    }
    public void Suspend() { lock (gate) { paused = true; Release(); } }
    public void Resume() { lock (gate) { governor.Reset(); written = null; paused = false; } }
    public void Stop() { lock (gate) { paused = true; Release(); } }
    public Dictionary<string, object> Snapshot() {
        lock (gate) {
            var result = new Dictionary<string, object>(state);
            result["settings"] = config.fan.Copy(); result["revision"] = config.revision;
            result["execution"] = "service"; result["serviceVersion"] = "1.0.3";
            result["servicePid"] = Process.GetCurrentProcess().Id;
            result["target"] = written; result["temperature"] = temperature; result["smoothedTemperature"] = governor.Smoothed;
            result["writeCount"] = writes; result["lastWrite"] = lastWrite;
            result["error"] = error; result["paused"] = paused;
            result["repairCount"] = repairs; result["temperatureRetries"] = temperatureRetries;
            result["lastIncident"] = lastIncident;
            result["thermalBoost"] = thermalBoost; result["thermalRecoveryCount"] = thermalRecoveries;
            return result;
        }
    }
}

// Uses the same signed PawnIO module and two read-only MSRs as ClawPower.Thermal.
sealed class FanHardware : IFanHardware, IDisposable {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_open(out IntPtr handle);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_execute(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name, ulong[] input, UIntPtr inSize, ulong[] output, UIntPtr outSize, out UIntPtr returned);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_close(IntPtr handle);
    IntPtr handle;
    ulong ReadMsr(uint register) {
        if (handle == IntPtr.Zero) {
            string library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");
            if (LoadLibraryW(library) == IntPtr.Zero) throw new IOException("PawnIO 温度组件未就绪");
            byte[] module = File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pawnio", "IntelMSR.bin"));
            IntPtr next;
            if (pawnio_open(out next) < 0) throw new IOException("无法读取 CPU 温度");
            if (pawnio_load(next, module, (UIntPtr)module.Length) < 0) { pawnio_close(next); throw new IOException("温度模块加载失败"); }
            handle = next;
        }
        var output = new ulong[1]; UIntPtr returned;
        if (pawnio_execute(handle, "ioctl_read_msr", new ulong[] { register }, (UIntPtr)1, output, (UIntPtr)1, out returned) < 0)
            throw new IOException("CPU 温度传感器读取失败");
        return output[0];
    }
    public double Temperature() {
        ulong target = ReadMsr(0x1A2), status = ReadMsr(0x1B1);
        return (int)((target >> 16) & 0xFF) - (int)((status >> 16) & 0x7F);
    }
    public Dictionary<string, object> Read() { return MsiFan.Read(); }
    public Dictionary<string, object> Apply(int duty, bool full) { return MsiFan.Apply(Enumerable.Repeat(duty, 6).ToArray(), Enumerable.Repeat(duty, 6).ToArray(), full); }
    public Dictionary<string, object> Auto(int[] cpu, int[] gpu) { return MsiFan.Restore(cpu, gpu); }
    public void Dispose() { if (handle != IntPtr.Zero) { pawnio_close(handle); handle = IntPtr.Zero; } }
}

sealed class ClawFanService : ServiceBase {
    internal const string Name = "ClawFanService", Pipe = "ClawToolbox.FanService.v1";
    readonly ManualResetEvent stopping = new ManualResetEvent(false);
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    FanEngine engine;
    FanHardware hardware;
    Task loop, listener;
    NamedPipeServerStream activePipe;
    readonly object pipeGate = new object();
    public ClawFanService() { ServiceName = Name; CanHandlePowerEvent = true; CanShutdown = true; AutoLog = true; }
    static double Now() { return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; }
    protected override void OnStart(string[] args) {
        var config = FanJson.Decode<FanConfig>(File.ReadAllText(MsiFan.ServiceConfigPath, Encoding.UTF8)).Checked();
        using (var search = new System.Management.ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
        using (var rows = search.Get())
            if (!rows.Cast<System.Management.ManagementObject>().Any(r => Convert.ToString(r["Model"]).IndexOf("CG3EM", StringComparison.OrdinalIgnoreCase) >= 0))
                throw new InvalidOperationException("风扇服务仅支持已验证的 CG3EM 机型");
        MsiFan.ServiceOwner = true;
        stopping.Reset(); hardware = new FanHardware();
        engine = new FanEngine(config, hardware, new FanFileStore(MsiFan.ServiceConfigPath), LogSample);
        loop = Task.Run(() => {
            while (!stopping.WaitOne(0)) { engine.Tick(Now()); WaitHandle.WaitAny(new WaitHandle[] { stopping, wake }, 1000); }
        });
        listener = Task.Run(() => Listen(config.ownerSid));
    }
    static void LogSample(Dictionary<string, object> sample) {
        string path = Path.Combine(Path.GetDirectoryName(MsiFan.ServiceConfigPath), "telemetry.jsonl");
        if (File.Exists(path) && new FileInfo(path).Length >= 4 * 1024 * 1024) {
            string previous = path + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(path, previous);
        }
        File.AppendAllText(path, FanJson.Encode(sample) + Environment.NewLine, new UTF8Encoding(false));
    }
    void Listen(string owner) {
        var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(owner), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!stopping.WaitOne(0)) {
            try {
                using (var pipe = new NamedPipeServerStream(Pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 32768, 32768, acl)) {
                    lock (pipeGate) { if (stopping.WaitOne(0)) return; activePipe = pipe; }
                    var connected = pipe.BeginWaitForConnection(null, null);
                    if (WaitHandle.WaitAny(new[] { connected.AsyncWaitHandle, stopping }) != 0) return;
                    pipe.EndWaitForConnection(connected);
                    var work = Task.Run(() => Exchange(pipe));
                    if (!work.Wait(5000)) pipe.Dispose();
                }
            } catch (Exception) { if (!stopping.WaitOne(0)) stopping.WaitOne(250); }
            finally { lock (pipeGate) { activePipe = null; } }
        }
    }
    static string Line(Stream stream) {
        var bytes = new List<byte>();
        while (bytes.Count < 16384) {
            int value = stream.ReadByte();
            if (value < 0) throw new EndOfStreamException();
            if (value == 10) return new UTF8Encoding(false, true).GetString(bytes.ToArray());
            bytes.Add((byte)value);
        }
        throw new IOException("风扇请求过长");
    }
    void Exchange(NamedPipeServerStream pipe) {
        var reply = new Dictionary<string, object>();
        try {
            var request = FanJson.Decode<Dictionary<string, object>>(Line(pipe));
            string command = request.ContainsKey("command") ? request["command"] as string : null;
            if (command == "get") reply["fan"] = engine.Snapshot();
            else if (command == "update") {
                if (!request.ContainsKey("fan") || !request.ContainsKey("revision")) throw new ArgumentException("缺少风扇设置或版本");
                if (!(request["revision"] is int) && !(request["revision"] is long)) throw new ArgumentException("风扇设置版本无效");
                reply["fan"] = engine.Update(FanJson.Decode<FanSettings>(FanJson.Encode(request["fan"])), Convert.ToInt64(request["revision"]), Now());
            } else throw new ArgumentException("风扇服务不支持此操作");
            reply["ok"] = true;
        } catch (Exception failure) { reply["ok"] = false; reply["error"] = failure.Message; }
        try { byte[] bytes = Encoding.UTF8.GetBytes(FanJson.Encode(reply) + "\n"); pipe.Write(bytes, 0, bytes.Length); pipe.Flush(); } catch (IOException) { }
    }
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus) {
        if (powerStatus == PowerBroadcastStatus.Suspend) { try { engine.Suspend(); } catch (Exception e) { EventLog.WriteEntry(e.Message, EventLogEntryType.Error); } }
        else if (powerStatus == PowerBroadcastStatus.ResumeAutomatic || powerStatus == PowerBroadcastStatus.ResumeSuspend || powerStatus == PowerBroadcastStatus.ResumeCritical) { engine.Resume(); wake.Set(); }
        return true;
    }
    protected override void OnStop() {
        RequestAdditionalTime(15000); StopCore();
    }
    void StopCore() {
        stopping.Set(); wake.Set();
        lock (pipeGate) { if (activePipe != null) activePipe.Dispose(); }
        if (loop != null && !loop.Wait(10000)) throw new System.TimeoutException("风扇控制循环未及时退出");
        try { if (engine != null) engine.Stop(); } finally { if (hardware != null) hardware.Dispose(); }
        if (listener != null) listener.Wait(1000);
    }
    protected override void OnShutdown() { StopCore(); }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string machine, string database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatusEx(IntPtr service, int level, byte[] buffer, int size, out int needed);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
    static uint ServicePid() {
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new IOException("无法查询风扇服务");
        try {
            IntPtr service = OpenService(manager, Name, 4);
            if (service == IntPtr.Zero) throw new IOException("风扇服务未安装");
            try {
                var buffer = new byte[36]; int needed;
                if (!QueryServiceStatusEx(service, 0, buffer, buffer.Length, out needed) || BitConverter.ToUInt32(buffer, 4) != 4)
                    throw new IOException("风扇服务未运行");
                return BitConverter.ToUInt32(buffer, 28);
            } finally { CloseServiceHandle(service); }
        } finally { CloseServiceHandle(manager); }
    }
    static int Client() {
        try {
            string request = Line(Console.OpenStandardInput());
            uint expected = ServicePid();
            using (var pipe = new NamedPipeClientStream(".", Pipe, PipeDirection.InOut, PipeOptions.Asynchronous)) {
                pipe.Connect(3000); uint actual;
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out actual) || actual != expected)
                    throw new IOException("风扇服务身份不匹配");
                byte[] bytes = Encoding.UTF8.GetBytes(request + "\n"); pipe.Write(bytes, 0, bytes.Length); pipe.Flush();
                var read = Task.Run(() => Line(pipe));
                if (!read.Wait(10000)) throw new System.TimeoutException("风扇服务响应超时");
                Console.WriteLine(read.Result);
            }
            return 0;
        } catch (Exception error) { Console.WriteLine(FanJson.Encode(new { ok = false, error = error.Message })); return 1; }
    }
    public static int Main(string[] args) {
        if (args.Length == 1 && args[0] == "--client") { Console.OutputEncoding = new UTF8Encoding(false); return Client(); }
        if (args.Length != 0) return 2;
        ServiceBase.Run(new ClawFanService()); return 0;
    }
}
