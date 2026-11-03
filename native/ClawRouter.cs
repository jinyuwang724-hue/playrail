using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClawRouter {
    public static class Policy {
        public static string SteamMode(bool known, bool active, double ageMs) { return !known || ageMs < 0 || ageMs > 15000 ? "Unknown" : active ? "BigPicture" : "SteamClosed"; }
        public static bool CanRoute(bool enabled, bool ready, string mode, bool released) { return enabled && ready && mode == "BigPicture" && released; }
        public static bool GeneralContext(string mode, bool suspended, bool configured, bool steamWasActive=false) { return configured && !suspended && mode != "BigPicture" && !(mode=="Unknown" && steamWasActive); }
        public static string Action(object value) { string s = value as string; return s == "MainMenu" || s == "QuickAccess" || s == "MagpieToggle" || s == "None" ? s : "None"; }
        public static bool IsGameBarCommand(object raw) {
            var command=raw as Dictionary<string,object>;object type,action,rawKeys;
            if(command==null || !command.TryGetValue("type",out type))return false;
            if(Equals(type,"system"))return command.TryGetValue("action",out action) && Equals(action,"gamebar");
            if(!Equals(type,"keyboard") || !command.TryGetValue("keys",out rawKeys))return false;
            var keys=rawKeys as System.Collections.IList;
            if(keys==null || keys.Count!=2)return false;
            return Equals(keys[0],"G") && IsWindowsKey(keys[1]) || Equals(keys[1],"G") && IsWindowsKey(keys[0]);
        }
        static bool IsWindowsKey(object key) {return Equals(key,"LWin") || Equals(key,"RWin");}
    }
    public sealed class GameBarPermission {
        string context;
        public bool Requested { get; private set; }
        public void EnterContext(string next) { if(context!=next) {context=next;Requested=false;} }
        public void Allow() { Requested=true; }
        public void Clear() { Requested=false; }
    }
    public sealed class Router : IDisposable {
        readonly object gate = new object(), outputGate = new object(), logGate = new object();
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly string configDir, dataDir, handoverDir, journalPath;
        readonly Dictionary<int, long> lastButtons = new Dictionary<int, long>();
        readonly GameBarShortcutCorrelation gamebarShortcuts = new GameBarShortcutCorrelation();
        string shortcutContext;
        readonly Dictionary<string, System.Threading.Timer> pending = new Dictionary<string, System.Threading.Timer>();
        readonly Dictionary<string, KeyValuePair<long,int>> pendingInputs = new Dictionary<string, KeyValuePair<long,int>>();
        Dictionary<string,object> lastTimings;
        FileSystemWatcher files;
        ManagementEventWatcher buttons;
        FirmwareShortcutFilter shortcutFilter;
        SessionModeProbe sessionMode;
        volatile bool sessionGameMode;
        readonly SpecialShortcutCorrelation specialKeys = new SpecialShortcutCorrelation();
        GameBarGuard gameBarGuard;
        System.Threading.Timer changed;
        System.Threading.Timer steamWatchdog;
        string mode = "Unknown", phase = "观察中", error = null;
        string msiAction = "QuickAccess", qamAction = "MainMenu";
        Dictionary<string,object> generalMappings = new Dictionary<string,object>(), steamBindings = new Dictionary<string,object>();
        bool generalFailed;
        bool steamWindowOpen;
        readonly GameBarPermission gamebarPermission = new GameBarPermission();
        int menuFailures;
        bool enabled, ready, released, restoreHelper, disposed, configured;
        bool steamKnown, steamActive;
        volatile bool suspended;
        long steamReport;
        string maintenance;
        System.Threading.Timer maintenanceTimeout;
        int queries, playhubPort = 47991;
        long observed, routed;
        readonly bool diagnostic;
        public Router(string data, string config, bool observe) {
            diagnostic = observe; dataDir = Path.GetFullPath(data); configDir = Path.GetFullPath(config);
            Directory.CreateDirectory(dataDir);
            handoverDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "MSIClaw.ClawTweaks_7eszav2039cvc", "LocalCache", "Local");
            journalPath = Path.Combine(dataDir, "restore.json");
        }
        Dictionary<string, object> Parse(string raw) { return json.Deserialize<Dictionary<string, object>>(raw); }
        static object Get(Dictionary<string, object> d, string key) { object value; return d != null && d.TryGetValue(key, out value) ? value : null; }
        public void Emit(Dictionary<string, object> record) {
            lock(outputGate) { record["at"] = DateTime.UtcNow.ToString("o"); Console.WriteLine(new JavaScriptSerializer().Serialize(record)); Console.Out.Flush(); }
        }
        public void Status() {
            Emit(new Dictionary<string, object> { {"type","status"}, {"configured",configured}, {"mode",mode}, {"bigPictureOpen",mode=="BigPicture"}, {"enabled",enabled}, {"ready",ready}, {"released",released}, {"phase",phase}, {"error",error}, {"canObserve",buttons != null}, {"helperCheck","按压时核对，不定时扫描"}, {"canSuppress","Steam 大屏幕接管期间过滤固件快捷键"}, {"shortcutFilterReady",shortcutFilter != null}, {"sessionGameMode",sessionGameMode}, {"rawKeyboardReady",shortcutFilter!=null&&shortcutFilter.RawReady}, {"firmwareWindows",shortcutFilter==null?0:shortcutFilter.FirmwareWindows}, {"gameBarGuardActive",gameBarGuard!=null && gameBarGuard.Active}, {"gameBarClosed",gameBarGuard==null?0:gameBarGuard.Closed}, {"gameBarCloseFailures",gameBarGuard==null?0:gameBarGuard.Failures}, {"shortcutSuppressed",shortcutFilter==null?0:shortcutFilter.Suppressed}, {"shortcutReplayFailures",shortcutFilter==null?0:shortcutFilter.ReplayFailures}, {"modeSource","Steam 窗口状态通知 / 15 秒连接保护"}, {"playhubPort",playhubPort}, {"modeQueries",queries}, {"observed",observed}, {"routed",routed}, {"msiAction",msiAction}, {"qamAction",qamAction}, {"restorePending",restoreHelper}, {"helperRunning",Helpers().Length > 0}, {"diagnostic",diagnostic}, {"lastTimings",lastTimings} });
        }
        static Process[] Helpers() {var result=new List<Process>();foreach(var p in Process.GetProcessesByName("XboxGamingBarHelper")){if(Native.IsAlive(p.Id))result.Add(p);else p.Dispose();}return result.ToArray();}
        static bool HelperRunning(int id) {foreach(var p in Helpers()){int pid=p.Id;p.Dispose();if(pid==id)return true;}return false;}
        void Journal() { Atomic(journalPath, json.Serialize(new Dictionary<string, object> { {"restoreHelper",restoreHelper}, {"task","\\ClawTweaks\\ClawTweaksHelper"}, {"at",DateTime.UtcNow.ToString("o")} })); }
        static void Atomic(string path, string raw) { string temp = path + ".tmp"; File.WriteAllText(temp, raw, new UTF8Encoding(false)); if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path); }
        void Log(string kind, string detail) { lock(logGate) {
            string path = Path.Combine(dataDir,"events.jsonl");
            if(File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) { string previous = path + ".1"; if(File.Exists(previous)) File.Delete(previous); File.Move(path,previous); }
            File.AppendAllText(path, json.Serialize(new Dictionary<string,object>{{"at",DateTime.UtcNow.ToString("o")},{"event",kind},{"detail",detail}}) + Environment.NewLine, new UTF8Encoding(false));
        }
        }
        public void Start() {
            lock(gate) {
                if(!diagnostic && File.Exists(journalPath)) {
                    try { restoreHelper = Equals(Get(Parse(File.ReadAllText(journalPath)),"restoreHelper"),true); if(restoreHelper) Restore(); }
                    catch(Exception ex) { error = "上次恢复记录无法读取：" + ex.Message; }
                }
                try {
                    buttons = new ManagementEventWatcher(new ManagementScope(@"\\.\root\WMI"), new WqlEventQuery("SELECT * FROM MSI_Event"));
                    buttons.EventArrived += OnButton; buttons.Start();
                } catch(Exception ex) { if(buttons != null) buttons.Dispose(); buttons = null; error = "按键通知不可用：" + ex.Message; }
                if(!diagnostic) {
                    try {sessionMode=new SessionModeProbe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClawToolbox","shell","state.json"),OnSessionMode);sessionGameMode=sessionMode.Game;shortcutFilter=new FirmwareShortcutFilter(delegate{return sessionGameMode&&!suspended&&maintenance==null;});shortcutFilter.GameBarSuppressed+=OnSuppressedGamebar;shortcutFilter.ShortcutPressed+=OnImmediateShortcut;}
                    catch(Exception ex){Log("shortcut-filter-unavailable",ex.Message);}
                    try {gameBarGuard=new GameBarGuard(Log);}
                    catch(Exception ex){Log("gamebar-guard-unavailable",ex.Message);}
                }
                changed = new System.Threading.Timer(delegate { Task.Run((Action)Refresh); },null,Timeout.Infinite,Timeout.Infinite);
                if(Directory.Exists(configDir)) {
                    files = new FileSystemWatcher(configDir); files.NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.FileName; files.IncludeSubdirectories=false;
                    files.Changed += OnFile; files.Created += OnFile; files.Deleted += OnFile; files.Renamed += OnFile;
                    files.Error += delegate { Schedule(); }; files.EnableRaisingEvents=true;
                }
                ReadPort();
                steamWindowOpen=Native.BigPictureOpen();
                steamWatchdog = new System.Threading.Timer(delegate { lock(gate) {
                    if(disposed)return;
                    bool oldWindow=steamWindowOpen;
                    if(!steamKnown || !ready)steamWindowOpen=Native.BigPictureOpen();
                    if(oldWindow && !steamWindowOpen && !ready)steamActive=false;
                    string current=ReadMode();
                    if(current!=mode || oldWindow!=steamWindowOpen) { mode=current;Reconcile();Status(); }
                } },null,1000,1000);
                SystemEvents.PowerModeChanged += OnPower;
            }
            Refresh();
        }
        void OnFile(object sender, FileSystemEventArgs e) { if(e.Name == "config.json") Schedule(); }
        void Schedule() { lock(gate) { if(!disposed) changed.Change(120,Timeout.Infinite); } }
        void OnPower(object sender, PowerModeChangedEventArgs e) {
            if(e.Mode == PowerModes.Resume) { suspended=false;Schedule(); }
            if(e.Mode == PowerModes.Suspend) { suspended=true;Task.Run((Action)delegate { lock(gate) { if(disposed)return; steamKnown=false;mode="Unknown"; released=false; if(shortcutFilter!=null)shortcutFilter.SetEnabled(false); ClearPending(); if(maintenance==null)Restore(); phase="睡眠中"; Status(); } }); }
        }
        string ReadMode() {
            queries++;
            return Policy.SteamMode(ready && steamKnown && !suspended,steamActive,(Stopwatch.GetTimestamp()-steamReport)*1000.0/Stopwatch.Frequency);
        }
        void ReadPort() {
            try {
                int port=47991;
                var cfg=Parse(File.ReadAllText(Path.Combine(configDir,"config.json")));
                var safety=Get(cfg,"safety") as Dictionary<string,object>;
                if(Get(safety,"apiPort") != null) port=Convert.ToInt32(Get(safety,"apiPort"));
                if(port < 1 || port > 65535) throw new InvalidDataException("端口无效");
                playhubPort=port;
            } catch { /* Playhub is optional for ownership; keep the last menu fallback port. */ }
        }
        void Refresh() { lock(gate) { if(disposed)return; ReadPort();mode=ReadMode();Reconcile();Status(); } }
        bool GeneralWanted() { return !diagnostic && !generalFailed && !suspended && (sessionGameMode || !(mode=="Unknown" && steamWindowOpen) && Policy.GeneralContext(mode,suspended,generalMappings.Count!=0,steamActive)); }
        bool CanFilter() { return sessionGameMode && !suspended && maintenance==null; }
        bool AllowsConfiguredGamebar() {
            foreach(var raw in steamBindings.Values) {
                var command=raw as Dictionary<string,object>;
                if(Equals(Get(command,"type"),"system") && (Equals(Get(command,"action"),"gamebar") || Equals(Get(command,"action"),"screenshot")))return true;
            }
            return false;
        }
        void EmitMapping(string source, object command, string scope) {
            var value=command as Dictionary<string,object>;
            if(value==null || Equals(Get(value,"type"),"none"))return;
            routed++; Emit(new Dictionary<string,object>{{"type","mapped_action"},{"source",source},{"command",value},{"scope",scope}});
        }
        void OnSessionMode(bool game) {
            sessionGameMode=game;
            Task.Run((Action)delegate {lock(gate){if(disposed)return;gamebarShortcuts.Reset();Reconcile();Emit(new Dictionary<string,object>{{"type","session_mode"},{"game",game}});Status();}});
        }
        void OnImmediateShortcut(string action,IntPtr foreground,bool game) {
            long at=Stopwatch.GetTimestamp();
            if(action!="gamebarInput")specialKeys.Keyboard(action=="windowSwitcher"||action=="gamebar"?42:88,at*1000.0/Stopwatch.Frequency);
            Task.Run((Action)delegate {lock(gate){
                if(disposed||maintenance!=null||suspended||game!=sessionGameMode)return;
                if(action=="gamebarInput"){Emit(new Dictionary<string,object>{{"type","desktop_gamebar_input"}});return;}
                routed++;
                Emit(new Dictionary<string,object>{{"type","mapped_action"},{"source","QS"},{"scope","general"},
                    {"command",new Dictionary<string,object>{{"type","system"},{"action",action}}},
                    {"gesture",action=="windowSwitcher"?"long":"short"},{"gameMode",game},{"foregroundWindow",foreground.ToInt64()},
                    {"nativeMs",(Stopwatch.GetTimestamp()-at)*1000.0/Stopwatch.Frequency}});
            }});
        }
        void OnSuppressedGamebar() {
            double at=Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency;
            int epoch=gamebarShortcuts.Generation;
            Task.Run(async delegate {
                await Task.Delay(GameBarShortcutCorrelation.ReplayDelayMs);
                lock(gate) {
                    // Retain the old strict Steam suppression. In general mode,
                    // wait for the delayed QS report; the Claw button is unrelated.
                    if(!disposed && maintenance==null && GeneralWanted() && released &&
                        gamebarShortcuts.ShouldReplay(at,Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency,epoch))
                        EmitMapping("Keyboard",new Dictionary<string,object>{{"type","system"},{"action","gamebar"}},"general");
                }
            });
        }
        void Reconcile() {
            if(shortcutContext!=mode) {shortcutContext=mode;gamebarShortcuts.Reset();}
            // An intentional Game Bar action in the desktop scene must not disable protection in Steam.
            gamebarPermission.EnterContext(mode);
            if(maintenance!=null) { if(shortcutFilter!=null)shortcutFilter.SetEnabled(false); if(gameBarGuard!=null)gameBarGuard.SetEnabled(false); phase="插件正在维护兼容性修复"; return; }
            // Disable before restoring the helper, which may take several seconds.
            if(shortcutFilter!=null && !CanFilter())shortcutFilter.SetEnabled(false);
            try {ReconcileOwner();}
            finally {
                if(shortcutFilter!=null)shortcutFilter.SetEnabled(CanFilter());
                if(gameBarGuard!=null)gameBarGuard.SetEnabled(sessionGameMode && !gamebarPermission.Requested);
            }
        }
        void ReconcileOwner() {
            if(diagnostic) { phase="只读诊断"; return; }
            if(GeneralWanted()) {
                if(buttons==null || shortcutFilter==null) { phase="常规按键映射不可用";return; }
                try {
                    if(!released)StopTweaks();
                    released=true; phase="常规按键映射 · Steam 接管设置保持独立"; error=null;
                } catch(Exception ex) {
                    generalFailed=true; released=false; error=ex.Message; Restore(); phase="常规映射接管失败，原功能已尝试恢复";
                }
                return;
            }
            if(!enabled || !ready || mode != "BigPicture") {
                ClearPending();
                released=false; Restore(); phase=restoreHelper ? "需要恢复 ClawTweaks" : !enabled ? "自动接管已关闭：原映射" : !ready ? "Steam 按键连接未就绪：原映射" : mode=="SteamClosed" ? "Steam 大屏幕已关闭：原映射" : "Steam 状态未知，已停止接管"; return;
            }
            if(buttons == null) { phase="按键通知不可用，无法接管"; return; }
            if(!released) {
                try {
                    if(shortcutFilter==null)throw new InvalidOperationException("Game Bar 快捷键过滤不可用，未执行接管");
                    if(gameBarGuard==null)throw new InvalidOperationException("Game Bar 接管保护不可用，未执行接管");
                    StopTweaks(); gameBarGuard.SetEnabled(!AllowsConfiguredGamebar()); released=true; phase="Steam 大屏幕：插件接管"; error=null;
                }
                catch(Exception ex) { released=false; enabled=false; error=ex.Message; Log("handoff-failed",ex.ToString()); phase="接管失败，已恢复"; Restore(); }
            }
            if(released)phase="Steam 大屏幕：插件接管";
        }
        void ClearPending() { gamebarShortcuts.Reset();foreach(var timer in pending.Values)timer.Dispose();pending.Clear();pendingInputs.Clear(); }
        void StopTweaks() {
            var processes=Helpers();
            if(processes.Length==0) { Log("handoff","helper 本来未运行"); return; }
            int countBefore=Native.ControllerCount();
            if(countBefore==0) throw new InvalidOperationException("当前看不到可用手柄，未执行停用");
            // The widget reconnects its helper when its pipe closes. Close that UI owner first.
            foreach(var widget in Process.GetProcessesByName("XboxGamingBar")) {
                if(Native.PackageFamily(widget.Id)!="MSIClaw.ClawTweaks_7eszav2039cvc") {widget.Dispose();continue;}
                int pid=widget.Id;
                try {widget.CloseMainWindow();}catch{}
                Thread.Sleep(150);
                if(Native.IsAlive(pid))Native.CloseWidget(pid);
                widget.Dispose();Log("widget-closed","仅关闭经过包名核对的 ClawTweaks 组件界面，PID="+pid);
            }
            string requestPath=Path.Combine(handoverDir,"shutdown-request.json"), ackPath=Path.Combine(handoverDir,"shutdown-ack.json");
            Directory.CreateDirectory(handoverDir);
            if(File.Exists(requestPath)) throw new InvalidOperationException("ClawTweaks 已有退出请求，未覆盖它");
            restoreHelper=true; Journal();
            foreach(var p in processes) {
                string token=Guid.NewGuid().ToString("N");
                try {
                    Atomic(requestPath,json.Serialize(new Dictionary<string,object>{{"TargetPid",p.Id},{"Token",token},{"RequestedAtUtc",DateTime.UtcNow.ToString("o")},{"Reason","claw-button-router"},{"RequesterPid",Process.GetCurrentProcess().Id}}));
                    bool ack=false; var end=DateTime.UtcNow.AddSeconds(3);
                    while(DateTime.UtcNow<end && HelperRunning(p.Id)) {
                        try { var a=Parse(File.ReadAllText(ackPath)); ack=Equals(Get(a,"Token"),token) && Convert.ToInt32(Get(a,"HelperPid"))==p.Id; } catch { }
                        if(ack)break; Thread.Sleep(80);
                    }
                    end=DateTime.UtcNow.AddSeconds(6);while(DateTime.UtcNow<end && HelperRunning(p.Id))Thread.Sleep(80);
                    if(HelperRunning(p.Id)) throw new InvalidOperationException("ClawTweaks 未正常退出；不会强制结束进程");
                    Log("helper-stopped","正常退出，PID="+p.Id+", ack="+ack);
                } finally {
                    DeleteOwned(requestPath,token,"Token"); DeleteOwned(ackPath,token,"Token"); p.Dispose();
                }
            }
            Thread.Sleep(200);
            if(Helpers().Length != 0) throw new InvalidOperationException("ClawTweaks helper 仍在运行或被重新启动");
            var settle=DateTime.UtcNow.AddSeconds(4);
            while(Native.ControllerCount()<countBefore && DateTime.UtcNow<settle)Thread.Sleep(100);
            if(Native.ControllerCount()<countBefore) throw new InvalidOperationException("停用后可见手柄减少，已取消接管并恢复 ClawTweaks");
        }
        void DeleteOwned(string path,string token,string key) { try { if(File.Exists(path) && Equals(Get(Parse(File.ReadAllText(path)),key),token)) File.Delete(path); } catch { } }
        void Restore() {
            if(gameBarGuard!=null)gameBarGuard.SetEnabled(false);
            if(!restoreHelper || diagnostic)return;
            try {
                if(Helpers().Length==0) {
                    using(var p=Process.Start(new ProcessStartInfo("schtasks.exe","/Run /TN \"\\ClawTweaks\\ClawTweaksHelper\"") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})) {
                        if(!p.WaitForExit(4000)) throw new InvalidOperationException("恢复任务未响应");
                        if(p.ExitCode!=0) throw new InvalidOperationException("恢复 ClawTweaks 任务失败："+p.StandardError.ReadToEnd().Trim());
                    }
                    var end=DateTime.UtcNow.AddSeconds(5); while(DateTime.UtcNow<end && Helpers().Length==0) Thread.Sleep(100);
                    if(Helpers().Length==0) throw new InvalidOperationException("ClawTweaks 尚未恢复；恢复记录已保留");
                }
                restoreHelper=false; Journal(); Log("helper-restored","原后台已恢复");
            } catch(Exception ex) { error=ex.Message; phase="需要恢复 ClawTweaks"; Log("restore-failed",ex.Message); }
        }
        void OnButton(object sender, EventArrivedEventArgs args) {
            int raw; try { raw=Convert.ToInt32(args.NewEvent.Properties["MSIEvt"].Value); } catch { return; }
            int code=raw & 255; if(code != 41 && code != 88 && code != 42)return;
            // Timestamp before dispatch: queued work must not turn one burst into multiple presses.
            long now=Stopwatch.GetTimestamp();
            lock(lastButtons) { long before; if(lastButtons.TryGetValue(code,out before) && (now-before)*1000.0/Stopwatch.Frequency < 80)return; lastButtons[code]=now; }
            if(code==88)gamebarShortcuts.QuickSettings(now*1000.0/Stopwatch.Frequency);
            Task.Run((Action)delegate { lock(gate) {
                if(disposed)return; observed++;
                Emit(new Dictionary<string,object>{{"type","button"},{"button",code==41?"MSI":"QuickSettings"},{"rawCode",raw},{"source","MSI_Event"}});
                if(diagnostic || maintenance!=null || suspended)return;
                if((Stopwatch.GetTimestamp()-now)*1000.0/Stopwatch.Frequency>700) { Log("button-expired","过渡期间的旧按压已丢弃");return; }
                if(code==88 || code==42) {
                    if(specialKeys.Consume(code,now*1000.0/Stopwatch.Frequency)){Log("special-key-correlated","delayed WMI "+code+" already handled by keyboard path");return;}
                    if(sessionGameMode)EmitMapping("QS",new Dictionary<string,object>{{"type","system"},{"action",code==42?"windowSwitcher":"controlCenter"}},"general");
                    else if(code==42)EmitMapping("QS",new Dictionary<string,object>{{"type","system"},{"action","gamebar"}},"general");
                    else Emit(new Dictionary<string,object>{{"type","desktop_gamebar_input"}});
                    return;
                }
                if(ReadMode()=="Unknown" && steamActive) {
                    var steam=Process.GetProcessesByName("steam");
                    if(steam.Length==0)steamActive=false;
                    foreach(var process in steam)process.Dispose();
                }
                mode=ReadMode(); Reconcile();
                string source=code==41?"Claw":"QS";
                if(GeneralWanted() && released) {
                    if(Helpers().Length>0) {generalFailed=true;released=false;Restore();error="原按键后台已重新启动，常规映射已暂停";Status();return;}
                    // QS already emits a native Win+G. Windows can activate Game Bar
                    // independently of the low-level hook, before this delayed WMI
                    // report. Let that original shortcut toggle it exactly once.
                    if(source=="QS" && Policy.IsGameBarCommand(Get(generalMappings,source))) {
                        routed++;Log("native-gamebar","QS 使用固件原生 Win+G，不重复发送开关指令");Status();return;
                    }
                    EmitMapping(source,Get(generalMappings,source),"general");return;
                }
                if(!Policy.CanRoute(enabled,ready,mode,released)) {Status();return;}
                if(Helpers().Length>0) {released=false;enabled=false;if(shortcutFilter!=null)shortcutFilter.SetEnabled(false);Restore();error="ClawTweaks 已重新启动";Status();return;}
                string action=code==41?msiAction:qamAction;
                var binding=Get(steamBindings,source) as Dictionary<string,object>;
                if(binding!=null) {
                    string menu=Equals(Get(binding,"type"),"system")?Get(binding,"action") as string:null;
                    if(menu=="steamMainMenu")action="MainMenu";
                    else if(menu=="steamQuickAccess")action="QuickAccess";
                    else {EmitMapping(source,binding,"steam");return;}
                }
                if(action=="None")return;
                if(action=="MainMenu" || action=="QuickAccess") {
                    gamebarPermission.Clear();
                    if(gameBarGuard!=null)gameBarGuard.SetEnabled(!AllowsConfiguredGamebar());
                }
                string id=Guid.NewGuid().ToString("N");
                pendingInputs[id]=new KeyValuePair<long,int>(now,code);
                pending[id]=new System.Threading.Timer(delegate { Task.Run((Action)delegate { lock(gate) { if(disposed || !pending.ContainsKey(id))return; ClearPending();enabled=false;released=false;if(shortcutFilter!=null)shortcutFilter.SetEnabled(false);error="Steam 菜单未确认响应，已恢复 ClawTweaks";Restore();Status(); } }); },null,5000,Timeout.Infinite);
                routed++; Emit(new Dictionary<string,object>{{"type","action"},{"id",id},{"action",action},{"button",code==41?"MSI":"QuickSettings"},{"nativeMs",(Stopwatch.GetTimestamp()-now)*1000.0/Stopwatch.Frequency}});
            } });
        }
        public void Command(string raw) { lock(gate) {
            if(disposed)return;
            try {
                var c=Parse(raw); string kind=Get(c,"command") as string;
                if(kind=="begin_maintenance") {
                    string id=Get(c,"id") as string;
                    bool ok=false;string reason=null;
                    try {
                        if(diagnostic || id==null || id.Length!=32 || maintenance!=null)throw new InvalidOperationException("当前不能开始维护");
                        maintenance=id;ClearPending();if(shortcutFilter!=null)shortcutFilter.SetEnabled(false);if(gameBarGuard!=null)gameBarGuard.SetEnabled(false);
                        StopTweaks();released=false;
                        maintenanceTimeout=new System.Threading.Timer(delegate { lock(gate) { if(maintenance==id) {maintenance=null;Reconcile();Status();} } },null,120000,Timeout.Infinite);
                        ok=true;
                    } catch(Exception ex) {reason=ex.Message;maintenance=null;Restore();Reconcile();}
                    Emit(new Dictionary<string,object>{{"type","maintenance"},{"id",id},{"ok",ok},{"error",reason}});
                } else if(kind=="end_maintenance") {
                    string id=Get(c,"id") as string;bool ok=id!=null&&maintenance==id;
                    if(ok) {maintenance=null;if(maintenanceTimeout!=null){maintenanceTimeout.Dispose();maintenanceTimeout=null;}mode=ReadMode();Reconcile();Status();}
                    Emit(new Dictionary<string,object>{{"type","maintenance"},{"id",id},{"ok",ok}});
                } else if(kind=="configure") {
                    configured=true;
                    enabled=!diagnostic && Equals(Get(c,"enabled"),true); ready=Equals(Get(c,"ready"),true);
                    if(!ready || Equals(Get(c,"resetSteamContext"),true))steamKnown=false;
                    msiAction=Policy.Action(Get(c,"msiAction"));qamAction=Policy.Action(Get(c,"qamAction"));
                    generalMappings=Get(c,"buttonMappings") as Dictionary<string,object> ?? new Dictionary<string,object>();
                    steamBindings=Get(c,"steamBindings") as Dictionary<string,object> ?? new Dictionary<string,object>();
                    generalFailed=false;
                    mode=ReadMode();Reconcile();Status();
                } else if(kind=="allow_gamebar") {
                    gamebarPermission.EnterContext(mode);
                    gamebarPermission.Allow();
                    if(gameBarGuard!=null)gameBarGuard.SetEnabled(false);
                    Emit(new Dictionary<string,object>{{"type","mapping_permission"},{"id",Get(c,"id")}});
                } else if(kind=="steam_context") {
                    if(!(Get(c,"active") is bool))throw new InvalidDataException("Steam 状态格式错误");
                    steamKnown=ready;steamActive=Equals(Get(c,"active"),true);steamReport=Stopwatch.GetTimestamp();
                    string current=ReadMode();
                    if(current!=mode) {mode=current;Reconcile();Status();}
                } else if(kind=="result") {
                    string id=Get(c,"id") as string;
                    if(id != null && pending.ContainsKey(id)) {
                        pending[id].Dispose();pending.Remove(id);
                        var input=pendingInputs[id];pendingInputs.Remove(id);
                        var timings=Get(c,"timings") as Dictionary<string,object>;
                        lastTimings=new Dictionary<string,object>{{"button",input.Value==41?"MSI":"Xbox"},{"responseMs",(Stopwatch.GetTimestamp()-input.Key)*1000.0/Stopwatch.Frequency},{"nativeMs",Get(timings,"nativeMs")},{"bridgeMs",Get(timings,"bridgeMs")},{"menuMs",Get(timings,"menuMs")}};
                        bool ok=Equals(Get(c,"ok"),true); string path=Get(c,"path") as string, reason=Get(c,"reason") as string;
                        Log("menu-result",(ok?"菜单状态已确认":"菜单执行失败")+", path="+path+", timings="+json.Serialize(lastTimings)+(String.IsNullOrEmpty(reason)?"":", "+reason));
                        // One slow or blocked menu (Steam screensaver, a busy overlay) must not drop the mapping;
                        // the frontend toasts it. Only repeated failures hand the buttons back to ClawTweaks.
                        if(ok) menuFailures=0;
                        else if(++menuFailures>=3) {menuFailures=0;ClearPending();enabled=false;released=false;if(shortcutFilter!=null)shortcutFilter.SetEnabled(false);error="菜单连续 3 次操作失败，已恢复 ClawTweaks"+(String.IsNullOrEmpty(reason)?"":"："+reason);Restore();}
                        Status();
                    }
                } else if(kind=="status") {mode=ReadMode();Reconcile();Status();}
                else if(kind=="stop") { Application.Exit(); }
            } catch(Exception ex) {error=ex.Message;Status();}
        } }
        public bool TestHandoff() { lock(gate) {
            bool ok=false;
            try {
                if(mode=="BigPicture" || released)throw new InvalidOperationException("交接测试要求先退出 Steam 大屏幕");
                int before=Native.ControllerCount();StopTweaks();int after=Native.ControllerCount();
                ok=after>=before && Helpers().Length==0;
                Emit(new Dictionary<string,object>{{"type","handoff-test"},{"ok",ok},{"controllersBefore",before},{"controllersAfter",after},{"helperStopped",Helpers().Length==0}});
            } catch(Exception ex) { error=ex.Message;Log("handoff-test-failed",ex.ToString());Emit(new Dictionary<string,object>{{"type","handoff-test"},{"ok",false},{"error",ex.Message}}); }
            finally {Restore();Status();}
            return ok && !restoreHelper;
        } }
        public void Dispose() { lock(gate) {
            if(maintenanceTimeout!=null)maintenanceTimeout.Dispose();maintenance=null;
            if(disposed)return;disposed=true;enabled=false;ready=false;released=false;
            if(gameBarGuard!=null){gameBarGuard.Dispose();gameBarGuard=null;}
            if(shortcutFilter!=null){shortcutFilter.Dispose();shortcutFilter=null;}
            if(sessionMode!=null){sessionMode.Dispose();sessionMode=null;}
            ClearPending();
            SystemEvents.PowerModeChanged-=OnPower;
            if(files != null)files.Dispose();if(changed != null)changed.Dispose();
            if(steamWatchdog != null)steamWatchdog.Dispose();
            if(buttons != null) {try{buttons.Stop();}catch{}buttons.Dispose();}
            Restore();phase=restoreHelper?"需要恢复 ClawTweaks":"已退出";Status();
        } }
    }
    public static class Native {
        delegate bool EnumWindow(IntPtr window,IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback,IntPtr data);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int length);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        // A fail-closed cold-start fallback only. The existing Steam frontend report
        // remains authoritative whenever it is connected, including minimisation.
        public static bool BigPictureOpen() {
            bool found=false;
            EnumWindows(delegate(IntPtr window,IntPtr unused) {
                if(!IsWindowVisible(window))return true;
                var title=new StringBuilder(256);GetWindowText(window,title,title.Capacity);
                string value=title.ToString();
                if(value!="Steam 大屏幕模式" && value!="Steam Big Picture Mode" && value!="Steam Big Picture")return true;
                uint pid;GetWindowThreadProcessId(window,out pid);
                try {using(var process=Process.GetProcessById((int)pid)) {
                    if(process.ProcessName!="steamwebhelper" && process.ProcessName!="steam")return true;
                    found=true;return false;
                }}catch{return true;}
            },IntPtr.Zero);
            return found;
        }
        [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr handle,out uint code);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern int GetPackageFamilyName(IntPtr handle,ref uint length,StringBuilder name);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateProcess(IntPtr handle,uint code);
        public static bool IsAlive(int pid) {var h=OpenProcess(0x1000,false,pid);if(h==IntPtr.Zero)return Marshal.GetLastWin32Error()!=87;try{uint code;return !GetExitCodeProcess(h,out code)||code==259;}finally{CloseHandle(h);}}
        public static string PackageFamily(int pid) {var h=OpenProcess(0x1000,false,pid);if(h==IntPtr.Zero)return null;try{uint n=0;GetPackageFamilyName(h,ref n,null);if(n==0 || n>1024)return null;var b=new StringBuilder((int)n);return GetPackageFamilyName(h,ref n,b)==0?b.ToString():null;}finally{CloseHandle(h);}}
        public static void CloseWidget(int pid) {if(PackageFamily(pid)!="MSIClaw.ClawTweaks_7eszav2039cvc")throw new InvalidOperationException("组件包名不匹配，未关闭");var h=OpenProcess(1,false,pid);if(h==IntPtr.Zero)throw new InvalidOperationException("无法关闭 ClawTweaks 组件界面");try{if(!TerminateProcess(h,0))throw new InvalidOperationException("ClawTweaks 组件界面关闭失败");}finally{CloseHandle(h);}}
        [StructLayout(LayoutKind.Sequential)] struct XState { public uint Packet;public ushort Buttons;public byte LeftTrigger,RightTrigger;public short LX,LY,RX,RY; }
        [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint index,out XState state);
        public static int ControllerCount() {int count=0;try{for(uint i=0;i<4;i++){XState s;if(XInputGetState(i,out s)==0)count++;}}catch{}return count;}
    }
    public static class Program {
        [StructLayout(LayoutKind.Sequential)] struct QueueMessage { public IntPtr Window;public uint Message;public UIntPtr WParam;public IntPtr LParam;public uint Time;public int X,Y;public uint Private; }
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool PeekMessage(out QueueMessage message,IntPtr window,uint first,uint last,uint flags);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread,uint message,UIntPtr wp,IntPtr lp);
        [STAThread] public static int Main(string[] args) {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
            string data=args.Length>0?args[0]:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data");
            string config=args.Length>1?args[1]:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"GamingMode");
            bool diagnostic=Array.IndexOf(args,"--diagnose")>=0;
            bool created; using(var mutex=new Mutex(true,"Local\\ClawPlayhubButtonRouter",out created)) {
                if(!created){Console.WriteLine("{\"type\":\"error\",\"error\":\"按键工具已在运行\"}");return 2;}
                using(var router=new Router(data,config,diagnostic)) {
                    Process owner=null;
                    try {
                        // Bind this listener to its agent. Inherited stdin handles
                        // can outlive the original writer, so EOF alone is insufficient.
                        uint uiThread=GetCurrentThreadId();QueueMessage queued;
                        PeekMessage(out queued,IntPtr.Zero,0,0,0);
                        foreach(string argument in args) if(argument.StartsWith("--owner-pid=",StringComparison.Ordinal)) {
                            int ownerId;
                            if(owner!=null || !Int32.TryParse(argument.Substring(12),out ownerId) || ownerId<=0 || ownerId==Process.GetCurrentProcess().Id)
                                throw new InvalidDataException("按键后台所属进程参数无效");
                            owner=Process.GetProcessById(ownerId);
                            IntPtr ownerHandle=owner.Handle;
                            if(owner.HasExited)throw new InvalidOperationException("按键后台所属进程已经退出");
                        }
                        if(owner!=null) {
                            Process trackedOwner=owner;
                            Task.Run((Action)delegate {try {trackedOwner.WaitForExit();PostThreadMessage(uiThread,0x12,UIntPtr.Zero,IntPtr.Zero);}catch(ObjectDisposedException){}catch(InvalidOperationException){}});
                        }
                        router.Start();
                        if(Array.IndexOf(args,"--handoff-test")>=0)return router.TestHandoff()?0:3;
                        Task.Run((Action)delegate {string line;while((line=Console.ReadLine())!=null)router.Command(line);PostThreadMessage(uiThread,0x12,UIntPtr.Zero,IntPtr.Zero);});
                        Application.Run();
                    }catch(Exception ex){router.Emit(new Dictionary<string,object>{{"type","error"},{"error",ex.Message}});return 1;}
                    finally {if(owner!=null)owner.Dispose();}
                }
            }
            return 0;
        }
    }
}
