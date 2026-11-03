using System.Text.Json.Nodes;
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // Exercises the real page handlers with in-memory values; never writes hardware or user settings.
    internal async Task SettingsEditProbeAsync()
    {
        var checks = new JsonArray();
        void Check(string name, bool passed)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["passed"] = passed });
            if (!passed) throw new InvalidOperationException(name);
        }
        try
        {
            await Task.Delay(2000);
            if (!_ccOpen) OpenControlCenter(quiet: true);
            void Exercise(string page, Action open, Action close, List<PerfRow> rows, Action activate, Action<Direction> move, Action back)
            {
                open();
                int changed = 0, pressed = 0, number = 50;
                var selector = new PerfRow { Title = "测试选项", Value = "当前值", Numeric = true, Step = _ => changed++, Press = () => pressed++ };
                var slider = new PerfRow { Title = "测试数值", Value = "50 %", Numeric = true, Number = 50, Minimum = 0, Maximum = 100,
                    Step = _ => changed++, SetNumber = value => { number = value; } };
                rows.Clear(); rows.Add(selector); rows.Add(slider);
                SettingsFocus = 0; SettingsPaint();
                Check(page + ": plain values have no adjustment targets", _settingsTargets.All(t => t.Kind == SettingsTargetKind.Row));
                move(Direction.Right);
                Check(page + ": left/right cannot change a locked value", changed == 0);
                activate();
                Check(page + ": confirm arms without invoking its previous action", SettingsRowEditing(selector) && changed == 0 && pressed == 0);
                Check(page + ": armed selector exposes arrows", _settingsTargets.Any(t => t.Row == 0 && t.Kind == SettingsTargetKind.Increase));
                move(Direction.Right);
                Check(page + ": armed keyboard adjustment works", changed == 1);
                activate(); move(Direction.Left);
                Check(page + ": second confirm locks without changing", !SettingsRowEditing(selector) && changed == 1 && pressed == 0);
                activate(); back();
                Check(page + ": back consumes adjustment before leaving", SettingsPageOpen && !SettingsRowEditing(selector));
                activate(); move(Direction.Down); move(Direction.Up);
                Check(page + ": returning to a row requires confirmation again", !SettingsRowEditing(selector));
                SettingsApplyTarget(new(0, SettingsTargetKind.Row, 0, 0, 812, 42), 0);
                SettingsApplyTarget(new(0, SettingsTargetKind.Increase, 0, 0, 44, 42), 0);
                Check(page + ": pointer click arms then arrow changes", changed == 2 && SettingsRowEditing(selector));
                SettingsApplyTarget(new(0, SettingsTargetKind.Row, 0, 0, 812, 42), 0);
                SettingsApplyTarget(new(0, SettingsTargetKind.Increase, 0, 0, 44, 42), 0);
                Check(page + ": stale arrow cannot change a completed value", changed == 2 && !SettingsRowEditing(selector));
                SettingsFocus = 1; SettingsPaint();
                var drag = new SettingsPointerTarget(1, SettingsTargetKind.Slider, 0, 42, 200, 42);
                SettingsSliderAt(drag, (SettingsListX + 200) * _ccK);
                Check(page + ": unarmed slider ignores drag", number == 50 && !_settingsTargets.Any(t => t.Kind == SettingsTargetKind.Slider));
                activate();
                SettingsSliderAt(drag, (SettingsListX + 200) * _ccK);
                Check(page + ": armed slider accepts drag", number == 100 && _settingsTargets.Any(t => t.Kind == SettingsTargetKind.Slider));
                SettingsResetPointer(); close();
            }
            Exercise("display", OpenDisplay, () => CloseDisplay(true), _dsRows, DsActivate, DsMove, DsBack);
            Exercise("system", OpenSystem, () => CloseSystem(true), _syRows, SyActivate, SyMove, SyBack);
            Exercise("performance", OpenPerf, () => ClosePerf(true), _perfRows, PerfActivate, PerfMove, PerfBack);
            Exercise("settings", () => { OpenSettingsPage(); _stZone = 1; }, () => CloseSettingsPage(true), _stRows, StActivate, StMove, StBack);

            OpenGamepad();
            int gpValue = 0;
            GpItem Fake(bool map = false) => new() { Title = "测试手柄", Value = "第一项", X = 400, Y = 300, Side = 1,
                Choices = new[] { "第一项", "第二项" }, Choice = gpValue, SetChoice = value => gpValue = value,
                Map = map, Key = "A", MapW = 200, MapH = 52, Col = -2 };
            void GpFixture(bool map = false) { _gpItems.Clear(); _gpItems.Add(Fake(map)); _gpFocus = 0; _gpInSide = false; PaintGpBody(); }
            GpFixture();
            var itemHit = new SettingsPointerTarget(0, SettingsTargetKind.Item, 0, 0, 100, 100);
            GpApplyPointerTarget(itemHit, 0);
            Check("gamepad: first pointer click arms without changing", _gpEditing && gpValue == 0);
            Check("gamepad: armed item exposes clickable arrows", _gpTargets.Any(t => t.Kind == SettingsTargetKind.Increase));
            GpApplyPointerTarget(itemHit, 0);
            Check("gamepad: second pointer click finishes without changing", !_gpEditing && gpValue == 0);
            GpFixture(true); GpApplyPointerTarget(itemHit, 0);
            Check("gamepad: mapping row exposes arrows only after confirmation", _gpEditing && _gpTargets.Any(t => t.Kind == SettingsTargetKind.Increase));
            GpApplyPointerTarget(new(0, SettingsTargetKind.Increase, 0, 0, 44, 44), 0);
            Check("gamepad: armed pointer arrow changes the choice", gpValue == 1);
            _gpEditing = false; GpFixture();
            GpApplyPointerTarget(new(0, SettingsTargetKind.Decrease, 0, 0, 44, 44), 0);
            Check("gamepad: stale arrow cannot change an unarmed item", gpValue == 1);
            _gpPicker = new GpPickerState { Level = GpSubLevel.Settings, Source = "A", SettingsFocus = 1 };
            var pickerRows = GpSettingsRows(_gpPicker);
            _gpPicker.SettingsFocus = pickerRows.FindIndex(r => r.Numeric);
            var pickerRow = pickerRows[_gpPicker.SettingsFocus];
            RowPress(pickerRow);
            GpPickerBack();
            Check("gamepad picker: back finishes adjustment before leaving subpage", _gpPicker?.Level == GpSubLevel.Settings && !SettingsRowEditing(pickerRow));
            _gpPicker = null; _gpEditing = false; CloseGamepad(true);
        }
        catch (Exception ex) { Log.Error("settings edit probe", ex); checks.Add((JsonNode)new JsonObject { ["error"] = ex.Message }); }
        finally
        {
            _settingsEdit.End();
            CloseDisplay(true); CloseSystem(true); ClosePerf(true); CloseGamepad(true); CloseSettingsPage(true);
            OpenSettingsPage(); _stCat = 0; _stZone = 1; Repaint();
            File.WriteAllText(Path.Combine(App.CacheDir, "settings-edit-probe.json"), checks.ToJsonString());
        }
    }
}
