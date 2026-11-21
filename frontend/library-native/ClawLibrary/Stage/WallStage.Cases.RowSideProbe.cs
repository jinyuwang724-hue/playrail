// 右侧栏的录屏自测（--state=recside）：打开 / 关闭、连按打断、焦点走一圈、滑块和功耗连发、下拉、子页。
// 只读：_spDry 打开后不往后台写任何东西（亮度、音量、功耗、开关都只改界面），免得录屏改掉用户的设置。
// 帧存到 %LOCALAPPDATA%\ClawLibrary\rec\side-*。
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    /// <summary>录屏自测：界面照常动，但不写后台、不切无线电。</summary>
    private bool _spDry;

    internal async Task RecSideAsync()
    {
        await Task.Delay(2500);
        _spDry = true;
        var style = _prefs.Home;
        string dir = Path.Combine(App.CacheDir, "rec");
        async Task Still(string name)
        {
            await Task.Delay(900);
            var r = new FrameRecorder { Full = true };
            if (!r.Start(HostWindow)) return;
            await Task.Delay(250);
            Log.Info($"recside {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "side-" + name), System.Diagnostics.Stopwatch.GetTimestamp())} frames");
        }
        async Task Rec(string name, Func<Task> act, int tail)
        {
            var r = new FrameRecorder();
            if (!r.Start(HostWindow)) { await act(); await Task.Delay(tail); return; }
            long m = System.Diagnostics.Stopwatch.GetTimestamp();
            await Task.Delay(120);
            await act();
            await Task.Delay(tail);
            Log.Info($"recside {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "side-" + name), m)} frames");
        }
        async Task Keys(params (Direction D, int Ms)[] steps) { foreach (var (d, ms) in steps) { Move(d); await Task.Delay(ms); } }
        try
        {
            if (_cs != null && _prefs.Home != HomeStyle.Row) { SaveUi(_prefs with { Home = HomeStyle.Row }); CsMorphStyle(); await Task.Delay(1500); }
            await Still("home");
            await Rec("open", () => { SwitchTab(1); return Task.CompletedTask; }, 600);
            await Still("panel");
            await Rec("tiles", () => Keys((Direction.Right, 260), (Direction.Right, 260), (Direction.Down, 300), (Direction.Left, 260)), 400);
            await Rec("rows", () => Keys((Direction.Down, 320), (Direction.Down, 320), (Direction.Down, 320), (Direction.Down, 320), (Direction.Up, 320)), 400);
            // 功耗：连按（110 ms 一下，模拟按住）加 4 W 再减 2 W
            await Rec("power", async () => { await Keys((Direction.Right, 110), (Direction.Right, 110), (Direction.Right, 110), (Direction.Right, 400), (Direction.Left, 110), (Direction.Left, 300)); }, 500);
            await Still("power-focus");
            // 亮度：滑块连发（75 ms 一下）
            await Rec("slider", async () => { await Keys((Direction.Up, 350), (Direction.Up, 350)); await Keys((Direction.Right, 75), (Direction.Right, 75), (Direction.Right, 75), (Direction.Right, 300), (Direction.Left, 300)); }, 400);
            await Rec("mute", async () => { await Keys((Direction.Down, 350)); Activate(); await Task.Delay(500); Activate(); }, 500);
            // 下拉：刷新率行，A 展开（向上），↓ 再 B 收起；帧率行 → 直接切
            await Rec("drop", async () => { await Keys((Direction.Down, 300), (Direction.Down, 300), (Direction.Down, 300), (Direction.Down, 300)); Activate(); await Task.Delay(500); await Keys((Direction.Up, 300)); }, 400);
            await Still("drop-open");
            await Rec("dropclose", () => { Back(); return Task.CompletedTask; }, 400);
            await Rec("pickstep", async () => { await Keys((Direction.Up, 300), (Direction.Up, 300)); await Keys((Direction.Right, 400)); await Keys((Direction.Left, 300)); }, 400);
            // 子页：电源（第二行最后一个），A 进，↓ 重启，A 一次（只是变红，不会真的重启），B 退
            for (int i = 0; i < 6; i++) { Move(Direction.Up); await Task.Delay(200); }
            for (int i = 0; i < 4; i++) { Move(Direction.Right); await Task.Delay(200); }
            Move(Direction.Down); await Task.Delay(300);
            await Rec("sub", async () => { Activate(); await Task.Delay(500); await Keys((Direction.Down, 300)); Activate(); await Task.Delay(500); }, 300);
            await Still("sub-armed");
            await Rec("subback", () => { Back(); return Task.CompletedTask; }, 500);
            await Rec("close", () => { SwitchTab(1); return Task.CompletedTask; }, 500);
            // 打断：开到一半又关、关到一半又开
            await Rec("interrupt", async () => { SwitchTab(1); await Task.Delay(70); SwitchTab(1); await Task.Delay(60); SwitchTab(1); }, 600);
            await Still("reopened");
            Back(); await Task.Delay(600);
            await Still("closed");
        }
        catch (Exception ex) { Log.Error("recside", ex); }
        _spDry = false;
        if (_cs != null && style != HomeStyle.Row) { SaveUi(_prefs with { Home = style }); CsMorphStyle(); }
        Log.Info("recside done");
    }
}
