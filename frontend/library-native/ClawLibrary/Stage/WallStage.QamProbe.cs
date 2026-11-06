// 快捷面板的录屏自测（--state=recqam [--qsgame=游戏名]）：在游戏库里打开面板，原分辨率静帧（默认 / 选项行焦点 / 网格提示 /
// 电源展开 / 退出确认），再真速录一段：进场、光标在行间走、改值、LB/LT 一键调、进网格、电源展开收回、B 收起。
// 每一步的焦点写进日志（手柄导航日志），帧存到 %LOCALAPPDATA%\ClawLibrary\rec\qam-*。
using ClawLibrary.Data;
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    /// <summary>
    /// --state=qampress：选项行连按自测。每行（帧率上限、性能显示、震动）→ 连按 10 次（间隔 90 ms，比手柄连发还快），
    /// 每按一次记界面上的值；停 4 s 等写入和回读，记界面值和后台真实值，两者应等于第 10 次的值；最后按回原值。
    /// </summary>
    /// <summary>自测输入（LibraryWindow 收到 wp 100+ 的注册消息）：0–3 上下左右、4 A、5 B、6/7 LB/RB、8/9 LT/RT、10 记状态。</summary>
    internal void TestInput(string code, nint lp = 0)
    {
        if (code == "12")
        {
            // 切换器里把焦点放到 lp 那个窗口的卡上（之后再发 A，走真实的切换路径）
            int i = _wsOpen ? _wsCards.FindIndex(c => c.Win.Hwnd == lp) : -1;
            if (i >= 0) WsSetFocus(i);
            Log.Info($"test input 12 -> switcher open={_wsOpen} cards={_wsCards.Count} focus={(i >= 0 ? _wsCards[i].Win.Process : "none")}");
            return;
        }
        switch (code)
        {
            case "0": Move(Direction.Up); break;
            case "1": Move(Direction.Down); break;
            case "2": Move(Direction.Left); break;
            case "3": Move(Direction.Right); break;
            case "4": Activate(); break;
            case "5": Back(); break;
            case "11": _qsTestNoExclude = true; Log.Info("test: panel stays visible to screen capture"); return;
        }
        Log.Info($"test input {code} -> {(_qsOpen ? QsFocusTest + " " + string.Join(" ", QsRowIds.Select(id => id + "=" + QuickPanelTestValue(id))) : "panel closed")}");
    }
    internal async Task QamPressAsync()
    {
        await Task.Delay(2500);
        OpenQuickPanel(0);
        await Task.Delay(2500);
        _agent ??= new AgentClient();
        async Task<string> Backend(string id)
        {
            var s = await _agent.Call("widget_state") as System.Text.Json.Nodes.JsonObject;
            return id switch
            {
                "fps" => $"{(double?)s?["system"]?["fpsLimit"] ?? -1:0}",
                "osd" => $"{s?["power"]?["settings"]?["osd"]?["enabled"]}/{s?["power"]?["settings"]?["osd"]?["level"]}",
                "rumble" => $"{s?["rumble"]?["strength"]}",
                _ => "?",
            };
        }
        foreach (var id in new[] { "fps", "osd", "rumble" })
        {
            QsSetFocus(id);
            string start = QuickPanelTestValue(id), startBackend = await Backend(id);
            Log.Info($"qampress {id} start ui={start} backend={startBackend}");
            var seen = new List<string>();
            for (int i = 0; i < 10; i++) { Move(Direction.Right); seen.Add(QuickPanelTestValue(id)); await Task.Delay(90); }
            Log.Info($"qampress {id} presses: {string.Join(" → ", seen)}");
            string want = seen[^1];
            var trail = new List<string>();
            for (int i = 0; i < 16; i++) { await Task.Delay(250); trail.Add(QuickPanelTestValue(id)); }
            string ui = QuickPanelTestValue(id), backend = await Backend(id);
            bool jumped = trail.Any(x => x != want);
            Log.Info($"qampress {id} after 4 s ui={ui} backend={backend} expected={want} ui-trail-stable={!jumped} [{string.Join(",", trail.Distinct())}] {(ui == want && !jumped ? "PASS" : "FAIL")}");
            // 按回原值
            for (int i = 0; i < 12 && QuickPanelTestValue(id) != start; i++) { Move(Direction.Right); await Task.Delay(150); }
            await Task.Delay(1500);
            Log.Info($"qampress {id} restored ui={QuickPanelTestValue(id)} backend={await Backend(id)}");
        }
        CloseQuickPanel(hideWindow: false, instant: true);
        Log.Info("qampress done");
    }

    /// <summary>--state=qamhalf：面板停在半开（左移 200）8 秒，外面截屏看面板窗口没画到的地方是不是透明。</summary>
    internal async Task QamHalfAsync()
    {
        await Task.Delay(2500);
        OpenQuickPanel(0);
        await Task.Delay(1500);
        ++_qsSlideGen;
        _qsSlide.StopAnimation("Offset");
        _qsSlide.Offset = new System.Numerics.Vector3(-200 * QsK, 0, 0);
        Log.Info("qamhalf holding");
        await Task.Delay(8000);
        CloseQuickPanel(hideWindow: false, instant: true);
        Log.Info("qamhalf done");
    }
    internal async Task RecQamAsync()
    {
        await Task.Delay(2500);
        _qsTestGame = Program.Args.FirstOrDefault(a => a.StartsWith("--qsgame="))?[9..];
        string root = Path.Combine(App.CacheDir, "rec");
        async Task Still(string name)
        {
            await Task.Delay(900);
            var r = new FrameRecorder { Full = true, Display = true };
            if (!r.Start(HostWindow)) return;
            await Task.Delay(300);
            int n = await r.StopAndSaveAsync(Path.Combine(root, "qam-" + name), System.Diagnostics.Stopwatch.GetTimestamp());
            Log.Info($"recqam still {name}: {n} frames focus={QsFocusTest}");
        }
        void Do(string what, Action a) { a(); Log.Info($"recqam {what} -> {QsFocusTest}"); }
        try
        {
            OpenQuickPanel(0);
            await Task.Delay(1500);
            await Still("default");
            Do("down", () => Move(Direction.Down)); Do("down", () => Move(Direction.Down)); Do("down", () => Move(Direction.Down));
            await Still("row");
            for (int i = 0; i < 5; i++) Do("down", () => Move(Direction.Down));
            Do("down", () => Move(Direction.Down));
            await Still("grid");
            for (int i = 0; i < 4; i++) Do("down", () => Move(Direction.Down));
            Do("left", () => Move(Direction.Left)); Do("left", () => Move(Direction.Left));
            Log.Info($"recqam before pwr {QsFocusTest}");
            QuickPanelTestState("pwr");
            Do("A pwr", Activate);
            await Still("power");
            Do("B", Back);
            Do("right x3", () => { Move(Direction.Right); Move(Direction.Right); Move(Direction.Right); });
            Do("A quit", Activate);
            await Still("quit-armed");
            Do("B", Back);
            CloseQuickPanel(hideWindow: false, instant: true);
            await Task.Delay(800);

            // 真速录一段
            var rec = new FrameRecorder { Display = true };
            if (rec.Start(HostWindow))
            {
                await Task.Delay(300);
                long mark = System.Diagnostics.Stopwatch.GetTimestamp();
                async Task Step(int ms, string what, Action a)
                {
                    try { a(); } catch (Exception ex) { Log.Error($"recqam {what}", ex); }
                    Log.Info($"recqam clip {what} @{FrameRecorder.QpcMs(System.Diagnostics.Stopwatch.GetTimestamp() - mark):F0}ms -> {QsFocusTest}");
                    await Task.Delay(ms);
                }
                await Step(900, "open", () => OpenQuickPanel(0));
                await Step(350, "down", () => Move(Direction.Down));
                await Step(350, "down", () => Move(Direction.Down));
                await Step(120, "down", () => Move(Direction.Down));
                await Step(400, "up (interrupt)", () => Move(Direction.Up));
                await Step(300, "right (volume +5)", () => Move(Direction.Right));
                await Step(500, "left (volume -5)", () => Move(Direction.Left));
                await Step(250, "LB", () => QsHot("bri", -1));
                await Step(250, "RB", () => QsHot("bri", 1));
                await Step(700, "LT", () => QsHot("vol", -1));
                await Step(250, "RT", () => QsHot("vol", 1));
                for (int i = 0; i < 9; i++) await Step(160, "down", () => Move(Direction.Down));   // 走到网格，不在选项行上按 ←→（不改真实设置）
                await Step(400, "right", () => Move(Direction.Right));
                await Step(400, "down", () => Move(Direction.Down));
                await Step(400, "down", () => Move(Direction.Down));
                await Step(400, "left", () => Move(Direction.Left));
                await Step(700, "A pwr", Activate);
                await Step(400, "right", () => Move(Direction.Right));
                await Step(700, "B", Back);
                await Step(700, "B", Back);
                await Step(250, "reopen (interrupt)", () => OpenQuickPanel(0));
                await Step(900, "B", Back);
                string dir = Path.Combine(root, "qam-clip");
                int n = await rec.StopAndSaveAsync(dir, mark);
                Log.Info($"recqam clip: {n} frames");
                try { await Bench.EncodeMp4(dir, Path.Combine(root, "qam-clip.mp4")); } catch (Exception ex) { Log.Error("recqam mp4", ex); }
            }
        }
        catch (Exception ex) { Log.Error("recqam", ex); }
        Log.Info("recqam done");
    }
}
