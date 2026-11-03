using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task LightingProbeAsync()
    {
        var checks = new JsonArray();
        string output = Path.Combine(App.CacheDir, "lighting");
        void Check(string name, bool ok)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["passed"] = ok });
            if (!ok) throw new InvalidOperationException("lighting probe: " + name);
        }
        async Task Capture(string name)
        {
            var rec = new FrameRecorder();
            Check("capture " + name, rec.Start(_window));
            await Task.Delay(180);
            Check("frames " + name, await rec.StopAndSaveAsync(Path.Combine(output, name), Stopwatch.GetTimestamp()) > 0);
        }
        try
        {
            _appearanceProbeRunning = true;
            _pad?.Dispose();
            _reducedLocal = false;
            _prefs = _prefs with { Screensaver = true, ScreensaverIdleSeconds = 900, ScreensaverIntervalSeconds = 20, LightsOn = true };
            var focus = ScreensaverCandidates().First();
            Focus(focus.Slot.Key);
            ApplyWallLook();
            await Task.Delay(900);
            Check("lights on backdrop", _backgroundLightsT.Target == 0);
            Check("normal focus pops", focus.PopTarget == 1 && focus.LightsTarget == 0);
            await Capture("on");
            _prefs = _prefs with { LightsOn = false };
            ApplyWallLook();
            Check("lights off covers whole backdrop", Math.Abs(_backgroundLightsT.Target - .82) < .0001);
            await Task.Delay(110);
            Check("background fades rather than jumps", _backgroundLightsT.Value(NowMs) is > 0 and < .82f);
            await Task.Delay(650);
            Check("focus stays fully lit", focus.LightsTarget == 0 && focus.TintTarget == 0);
            Check("other posters dim once", _tiles.Values.Where(t => t.Shown != null && !t.Focused && !t.Expanded)
                .All(t => Math.Abs(t.LightsTarget - .82) < .0001 && t.TintTarget == 0));
            await Capture("off");
            _prefs = _prefs with { LightsOn = true };
            ApplyWallLook();
            await Task.Delay(350);
            var originalRects = _tiles.ToDictionary(p => p.Key, p => p.Value.Rect);
            var recorder = new FrameRecorder();
            Check("record saver entry", recorder.Start(_window));
            long mark = Stopwatch.GetTimestamp();
            Check("saver enters", EnterScreensaver());
            Check("saver dims backdrop even with lights preference on", Math.Abs(_backgroundLightsT.Target - .82) < .0001);
            Check("exactly one raised spotlight", _tiles.Values.Count(t => t.PopTarget == 1) == 1);
            var first = _tiles[_screensaverLitKey!.Value];
            Check("spotlight shows game title", first.TitleVisible && _tiles.Values.Count(t => t.TitleVisible) == 1);
            first.Redraw();
            Check("title survives screensaver redraw", first.TitleVisible);
            await Task.Delay(800);
            Check("entry frames", await recorder.StopAndSaveAsync(Path.Combine(output, "entry"), mark) > 0);
            Check("spotlight fully lit", first.LightsTarget == 0);
            Check("layout and gaps preserved", originalRects.All(p => !_tiles.TryGetValue(p.Key, out var t) || t.Rect == p.Value));
            await Capture("saver-first");
            recorder = new FrameRecorder();
            Check("record spotlight change", recorder.Start(_window));
            mark = Stopwatch.GetTimestamp();
            PickScreensaverGame();
            var next = _tiles[_screensaverLitKey!.Value];
            Check("spotlight moves", next != first && next.PopTarget == 1 && first.PopTarget == 0);
            Check("game title follows spotlight", next.TitleVisible && !first.TitleVisible && _tiles.Values.Count(t => t.TitleVisible) == 1);
            first.Redraw();
            Check("redraw keeps unlit titles hidden", !first.TitleVisible);
            await Task.Delay(110);
            Check("new spotlight scales up gradually", next.PopProgress is > 0 and < 1);
            Check("old spotlight scales down gradually", first.PopProgress is > 0 and < 1);
            await Task.Delay(700);
            Check("change frames", await recorder.StopAndSaveAsync(Path.Combine(output, "change"), mark) > 0);
            Check("only new spotlight stays raised", _tiles.Values.Count(t => t.PopTarget == 1) == 1 && next.PopProgress == 1 && first.PopProgress == 0);
            Check("old spotlight dims again", Math.Abs(first.LightsTarget - .82) < .0001);
            Check("spotlight is above both halves", _field.Children.Last() == _worlds[next.Side] && _worlds[next.Side].Children.Last() == next.El);
            await Capture("saver-next");
            var edge = ScreensaverCandidates().FirstOrDefault(t => ScreensaverTitleArea(t).Height < t.Rect.Height - 32);
            if (edge != null)
            {
                _screensaverLitKey = edge.Slot.Key;
                ApplyWallLook();
                await Task.Delay(700);
                Check("clipped cover keeps its title", edge.TitleVisible && _tiles.Values.Count(t => t.TitleVisible) == 1);
                await Capture("saver-edge");
            }
            Check("wake consumed", ConsumeScreensaverWake());
            await Task.Delay(700);
            Check("wake restores lights on backdrop", _backgroundLightsT.Target == 0);
            Check("wake preserves lights preference", _prefs.LightsOn);
            Check("wake restores all game titles", _tiles.Values.Where(t => t.Shown != null).All(t => t.TitleVisible));
            Check("wake restores normal focus pop", FocusKey is { } key && _tiles[key].PopTarget == 1 && _tiles.Values.Count(t => t.PopTarget == 1) == 1);
            await Capture("wake");
            _reducedLocal = true;
            Check("reduced motion saver enters", EnterScreensaver());
            Check("reduced spotlight snaps", _tiles[_screensaverLitKey!.Value].PopProgress == 1);
            Check("reduced motion keeps spotlight title", _tiles[_screensaverLitKey!.Value].TitleVisible && _tiles.Values.Count(t => t.TitleVisible) == 1);
            ExitScreensaver();
            Check("reduced wake snaps background", _backgroundLightsT.Value(NowMs) == 0);
            Log.Info("lighting probe passed");
        }
        catch (Exception ex) { Log.Error("lighting probe", ex); }
        finally
        {
            _appearanceProbeRunning = false;
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "checks.json"), checks.ToJsonString());
        }
    }
}
