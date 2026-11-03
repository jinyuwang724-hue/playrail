using System.Text.Json.Nodes;
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task ServicesProbeAsync()
    {
        var checks = new JsonArray();
        void Check(string label, bool value)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = label, ["passed"] = value });
            if (!value) throw new InvalidOperationException("services probe: " + label);
        }
        try
        {
            await Task.Delay(2000);
            if (!_ccOpen) OpenControlCenter(quiet: true);
            OpenSettingsPage();
            _stCat = 6; _stZone = 1;
            Repaint();
            Check("no library restart or preload action in account page", _stRows.All(r => !r.Title.Contains("载入") && !r.Title.Contains("托盘") && !r.Title.Contains("启动")));
            var counts = _stRows.Select((row, index) => (row, index)).Where(x => x.row.Title.EndsWith("已识别游戏")).ToList();
            Check("both platforms have bright nonselectable counts", counts.Count == 2 && counts.All(x => x.row.Skip && !x.row.Disabled && x.row.BrightValue));
            Check("counts are absent from pointer targets", counts.All(x => _settingsTargets.All(t => t.Row != x.index)));
            _stFocus = counts[0].index - 1;
            StMove(Direction.Down);
            Check("controller skips count to platform refresh", _stRows[_stFocus].Title == "更新 Steam 游戏库");
            int pid = Environment.ProcessId;
            string[] local = _lib.Games.Where(g => g.Platform != "steam" && g.Platform != "xbox").Select(g => g.Id).ToArray();
            await RefreshPlatformLibrary("steam");
            Check("Steam refresh succeeds within same process", _steamRefreshError == null && Environment.ProcessId == pid);
            Check("Steam refresh retains local games", local.SequenceEqual(_lib.Games.Where(g => g.Platform != "steam" && g.Platform != "xbox").Select(g => g.Id)));
            string[] steam = _lib.Games.Where(g => g.Platform == "steam").Select(g => g.Id).ToArray();
            await RefreshPlatformLibrary("xbox");
            Check("Xbox refresh succeeds within same process", _xboxRefreshError == null && Environment.ProcessId == pid);
            Check("Xbox refresh retains Steam games", steam.SequenceEqual(_lib.Games.Where(g => g.Platform == "steam").Select(g => g.Id)));
            _stCat = 0; Repaint();
            Check("Steam startup moved into general settings", _stRows.Any(r => r.Title == "在托盘启动 Steam") && _stRows.Any(r => r.Title == "启动 Steam 至托盘"));
            _stCat = 6; _stFocus = FirstStRow(); _stZone = 0; Repaint();
        }
        catch (Exception ex) { Log.Error("services probe", ex); checks.Add((JsonNode)new JsonObject { ["error"] = ex.Message }); }
        File.WriteAllText(Path.Combine(App.CacheDir, "services-probe.json"), checks.ToJsonString());
    }
}
