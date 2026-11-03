using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

// Software input layer of the Claw handheld toolbox (docs/gamepad-input-layer.md). Roles:
//   <dataDir>                        stdio bridge, started by the agent at the agent's integrity level;
//                                    owns a named pipe and starts the engine elevated (games run elevated
//                                    here, and keyboard / mouse input to them needs the same level).
//   --serve <pipe> <pid> <dataDir>   the engine: hides the real pad with HidHide, reads it at ~1 kHz,
//                                    translates it by the layout and writes a ViGEm Xbox 360 pad + SendInput.
//   --guard <pid> <dataDir>          started by the engine: if the engine dies or stops ticking, unhides the pad.
//   --restore <dataDir>              undo a hiding left behind (the agent runs it at start).
//   --osd <gamepad|desktop>          the pad-mode notice (Osd.cs), started by the agent on a mode change.
// Every path that can end the engine ends in Hiding.Restore: the pad is never left hidden.
namespace ClawInput {
    public static class Program {
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint process);
        public const string Hardware = "VID_0DB0&PID_1901";

        public static int Main(string[] args) {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            try {
                if (args.Length == 1) return Bridge(args[0]);
                if (args.Length == 4 && args[0] == "--serve") return Serve(args[1], int.Parse(args[2]), args[3]);
                if (args.Length == 3 && args[0] == "--guard") return Guard.Run(int.Parse(args[1]), args[2]);
                if (args.Length == 2 && args[0] == "--restore") return RestoreCommand(args[1]);
                if (args.Length == 2 && args[0] == "--osd") return ModeOsd.Run(args[1]);
                throw new ArgumentException("启动参数错误");
            } catch (Exception error) {
                Console.WriteLine(Json(new Dictionary<string, object> { { "type", "fatal" }, { "error", error.Message } }));
                Console.Out.Flush();
                return 1;
            }
        }

