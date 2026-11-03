using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // Opt-in probe: all actions use fixtures or a memory transport; no user or hardware settings are written.
    internal async Task TouchProbeAsync()
    {
        var checks = new JsonArray();
        string? error = null;
        void Check(string name, bool passed)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["passed"] = passed });
            if (!passed) throw new InvalidOperationException(name);
        }
        try
        {
            await Task.Delay(2000);
            if (!_ccOpen) OpenControlCenter(quiet: true);
            void Exercise(string page, Action open, Action close, List<PerfRow> rows)
            {
                open();
                int pressed = 0, disabled = 0, secondary = 0, number = 50;
                var slider = new PerfRow { Title = "测试滑条", Value = "50 %", Numeric = true, Number = 50, Minimum = 0, Maximum = 100,
                    Increment = 5, Step = _ => { }, SetNumber = value => { number = value; } };
                rows.Clear();
                rows.Add(new() { Title = "测试操作", Press = () => pressed++ });
                rows.Add(new() { Title = "不可用", Disabled = true, Press = () => disabled++ });
                rows.Add(slider);
                rows.Add(new() { Title = "测试次级操作", Secondary = () => secondary++ });
                for (int i = 0; i < 36; i++) rows.Add(new() { Title = "列表 " + i, Press = () => pressed++ });
                SettingsResetPointer(); SettingsFocus = 0; SettingsScroll = 0; SettingsPaint();
                double x = (SettingsListX + 100) * _ccK, y = (SettingsListY + PerfRowH / 2) * _ccK;
                Check(page + ": press starts without activation", SettingsPointerBegin(1, x, y) && pressed == 0);
                SettingsPointerEnd(2, x, y);
                Check(page + ": second finger release is ignored", pressed == 0 && _settingsGesture.Owns(1));
                SettingsPointerEnd(1, x + 2, y + 2);
                Check(page + ": release activates exactly once", pressed == 1);
                SettingsPointerEnd(1, x, y);
                Check(page + ": duplicate release is ignored", pressed == 1);
                SettingsPointerBegin(3, x, y);
                SettingsPointerMove(3, x, y - 160);
                SettingsPointerEnd(3, x, y - 160);
                Check(page + ": vertical drag scrolls without activation", SettingsScroll > 100 && pressed == 1);
                SettingsScroll = 0; SettingsPaint();
                SettingsPointerBegin(4, x, y);
                rows[0].Title = "刷新后的另一项";
                SettingsPointerEnd(4, x, y);
                Check(page + ": changed row identity cancels a pending tap", pressed == 1);
                SettingsPointerBegin(5, x, y);
                SettingsPointerCancel(5); SettingsPointerEnd(5, x, y);
                Check(page + ": canceled contact cannot activate", pressed == 1);
                double disabledY = y + PerfRowH * _ccK;
                SettingsPointerBegin(6, x, disabledY); SettingsPointerEnd(6, x, disabledY);
                Check(page + ": disabled rows stay disabled", disabled == 0);
                SettingsFocus = 2; RowPress(slider); SettingsPaint();
                var target = _settingsTargets.First(t => t.Row == 2 && t.Kind == SettingsTargetKind.Slider);
                double sliderX = (SettingsListX + target.X + target.Width / 2) * _ccK;
                double sliderY = (SettingsListY + target.Y + target.Height / 2) * _ccK;
                SettingsPointerBegin(7, sliderX, sliderY);
                SettingsPointerMove(7, sliderX + 500 * _ccK, sliderY);
                SettingsPointerEnd(7, sliderX + 500 * _ccK, sliderY);
                Check(page + ": slider drag clamps to the allowed range", number == 100);
                SettingsFocus = 3; SettingsPaint();
                var gear = _settingsTargets.First(t => t.Row == 3 && t.Kind == SettingsTargetKind.Secondary);
                double gx = (SettingsListX + gear.X + gear.Width / 2) * _ccK;
                double gy = (SettingsListY + gear.Y + gear.Height / 2) * _ccK;
                SettingsPointerBegin(8, gx, gy); SettingsPointerEnd(8, gx, gy);
                Check(page + ": secondary action is reachable by touch", secondary == 1);
                SettingsResetPointer(); close();
            }
            Exercise("display", OpenDisplay, () => CloseDisplay(true), _dsRows);
            Exercise("system", OpenSystem, () => CloseSystem(true), _syRows);
            Exercise("performance", OpenPerf, () => ClosePerf(true), _perfRows);
            Exercise("software", OpenSettingsPage, () => CloseSettingsPage(true), _stRows);

            OpenGamepad();
            _gpPicker = new GpPickerState { Level = GpSubLevel.Chooser, Source = "A", Tab = 0 };
            PaintGpBody();
            var tab = _gpTargets.First(t => t.Kind == SettingsTargetKind.Tab && t.Row == 1);
            double tx = (tab.X + tab.Width / 2) * _ccK, ty = (tab.Y + tab.Height / 2) * _ccK;
            SettingsPointerBegin(10, tx, ty); SettingsPointerEnd(10, tx, ty);
            Check("gamepad: chooser tabs accept touch", _gpPicker?.Tab == 1);
            SettingsPointerBegin(11, 900 * _ccK, 400 * _ccK);
            SettingsPointerMove(11, 750 * _ccK, 402 * _ccK);
            SettingsPointerEnd(11, 750 * _ccK, 402 * _ccK);
            Check("gamepad: horizontal swipe switches chooser tab", _gpPicker?.Tab == 2);
            _gpPicker = null; CloseGamepad(true);

            OpenSettingsPage();
            _gsItem = new LibraryItem { Id = "touch-probe", Title = "触控测试", Platform = "local" };
            _stCat = 2;
            _gpPicker = new GpPickerState { Level = GpSubLevel.Chooser, Source = "A", Tab = 0 };
            PaintSt();
            var embeddedTab = _gpTargets.First(t => t.Kind == SettingsTargetKind.Tab && t.Row == 1);
            tx = (embeddedTab.X + embeddedTab.Width / 2 + GsPadDx) * _ccK;
            ty = (embeddedTab.Y + embeddedTab.Height / 2) * _ccK;
            Check("per-game controller: translated chooser uses shared touch routing", UseSettingsPointer(tx, ty) && SettingsTargetAt(tx, ty)?.Kind == SettingsTargetKind.Tab);
            SettingsPointerBegin(12, tx, ty); SettingsPointerEnd(12, tx, ty);
            Check("per-game controller: translated chooser tab activates", _gpPicker?.Tab == 1);
            _gpPicker = new GpPickerState { Level = GpSubLevel.Group, Source = "A" };
            PaintSt();
            Check("per-game controller: grouped mappings expose trigger settings gear", _gpPickRows.Any(r => r.Secondary != null));
            Check("per-game controller: embedded subpage participates in list gestures", SettingsPickerRows && SettingsHasList);
            double ex = (SettingsListX + 100) * _ccK, ey = (SettingsListY + 160) * _ccK;
            SettingsPointerBegin(13, ex, ey);
            SettingsPointerMove(13, ex, ey - 200 * _ccK);
            SettingsPointerEnd(13, ex, ey - 200 * _ccK);
            Check("per-game controller: long mapping list scrolls without changing a binding", _gpPickScroll > 0);
            _gpPicker = null; _gsItem = null; CloseSettingsPage(true);

            var draft = new AgDraft();
            for (int i = 0; i < 40; i++) draft.Matches.Add(new JsonObject { ["name"] = "匹配结果 " + i });
            AgShow(draft, AgPage.Match);
            AgDrag(-500);
            Check("add game: long match results scroll", _agMatchScroll > 0);
            Check("add game: scrolled result hits stay below search and inside viewport", _agHits.Where(h => h.Zone == 1).All(h => h.R.Y >= AgMatchMap(new(0, AgMatchTop, 1, 1)).Y && h.R.Bottom <= 760.1));
            Check("add game: results outside viewport have no active hit region", _agHits.Count(h => h.Zone == 1) < draft.Matches.Count);
            AgDrag(-100000);
            Check("add game: end of results remains reachable", _agHits.Any(h => h.Zone == 1 && h.Index == 39));
            AgClose();

            OpenSettingsPage();
            OpenShots();
            Check("overlays: screenshot input is not intercepted by underlying settings", !UseSettingsPointer(700 * _ccK, 400 * _ccK));
            Check("screenshots: every new touch icon has a rendered path", new[] { "", "", "", "" }.All(LineIcons.Has));
            Check("screenshots: filter has a visible touch entry", TouchFilterHit(118 * _ccK, 48 * _ccK));
            Check("screenshots: layout has a touch entry", TouchShotStyleHit(170 * _ccK, 48 * _ccK));
            ShToggleFilter();
            _shFilterItems = Enumerable.Range(0, 40).Select(i => new ShFilterItem(i.ToString(), "游戏 " + i, i)).ToList();
            _shFltFirst = _shFilterFocus = 0; _shFilterDragRemainder = 0;
            ShDragFilter(-192);
            Check("screenshots: filter list scrolls", _shFltFirst == 4);
            Check("screenshots: filter hits follow the scrolled viewport", ShFilterHit(ShFltX + 80, ShFltTop + ShFltHead + 24) == 4);
            ShDragFilter(-100000);
            Check("screenshots: filter scroll clamps at last visible row", _shFltFirst == 29);
            CloseShots(true);
            CloseSettingsPage(true);

            var sent = new List<(string Method, JsonNode Value)>();
            _qsTouchTransport = (method, value) => { sent.Add((method, value.DeepClone())); return Task.CompletedTask; };
            OpenQuickPanel(0);
            _qsState = new JsonObject { ["system"] = new JsonObject { ["brightness"] = 50, ["volume"] = 50, ["muted"] = true } };
            _qsDraft = new JsonObject { ["manual"] = new JsonObject { ["tdp"] = 15 } };
            QsBuild();
            _pagePointer.Begin(99, 0, 0, PointerSurface); // Prevent asynchronous readback from replacing the fixture.
            var volume = _qsItems.First(i => i.Id == "volume");
            QsTouchDrag("volume", (volume.X + volume.W + 300) * _ccK);
            Check("quick settings: volume drag updates value and unmutes", Int(_qsState["system"]?["volume"]) == 100 && !Bool(_qsState["system"]?["muted"]));
            var brightness = _qsItems.First(i => i.Id == "brightness");
            QsSetLevel(brightness, .2f); QsSetLevel(brightness, .3f);
            Check("quick settings: brightness tap updates optimistic value", Int(_qsState["system"]?["brightness"]) == 30);
            QsSetLevel(new() { Id = "brightness", Kind = QsKind.Slider, Disabled = true }, .9f);
            Check("quick settings: disabled slider ignores touch", Int(_qsState["system"]?["brightness"]) == 30);
            QsSetLevel(new() { Id = "tdp", Kind = QsKind.Slider }, .5f);
            CloseQuickPanel(hideWindow: false, instant: true);
            await _qsTouchPending;
            _pagePointer.Reset();
            Check("quick settings: volume uses correct backend request", sent.Any(s => s.Method == "set_volume" && Int(s.Value) == 100));
            Check("quick settings: repeated brightness changes coalesce", sent.Count(s => s.Method == "set_brightness") == 1 && Int(sent.First(s => s.Method == "set_brightness").Value) == 30);
            Check("quick settings: TDP retains the save_power contract", sent.Any(s => s.Method == "save_power" && s.Value["manual"]?["tdp"] != null));
            _qsTouchTransport = null;

            OpenControlCenter(quiet: true);
            Check("navigation: control centre has no added back button or hotspot", TouchNavigationMode == 0 && !TouchNavigationHit(20 * _ccK, 48 * _ccK));
        }
        catch (Exception ex) { error = ex.Message; Log.Error("touch probe", ex); }
        finally
        {
            _pagePointer.Reset(); SettingsResetPointer();
            await _qsTouchPending;
            _qsTouchTransport = null;
            _gpPicker = null; _gsItem = null;
            CloseQuickPanel(hideWindow: false, instant: true);
            AgClose(); CloseShots(true); CloseGamepad(true);
            CloseDisplay(true); CloseSystem(true); ClosePerf(true); CloseSettingsPage(true);
            OpenSettingsPage(); _stCat = 0; _stZone = 0; Repaint();
            File.WriteAllText(Path.Combine(App.CacheDir, "touch-probe.json"), new JsonObject { ["checks"] = checks, ["error"] = error }.ToJsonString());
        }
    }
}
