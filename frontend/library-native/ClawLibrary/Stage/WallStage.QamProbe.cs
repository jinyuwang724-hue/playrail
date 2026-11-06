// 快捷面板的录屏自测（--state=recqam [--qsgame=游戏名]）：在游戏库里打开面板，原分辨率静帧（默认 / 选项行焦点 / 网格提示 /
// 电源展开 / 退出确认），再真速录一段：进场、光标在行间走、改值、LB/LT 一键调、进网格、电源展开收回、B 收起。
// 每一步的焦点写进日志（手柄导航日志），帧存到 %LOCALAPPDATA%\ClawLibrary\rec\qam-*。
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task RecQamAsync()
    {
        await Task.Delay(2500);
        _qsTestGame = Program.Args.FirstOrDefault(a => a.StartsWith("--qsgame="))?[9..];
        string root = Path.Combine(App.CacheDir, "rec");
        async Task Still(string name)
        {
            await Task.Delay(900);
            var r = new FrameRecorder { Full = true };
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
            var rec = new FrameRecorder();
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
                await Step(300, "right", () => Move(Direction.Right));
                await Step(500, "left", () => Move(Direction.Left));
                await Step(250, "LB", () => QsHot("bri", -1));
                await Step(250, "RB", () => QsHot("bri", 1));
                await Step(700, "LT", () => QsHot("vol", -1));
                await Step(250, "RT", () => QsHot("vol", 1));
                for (int i = 0; i < 6; i++) await Step(160, "down", () => Move(Direction.Down));
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
