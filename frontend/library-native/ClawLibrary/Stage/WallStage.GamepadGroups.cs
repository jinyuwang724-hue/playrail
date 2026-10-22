// 手柄 → 按键映射的分组页（照 Steam 控制器设置的子页：左边一列分组，右边这一组每个键一行）。
// 分组：按键（正面按键、肩键、功能键）、十字键、扳机键、摇杆、背键。每行 = 键的图标 + 名字 + 现在映射到什么；
// A 直接进「选择指令」（选完回到这页），Y 进这个键的按键页（触发方式、触发设置、恢复原键位），LB / RB 换分组。
// 游戏设置 › 手柄里没有左边的分组列（那里左栏是游戏设置的分类）：几组连成一张列表，分组名当小标题。
// 键的图标用 Steam 自带的那套（controller_base/images/api/knockout 里的单色版，复制到 Assets/Pad/Glyphs），
// 白色带镂空，按我们的字色和透明度画；快速设置键、Claw 键 Steam 没有图标，还用自己画的。
using System.Text.Json.Nodes;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Windows.UI;
using Rect = Windows.Foundation.Rect;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // ---------------- Steam 的按键图标 ----------------

    private static readonly Dictionary<string, string> GpGlyphFiles = new()
    {
        ["A"] = "shared_button_a", ["B"] = "shared_button_b", ["X"] = "shared_button_x", ["Y"] = "shared_button_y",
        ["LB"] = "xbox_lb", ["RB"] = "xbox_rb", ["LT"] = "xbox_lt", ["RT"] = "xbox_rt",
        ["Back"] = "xbox_button_select", ["Start"] = "xbox_button_start", ["Guide"] = "xbox_button_logo",
        ["Up"] = "shared_dpad_up", ["Down"] = "shared_dpad_down", ["Left"] = "shared_dpad_left", ["Right"] = "shared_dpad_right",
        ["LS"] = "shared_lstick_click", ["RS"] = "shared_rstick_click", ["LStick"] = "shared_lstick", ["RStick"] = "shared_rstick",
        ["M1"] = "shared_m1", ["M2"] = "shared_m2",
    };
    private readonly Dictionary<string, CanvasBitmap> _gpGlyphBmps = new();

    private async Task GpLoadGlyphs(string dir)
    {
        foreach (var (id, file) in GpGlyphFiles)
        {
            string path = Path.Combine(dir, file + ".png");
            if (_gpGlyphBmps.ContainsKey(id) || !File.Exists(path)) continue;
            try { _gpGlyphBmps[id] = await CanvasBitmap.LoadAsync(G.Device, path); }
            catch (Exception ex) { Log.Warn($"glyph {file}: {ex.Message}"); }
        }
    }

    /// <summary>一个键的图标，画在 size × size 的框里（左边缘 x、竖向中线 cy）；glow = 后面一圈淡光（焦点）。</summary>
    private void GpPaintGlyph(CanvasDrawingSession ds, string id, float x, float cy, float size, Color col, bool glow)
    {
        if (glow) ds.FillCircle(x + size / 2, cy, size * 0.62f, Gfx.WithAlpha(col, 0.12f));
        if (_gpGlyphBmps.TryGetValue(id, out var bmp))
        {
            ds.DrawImage(bmp, new Rect(x, cy - size / 2, size, size), bmp.Bounds, col.A / 255f, CanvasImageInterpolation.HighQualityCubic);
            return;
        }
        GpDrawGlyph(ds, id, x + (size - GpGlyphWidth(id)) / 2, cy, col, false);
    }

    // ---------------- 映射写成字（照 Steam 的叫法） ----------------

    private static string GpSteamName(string src) => src switch
    {
        "Up" => "十字上键", "Down" => "十字下键", "Left" => "十字左键", "Right" => "十字右键",
        "LS" => "左摇杆按下", "RS" => "右摇杆按下", "LStick" => "左摇杆", "RStick" => "右摇杆",
        "Back" => "View 键", "Start" => "Menu 键", "Guide" => "Xbox 键", "" => "无",
        _ => $"{src} 键",
    };

    private static string GpSteamCommand(JsonNode? cmd)
    {
        switch (Str(cmd?["type"]))
        {
            case "keyboard":
            {
                var keys = (cmd!["keys"] as JsonArray ?? new JsonArray()).Select(k => Str(k) ?? "").Where(k => k.Length > 0)
                    .Select(k => GpAllCells.Where(c => c.Cmd == "key:" + k).Select(c => c.Label).FirstOrDefault() ?? k).ToArray();
                return keys.Length == 0 ? "无" : string.Join(" + ", keys) + " 键";
            }
            case "mouse":
                if (cmd!["wheel"] != null)
                    return Str(cmd["wheel"]) switch { "Up" => "滚轮上", "Down" => "滚轮下", "Left" => "滚轮左", _ => "滚轮右" };
                return Str(cmd["button"]) switch
                {
                    "Left" => "鼠标左键点击", "Right" => "鼠标右键点击", "Middle" => "鼠标中键点击", "Back" => "鼠标后退键", "Forward" => "鼠标前进键", _ => "鼠标",
                };
            case "gamepad": return GpSteamName(Str(cmd!["button"]) ?? "");
            case "system": return GpCommandText(cmd);
            default: return "无";
        }
    }

    /// <summary>固件键码写成字：全是键盘键 →「Alt + F12 键」；一个手柄键 / 鼠标键 → 它的叫法。</summary>
    private static string GpCodesSteam(int[] codes)
    {
        if (codes.Length == 1 && codes[0] == 0xFF) return "禁用";
        var cells = codes.Select(c => GpAllCells.FirstOrDefault(x => x.Code == c)).ToList();
        if (cells.Count > 0 && cells.All(c => c.Cmd?.StartsWith("key:") == true)) return string.Join(" + ", cells.Select(c => c.Label)) + " 键";
        if (cells.Count == 1 && cells[0].Cmd is { Length: > 0 } cmd)
            return cmd.StartsWith("pad:") ? GpSteamName(cmd[4..]) : GpSteamCommand(GpCommandFromCell(cells[0], new List<GpCell>()));
        return GpCodesText(codes);
    }

    /// <summary>这个键现在映射到什么（字）：没改过的就是它自己（「LB 键」「十字上键」）；几个触发方式用「 / 」连起来。</summary>
    private string GpMapText(GpItem it, string src, bool desktop)
    {
        if (it.Choices != null || GpIsSpecial(src)) return it.Value;
        if (desktop)
        {
            if (_gpFw == null || _gpFw["desktopKeys"] == null) return "—";
            return GpDesktopStock(src) ? GpSteamName(src) : GpCodesSteam(GpDesktopCodes(src)!);
        }
        if (GpActs(src) is JsonArray acts)
        {
            if (acts.Count == 0) return "禁用";
            var parts = acts.Take(3).Select(a =>
            {
                int t = Math.Max(0, Array.IndexOf(GpActIds, Str(a?["trigger"]) ?? "regular"));
                string c = GpSteamCommand(a?["command"]);
                return t == 0 ? c : $"{GpActTypes[t]} {c}";
            });
            return string.Join(" / ", parts) + (acts.Count > 3 ? " …" : "");
        }
        var codes = GpFirmwareCodes(src);
        if (codes.Length == 0) return _gpFw == null && GpFirmwareSource(src) ? "—" : GpSteamName(src);
        if (codes.Length == 1 && codes[0] == GpNativeCode(src)) return GpSteamName(src);
        return GpCodesSteam(codes);
    }

    // ---------------- 分组 ----------------

    private static readonly (string Title, (string Section, string[] Keys)[] Parts)[] GpGroupDefs =
    {
        ("按键", new[] { ("正面按键", new[] { "A", "B", "X", "Y" }), ("肩键", new[] { "LB", "RB" }), ("功能键", new[] { "Back", "Start", "QS", "Claw" }) }),
        ("十字键", new[] { ("十字键", new[] { "Up", "Down", "Left", "Right" }) }),
        ("扳机键", new[] { ("扳机键", new[] { "LT", "RT" }) }),
        ("摇杆", new[] { ("左摇杆", new[] { "LStick", "LS" }), ("右摇杆", new[] { "RStick", "RS" }) }),
        ("背键", new[] { ("背键", new[] { "M1", "M2" }) }),
    };

    /// <summary>这一页有哪些组：桌面模式只有固件管得到的键；某款游戏自己的布局里没有快速设置键、Claw 键、摇杆互换（那些是全局的）。</summary>
    private List<(string Title, (string Section, string[] Keys)[] Parts)> GpGroups(bool desktop)
    {
        bool inGame = _gpGame != null;
        bool Keep(string k) => desktop ? GpFirmwareSource(k) && k is not ("M1" or "M2") : !(inGame && (GpIsSpecial(k) || k is "LStick" or "RStick"));
        return GpGroupDefs
            .Select(g => (g.Title, Parts: g.Parts.Select(p => (p.Section, Keys: p.Keys.Where(Keep).ToArray())).Where(p => p.Keys.Length > 0).ToArray()))
            .Where(g => g.Parts.Length > 0).ToList();
    }

    /// <summary>某个键所在分组页的状态，焦点落在这个键那行。</summary>
    private GpPickerState GpGroupState(string src, bool desktop)
    {
        var groups = GpGroups(desktop);
        int gi = Math.Max(0, groups.FindIndex(g => g.Parts.Any(p => p.Keys.Contains(src))));
        var p = new GpPickerState { Source = src, Desktop = desktop, Level = GpSubLevel.Group, Group = gi };
        var rows = GpGroupRows(p);
        int at = rows.FindIndex(r => r.Glyph == src);
        p.GroupFocus = at >= 0 ? at : Math.Max(0, rows.FindIndex(r => !r.Section));
        return p;
    }

    /// <summary>映射页上按 A：进这个键所在的分组页。</summary>
    private void GpOpenGroup(string src, bool desktop)
    {
        SettingsResetPointer();
        _gpPickScroll = 0;
        _gpPicker = GpGroupState(src, desktop);
        PaintGpBody();
    }

    private List<PerfRow> GpGroupRows(GpPickerState p)
    {
        var groups = GpGroups(p.Desktop);
        var rows = new List<PerfRow>();
        if (groups.Count == 0) return rows;
        var parts = _gpOpen ? groups[Math.Clamp(p.Group, 0, groups.Count - 1)].Parts : groups.SelectMany(g => g.Parts).ToArray();
        foreach (var (section, keys) in parts)
        {
            rows.Add(new PerfRow { Section = true, Title = section });
            foreach (var k in keys) rows.Add(GpGroupRow(p, k));
        }
        return rows;
    }

    private PerfRow GpGroupRow(GpPickerState p, string src)
    {
        var it = GpMapCell(src, p.Desktop);
        if (it.Choices != null)
        {
            // 摇杆输出：左右互换（固件一个开关）
            var choices = it.Choices;
            return new PerfRow
            {
                Title = "输出", Glyph = src, Choices = choices, Choice = it.Choice, Disabled = it.Disabled,
                Select = i => { if (i != it.Choice) it.SetChoice?.Invoke(i); PaintGpBody(); },
                Step = d => { int ni = Math.Clamp(it.Choice + d, 0, choices.Length - 1); if (ni != it.Choice) { it.SetChoice?.Invoke(ni); PaintGpBody(); } },
            };
        }
        return new PerfRow
        {
            Title = GpSourceName(src), Glyph = src, Value = GpMapText(it, src, p.Desktop) + "  ›", Disabled = it.Disabled,
            Press = () =>
            {
                p.Source = src; p.Act = 0; p.Cell = 0; p.InTabs = false; p.Chord.Clear();
                if (GpIsSpecial(src)) { p.Level = GpSubLevel.Key; p.FromGroup = true; p.KeyFocus = 1; p.Tab = 4; }   // 专用功能键：先看它的页（常规场景 / Steam 大屏）
                else { p.Level = GpSubLevel.Chooser; p.DirectChooser = true; p.Tab = 0; }
                PaintGpBody();
            },
        };
    }

    private void GpGroupMove(GpPickerState p, Direction dir)
    {
        var rows = GpGroupRows(p);
        int focus = Math.Clamp(p.GroupFocus, 0, Math.Max(0, rows.Count - 1));
        if (dir is Direction.Up or Direction.Down)
        {
            int d = dir == Direction.Up ? -1 : 1;
            for (int i = focus + d; i >= 0 && i < rows.Count; i += d)
                if (!rows[i].Section) { focus = i; break; }
            p.GroupFocus = focus;
        }
        else if (focus < rows.Count && rows[focus].Step != null && !rows[focus].Disabled) rows[focus].Step!(dir == Direction.Left ? -1 : 1);
        PaintGpBody();
    }

    /// <summary>LB / RB：换分组（游戏设置里是一张列表，不用换）。</summary>
    private void GpGroupSwitch(GpPickerState p, int delta)
    {
        if (!_gpOpen) return;
        int n = GpGroups(p.Desktop).Count;
        if (n == 0) return;
        p.Group = ((p.Group + delta) % n + n) % n;
        var rows = GpGroupRows(p);
        p.GroupFocus = Math.Max(0, rows.FindIndex(r => !r.Section));
        _gpPickScroll = 0;
        PaintGpBody();
    }

    /// <summary>Y：分组页上进焦点这个键的按键页（触发方式、触发设置、恢复原键位）。</summary>
    private bool GpGroupY()
    {
        if (_gpPicker is not { Level: GpSubLevel.Group } p) return false;
        var rows = GpGroupRows(p);
        if (p.GroupFocus < 0 || p.GroupFocus >= rows.Count || rows[p.GroupFocus] is not { Glyph: string src, Choices: null, Disabled: false }) return true;
        p.Source = src; p.Act = 0; p.Level = GpSubLevel.Key; p.FromGroup = true; p.KeyFocus = 1; p.Tab = GpIsSpecial(src) ? 4 : 0;
        PaintGpBody();
        return true;
    }

    // ---------------- 画 ----------------

    private void PaintGpGroup(CanvasDrawingSession ds, GpPickerState p)
    {
        var rows = GpGroupRows(p);
        p.GroupFocus = Math.Clamp(p.GroupFocus, 0, Math.Max(0, rows.Count - 1));
        var groups = GpGroups(p.Desktop);
        string title = _gpOpen && groups.Count > 0 ? groups[Math.Clamp(p.Group, 0, groups.Count - 1)].Title : "按键映射";
        PText(ds, p.Desktop ? $"{title}（桌面模式）" : title, TextKit.Ui(700), 22, CcText, GpPickX, 140, 900);
        PaintGpRows(ds, rows, p.GroupFocus);
        GpHints(ds, ("A", "选择指令"), ("Y", "触发设置"), ("LB / RB", "换分组"), ("B", "返回"));
    }

    /// <summary>分组页左边那列（代替手柄页的分类）：当前组白字粗一点，其余淡；点一下换过去。</summary>
    private void PaintGpGroupList(CanvasDrawingSession ds, GpPickerState p)
    {
        var groups = GpGroups(p.Desktop);
        float y0 = 196 + PerfSecH + PerfRowH / 2;   // 和右边第一行对齐
        for (int i = 0; i < groups.Count; i++)
        {
            float cy = y0 + i * GpCatH;
            bool cur = i == p.Group;
            PText(ds, groups[i].Title, cur ? TextKit.Ui(600) : TextKit.Ui(400), 17, cur ? CcText : Gfx.WithAlpha(CcText, 0.45f), GpSideX + 16, cy, 200);
            _gpTargets.Add(new(i, SettingsTargetKind.Tab, 32, cy - GpCatH / 2, 138, GpCatH));
        }
    }
}