        public static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }
        public static string JournalPath(string dataDir) { return Path.Combine(dataDir, "hidhide-journal.json"); }

        public static bool IsEngine(int pid) {
            try {
                using (var p = Process.GetProcessById(pid)) {
                    if (p.HasExited) return false;
                    string path = Foreground.ImagePath((uint)pid);
                    return path != null && Path.GetFileName(path).StartsWith("ClawInput", StringComparison.OrdinalIgnoreCase);
                }
            } catch (Exception) { return false; }
        }

        // Restore a journal whose engine is gone (or, with force, whatever state it is in).
        public static bool RestoreIfStale(string dataDir, int onlyPid, bool force, string by) {
            string path = JournalPath(dataDir);
            var j = HideJournal.Load(path);
            if (j == null || !j.Active) return false;
            if (onlyPid != 0 && j.Pid != onlyPid) return false;
            if (!force && IsEngine(j.Pid)) return false;
            using (var hh = new HidHide()) Hiding.Restore(hh, path, j, by);
            return true;
        }

        static int RestoreCommand(string dataDir) {
            var j = HideJournal.Load(JournalPath(dataDir));
            bool busy = j != null && j.Active && IsEngine(j.Pid);
            bool restored = !busy && RestoreIfStale(dataDir, 0, false, "agent-start");
            Console.WriteLine(Json(new Dictionary<string, object> { { "type", "restore" }, { "ok", true }, { "restored", restored }, { "busy", busy } }));
            return 0;
        }

        static int Bridge(string dataDir) {
            string name = "claw-input-" + Guid.NewGuid().ToString("N");
            var acl = new PipeSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            Process child = null;
            int childId = 0;
            try {
                using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, acl)) {
                    var pending = pipe.BeginWaitForConnection(null, null);
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    child = Process.Start(new ProcessStartInfo(exe, "--serve " + name + " " + Process.GetCurrentProcess().Id + " \"" + dataDir + "\"") {
                        UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(exe)
                    });
                    if (child == null) throw new IOException("输入层启动失败");
                    childId = child.Id;
                    if (!pending.AsyncWaitHandle.WaitOne(30000)) throw new TimeoutException("输入层连接超时，可能未获得管理员授权");
                    pipe.EndWaitForConnection(pending);
                    uint client;
                    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out client) || client != (uint)child.Id)
                        throw new IOException("输入层连接身份不匹配");
                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true }) {
                        Console.WriteLine(Json(new Dictionary<string, object> { { "type", "ready" }, { "pid", child.Id } }));
                        Console.Out.Flush();
                        var input = Task.Run(async () => { string line; while ((line = await Console.In.ReadLineAsync()) != null) await writer.WriteLineAsync(line); });
                        var output = Task.Run(async () => { string line; while ((line = await reader.ReadLineAsync()) != null) { Console.WriteLine(line); Console.Out.Flush(); } });
                        Task.WaitAny(input, output);
                    }
                }
            } finally {
                // The engine restores on its own when the pipe closes; give it a moment, then make sure.
                if (child != null) { try { child.WaitForExit(5000); } catch { } child.Dispose(); }
                if (childId != 0) {
                    try { if (RestoreIfStale(dataDir, childId, true, "bridge")) Console.WriteLine(Json(new Dictionary<string, object> { { "type", "restored" }, { "by", "bridge" } })); }
                    catch (Exception) { }
                }
            }
            return 0;
        }

        static int Serve(string name, int bridgePid, string dataDir) {
            using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None)) {
                pipe.Connect(10000);
                uint server;
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out server) || server != (uint)bridgePid)
                    throw new IOException("输入层管道身份不匹配");
                using (var bridge = Process.GetProcessById(bridgePid)) {
                    if (!string.Equals(bridge.MainModule.FileName, Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("输入层管道来源不匹配");
                }
                using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true }) {
                    var host = new Host(dataDir, writer);
                    AppDomain.CurrentDomain.UnhandledException += delegate { host.Dispose(); };
                    SystemEvents.SessionEnding += delegate { host.Dispose(); };
                    try {
                        string line;
                        while ((line = reader.ReadLine()) != null) if (!host.Command(line)) break;
                    } finally { host.Dispose(); }
                }
            }
            return 0;
        }
    }

    public sealed class LatencyStats {
        readonly double[] ring = new double[2048];
        int count, next;
        public double Last, Max;
        public void Add(double ms) { lock (ring) { ring[next] = ms; next = (next + 1) % ring.Length; if (count < ring.Length) count++; Last = ms; if (ms > Max) Max = ms; } }
        public Dictionary<string, object> Snapshot() {
            double[] copy;
            lock (ring) { copy = new double[count]; for (int i = 0; i < count; i++) copy[i] = ring[i]; }
            var d = new Dictionary<string, object> { { "samples", copy.Length } };
            if (copy.Length == 0) return d;
            Array.Sort(copy);
            double sum = 0; foreach (var x in copy) sum += x;
            d["lastMs"] = Math.Round(Last, 3); d["avgMs"] = Math.Round(sum / copy.Length, 3);
            d["p50Ms"] = Math.Round(copy[copy.Length / 2], 3); d["p99Ms"] = Math.Round(copy[Math.Min(copy.Length - 1, (int)(copy.Length * 0.99))], 3);
            d["maxMs"] = Math.Round(Max, 3);
            return d;
        }
        public void Clear() { lock (ring) { count = next = 0; Last = Max = 0; } }
    }

    // A scripted button timeline that stands in for the real pad (test_input_layer: end-to-end checks
    // through the real virtual pad and SendInput without someone pressing buttons).
    sealed class Script {
        public readonly List<KeyValuePair<double, bool[]>> Steps = new List<KeyValuePair<double, bool[]>>();
        public double Start, Length;
        public string LayoutFor;
        public readonly List<object> Timeline = new List<object>();
        public bool Frame(double now, InputFrame frame) {
            double at = now - Start;
            if (at > Length) return false;
            bool[] pressed = null;
            foreach (var s in Steps) if (s.Key <= at) pressed = s.Value;
            frame.Clear();
            if (pressed != null) Array.Copy(pressed, frame.Pressed, Sources.Count);
            if (frame.Pressed[Sources.LT]) frame.LT = 255;
            if (frame.Pressed[Sources.RT]) frame.RT = 255;
            return true;
        }
    }

    public sealed class Host : IDisposable {
        readonly object gate = new object();
        readonly string dataDir, journalPath;
        readonly StreamWriter writer;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly Translator translator = new Translator();
        readonly LatencyStats latency = new LatencyStats(), keyLatency = new LatencyStats(), testLatency = new LatencyStats();
        readonly Foreground foreground = new Foreground();
        Layout global = new Layout { Name = "global" };
        Dictionary<string, Layout> games = new Dictionary<string, Layout>();
        HashSet<string> notGames = new HashSet<string>();
        List<string> extraApps = new List<string>();
        int pollHz = 1000;
        bool enabled, disposed;
        volatile bool running;
        Thread loop;
        VirtualPad pad;
        BackKeyHook hook;
        HidHide hh;
        HideJournal journal;
        List<string> devices = new List<string>(), apps = new List<string>();
        Timer maintenance;
        MemoryMappedFile heartbeatFile;
        MemoryMappedViewAccessor heartbeat;
        EventWaitHandle guardStop;
        Process guard;
        volatile int padIndex = -1, virtualIndex = -1, otherVirtual;
        volatile string layoutName = "passthrough", error;
        long loops, packets, reapplied, ticks, sent;
        volatile string slots = "";
        List<string> virtualBefore = new List<string>();
        double loopHz, packetHz;
        long rateAt = Stopwatch.GetTimestamp(), rateLoops, ratePackets;
        volatile Script script, finished;
        volatile bool rescanDevices;
        bool highRes;

        public Host(string dataDir, StreamWriter writer) {
            this.dataDir = dataDir; this.writer = writer; journalPath = Program.JournalPath(dataDir);
            Output.SystemAction=action=>Emit(new Dictionary<string,object>{{"type","system_action"},{"action",action}});
            Directory.CreateDirectory(dataDir);
        }

        void Emit(Dictionary<string, object> message) {
            try { lock (writer) writer.WriteLine(json.Serialize(message)); } catch (Exception) { }
        }

        static double Ms(long ticks) { return ticks * 1000.0 / Stopwatch.Frequency; }
        static double Now() { return Ms(Stopwatch.GetTimestamp()); }

        // false: stop reading commands.
        public bool Command(string line) {
            Dictionary<string, object> c;
            try { c = json.Deserialize<Dictionary<string, object>>(line); } catch (Exception) { return true; }
            if (c == null) return true;
            object id; c.TryGetValue("id", out id);
            string kind = c.ContainsKey("command") ? c["command"] as string : null;
            var reply = new Dictionary<string, object> { { "id", id }, { "ok", true } };
            try {
                lock (gate) {
                    if (disposed) throw new InvalidOperationException("输入层正在退出");
                    switch (kind) {
                        case "configure": Configure(c); break;
                        case "enable": Enable(); break;
                        case "disable": Disable("disable"); break;
                        case "status": break;
                        case "test": StartScript(c); break;
                        case "test_result": {
                            var s = finished;
                            reply["finished"] = s != null && script == null;
                            if (s != null && script == null) { reply["timeline"] = s.Timeline; finished = null; }
                            break;
                        }
                        case "stop": Disable("stop"); Emit(reply); return false;
                        default: throw new ArgumentException("不认识的命令");
                    }
                }
                reply["status"] = Status();
            } catch (Exception ex) {
                reply["ok"] = false; reply["error"] = ex.Message;
            }
            Emit(reply);
            return true;
        }

        void Configure(Dictionary<string, object> c) {
            var layouts = c["layouts"] as IDictionary;
            var nextGlobal = LayoutJson.Parse(layouts["global"] as IDictionary);
            nextGlobal.Name = "global";
            var nextGames = new Dictionary<string, Layout>();
            var map = layouts.Contains("games") ? layouts["games"] as IDictionary : null;
            if (map != null) foreach (DictionaryEntry e in map) { var l = LayoutJson.Parse(e.Value as IDictionary); l.Name = "game:" + e.Key; nextGames[((string)e.Key).ToLowerInvariant()] = l; }
            var names = new HashSet<string>();
            if (c.ContainsKey("notGames") && c["notGames"] is IList) foreach (var n in (IList)c["notGames"]) names.Add(((string)n).ToLowerInvariant());
            var extra = new List<string>();
            if (c.ContainsKey("whitelist") && c["whitelist"] is IList) foreach (var n in (IList)c["whitelist"]) if (File.Exists((string)n)) extra.Add((string)n);
            if (c.ContainsKey("pollHz")) pollHz = Math.Max(250, Math.Min(1000, Convert.ToInt32(c["pollHz"])));
            global = nextGlobal; games = nextGames; notGames = names; extraApps = extra;
            layoutName = ""; // re-pick the layout on the next tick
        }

        List<string> Apps() {
            var list = new List<string> { Process.GetCurrentProcess().MainModule.FileName };
            list.AddRange(extraApps);
            list.AddRange(PackagedApps());
            var result = new List<string>();
            foreach (var p in list) { string image = HidHide.ImagePath(p); if (!result.Contains(image)) result.Add(image); }
            return result;
        }

        // Packaged apps that keep their own gamepad navigation on the real pad (outside games the virtual
        // pad stays at rest): the game library, Xbox Game Bar and the toolbox's Game Bar widget.
        // Installed packages from this user's package registry, plus copies that are running.
        static readonly string[,] Packaged = { { "ClawToolbox.Library_", "ClawLibrary.exe" },
            { "Microsoft.XboxGamingOverlay_", "GameBar.exe" }, { "ClawToolbox.GameBarWidget_", "ClawWidget.exe" } };

        static List<string> PackagedApps() {
            var result = new List<string>();
            try {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                    if (root != null) foreach (var name in root.GetSubKeyNames())
                        for (int i = 0; i < Packaged.GetLength(0); i++) {
                            if (!name.StartsWith(Packaged[i, 0], StringComparison.OrdinalIgnoreCase)) continue;
                            using (var key = root.OpenSubKey(name)) {
                                var folder = key == null ? null : key.GetValue("PackageRootFolder") as string;
                                if (folder != null && File.Exists(Path.Combine(folder, Packaged[i, 1]))) result.Add(Path.Combine(folder, Packaged[i, 1]));
                            }
                        }
            } catch (Exception) { }
            for (int i = 0; i < Packaged.GetLength(0); i++)
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Packaged[i, 1]))) {
                    try { var path = Foreground.ImagePath((uint)p.Id); if (path != null && !result.Contains(path)) result.Add(path); } catch { }
                    p.Dispose();
                }
            return result;
        }

        void Enable() {
            if (enabled) return;
            error = null;
            hh = new HidHide();
            try {
                devices = Setup.PadInstances(Program.Hardware);
                if (devices.Count == 0) throw new InvalidOperationException("没找到 XInput 模式的手柄（0DB0:1901）");
                var old = HideJournal.Load(journalPath);
                if (old != null && old.Active) {
                    if (old.Pid != Process.GetCurrentProcess().Id && Program.IsEngine(old.Pid)) throw new InvalidOperationException("另一个输入层还在运行");
                    Hiding.Restore(hh, journalPath, old, "engine-start");
                }
                apps = Apps();
                heartbeatFile = MemoryMappedFile.CreateOrOpen("Local\\ClawInputHeartbeat-" + Process.GetCurrentProcess().Id, 8);
                heartbeat = heartbeatFile.CreateViewAccessor(0, 8);
                heartbeat.Write(0, Win.UnbiasedMs());
                StartGuard();
                // Before hiding: find the pad while every process can still see it.
                padIndex = FindPad();
                virtualBefore = VirtualSlots();
                sent = 0;
                pad = new VirtualPad();
                pad.Rumble = ForwardRumble;
                journal = Hiding.Apply(hh, journalPath, Process.GetCurrentProcess().Id, devices, apps);
                // HidHide only stops new opens: programs that already had the pad open (Steam, a running
                // game) keep seeing it. Restarting the xusb22 function is not supported (pnputil error 50) and
                // disabling it could leave the user without a pad, so that stays a documented limitation.
                hook = new BackKeyHook();
                if (hook.Error != null) error = hook.Error;
                translator.SetLayout(Layout.Passthrough(), new List<OutputEvent>());
                layoutName = "";
                latency.Clear(); keyLatency.Clear(); testLatency.Clear();
                running = true; enabled = true;
                loop = new Thread(Loop) { IsBackground = true, Name = "input-loop", Priority = ThreadPriority.Highest };
                loop.Start();
                maintenance = new Timer(delegate { Maintain(); }, null, 1000, 1000);
            } catch (Exception) {
                Teardown("enable-failed");
                throw;
            }
        }

        void Disable(string why) { if (enabled || pad != null || journal != null) Teardown(why); }

        void Teardown(string why) {
            running = false;
            if (maintenance != null) { maintenance.Dispose(); maintenance = null; }
            if (loop != null && loop != Thread.CurrentThread) { loop.Join(2000); loop = null; }
            try { if (padIndex >= 0) { var off = new XInput.Vibration(); XInput.SetState((uint)padIndex, ref off); } } catch { }
            var events = new List<OutputEvent>();
            translator.ReleaseAll(events);
            try { Output.Send(events); } catch { }
            // Unhide first, then remove the virtual pad: the game never has no pad at all.
            try {
                if (journal != null && journal.Active) { if (hh == null) hh = new HidHide(); Hiding.Restore(hh, journalPath, journal, why); }
            } catch (Exception ex) { error = "解除隐藏失败：" + ex.Message; }
            if (pad != null) { try { pad.Dispose(); } catch { } pad = null; }
            if (hook != null) { hook.Dispose(); hook = null; }
            if (guardStop != null) { guardStop.Set(); guardStop.Dispose(); guardStop = null; }
            if (guard != null) { try { guard.WaitForExit(2000); } catch { } guard.Dispose(); guard = null; }
            if (heartbeat != null) { heartbeat.Dispose(); heartbeat = null; }
            if (heartbeatFile != null) { heartbeatFile.Dispose(); heartbeatFile = null; }
            if (hh != null) { hh.Dispose(); hh = null; }
            journal = null; enabled = false; padIndex = -1; virtualIndex = -1; layoutName = "passthrough";
        }

        void StartGuard() {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            int me = Process.GetCurrentProcess().Id;
            guardStop = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\ClawInputGuardStop-" + me);
            guardStop.Reset();
            guard = Process.Start(new ProcessStartInfo(exe, "--guard " + me + " \"" + dataDir + "\"") { UseShellExecute = false, CreateNoWindow = true });
        }

        void ForwardRumble(byte large, byte small) {
            int index = padIndex;
            if (index < 0) return;
            var v = new XInput.Vibration { Left = (ushort)(large * 257), Right = (ushort)(small * 257) };
            XInput.SetState((uint)index, ref v);
        }

        static List<string> VirtualSlots() {
            var result = new List<string>();
            for (uint i = 0; i < 4; i++) { ushort v, p; if (XInput.Identity(i, out v, out p) && v == 0x045E && p == 0x028E) result.Add(i.ToString()); }
            return result;
        }

        static int FindPad() {
            for (uint i = 0; i < 4; i++) {
                ushort vendor, product;
                if (XInput.Identity(i, out vendor, out product) && vendor == 0x0DB0 && product == 0x1901) return (int)i;
            }
            return -1;
        }

        void StartScript(Dictionary<string, object> c) {
            if (!enabled) throw new InvalidOperationException("输入层没开");
            var s = new Script();
            var steps = c["steps"] as IList;
            if (steps == null || steps.Count == 0 || steps.Count > 64) throw new ArgumentException("测试步骤要 1 到 64 步");
            foreach (IDictionary step in steps) {
                double at = Convert.ToDouble(step["at"]);
                var pressed = new bool[Sources.Count];
                foreach (var b in (IList)step["buttons"]) { int i = Sources.Index(b as string); if (i < 0) throw new ArgumentException("不认识的键 " + b); pressed[i] = true; }
                s.Steps.Add(new KeyValuePair<double, bool[]>(at, pressed));
                s.Length = Math.Max(s.Length, at);
            }
            s.Length = Math.Min(s.Length + Convert.ToDouble(c.ContainsKey("tailMs") ? c["tailMs"] : 600), 15000);
            s.LayoutFor = c.ContainsKey("layoutFor") ? c["layoutFor"] as string : null;
            finished = null;
            s.Start = Now();
            script = s;
        }

        Layout Pick(out string name) {
            var s = script;
            string fg = s != null && s.LayoutFor != null ? s.LayoutFor.ToLowerInvariant() : foreground.Name;
            Layout game;
            if (fg.Length == 0 || notGames.Contains(fg) || fg.StartsWith("claw")) { name = "passthrough"; return null; }
            if (games.TryGetValue(fg, out game)) { name = game.Name; return game; }
            name = "global"; return global;
        }

        void Loop() {
            var events = new List<OutputEvent>();
            var frame = new InputFrame();
            var last = new PadReport();
            bool sentOnce = false, m1 = false, m2 = false;
            uint lastPacket = 0;
            double nextScan = 0, nextForeground = 0;
            Layout active = null;
            using (var sleep = new EnhancedSleepGate())
            using (var ticker = new Ticker(pollHz)) {
                highRes = ticker.HighResolution;
                while (running) {
                    if (sleep.Sleeping) { ticker.Pause(); sleep.Wait(500); continue; }
                    ticker.Wait();
                    if (!running) break;
                    double now = Now();
                    try { heartbeat.Write(0, Win.UnbiasedMs()); } catch { }
                    if (now >= nextForeground) {
                        nextForeground = now + 100;
                        foreground.Refresh();
                        string name;
                        var want = Pick(out name);
                        if (name != layoutName) { active = want; translator.SetLayout(want, events); layoutName = name; }
                    }
                    bool fresh = false;
                    long readAt = Stopwatch.GetTimestamp();
                    var s = script;
                    if (s != null) {
                        if (!s.Frame(now, frame)) { script = null; finished = s; frame.Clear(); }
                        fresh = true;
                    } else if (padIndex >= 0) {
                        XInput.State state;
                        if (XInput.GetStateEx((uint)padIndex, out state) == 0) {
                            readAt = Stopwatch.GetTimestamp();
                            if (state.Packet != lastPacket) { fresh = true; lastPacket = state.Packet; packets++; }
                            Fill(frame, state.Pad);
                        } else { padIndex = -1; frame.Clear(); fresh = true; rescanDevices = true; }
                    } else {
                        frame.Clear();
                        if (now >= nextScan) {
                            nextScan = now + 1000;
                            padIndex = FindPad();
                            if (padIndex >= 0) rescanDevices = true;
                        }
                    }
                    var h = hook;
                    if (s == null && h != null) {
                        frame.Pressed[Sources.M1] = h.M1; frame.Pressed[Sources.M2] = h.M2;
                        if (h.M1 != m1 || h.M2 != m2) { fresh = true; m1 = h.M1; m2 = h.M2; }
                    }
                    // Outside games the virtual pad rests at zero: Steam, the game library, Game Bar read the
                    // real pad themselves (they are whitelisted), and a mirrored copy would get Steam's desktop
                    // configuration on top (left stick moving the mouse).
                    var report = active == null ? new PadReport() : translator.Update(frame, now, events);
                    bool wrote = false, typed = false;
                    if (!sentOnce || !report.Same(last)) {
                        if (pad != null && pad.Send(report)) { last = report; sentOnce = true; wrote = true; sent++; }
                    }
                    long padDone = Stopwatch.GetTimestamp();
                    if (events.Count > 0) {
                        if (s != null) foreach (var e in events) s.Timeline.Add(new Dictionary<string, object> { { "t", Math.Round(now - s.Start, 1) }, { "qpcMs", Math.Round(now, 3) }, { "event", e.ToString() } });
                        Output.Send(events); events.Clear(); typed = true;
                    }
                    if ((wrote || typed) && s != null) s.Timeline.Add(new Dictionary<string, object> { { "t", Math.Round(now - s.Start, 1) }, { "qpcMs", Math.Round(now, 3) }, { "buttons", (int)report.Buttons }, { "lt", (int)report.LT }, { "rt", (int)report.RT } });
                    // Read -> virtual pad report written (gamepad path) / SendInput returned (keyboard path).
                    // Scripted frames count apart; the first reports after plugging wait for the new device.
                    if (fresh && sent > 3) {
                        if (wrote) (s == null ? latency : testLatency).Add(Ms(padDone - readAt));
                        if (typed && s == null) keyLatency.Add(Ms(Stopwatch.GetTimestamp() - readAt));
                    }
                    loops++;
                    ticks++;
                }
            }
        }

        static void Fill(InputFrame f, XInput.Gamepad g) {
            for (int i = 0; i < Sources.Count; i++) { ushort bit = Sources.PadBit[i]; if (bit != 0) f.Pressed[i] = (g.Buttons & bit) != 0; }
            f.LT = g.LT; f.RT = g.RT; f.LX = g.LX; f.LY = g.LY; f.RX = g.RX; f.RY = g.RY;
        }

        void Maintain() {
            // A slow driver request or a command must not queue another blocked timer callback
            // every second. Only one maintenance pass may use the driver and its journal.
            if (!Monitor.TryEnter(gate)) return;
            try {
                long now = Stopwatch.GetTimestamp();
                double seconds = Ms(now - rateAt) / 1000.0;
                if (seconds > 0) { loopHz = (loops - rateLoops) / seconds; packetHz = (packets - ratePackets) / seconds; }
                rateAt = now; rateLoops = loops; ratePackets = packets;
                lock (gate) {
                    if (!enabled || disposed) return;
                    if (rescanDevices) { rescanDevices = false; var found = Setup.PadInstances(Program.Hardware); foreach (var d in found) if (!devices.Contains(d)) devices.Add(d); }
                    if (ticks / 1000 % 5 == 0) apps = Apps();
                    int fixes = Hiding.Ensure(hh, journalPath, journal, devices, apps);
                    if (fixes > 0) { reapplied += fixes; error = "HidHide 被别的程序改过，已重新隐藏（" + reapplied + " 次）"; }
                    // This bus can leave the user-index IOCTL pending even after cancellation.
                    // Discover our slot from XInput instead of blocking every subsequent command.
                    var now045e = VirtualSlots();
                    if (virtualIndex < 0) foreach (var slot in now045e) if (!virtualBefore.Contains(slot)) virtualIndex = int.Parse(slot);
                    otherVirtual = Math.Max(0, now045e.Count - (virtualIndex >= 0 ? 1 : 0));
                    var seen = new List<string>();
                    for (uint i = 0; i < 4; i++) { ushort v, p; seen.Add(XInput.Identity(i, out v, out p) ? v.ToString("X4") + ":" + p.ToString("X4") : "-"); }
                    slots = string.Join(" ", seen);
                }
                Emit(new Dictionary<string, object> { { "type", "status" }, { "status", Status() } });
            } catch (Exception ex) { error = ex.Message; }
            finally { Monitor.Exit(gate); }
        }

        Dictionary<string, object> Status() {
            List<string> white = null, black = null; bool? cloak = null;
            try { if (hh != null) { white = hh.Whitelist; black = hh.Blacklist; cloak = hh.Active; } } catch { }
            var h = hook;
            return new Dictionary<string, object> {
                { "enabled", enabled }, { "error", error }, { "pid", Process.GetCurrentProcess().Id },
                { "pad", new Dictionary<string, object> { { "connected", padIndex >= 0 }, { "index", padIndex }, { "packetHz", Math.Round(packetHz) } } },
                { "virtualPad", new Dictionary<string, object> { { "plugged", pad != null }, { "serial", pad == null ? 0 : (int)pad.Serial }, { "index", virtualIndex },
                    { "reports", pad == null ? 0 : pad.Submitted }, { "failed", pad == null ? 0 : pad.Failed }, { "rumbleRequests", pad == null ? 0 : pad.RumbleRequests } } },
                { "foreground", foreground.Name }, { "layout", layoutName },
                { "hidhide", new Dictionary<string, object> { { "cloak", cloak }, { "hidden", devices }, { "apps", apps }, { "blacklist", black }, { "whitelist", white }, { "reapplied", reapplied } } },
                { "latency", latency.Snapshot() }, { "keyLatency", keyLatency.Snapshot() }, { "testLatency", testLatency.Snapshot() }, { "xinputSlots", slots }, { "loopHz", Math.Round(loopHz) }, { "highResolutionTimer", highRes }, { "pollHz", pollHz },
                { "backKeys", new Dictionary<string, object> { { "hooked", h != null && h.Error == null }, { "m1", h != null && h.M1 }, { "m2", h != null && h.M2 }, { "swallowed", h == null ? 0 : h.Swallowed } } },
                { "keyboardMouse", new Dictionary<string, object> { { "sent", Output.Sent }, { "failed", Output.Failed } } },
                { "otherVirtualPads", otherVirtual }, { "testRunning", script != null },
            };
        }

        public void Dispose() {
            lock (gate) {
                if (disposed) return;
                disposed = true;
                try { Teardown("engine-exit"); } catch { }
            }
        }
    }

    // Watches one engine. If it exits without restoring, or its loop stops ticking for 3 s (hung),
    // unhides the pad and ends the engine. Ends quietly when the engine disables the layer.
    public static class Guard {
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr handle, uint code);
        public const long StaleMs = 3000;

        public static int Run(int pid, string dataDir) {
            IntPtr process = OpenProcess(0x00100001, false, pid); // SYNCHRONIZE | TERMINATE
            if (process == IntPtr.Zero) { Program.RestoreIfStale(dataDir, pid, false, "guard"); return 0; }
            EventWaitHandle stop = null;
            MemoryMappedFile file = null; MemoryMappedViewAccessor view = null;
            try {
                for (int i = 0; i < 50 && stop == null; i++) { try { stop = EventWaitHandle.OpenExisting("Local\\ClawInputGuardStop-" + pid); } catch { Thread.Sleep(100); } }
                long lastBeat = -1, lastChange = Win.UnbiasedMs(), wakeGraceUntil = 0;
                using (var sleep = new EnhancedSleepGate()) {
                while (true) {
                    if (WaitForSingleObject(process, 200) == 0) { Program.RestoreIfStale(dataDir, pid, true, "guard"); return 0; }
                    if (stop != null && stop.WaitOne(0)) return 0;
                    if (view == null) { try { file = MemoryMappedFile.OpenExisting("Local\\ClawInputHeartbeat-" + pid); view = file.CreateViewAccessor(0, 8); } catch { } }
                    long now = Win.UnbiasedMs();
                    if (sleep.Sleeping) { lastChange = now; wakeGraceUntil = now + 30000; }
                    if (view != null) {
                        long beat = view.ReadInt64(0);
                        if (beat != lastBeat) { lastBeat = beat; lastChange = now; }
                    }
                    var j = HideJournal.Load(Program.JournalPath(dataDir));
                    if (j != null && j.Active && j.Pid == pid && view != null && now > wakeGraceUntil && now - lastChange > StaleMs) {
                        Program.RestoreIfStale(dataDir, pid, true, "guard-hung");
                        TerminateProcess(process, 3);
                        return 0;
                    }
                }
                }
            } finally {
                if (view != null) view.Dispose(); if (file != null) file.Dispose(); if (stop != null) stop.Dispose();
                Win.CloseHandle(process);
            }
        }
    }
}
