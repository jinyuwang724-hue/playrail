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
            ToggleControlCenter(); await Still("cc"); ToggleControlCenter();
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
