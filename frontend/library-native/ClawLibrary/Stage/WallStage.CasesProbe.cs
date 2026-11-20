// 卡盒版的录屏自测（--state=reccases）：首页展架的进场和翻页（含中途打断）、信息区按钮、切到分类页走一圈、回首页、
// 切到叠放翻几张、插卡（只放启动画面，不真的启动游戏）后按 B 退卡，最后切回原来的首页样式。
// 每段用 FrameRecorder 录本窗口合成出来的每一帧，存到 rec\cases-*，再拼成 mp4。
using ClawLibrary.Data;
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    /// <summary>录屏时插卡不真的启动游戏：插到底只放启动画面。</summary>
    private bool _csDemo;

    /// <summary>--state=recrow：「单行」首页静帧（原分辨率 png）+ 左右翻几张的录屏（看弹簧、放大和焦点框流光）。</summary>
    internal async Task RecRowAsync()
    {
        await Task.Delay(2500);
        var style = _prefs.Home;
        string dir = Path.Combine(App.CacheDir, "rec");
        async Task Still(string name)
        {
            await Task.Delay(1500);
            var r = new FrameRecorder { Full = true };
            if (!r.Start(HostWindow)) return;
            await Task.Delay(300);
            int n = await r.StopAndSaveAsync(Path.Combine(dir, "row-" + name), System.Diagnostics.Stopwatch.GetTimestamp());
            Log.Info($"recrow {name}: {n} frames");
        }
        async Task Rec(string name, Action act, int ms)
        {
            var r = new FrameRecorder();
            if (!r.Start(HostWindow)) { act(); await Task.Delay(ms); return; }
            long m = System.Diagnostics.Stopwatch.GetTimestamp();
            await Task.Delay(120);
            act();
            await Task.Delay(ms);
            Log.Info($"recrow {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "row-" + name), m)} frames");
        }
        try
        {
            if (_cs != null && _prefs.Home != HomeStyle.Row) { SaveUi(_prefs with { Home = HomeStyle.Row }); CsMorphStyle(); await Task.Delay(1500); }
            await Still("home");
            var rec = new FrameRecorder();
            if (rec.Start(HostWindow))
            {
                long mark = System.Diagnostics.Stopwatch.GetTimestamp();
                await Task.Delay(400);
                foreach (var d in new[] { Direction.Right, Direction.Right, Direction.Right, Direction.Right, Direction.Left, Direction.Left })
                { Move(d); await Task.Delay(650); }
                Move(Direction.Right); await Task.Delay(120); Move(Direction.Right); await Task.Delay(1200);   // 连按打断
                int n = await rec.StopAndSaveAsync(Path.Combine(dir, "row-move"), mark);
                Log.Info($"recrow move: {n} frames");
            }
            await Still("after");
            // 左上面板：↑ 进面板，→ 到 Steam，A 切过去；网格里走几步；B 回首页
            Move(Direction.Up); await Still("panel");
            Move(Direction.Right); await Task.Delay(300);
            var sw = new FrameRecorder();
            if (sw.Start(HostWindow))
            {
                long m2 = System.Diagnostics.Stopwatch.GetTimestamp();
                Activate(); await Task.Delay(900);
                Log.Info($"recrow switch: {await sw.StopAndSaveAsync(Path.Combine(dir, "row-switch"), m2)} frames");
            }
            else { Activate(); await Task.Delay(1200); }
            await Still("steam");
            Move(Direction.Down); await Task.Delay(400); Move(Direction.Down); await Task.Delay(400);
            Move(Direction.Right); await Task.Delay(400); Move(Direction.Right); await Still("steam-grid");
            Back(); await Task.Delay(1200); await Still("back-home");
            // 游戏菜单：Y 打开，往下两行，B 关
            // 开菜单的动画逐帧录下来（和 Cocoon 视频对照）；先往右挪两张，起点在目标右边
            Move(Direction.Right); await Task.Delay(500); Move(Direction.Right); await Task.Delay(800);
            var mo = new FrameRecorder();
            if (mo.Start(HostWindow))
            {
                long m3 = System.Diagnostics.Stopwatch.GetTimestamp();
                await Task.Delay(150);
                CsGameSettings(); await Task.Delay(700);
                Log.Info($"recrow menuopen: {await mo.StopAndSaveAsync(Path.Combine(dir, "row-menuopen"), m3)} frames");
                var mc = new FrameRecorder();
                if (mc.Start(HostWindow))
                {
                    long m4 = System.Diagnostics.Stopwatch.GetTimestamp();
                    await Task.Delay(100);
                    Back(); await Task.Delay(500);
                    Log.Info($"recrow menuclose: {await mc.StopAndSaveAsync(Path.Combine(dir, "row-menuclose"), m4)} frames");
                }
                CsGameSettings(); await Task.Delay(800);
            }
            else CsGameSettings();
            await Still("menu");
            Move(Direction.Down); await Task.Delay(250); Move(Direction.Down); await Still("menu2");
            // 菜单里再按 Y 进编辑；A 进编辑元数据，往下走几行；B 一路退回
            await Rec("editopen", CsGameSettings, 1300); await Still("edit");
            await Rec("metapush", Activate, 900); await Still("meta");
            for (int i = 0; i < 6; i++) { Move(Direction.Down); await Task.Delay(220); }
            await Still("meta2");
            // 退回编辑 → 封面管理：A 到 SteamGridDB 取图，切到背景页签
            await Rec("pop", () => Back(), 800);
            Move(Direction.Down); await Task.Delay(300); Activate(); await Still("art");
            Activate(); await Task.Delay(7000); await Still("art2");
            CsSwitchTab(1); await Still("art3");
            await Rec("artpop", () => Back(), 800);
            // 刮削游戏：只看页面和匹配（不按开始，免得改了用户的资料）
            Move(Direction.Down); await Task.Delay(300); Activate(); await Task.Delay(5000); await Still("scrape");
            for (int i = 0; i < 9; i++) { Move(Direction.Down); await Task.Delay(200); }
            await Still("scrape2");
            {
                // LaunchBox 索引：下载 + 建索引 + 查两款（只读，不写用户资料）
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                bool ok = await LaunchBoxDb.EnsureAsync(m => Log.Info($"recrow lb: {m}"));
                var w = LaunchBoxDb.Find(new[] { "鸣潮", "Wuthering Waves" }, null);
                var h = LaunchBoxDb.Find(new[] { "Hollow Knight" }, "367520");
                Log.Info($"recrow lb ready={ok} {sw2.ElapsedMilliseconds} ms; ww={w?.Name}/{w?.Developer}/{w?.Date}/{string.Join(",", w?.Images.Keys ?? Enumerable.Empty<string>())}; hk={h?.Name}/{h?.Publisher}/{string.Join(",", h?.Images.Keys ?? Enumerable.Empty<string>())}");
            }
            Back(); await Task.Delay(500);
            await Rec("sheetclose", () => Back(), 600); Back(); await Task.Delay(500);
            // 游戏配置：小黄鸭、功耗档位
            if (_rm == null) { CsGameSettings(); await Task.Delay(600); }
            Move(Direction.Down); await Task.Delay(250); Move(Direction.Down); await Task.Delay(250); Activate(); await Task.Delay(2500); await Still("config");
            Activate(); await Still("config-duck");
            Back(); await Task.Delay(400);
            for (int i = 0; i < 3; i++) { Move(Direction.Down); await Task.Delay(250); }
            Activate(); await Still("config-power");
            Back(); await Task.Delay(400); Back(); await Task.Delay(400); Back(); await Task.Delay(600);
        }
        catch (Exception ex) { Log.Error("recrow", ex); }
        if (_cs != null && style != HomeStyle.Row) { SaveUi(_prefs with { Home = style }); CsMorphStyle(); }
        Log.Info("recrow done");
    }

    /// <summary>--state=recstatus：原分辨率静帧核对顶栏 / 状态栏 / 竖条 / 快捷面板的对齐（中英混排基线、电池和数字等高）。</summary>
    internal async Task RecStatusAsync()
    {
        await Task.Delay(2500);
        var style = _prefs.Home;
        async Task Still(string name)
        {
            await Task.Delay(1200);
            var r = new FrameRecorder { Full = true };
            if (!r.Start(HostWindow)) return;
            await Task.Delay(400);
            int n = await r.StopAndSaveAsync(Path.Combine(App.CacheDir, "rec", "status-" + name), System.Diagnostics.Stopwatch.GetTimestamp());
            Log.Info($"recstatus {name}: {n} frames");
        }
        void Style(HomeStyle s) { SaveUi(_prefs with { Home = s }); CsMorphStyle(); }
        try
        {
            if (_cs != null && _prefs.Home != HomeStyle.Shelf) Style(HomeStyle.Shelf);
            await Still("shelf");
            // 电池三种状态（照 Windows 11 任务栏）：充电中 = 闪电、插着电没在充 = 插头、低电量 = 红
            foreach (var (name, b) in new[] { ("charging", new BatteryState(true, 62, true, true)), ("plugged", new BatteryState(true, 100, false, true)), ("low", new BatteryState(true, 9, false, false)) })
            {
                SystemInfo.Override = b;
                _railModel.Battery = b;
                CsPaintStatus();
                await Still("bat-" + name);
                if (name == "charging") { ToggleControlCenter(); await Still("bat-cc-" + name); ToggleControlCenter(); await Task.Delay(800); }
            }
            SystemInfo.Override = null;
            _railModel.Battery = SystemInfo.Battery();
            CsPaintStatus();
            if (_cs != null) { SwitchTab(1); await Still("all"); for (int i = 0; i < 3; i++) { Move(Direction.Up); await Task.Delay(300); } await Still("recent"); Back(); }
            if (_cs != null) { Style(HomeStyle.Wall); await Still("wall"); }
            ToggleControlCenter(); await Still("cc");
            // 游戏模式入口：电源菜单里的「切换到桌面 / 游戏模式」、设置 › 通用的游戏模式开关（包里要有 Payload\shell\ClawShell.exe）
            Log.Info($"recstatus shell mode available={ShellMode.Available}");
            CcPowerShow(true); await Still("cc-power"); CcPowerShow(false);
            _stCat = 0; OpenSettingsPage(); await Still("settings-general"); CloseSettingsPage(true);
            ToggleControlCenter();
            await Task.Delay(800);
            OpenQuickPanel(0); await Still("quick"); CloseQuickPanel(hideWindow: false, instant: true);
        }
        catch (Exception ex) { Log.Error("recstatus", ex); }
        if (_cs != null) { SaveUi(_prefs with { Home = style }); CsMorphStyle(); }
        Log.Info("reccases done");
    }

    internal async Task RecCasesAsync()
    {
        if (_cs == null) { Log.Warn("reccases: cases view off"); return; }
        await Task.Delay(1500);
        var style = _prefs.Home;
        _csDemo = true;
        // --clips=a,b 只录这几段
        var only = Program.Args.FirstOrDefault(a => a.StartsWith("--clips="))?.Substring(8).Split(',');
        async Task Clip(string name, Func<Func<int, string, Action, Task>, Task> body)
        {
            if (only != null && !only.Contains(name)) return;
            var rec = new FrameRecorder();
            if (!rec.Start(HostWindow)) return;
            await Task.Delay(300);
            long mark = System.Diagnostics.Stopwatch.GetTimestamp();
            async Task Step(int ms, string what, Action a)
            {
                try { a(); } catch (Exception ex) { Log.Error($"reccases {what}", ex); }
                Log.Info($"reccases {name} {what} @{FrameRecorder.QpcMs(System.Diagnostics.Stopwatch.GetTimestamp() - mark):F0}ms");
                await Task.Delay(ms);
            }
            await body(Step);
            string dir = Path.Combine(App.CacheDir, "rec", "cases-" + name);
            int n = await rec.StopAndSaveAsync(dir, mark);
            Log.Info($"reccases {name}: {n} frames");
            try { await Bench.EncodeMp4(dir, Path.Combine(App.CacheDir, "rec", $"cases-{name}.mp4")); }
            catch (Exception ex) { Log.Error("reccases mp4", ex); }
        }
        if (style != HomeStyle.Shelf) { SaveUi(_prefs with { Home = HomeStyle.Shelf }); CsMorphStyle(); await Task.Delay(800); }
        await Clip("shelf", async Step =>
        {
            await Step(1300, "entry", () => CsShowTab(0, 0, entry: true));
            await Step(700, "right", () => Move(Direction.Right));
            await Step(700, "right", () => Move(Direction.Right));
            await Step(90, "right", () => Move(Direction.Right));
            await Step(90, "left", () => Move(Direction.Left));
            await Step(800, "left", () => Move(Direction.Left));
            await Step(600, "up", () => Move(Direction.Up));
            await Step(500, "right", () => Move(Direction.Right));
            await Step(700, "down", () => Move(Direction.Down));
        });
        await Clip("tabs", async Step =>
        {
            await Step(1200, "tab all", () => SwitchTab(1));
            await Step(600, "down", () => Move(Direction.Down));
            await Step(600, "right", () => Move(Direction.Right));
            await Step(600, "down", () => Move(Direction.Down));
            await Step(600, "up", () => Move(Direction.Up));
            await Step(800, "up", () => Move(Direction.Up));
            await Step(600, "right", () => Move(Direction.Right));
            await Step(1000, "tab steam", () => SwitchTab(1));
            await Step(150, "tab local", () => SwitchTab(1));
            await Step(1000, "tab steam", () => SwitchTab(-1));
            await Step(1200, "back home", Back);
        });
        await Clip("stack", async Step =>
        {
            await Step(1400, "style stack", () => { SaveUi(_prefs with { Home = HomeStyle.Stack }); CsMorphStyle(); });
            await Step(1300, "entry", () => CsShowTab(0, 0, entry: true));
            // 叠放（11-05）：→ 下一张、← 上一张，第一张再按 ← 回弹；↓ 进按钮，按钮间 ← →，↑ 回牌堆
            await Step(800, "right", () => Move(Direction.Right));
            await Step(800, "right", () => Move(Direction.Right));
            await Step(120, "left", () => Move(Direction.Left));
            await Step(900, "left", () => Move(Direction.Left));
            await Step(700, "left", () => Move(Direction.Left));
            await Step(700, "left at first (bounce)", () => Move(Direction.Left));
            await Step(700, "left at first again (bounce)", () => Move(Direction.Left));
            await Step(600, "down (buttons)", () => Move(Direction.Down));
            for (int i = 0; i < 2; i++) await Step(450, "right (buttons)", () => Move(Direction.Right));
            await Step(450, "left (buttons)", () => Move(Direction.Left));
            await Step(700, "up (pile)", () => Move(Direction.Up));
            // 最右边的按钮上按 → 回牌堆；按钮上按 B 回牌堆
            await Step(600, "down (buttons)", () => Move(Direction.Down));
            for (int i = 0; i < 2; i++) await Step(450, "right (buttons)", () => Move(Direction.Right));
            await Step(700, "right at last button (pile)", () => Move(Direction.Right));
            await Step(600, "down (buttons)", () => Move(Direction.Down));
            await Step(700, "B (pile)", Back);
            await Step(700, "up on pile (nothing)", () => Move(Direction.Up));
            await Step(1400, "style shelf", () => { SaveUi(_prefs with { Home = HomeStyle.Shelf }); CsMorphStyle(); });
        });
        await Clip("wall", async Step =>
        {
            // 海报墙作为首页：三种样式互切，墙上走两步，切到分类页再回来
            await Step(1600, "style wall", () => { SaveUi(_prefs with { Home = HomeStyle.Wall }); CsMorphStyle(); });
            await Step(700, "wall right", () => Move(Direction.Right));
            await Step(900, "wall down", () => Move(Direction.Down));
            await Step(1300, "tab all", () => SwitchTab(1));
            await Step(1300, "back to wall", Back);
            await Step(1600, "style stack", () => { SaveUi(_prefs with { Home = HomeStyle.Stack }); CsMorphStyle(); });
            await Step(1600, "style wall", () => { SaveUi(_prefs with { Home = HomeStyle.Wall }); CsMorphStyle(); });
            await Step(1600, "style shelf", () => { SaveUi(_prefs with { Home = HomeStyle.Shelf }); CsMorphStyle(); });
        });
        await Clip("stand", async Step =>
        {
            // 展台：慢按两下、连按三下（上一段直接做完再开始下一段）、往回一下
            await Step(1600, "style stand", () => { SaveUi(_prefs with { Home = HomeStyle.Stand }); CsMorphStyle(); });
            // 焦点：一直按 → 再一直按 ← 回来，排里的小卡盒不能留下焦点框，一屏只有一个焦点
            for (int i = 0; i < 4; i++) await Step(450, "right (walk)", () => Move(Direction.Right));
            for (int i = 0; i < 4; i++) await Step(450, "left (walk back)", () => Move(Direction.Left));
            await Step(900, "up (buttons)", () => Move(Direction.Up));
            await Step(900, "down (row)", () => Move(Direction.Down));
            await Step(900, "right", () => Move(Direction.Right));
            await Step(900, "right", () => Move(Direction.Right));
            for (int i = 0; i < 3; i++) await Step(140, "right fast", () => Move(Direction.Right));
            await Step(1000, "left", () => Move(Direction.Left));
            await Step(800, "up", () => Move(Direction.Up));
            await Step(800, "down", () => Move(Direction.Down));
            // 切标签再回来、切样式再回来：展台上和排里都不能留下多余的卡盒
            await Step(1200, "tab all", () => SwitchTab(1));
            await Step(1400, "back home", Back);
            await Step(900, "right", () => Move(Direction.Right));
            await Step(1600, "style stack", () => { SaveUi(_prefs with { Home = HomeStyle.Stack }); CsMorphStyle(); });
            await Step(1600, "style stand", () => { SaveUi(_prefs with { Home = HomeStyle.Stand }); CsMorphStyle(); });
            await Step(900, "left", () => Move(Direction.Left));
            await Step(1600, "style shelf", () => { SaveUi(_prefs with { Home = HomeStyle.Shelf }); CsMorphStyle(); });
        });
        await Clip("carts", async Step =>
        {
            // 分类页「最近」一排的新卡带：进最近、左右走两步
            await Step(1300, "tab all", () => SwitchTab(1));
            for (int i = 0; i < 4; i++) await Step(500, "up", () => Move(Direction.Up));
            await Step(800, "right", () => Move(Direction.Right));
            await Step(800, "right", () => Move(Direction.Right));
            await Step(800, "left", () => Move(Direction.Left));
            await Step(1200, "back home", Back);
        });
        await Clip("banner", async Step =>
        {
            // 横幅：游戏库 → 游戏设置（同一张横幅，不跳不闪）→ 返回；分类页 → 游戏设置 → 返回；游戏库 → 控制中心 → 设置；开关切换
            // 先选一款装好的游戏（没装的按 Y 只提示未安装）
            await Step(1500, "select installed", () =>
            {
                if (_cs?.Page is not { Tab: 0 } p) return;
                int j = p.Items.FindIndex(g => g.Installed && g.Platform != "folder");
                if (j >= 0 && j != p.Sel) CsStep(p, j - p.Sel);
            });
            await Step(2200, "Y game settings", PadY);
            await Step(1800, "back to library", Back);
            await Step(1300, "tab all", () => SwitchTab(1));
            await Step(2200, "Y game settings (cat)", PadY);
            await Step(1800, "back to library", Back);
            await Step(1300, "back home", Back);
            await Step(2000, "control center", ToggleControlCenter);
            await Step(2000, "settings page", OpenSettingsPage);
            await Step(1200, "close settings", () => CloseSettingsPage());
            await Step(1600, "close cc", ToggleControlCenter);
            await Step(2000, "banner off", () => SetGameBanner(false));
            await Step(900, "right", () => Move(Direction.Right));
            await Step(1500, "tab all", () => SwitchTab(1));
            await Step(1500, "back home", Back);
            await Step(2200, "Y game settings (off)", PadY);
            await Step(1800, "back to library", Back);
            await Step(2000, "banner on", () => SetGameBanner(true));
        });
        await Clip("saver", async Step =>
        {
            // 屏保：进入、轮换一次（换边）、按 A 唤醒并选中屏幕上那款
            await Step(1500, "home drift", () => { });
            // 测试窗口不在前台（录屏跑在后台任务里）：按探针方式放行前台检查
            await Step(14_500, "enter", () => { _appearanceProbeRunning = true; _screensaverProbe = true; _lastInput = NowMs - 3_600_000; if (!EnterScreensaver()) Log.Warn("reccases: screensaver refused"); });
            await Step(1800, "wake A", () => { Activate(); _appearanceProbeRunning = false; _screensaverProbe = false; });
        });
        await Clip("insert", async Step =>
        {
            await Step(3400, "insert", Activate);
            await Step(2900, "cancel", Back);
            await Step(700, "insert again", Activate);
            await Step(2800, "back mid-way", Back);
        });
        SaveUi(_prefs with { Home = style });
        if (style != HomeStyle.Shelf) CsMorphStyle();
        _csDemo = false;
        Log.Info("reccases done");
    }
}
