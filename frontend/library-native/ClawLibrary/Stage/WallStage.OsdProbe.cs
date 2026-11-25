using System.Text.Json.Nodes;
using ClawLibrary.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // Uses actual row builders and input components; no credentials, user settings or hardware writes.
    internal async Task OsdSettingsProbeAsync()
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
            _ccLight = true; _cpComp = "disp"; _cpZone = 0;
            OpenDisplay();
            var cats = CpCats(SettingsRows);
            Check("single sound category", cats.Count(c => c.Name.StartsWith(DsSoundSection, StringComparison.Ordinal)) == 1);
            Check("moved performance display category", !StCatsMain.Contains(DsOsdSection) && cats.Any(c => c.Name == DsOsdSection));
            CpMove(Direction.Down);
            Check("controller enters sound", DsFocusedSection == DsSoundSection);
            int sound = cats.FindIndex(c => c.Name == DsSoundSection);
            var soundRows = CpVisible(_dsRows, cats, sound).Select(i => _dsRows[i]).ToList();
            Check("sound options share one list", new[] { "音量", "输出设备", "界面音效", "背景音乐", "导航震动", "充电与电池供电提示" }.All(title => soundRows.Any(r => r.Title == title)));
            soundRows.Single(r => r.Title == "导入本地音乐").Press!();
            Check("music browser stays under sound", _stOpen && _stBrowseMusic && CpCats(SettingsRows)[CpCatIndex(SettingsRows, CpCats(SettingsRows))].Name == DsSoundSection);
            StBack();
            Check("music browser returns to sound", !_stBrowseMusic && _dsOpen && DsFocusedSection == DsSoundSection);

            cats = CpCats(SettingsRows);
            CpGoCat(SettingsRows, cats, cats.FindIndex(c => c.Name == DsOsdSection));
            for (int i = 0; i < 80 && _osdLoading; i++) await Task.Delay(50);
            Check("OSD rows load on the display page", DsFocusedSection == DsOsdSection && _osdDraft != null
                && _dsRows.Any(r => r.Title == "显示性能数据" && !r.Disabled));
            var catalogue = _osdState?["catalog"] as JsonArray;
            var defaults = _osdState?["layoutDefaults"] as JsonObject;
            Check("all 25 metric definitions available", catalogue?.Count == 25 && defaults != null);
            var fixture = (JsonObject)_osdDraft!.DeepClone(); fixture["level"] = 4; fixture.Remove("layout");
            JsonObject? changed = null;
            void Build(JsonObject value)
            {
                _stRows.Clear(); changed = null;
                BuildOsdOptions(value, next => changed = (JsonObject)next.DeepClone(), catalogue, defaults, "probe");
            }
            Build(fixture);
            var mode = _stRows.Single(r => r.Title == "显示布局");
            Check("layout selector reuses confirm-to-edit numeric row", mode.Numeric && mode.Choices == null);
            mode.Step!(1);
            Check("choosing custom keeps the preset and seeds a complete layout", Bool(changed?["layout"]?["custom"]) && Int(changed?["level"]) == 4 && (changed?["layout"]?["items"] as JsonArray)?.Count == 25);
            fixture = changed!; Build(fixture);
            Check("custom page uses existing row controls only", _stRows.Any(r => r.Title == "位置" && r.Numeric) && _stRows.Any(r => r.Title == "选择项目" && r.Numeric));
            Check("custom page has no restart requirement", _stRows.All(r => !r.Title.Contains("重启") && !r.Title.Contains("重新载入")));
            _stRows.Single(r => r.Title == "显示此项目").Press!();
            Check("item visibility saves only the selected metric", (changed?["layout"]?["items"] as JsonArray)?.Single(i => Str(i?["id"]) == "fps")?["enabled"]?.GetValue<bool>() == false);
            Build(fixture); _stRows.Single(r => r.Title == "所在行").Step!(1);
            Check("metric row can be changed independently", Int((changed?["layout"]?["items"] as JsonArray)?.Single(i => Str(i?["id"]) == "fps")?["row"]) == 1);
            Build(fixture); _stRows.Single(r => r.Title == "排列顺序").Step!(1);
            Check("reordering preserves every item", (changed?["layout"]?["items"] as JsonArray)?.Count == 25 && Str(changed?["layout"]?["items"]?[2]?["id"]) == "fps");
            Build(fixture); _stRows.Single(r => r.Title == "项目颜色").Step!(1);
            Check("metric colour stays separate from text colour", Str((changed?["layout"]?["items"] as JsonArray)?.Single(i => Str(i?["id"]) == "fps")?["color"]) == "FFFFFF" && Str(changed?["layout"]?["color"]) == "FFFFFF");
            Build(fixture); _stRows.Single(r => r.Title == "显示布局").Step!(-1);
            Check("returning to a preset retains custom layout", !Bool(changed?["layout"]?["custom"]) && JsonNode.DeepEquals(changed?["layout"]?["items"], fixture["layout"]?["items"]));
            CloseDisplay(true); _cpComp = null; OpenSettingsPage();
            Repaint(); _stFocus = FirstStRow();
            bool committed = false;
            StEditSecret(_ => committed = true);
            Check("key editor uses the existing inline placement", _stSecretBox?.Visibility == Visibility.Visible && _stSecretBox.Width == (StRightW - 320) * _ccK);
            Check("key editor uses existing font and transparent backgrounds", _stSecretBox?.FontFamily.Source == TextKit.XamlUi
                && _stSecretBox.Background is SolidColorBrush bg && bg.Color.A == 0
                && _stSecretBox.Resources["TextControlBackgroundFocused"] is SolidColorBrush focused && focused.Color.A == 0
                && _stSecretBox.BorderThickness == new Thickness(0));
            StEditEnd(false);
            Check("cancel leaves no key or pending confirmation", !committed && !StEditing && _stSecretBox?.Password == "");
        }
        catch (Exception ex) { checks.Add((JsonNode)new JsonObject { ["error"] = ex.Message }); Log.Error("OSD settings probe", ex); }
        finally
        {
            StEditEnd(false); _settingsEdit.End();
            File.WriteAllText(Path.Combine(App.CacheDir, "osd-settings-probe.json"), checks.ToJsonString());
        }
    }
}
