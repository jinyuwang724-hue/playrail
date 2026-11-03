using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // Read-only rendering coverage for the real page builders in both supported languages.
    // Never invokes a row action, selection callback or hardware write.
    internal async Task CopyReviewProbeAsync()
    {
        var pages = new JsonArray();
        bool english = I18n.En;
        using var target = new CanvasRenderTarget(G.Device, 1, 1, 96);
        using var ds = target.CreateDrawingSession();
        void Inspect(string page, List<PerfRow> rows, float width = PerfRightW)
        {
            var hits = new List<SettingsPointerTarget>();
            PaintRowList(ds, rows, -1, hits, numbers: false, listWidth: width);
            var issues = new JsonArray();
            var clippedNotes = new JsonArray();
            var font = TextKit.Ui(400);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Section || row.Skip || row.Choices == null) continue;
                var choices = hits.Where(h => h.Row == i && h.Kind == SettingsTargetKind.Choice).ToArray();
                if (choices.Length == 0) continue;
                double left = choices.Min(c => c.X);
                if (choices.Any(c => c.X < 0 || c.X + c.Width > width + .1)) issues.Add(JsonValue.Create(row.Title + ": controls outside list"));
                float titleRight = 36 + row.Indent + PWidth(ds, row.Title, font, 17);
                if (titleRight + 24 > left) issues.Add(JsonValue.Create(row.Title + ": label overlaps controls"));
                if (row.Value.Length > 0 && PWidth(ds, row.Value, font, 14) > left - 24 - (titleRight + 24)) clippedNotes.Add(JsonValue.Create(row.Title));
            }
            pages.Add((JsonNode)new JsonObject { ["page"] = page, ["language"] = I18n.En ? "en" : "zh", ["rows"] = rows.Count,
                ["layoutIssues"] = issues, ["clippedStatus"] = clippedNotes });
        }
        string? error = null;
        try
        {
            await Task.Delay(2000);
            if (!_ccOpen) OpenControlCenter(quiet: true);
            foreach (string language in new[] { "zh-CN", "en-US" })
            {
                I18n.Init(language);
                OpenDisplay(); await DsRefresh(); Inspect("display", _dsRows); CloseDisplay(true);
                OpenPerf(); await PerfRefresh(false); Inspect("performance", _perfRows); ClosePerf(true);
                OpenSystem(); await SyRefresh(); Inspect("system", _syRows); CloseSystem(true);
                OpenSettingsPage();
                for (int category = 0; category < StCatsMain.Length; category++)
                {
                    _stCat = category; _stZone = 0;
                    await StRefresh(); Repaint();
                    Inspect("settings/" + StCatsMain[category], _stRows, StRightW);
                }
                CloseSettingsPage(true);
            }
        }
        catch (Exception ex) { error = ex.Message; Log.Error("UI copy review", ex); }
        finally
        {
            I18n.Init(english ? "en-US" : "zh-CN");
            CloseDisplay(true); ClosePerf(true); CloseSystem(true); CloseSettingsPage(true);
            OpenSettingsPage(); _stCat = 0; _stZone = 0; Repaint();
            var result = new JsonObject { ["pages"] = pages, ["error"] = error };
            File.WriteAllText(Path.Combine(App.CacheDir, "copy-review-probe.json"), result.ToJsonString());
        }
    }
}
