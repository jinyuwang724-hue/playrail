using System.Diagnostics;
using System.Text.Json.Nodes;
namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task DesktopMappingProbeAsync()
    {
        var checks = new JsonArray();
        string output = Path.Combine(App.CacheDir, "desktop-mapping");
        void Check(string name, bool passed)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["passed"] = passed });
            if (!passed) throw new InvalidOperationException("desktop mapping probe: " + name);
        }
        async Task Capture(string name)
        {
            var rec = new FrameRecorder();
            if (!rec.Start(_window))
            {
                checks.Add((JsonNode)new JsonObject { ["captureUnavailable"] = name });
                return; // UI is inspected independently with the window capture tool.
            }
            await Task.Delay(200);
            Check("frames " + name, await rec.StopAndSaveAsync(Path.Combine(output, name), Stopwatch.GetTimestamp()) > 0);
        }
        try
        {
            _appearanceProbeRunning = true;
            ToggleControlCenter(); await Task.Delay(1200);
            OpenGamepad(); await Task.Delay(1200); await GpRefresh();
            _gpCat = Array.FindIndex(GpCatRows, i => GpSidebar[i].Id == "dkmap");
            _gpInSide = false; BuildGpItems(); PaintGp();
            Check("desktop has both sticks", _gpItems.Any(i => i.Title == "左摇杆") && _gpItems.Any(i => i.Title == "右摇杆"));
            Check("desktop has both back keys", _gpItems.Any(i => i.Key == "M1") && _gpItems.Any(i => i.Key == "M2"));
            Check("desktop groups include sticks/back", GpGroups(true).Count == 5);
            await Task.Delay(450); await Capture("map");
            GpOpenGroup("LStick", true);
            Check("desktop uses shared stick behaviors and deadzones", GpGroupRows(_gpPicker!).Count(r => r.Title == "行为") == 2
                && GpGroupRows(_gpPicker!).Count(r => r.Title == "死区") == 2);
            Check("desktop and gamepad have the same behaviors", GpGroupRows(_gpPicker!).Where(r => r.Title == "行为").All(r => r.Choices!.SequenceEqual(GpStickModes.Select(GpStickModeName))));
            await Task.Delay(450); await Capture("sticks");
            GpOpenGroup("M1", true);
            Check("back key group contains two bindings", GpGroupRows(_gpPicker!).Count(r => !r.Section) == 2);
            await Task.Delay(450); await Capture("backkeys");
            CloseGamepad(true);
            OpenDisplay();
            for (int i = 0; i < 30 && !_dsRows.Any(r => r.Title == "XeSS 多帧生成"); i++)
            {
                await Task.Delay(500);
                if (!_dsBusy) await DsRefresh();
            }
            int fg = _dsRows.FindIndex(r => r.Title == "XeSS 多帧生成");
            Check("four XeSS choices", fg >= 0 && _dsRows[fg].Choices?.Length == 4);
            _dsFocus = fg; PaintDs();
            await Task.Delay(450); await Capture("xess");
            CloseDisplay(true);
            OpenSettingsPage();
            _stCat = 5;
            for (int i = 0; i < 30 && !_stRows.Any(r => r.Title == "充电与电池供电提示"); i++)
            {
                await Task.Delay(500);
                if (!_stBusy) await StRefresh();
                BuildStRows();
            }
            PaintSt();
            Check("global notification settings", _stRows.Any(r => r.Title == "充电与电池供电提示")
                && _stRows.Any(r => r.Title == "手柄与桌面模式切换提示"));
            await Task.Delay(450); await Capture("notifications");
        }
        catch (Exception ex) { Log.Warn(ex.ToString()); checks.Add((JsonNode)new JsonObject { ["error"] = ex.Message, ["passed"] = false }); }
        finally
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "checks.json"), checks.ToJsonString());
            _appearanceProbeRunning = false;
        }
    }
}
