using System.Text.Json.Nodes;
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
            OpenSettingsPage(); _stCat = StOsdCat; _stZone = 1;
            await OsdLoad(); Repaint();
            Check("OSD is in settings", StCatsMain[StOsdCat] == "性能显示" && _osdDraft != null);
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
            StEditEnd(false); _settingsEdit.End(); _stCat = StOsdCat; _stZone = 1; Repaint();
            File.WriteAllText(Path.Combine(App.CacheDir, "osd-settings-probe.json"), checks.ToJsonString());
        }
    }
}
