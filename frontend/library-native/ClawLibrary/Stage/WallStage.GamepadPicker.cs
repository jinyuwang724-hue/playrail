// 控制中心 → 手柄 → 按键映射里，选中线稿上一个键按 A 之后的几层（结构照 Steam 输入，样式是我们自己的设计系统）：
//   按键页：这个键的触发方式列表（触发方式 / 指令 / 设置），移除指令、恢复原键位、添加触发方式。
//   选择指令：顶上一排页签（手柄 / 鼠标 / 键盘 / 数字键盘 / 系统，LB / RB 或在页签上 ← → 切），下面按实物布局摆键；
//            键盘里 Ctrl / Shift / Alt / Win 按一下是叠加（组合键），再按一个普通键完成。最多 5 个键码（固件上限）。
//   触发设置：连发、触感、延迟、切换、可中断……照 Steam；这些要软件输入层，先摆上、写「开发中」。
// 写入：普通按压 + 手柄 / 键盘 / 鼠标 / 媒体键 → 固件（后台 set_back_key / set_key_map / set_desktop_key_map），零延迟；
// 长按、双击、组合触发、系统动作等要软件输入层（另一个会话在做），没上线时提示。键码照 MSI KeyMapDefine。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Rect = Windows.Foundation.Rect;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum GpSubLevel { Key, Chooser, Settings }

    /// <summary>键格：名字、键码（-1 = 要软件输入层）、位置（以格为单位的列、行）、宽度（格）。</summary>
    private readonly record struct GpCell(string Label, int Code, float Col, float Row, float Units = 1);

    private sealed class GpPickerState
    {
        public string Source = "";
        public bool Desktop;
        public GpSubLevel Level = GpSubLevel.Key;
        public int KeyFocus = 1, SettingsFocus = 1, Tab, Cell;
        public bool InTabs;
        public readonly List<int> Chord = new();
        public int[] Current = Array.Empty<int>();
    }

    private GpPickerState? _gpPicker;

    private static readonly string[] GpActTypes = { "普通按压", "长按", "双击", "开始按压", "释放按压", "组合键" };
    private static readonly string[] GpChooserTabs = { "手柄", "鼠标", "键盘", "数字键盘", "系统" };
    private const string GpNeedsLayer = "要用软件输入层（另一个会话正在做），上线后就能用";

    // ---------------- 键表（按实物布局摆，列 / 行以格为单位） ----------------

    private static readonly GpCell[] GpPadCells =
    {
        new("LT", 0x13, 0, 0, 2), new("LB", 0x05, 0, 1, 2),
        new("↑", 0x15, 1, 2.6f), new("←", 0x17, 0, 3.6f), new("L 按下", 0x07, 1, 3.6f), new("→", 0x18, 2, 3.6f), new("↓", 0x16, 1, 4.6f),
        new("上", 0x01, 1, 6), new("左", 0x03, 0, 7), new("右", 0x04, 2, 7), new("下", 0x02, 1, 8),
        new("View", 0x0E, 5.5f, 0, 1.5f), new("Menu", 0x0F, 7.5f, 0, 1.5f), new("Xbox", 0x0D, 5.5f, 1, 1.5f), new("截图", 0x10, 7.5f, 1, 1.5f),
        new("RT", 0x14, 12, 0, 2), new("RB", 0x06, 12, 1, 2),
        new("Y", 0x0C, 13, 2.6f), new("X", 0x0B, 12, 3.6f), new("B", 0x0A, 14, 3.6f), new("A", 0x09, 13, 4.6f),
        new("↑", 0x19, 13, 6), new("←", 0x1B, 12, 7), new("R 按下", 0x08, 13, 7), new("→", 0x1C, 14, 7), new("↓", 0x1A, 13, 8),
    };

    private static readonly GpCell[] GpMouseCells =
    {
        new("左键", 0xC8, 0, 0, 3), new("中键", 0xC9, 0, 1, 3), new("右键", 0xCA, 0, 2, 3), new("前进键", 0xCC, 0, 3.4f, 3), new("后退键", 0xCB, 0, 4.4f, 3),
        new("滚轮上", 0xCD, 5, 0, 3), new("滚轮下", 0xCE, 5, 1, 3), new("移动至指定位置", -1, 5, 3.4f, 3), new("按像素量移动", -1, 5, 4.4f, 3),
    };

    private static readonly GpCell[] GpKeyboardCells = BuildKeyboardCells();

    private static GpCell[] BuildKeyboardCells()
    {
        var cells = new List<GpCell>();
        void Row(float row, params (string Label, int Code, float Units)[] keys)
        {
            float col = 0;
            foreach (var (label, code, units) in keys) { cells.Add(new(label, code, col, row, units)); col += units + 0.12f; }
        }
        (string, int, float) K(string l, int c, float u = 1) => (l, c, u);
        Row(0, new[] { K("Esc", 0x32) }.Concat(Enumerable.Range(0, 12).Select(i => K($"F{i + 1}", 0x33 + i))).ToArray());
        Row(1.1f, new[] { K("`", 0x3F) }.Concat("1 2 3 4 5 6 7 8 9 0".Split(' ').Select((l, i) => K(l, 0x40 + i))).Concat(new[] { K("-", 0x4A), K("=", 0x4B), K("Backspace", 0x4C, 2) }).ToArray());
        Row(2.2f, new[] { K("Tab", 0x4D, 1.5f) }.Concat("Q W E R T Y U I O P".Split(' ').Select((l, i) => K(l, 0x4E + i))).Concat(new[] { K("[", 0x58), K("]", 0x59), K("\\", 0x5A, 1.5f) }).ToArray());
        Row(3.3f, new[] { K("Caps", 0x5B, 1.75f) }.Concat("A S D F G H J K L".Split(' ').Select((l, i) => K(l, 0x5C + i))).Concat(new[] { K(";", 0x65), K("'", 0x66), K("Enter", 0x67, 2.4f) }).ToArray());
        Row(4.4f, new[] { K("Shift", 0x68, 2.3f) }.Concat("Z X C V B N M".Split(' ').Select((l, i) => K(l, 0x69 + i))).Concat(new[] { K(",", 0x70), K(".", 0x71), K("/", 0x72), K("Shift", 0x73, 2.9f) }).ToArray());
        Row(5.5f, K("Ctrl", 0x74, 1.5f), K("Win", 0x75, 1.5f), K("Alt", 0x76, 1.5f), K("空格", 0x77, 6.2f), K("Alt", 0x78, 1.5f), K("Ctrl", 0x79, 1.5f));
        return cells.ToArray();
    }

    private static readonly GpCell[] GpNumpadCells =
    {
        new("Insert", 0x7A, 0, 0, 1.3f), new("Home", 0x7B, 1.42f, 0, 1.3f), new("PgUp", 0x7C, 2.84f, 0, 1.3f),
        new("Delete", 0x7D, 0, 1.1f, 1.3f), new("End", 0x7E, 1.42f, 1.1f, 1.3f), new("PgDn", 0x7F, 2.84f, 1.1f, 1.3f),
        new("↑", 0x80, 1.42f, 3.3f, 1.3f), new("←", 0x82, 0, 4.4f, 1.3f), new("↓", 0x81, 1.42f, 4.4f, 1.3f), new("→", 0x83, 2.84f, 4.4f, 1.3f),
        new("播放", 0x95, 5, 0, 1.4f), new("停止", 0x96, 6.52f, 0, 1.4f), new("上一首", 0x98, 5, 1.1f, 1.4f), new("下一首", 0x97, 6.52f, 1.1f, 1.4f),
        new("Num", 0x84, 9.5f, 0), new("/", 0x85, 10.62f, 0), new("*", 0x86, 11.74f, 0), new("-", 0x87, 12.86f, 0),
        new("7", 0x92, 9.5f, 1.1f), new("8", 0x93, 10.62f, 1.1f), new("9", 0x94, 11.74f, 1.1f), new("+", 0x88, 12.86f, 1.1f),
        new("4", 0x8F, 9.5f, 2.2f), new("5", 0x90, 10.62f, 2.2f), new("6", 0x91, 11.74f, 2.2f),
        new("1", 0x8C, 9.5f, 3.3f), new("2", 0x8D, 10.62f, 3.3f), new("3", 0x8E, 11.74f, 3.3f), new("Enter", 0x8A, 12.86f, 3.3f),
        new("0", 0x8B, 9.5f, 4.4f, 2.12f), new(".", 0x89, 11.74f, 4.4f),
    };

    private static readonly GpCell[] GpSystemCells =
    {
        new("音量 +", 0x99, 0, 0, 2.5f), new("音量 −", 0x9A, 0, 1.1f, 2.5f), new("静音", 0x9B, 0, 2.2f, 2.5f),
        new("截图", -1, 3.2f, 0, 3), new("显示键盘", -1, 3.2f, 1.1f, 3), new("强制关闭游戏", -1, 3.2f, 2.2f, 3),
        new("打开控制中心", -1, 6.9f, 0, 3), new("Steam 快速菜单", -1, 6.9f, 1.1f, 3), new("切换超分", -1, 6.9f, 2.2f, 3),
    };

    private static GpCell[] GpChooserCells(int tab) => tab switch { 0 => GpPadCells, 1 => GpMouseCells, 2 => GpKeyboardCells, 3 => GpNumpadCells, _ => GpSystemCells };

    private static readonly int[] GpModifiers = { 0x68, 0x73, 0x74, 0x75, 0x76, 0x78, 0x79 };

    /// <summary>键码 → 显示名（映射说明里用）。</summary>
    private static string GpCodeName(int code)
    {
        foreach (var (cells, prefix) in new[] { (GpPadCells, "手柄 "), (GpMouseCells, "鼠标"), (GpKeyboardCells, ""), (GpNumpadCells, ""), (GpSystemCells, "") })
            foreach (var c in cells)
                if (c.Code == code)
                    return cells == GpPadCells && code is >= 0x15 and <= 0x18 ? $"左摇杆 {c.Label}"
                         : cells == GpPadCells && code is >= 0x19 and <= 0x1C ? $"右摇杆 {c.Label}"
                         : cells == GpNumpadCells && code is >= 0x84 and <= 0x94 ? $"小键盘 {c.Label}" : prefix + c.Label;
        return code switch { 0x11 => "M1", 0x12 => "M2", 0x9C => "F23", 0x9D => "F22", 0xFF => "不映射", _ => $"0x{code:X2}" };
    }

    // ---------------- 开 / 关 ----------------

    private void GpOpenPicker(string source, bool desktop)
    {
        JsonNode? codes = source switch
        {
            "M1" => _gpFw?["backKeys"]?["m1"]?["codes"], "M2" => _gpFw?["backKeys"]?["m2"]?["codes"],
            _ => desktop ? _gpFw?["desktopKeys"]?[source]?["codes"] : _gpFw?["keys"]?[source]?["codes"],
        };
        var current = (codes as JsonArray)?.Select(n => Int(n)).Where(c => c is > 0 and <= 0xFF).ToArray() ?? Array.Empty<int>();
        _gpPicker = new GpPickerState { Source = source, Desktop = desktop, Current = current };
        PaintGpBody();
    }

    private void GpPickerBack()
    {
        var p = _gpPicker!;
        if (p.Level != GpSubLevel.Key) { p.Level = GpSubLevel.Key; p.Chord.Clear(); p.InTabs = false; PaintGpBody(); return; }
        _gpPicker = null;
        PaintGpBody();
    }

    /// <summary>LB / RB：选择指令里换页签。</summary>
    private void GpSwitchTab(int delta)
    {
        var p = _gpPicker;
        if (p?.Level != GpSubLevel.Chooser) return;
        p.Tab = (p.Tab + delta + GpChooserTabs.Length) % GpChooserTabs.Length;
        p.Cell = 0;
        p.Chord.Clear();
        PaintGpBody();
    }

    // ---------------- 按键页 / 触发设置的行 ----------------

    private string GpCurrentText(GpPickerState p) =>
        p.Current.Length == 0 ? (_gpFw == null ? "—" : "原键位")
        : p.Current.Length == 1 && p.Current[0] == GpNativeCode(p.Source) ? "原键位"
        : string.Join(" + ", p.Current.Select(GpCodeName));

    private List<PerfRow> GpKeyRows(GpPickerState p) => new()
    {
        new() { Section = true, Title = "触发 1" },
        new()
        {
            Title = "触发方式", Choices = GpActTypes, Choice = 0,
            Step = d => { if (d > 0) Toast($"{GpActTypes[1]}、{GpActTypes[2]}等{GpNeedsLayer}"); },
            Press = () => Toast($"{GpActTypes[1]}、{GpActTypes[2]}等{GpNeedsLayer}"),
        },
        new() { Title = "指令", Value = GpCurrentText(p) + "  ›", Press = () => { p.Level = GpSubLevel.Chooser; p.Cell = 0; p.InTabs = false; PaintGpBody(); } },
        new() { Title = "设置", Value = "连发 关 · 切换 关 · 延迟 0 ms  ›", Press = () => { p.Level = GpSubLevel.Settings; p.SettingsFocus = 1; PaintGpBody(); } },
        new() { Section = true, Title = "这个键" },
        new() { Title = "添加触发方式", Value = "开发中", Disabled = true, Press = () => Toast($"一个键叠多个触发方式{GpNeedsLayer}") },
        new() { Title = "移除指令", Value = "按下不起作用  ›", Press = () => GpApplyMapping(new[] { 0xFF }) },
        new() { Title = "恢复原键位", Value = $"{GpSourceName(p.Source)}自己  ›", Press = () => GpApplyMapping(new[] { GpNativeCode(p.Source) }) },
    };

    private List<PerfRow> GpSettingsRows()
    {
        PerfRow Later(string title, string value) => new() { Title = title, Value = value, Disabled = true, Press = () => Toast(GpNeedsLayer) };
        return new List<PerfRow>
        {
            new() { Section = true, Title = "普通按压设置" },
            Later("按住以重复（连发）", "关"),
            Later("连发频率", "—"),
            Later("触感强度", "关闭"),
            Later("触发启动延迟", "0 ms"),
            Later("触发结束延迟", "0 ms"),
            Later("指令循环", "关"),
            Later("切换", "关"),
            Later("可中断", "开"),
            Later("反转输入", "关"),
        };
    }

    // ---------------- 输入 ----------------

    private void GpPickerMove(Direction dir)
    {
        var p = _gpPicker!;
        switch (p.Level)
        {
            case GpSubLevel.Key or GpSubLevel.Settings:
            {
                var rows = p.Level == GpSubLevel.Key ? GpKeyRows(p) : GpSettingsRows();
                int focus = p.Level == GpSubLevel.Key ? p.KeyFocus : p.SettingsFocus;
                if (dir is Direction.Up or Direction.Down)
                {
                    int d = dir == Direction.Up ? -1 : 1;
                    for (int i = focus + d; i >= 0 && i < rows.Count; i += d)
                        if (!rows[i].Section) { focus = i; break; }
                }
                else if (focus >= 0 && focus < rows.Count) rows[focus].Step?.Invoke(dir == Direction.Left ? -1 : 1);
                if (p.Level == GpSubLevel.Key) p.KeyFocus = focus; else p.SettingsFocus = focus;
                break;
            }
            default:
            {
                if (p.InTabs)
                {
                    if (dir is Direction.Left or Direction.Right) { GpSwitchTab(dir == Direction.Left ? -1 : 1); return; }
                    if (dir == Direction.Down) p.InTabs = false;
                    break;
                }
                var cells = GpChooserCells(p.Tab);
                var c0 = GpCellRect(cells[p.Cell]);
                var from = new Vector2((float)(c0.X + c0.Width / 2), (float)(c0.Y + c0.Height / 2));
                int best = -1;
                double bestScore = double.MaxValue;
                for (int pass = 0; pass < 2 && best < 0; pass++)
                    for (int i = 0; i < cells.Length; i++)
                    {
                        if (i == p.Cell) continue;
                        var r = GpCellRect(cells[i]);
                        var v = new Vector2((float)(r.X + r.Width / 2), (float)(r.Y + r.Height / 2)) - from;
                        float along = dir switch { Direction.Right => v.X, Direction.Left => -v.X, Direction.Down => v.Y, _ => -v.Y };
                        float across = dir is Direction.Left or Direction.Right ? Math.Abs(v.Y) : Math.Abs(v.X);
                        if (along <= 4 || (pass == 0 && across > along * 1.5f)) continue;
                        double score = along + across * 2;
                        if (score < bestScore) { bestScore = score; best = i; }
                    }
                if (best >= 0) p.Cell = best;
                else if (dir == Direction.Up) p.InTabs = true;
                break;
            }
        }
        PaintGpBody();
    }

    private void GpPickerActivate()
    {
        var p = _gpPicker!;
        if (p.Level is GpSubLevel.Key or GpSubLevel.Settings)
        {
            var rows = p.Level == GpSubLevel.Key ? GpKeyRows(p) : GpSettingsRows();
            int focus = p.Level == GpSubLevel.Key ? p.KeyFocus : p.SettingsFocus;
            if (focus >= 0 && focus < rows.Count) rows[focus].Press?.Invoke();
            return;
        }
        if (p.InTabs) { p.InTabs = false; PaintGpBody(); return; }
        var cell = GpChooserCells(p.Tab)[p.Cell];
        if (cell.Code < 0) { Toast($"「{cell.Label}」{GpNeedsLayer}"); return; }
        if (p.Tab == 2 && Array.IndexOf(GpModifiers, cell.Code) >= 0)
        {
            // 修饰键：叠加 / 取消，等普通键来完成组合
            if (!p.Chord.Remove(cell.Code) && p.Chord.Count < 4) p.Chord.Add(cell.Code);
            PaintGpBody();
            return;
        }
        GpApplyMapping(p.Chord.Append(cell.Code).Take(5).ToArray());
    }

    /// <summary>写映射（固件）：背键 set_back_key，其余 set_key_map / set_desktop_key_map；写完回到按键页，读回真实值。</summary>
    private void GpApplyMapping(int[] codes)
    {
        var p = _gpPicker!;
        p.Current = codes;
        p.Level = GpSubLevel.Key;
        p.Chord.Clear();
        var arr = new JsonArray(codes.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        if (p.Desktop) _ = GpWrite("set_desktop_key_map", JsonValue.Create(p.Source), arr);
        else if (p.Source is "M1" or "M2") _ = GpWrite("set_back_key", JsonValue.Create(p.Source.ToLowerInvariant()), arr);
        else _ = GpWrite("set_key_map", JsonValue.Create(p.Source), arr);
        PaintGpBody();
    }

    // ---------------- 绘制 ----------------

    private const float GpPickX = 250, GpChooserTop = 238, GpCellUnit = 54, GpCellStepX = 60, GpCellStepY = 52, GpCellH = 44;

    private static Rect GpCellRect(GpCell c) => new(GpPickX + c.Col * GpCellStepX, GpChooserTop + c.Row * GpCellStepY, c.Units * GpCellUnit + (c.Units - 1) * (GpCellStepX - GpCellUnit), GpCellH);

    private void PaintGpPicker(CanvasDrawingSession ds, Color accent)
    {
        var p = _gpPicker!;
        var ui = TextKit.Ui(400);
        var muted = Gfx.WithAlpha(CcText, 0.5);
        string where = p.Desktop ? "（桌面模式）" : "";
        switch (p.Level)
        {
            case GpSubLevel.Key:
                PText(ds, GpSourceName(p.Source) + where, TextKit.Ui(700), 22, CcText, GpPickX, 140, 900);
                PText(ds, $"现在：{GpCurrentText(p)}  ·  只用普通按压时写进手柄固件，改了立刻生效、零延迟", ui, 13, muted, GpPickX, 170, 900);
                PaintGpRows(ds, GpKeyRows(p), p.KeyFocus);
                break;
            case GpSubLevel.Settings:
                PText(ds, $"{GpSourceName(p.Source)}{where} · 普通按压", TextKit.Ui(700), 22, CcText, GpPickX, 140, 900);
                PText(ds, "这些设置照 Steam 输入；要用软件输入层，正在开发", ui, 13, muted, GpPickX, 170, 900);
                PaintGpRows(ds, GpSettingsRows(), p.SettingsFocus);
                break;
            default:
                PaintGpChooser(ds, p, accent);
                break;
        }
    }

    /// <summary>按键页 / 触发设置：和性能页同一套行列表（行号、▸ 焦点、几选一下划线），放在分类右边。</summary>
    private void PaintGpRows(CanvasDrawingSession ds, List<PerfRow> rows, int focus)
    {
        var old = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(GpPickX, 196) * old;
        PaintRowList(ds, rows, focus);
        ds.Transform = old;
    }

    /// <summary>选择指令：标题、一排页签（当前页签白字加下划线，焦点在页签上时下划线是强调色）、按实物布局的键格。</summary>
    private void PaintGpChooser(CanvasDrawingSession ds, GpPickerState p, Color accent)
    {
        var ui = TextKit.Ui(400);
        var muted = Gfx.WithAlpha(CcText, 0.5);
        PText(ds, $"选择一个{GpChooserTabs[p.Tab]}指令：{GpSourceName(p.Source)} → 普通按压", TextKit.Ui(700), 22, CcText, GpPickX, 140, 900);
        // 页签：和性能页的几选一同一种写法，平铺文字、当前项下划线；LB / RB 切
        float x = GpPickX, ty = 188;
        PText(ds, "LB", TextKit.Ui(600), 11, Gfx.WithAlpha(CcText, 0.4f), x, ty, 30);
        x += 34;
        for (int i = 0; i < GpChooserTabs.Length; i++)
        {
            bool on = i == p.Tab;
            float w = PWidth(ds, GpChooserTabs[i], ui, 17);
            var col = on ? CcText : Gfx.WithAlpha(CcText, 0.42f);
            PText(ds, GpChooserTabs[i], on ? TextKit.Ui(600) : ui, 17, col, x, ty, w + 4);
            if (on) ds.FillRectangle(x, ty + 14, w, 2, p.InTabs ? accent : col);
            x += w + 26;
        }
        PText(ds, "RB", TextKit.Ui(600), 11, Gfx.WithAlpha(CcText, 0.4f), x, ty, 30);

        // 键格：细边框；当前映射的键描强调色边，焦点键填强调色，叠加中的修饰键淡填；要软件层的写淡一点
        using var font = new CanvasTextFormat { FontFamily = TextKit.Ui(400).Family, FontSize = 14, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
        var cells = GpChooserCells(p.Tab);
        float bottom = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            var rect = GpCellRect(cell);
            bottom = Math.Max(bottom, (float)(rect.Y + rect.Height));
            bool focused = !p.InTabs && i == p.Cell;
            bool current = cell.Code > 0 && Array.IndexOf(p.Current, cell.Code) >= 0;
            bool chord = p.Chord.Contains(cell.Code);
            bool later = cell.Code < 0;
            if (focused) ds.FillRoundedRectangle(rect, 7, 7, Gfx.WithAlpha(accent, 0.45f));
            else if (chord) ds.FillRoundedRectangle(rect, 7, 7, Gfx.WithAlpha(accent, 0.22f));
            ds.DrawRoundedRectangle(rect, 7, 7, current || chord ? Gfx.WithAlpha(accent, 0.95f) : Gfx.WithAlpha(CcText, focused ? 0.6f : later ? 0.08f : 0.16f), current ? 1.6f : 1f);
            ds.DrawText(cell.Label, rect, focused ? CcText : Gfx.WithAlpha(CcText, later ? 0.35f : 0.8f), font);
        }
        if (p.Tab == 0)
        {
            // 手柄页：左右两组上面标一下是哪个摇杆 / 十字键
            foreach (var (label, col, row) in new[] { ("左摇杆", 0f, 2.6f), ("十字键", 0f, 6f), ("右摇杆", 12f, 6f) })
                PText(ds, label, ui, 11, Gfx.WithAlpha(CcText, 0.38f), GpPickX + col * GpCellStepX + 140, GpChooserTop + row * GpCellStepY + 22, 80);
        }
        string hint = p.Tab == 2
            ? (p.Chord.Count > 0 ? $"组合：{string.Join(" + ", p.Chord.Select(GpCodeName))} + …  再按一个普通键完成" : "Ctrl / Shift / Alt / Win 按一下是叠加，可以组合；再按一个普通键完成")
            : p.Tab == 4 ? "音量、静音直接写进固件；其余系统动作要软件输入层" : "A 选定  ·  LB / RB 换页签  ·  B 返回";
        PText(ds, hint, ui, 13, muted, GpPickX, bottom + 30, 900);
    }
}
