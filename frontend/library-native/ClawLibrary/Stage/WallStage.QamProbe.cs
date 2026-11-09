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
#if DEBUG
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint QamProbeSendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>截图和导航回归使用固定数据，不改无线配置，不启动游戏。</summary>
    internal async Task QamWirelessProbeAsync()
    {
        await Task.Delay(1800);
        _qsTestNoExclude = true;
        OpenQuickPanel(0);
        _qsPoll?.Stop();
        await Task.Delay(2200);

        async Task Still(string name)
        {
            await Task.Delay(700);
            Log.Info($"qamwireless state={name} focus={_networkFocus} keyboard={_networkKeyboard}");
            await Task.Delay(2300);
        }

        void Tap(double x, double y)
        {
            var point = new Windows.Foundation.Point(x * QsK, y * QsK);
            QsOwnPress(point);
            QsOwnRelease(point);
        }

        _qsWifi = false;
        QsSetFocus("wifi");
        QsMove(Direction.Right);
        if (_networkPage != null || _qsFocusId != "bt") throw new InvalidOperationException("Right did not move between tiles");
        var wifi = QsFind("wifi")!;
        Tap(wifi.X + wifi.W / 2, wifi.Y + wifi.H / 2);
        if (_networkPage != "wifi") throw new InvalidOperationException("Wi-Fi tap did not open the list");
        _qsWifi = true;
        _networkEntries.Add(new("home5", "Home-5G", "已连接 · 5 GHz", "qam.wifi", true, true, Signal: 4, Secured: true));
        _networkEntries.Add(new("home24", "Home-2.4G", "已保存", "qam.wifi", true, false, Signal: 4, Secured: true));
        var locked = new QamWireless.Entry("china", "ChinaNet-7F2A", "", "qam.wifi", false, false, Signal: 3, Secured: true);
        _networkEntries.Add(locked);
        _networkEntries.Add(new("coffee", "Starbucks", "开放网络", "qam.wifi", false, false, Signal: 2));
        NetworkPaint();
        QsMove(Direction.Down);
        await Still("wifi-list");
        var lockedBounds = _networkHits.Single(h => h.Id == locked.Id).Bounds;
        Tap(lockedBounds.X + 40, lockedBounds.Y + 22);
        nint keyboardWindow = _qsWin!.Hwnd;
        if (QamProbeSendMessage(keyboardWindow, 0x024B, 0, keyboardWindow) != 3)
            throw new InvalidOperationException("QAM window must answer WM_POINTERACTIVATE with PA_NOACTIVATE");
        foreach (string key in new[] { "q", "a", "m" })
        {
            var bounds = _networkKeys.Single(h => h.Id == key).Bounds;
            Tap(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        }
        if (!_qsOpen || !_networkKeyboard || _networkPassword != "qam")
            throw new InvalidOperationException("Touch typing did not keep the QAM and keyboard open");
        await Still("wifi-password");
        QsBack();
        if (_networkKeyboard || _networkExpand != locked.Id) throw new InvalidOperationException("B did not close only the keyboard");
        QsBack();
        if (_networkExpand != null || _networkPage != "wifi") throw new InvalidOperationException("B did not collapse only the row");
        QsBack();
        if (_networkPage != null || _qsFocusId != "wifi") throw new InvalidOperationException("B did not restore the Wi-Fi tile");

        _qsBt = false;
        QsSetFocus("bt");
        QsActivate();
        if (_networkPage != "bt") throw new InvalidOperationException("Bluetooth activation did not open the list");
        _qsBt = true;
        _networkEntries.Add(new("xbox", "Xbox 无线控制器", "已连接", "controller", true, true));
        _networkBatteries["xbox"] = 80;
        _networkEntries.Add(new("pods", "AirPods Pro", "未连接", "headphones", true, false));
        _networkEntries.Add(new("mouse", "MX Master 3S", "未连接", "mouse", true, false));
        _networkEntries.Add(new("dualsense", "DualSense 无线控制器", "", "controller", false, false));
        _networkEntries.Add(new("buds", "Redmi Buds 5", "", "headphones", false, false));
        _networkExpand = "dualsense";
        _networkPin = "482915";
        _networkFocus = "connect";
        NetworkStartExpansion();
        NetworkPaint();
        await Still("bt-pin");
        QsBack(); QsBack();
        if (_networkPage != null || _qsFocusId != "bt") throw new InvalidOperationException("B did not restore the Bluetooth tile");
        var recorder = new FrameRecorder { Display = true };
        if (recorder.Start(HostWindow))
        {
            await Task.Delay(250);
            long mark = System.Diagnostics.Stopwatch.GetTimestamp();
            void OpenMockWifi()
            {
                _qsWifi = false;
                QsSetFocus("wifi");
                QsActivate();
                _qsWifi = true;
                _networkEntries.Add(new("home5", "Home-5G", "已连接 · 5 GHz", "qam.wifi", true, true, Signal: 4, Secured: true));
                _networkEntries.Add(locked);
                NetworkPaint();
            }
            OpenMockWifi();
            await Task.Delay(600);
            QsBack();
            await Task.Delay(160);
            OpenMockWifi();
            await Task.Delay(160);
            QsBack();
            await Task.Delay(600);
            string dir = Path.Combine(App.CacheDir, "rec", "qam-wireless");
            int frames = await recorder.StopAndSaveAsync(dir, mark);
            Log.Info($"qamwireless recording frames={frames}");
            if (frames > 0) await Bench.EncodeMp4(dir, Path.Combine(App.CacheDir, "rec", "qam-wireless.mp4"));
        }
        CloseQuickPanel(hideWindow: false, instant: true);
        Log.Info("qamwireless PASS");
    }
#endif

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
            Do("focus quit", () => QsSetFocus("quit"));
            await Still("quit-focus");
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
                await Step(400, "focus bluetooth", () => QsSetFocus("bt"));
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
