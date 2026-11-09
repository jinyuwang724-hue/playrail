// 隔离缓存下的原生回归入口：验证真实舞台状态并记录合成后的画面。
using System.Text.Json.Nodes;
using ClawLibrary.Engine;
using static ClawLibrary.Engine.Geometry;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // 隔离探针可在窗口被其他验证任务遮住时继续检查合成状态；正常使用始终要求前台。
    private bool _appearanceProbeRunning;

    internal async Task IdleScreensaverProbeAsync()
    {
        _appearanceProbeRunning = true;
        _pad?.Dispose();
        _prefs = _prefs with { Screensaver = true, ScreensaverIdleSeconds = 15 };
        var selected = ScreensaverCandidates().First();
        Focus(selected.Slot.Key);
        await Task.Delay(800);
        Activate();
        await Task.Delay(21000);
        Log.Info($"probe idle result: active={ScreensaverActive} idle={NowMs - _lastInput} blocked={ScreensaverBlocked} expanded={_expandedKey} pending={_screensaverCollapseInput} stopped={_screensaverStopped}");
        _appearanceProbeRunning = false;
    }

    internal async Task AppearanceProbeAsync()
    {
        var checks = new JsonArray();
        void Check(string name, bool ok)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["passed"] = ok });
            if (!ok) throw new InvalidOperationException("appearance probe: " + name);
        }
        async Task Capture(string name)
        {
            var recorder = new FrameRecorder();
            Check("capture " + name, recorder.Start(_window));
            await Task.Delay(160);
            await recorder.StopAndSaveAsync(Path.Combine(App.CacheDir, "appearance", name), System.Diagnostics.Stopwatch.GetTimestamp());
        }
        try
        {
            _appearanceProbeRunning = true;
            _pad?.Dispose();
            _prefs = _prefs with { Screensaver = true, ScreensaverIdleSeconds = 60, ScreensaverIntervalSeconds = 6 };
            SetLibraryRail(true);
            await Task.Delay(1800);
            foreach (var style in TitleStyles.Values)
            {
                SetTitleStyle(style);
                await Task.Delay(200);
                await Capture("title-" + style);
            }
            var selected = ScreensaverCandidates().First(t => Lookup(t.Shown!)?.Platform != "folder");
            Focus(selected.Slot.Key);
            await Task.Delay(700);
            PadY();
            Check("collapsed game ignores Y", _expandedKey == null && !_ccOpen && _gsItem == null);
            foreach (var style in new[] { TitleStyle.Folia, TitleStyle.Quiet, TitleStyle.Editorial })
            {
                SetTitleStyle(style);
                Activate();
                await Task.Delay(1100);
                Check("expand " + style, _expandedKey != null);
                await Capture("expanded-" + style);
                if (style == TitleStyle.Folia)
                {
                    PadY();
                    Check("expanded game Y opens its settings", _ccOpen && _stOpen && _gsItem?.Id == selected.Shown);
                    await Task.Delay(800);
                    await Capture("game-settings-Y");
                    CloseSettingsToLibrary();
                    await Task.Delay(700);
                    PadY();
                    Check("returned collapsed game ignores Y", _expandedKey == null && !_ccOpen && _gsItem == null);
                }
                else Back();
                await Task.Delay(1100);
            }
            var previousStyle = Style;
            _pad!.InjectPress(Input.GamepadPoller.PadXButton);
            await Task.Delay(700);
            Check("X hides rail and closes gap", !_prefs.LibraryRail && _openWidth < 1 && !_seamHost.IsVisible);
            Check("X preserves title", Style == previousStyle);
            await Capture("rail-hidden");
            Activate();
            await Task.Delay(900);
            PadX();
            await Task.Delay(700);
            Check("expanded X controls rail", _expandedKey != null && !_ccOpen && _prefs.LibraryRail);
            Back();
            await Task.Delay(900);
            _prefs = _prefs with { Screensaver = true, ScreensaverIntervalSeconds = 3 };
            _window.Activate();
            for (int wait = 0; wait < 40 && ScreensaverBlocked; wait++) await Task.Delay(100);
            Log.Info($"probe screensaver ready: foreground={IsForeground()} active={_windowActive} blocked={ScreensaverBlocked} expanded={_expandedKey} wallBusy={_wallBusyUntil - NowMs} morph={MorphBusy}");
            Check("screensaver enters", EnterScreensaver());
            await Task.Delay(700);
            Check("screensaver hides rail and closes gap", !_seamHost.IsVisible && _openWidth < 1);
            Check("screensaver shows clock", _screensaverClock?.IsVisible == true);
            Check("screensaver lights one slot", _tiles.Values.Count(ScreensaverLights) == 1);
            await Capture("screensaver-first");
            var previousGame = _screensaverPreviousGame;
            await Task.Delay(3600);
            Check("screensaver changes game", ScreensaverCandidates().Select(t => t.Shown).Distinct().Count() < 2 || _screensaverPreviousGame != previousGame);
            await Capture("screensaver-next");
            // 同一帧方向 + A，以及按住的 A，在回到中立前都只应唤醒。
            GamepadActivity(true);
            Check("wake blocks held input", GamepadInputBlocked && !ScreensaverActive);
            if (!GamepadInputBlocked) Activate();
            Check("wake leaves card collapsed", _expandedKey == null);
            GamepadActivity(false);
            Check("neutral releases input", !GamepadInputBlocked);
            await Task.Delay(700);
            Check("wake restores rail preference", _prefs.LibraryRail && _seamHost.IsVisible);
            await Capture("screensaver-wake");
            SetLibraryRail(false);
            await Task.Delay(700);
            Check("screensaver enters with hidden rail", EnterScreensaver());
            await Task.Delay(500);
            Activate();
            await Task.Delay(700);
            Check("A only wakes and preserves hidden rail", !ScreensaverActive && !_prefs.LibraryRail && !_seamHost.IsVisible && _expandedKey == null);
            // 真正经过空闲计时器（而非直接调用进入函数），展开的详情也应归位后进入屏保。
            _prefs = _prefs with { ScreensaverIdleSeconds = 15 };
            Activate();
            for (int wait = 0; wait < 28 && !ScreensaverActive; wait++) await Task.Delay(1000);
            Check("idle timer collapses expanded card and starts screensaver", ScreensaverActive && _expandedKey == null);
            Activate();
            await Task.Delay(700);
            Check("idle screensaver A only wakes", !ScreensaverActive && _expandedKey == null);
            // 合集进入和退出时也不能偷偷重新打开已隐藏的竖条。
            var visibleSlot = SlotsIn(Visible()).First(sl => HasContent(sl));
            PushLayer(visibleSlot, new Layer { Id = "probe:collection", Title = "屏保测试集合",
                Items = CurrentLayer.Items.ToList(), Layout = Content.Infinite(CurrentLayer.Items.Count, visibleSlot), Home = false });
            await Task.Delay(1300);
            Check("collection keeps hidden rail", !_seamHost.IsVisible && _openWidth < 1);
            Pop();
            await Task.Delay(1300);
            Check("return keeps hidden rail", !_seamHost.IsVisible && _openWidth < 1);
            SetLibraryRail(true);
            OpenSettingsFromLibrary();
            _stCat = 2; Repaint();
            await Task.Delay(700);
            Check("title setting only in appearance", _stRows.Any(r => r.Title == "标题样式"));
            await Capture("appearance-settings");
            _stCat = 1; Repaint();
            Check("library has no title switch", _stRows.All(r => r.Title != "标题样式"));
            Log.Info("appearance probe passed");
        }
        catch (Exception ex) { Log.Error("appearance probe", ex); }
        finally
        {
            _appearanceProbeRunning = false;
            Directory.CreateDirectory(Path.Combine(App.CacheDir, "appearance"));
            File.WriteAllText(Path.Combine(App.CacheDir, "appearance", "checks.json"), checks.ToJsonString());
        }
    }
}
