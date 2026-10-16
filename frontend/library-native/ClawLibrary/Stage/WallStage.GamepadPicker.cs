// 控制中心 → 手柄 → 按键映射 → 「映射到」（第三级）：选中线稿上一个键按 A 打开，盖在线稿上。
// 左边一列类型：原键位 / 手柄按键 / 键盘 / 鼠标 / 不映射 / 宏（之后）；右边是这一类的键（手柄键按布局排、键盘是一整块键盘、鼠标几个键）。
// 键盘里 Ctrl / Shift / Alt / Win 按一下是叠加（可以组合），再按一个普通键就完成。最多 5 个键码（固件 KeyProfile 的上限）。
// 键码照 MSI Center M 的 KeyMapDefine（和 Linux hid-msi 一致，见 docs/gamepad-firmware-protocol.md）。
// 映射点照 ClawTweaks（背键：手柄 / 键盘 / 鼠标 / 宏）和 MSI Center M（16 个键）。写入走后台 set_back_key / set_key_map，没有时提示。
using System.Numerics;
using ClawLibrary.Engine;
using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Rect = Windows.Foundation.Rect;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    /// <summary>键格：名字、键码、宽度（单位格）。</summary>
    private readonly record struct GpCell(string Label, int Code, float Units = 1);

    private sealed class GpPickerState
    {
        public string Source = "";
        public bool Desktop;
        public int Type, Row, Col;
        public bool InGrid;
        public readonly List<int> Chord = new();
        public int[] Current = Array.Empty<int>();
    }

    private GpPickerState? _gpPicker;

    private static readonly string[] GpPickTypes = { "原键位", "手柄按键", "键盘", "鼠标", "不映射", "宏" };

    private static readonly GpCell[][] GpPadGrid =
    {
        new GpCell[] { new("A", 0x09), new("B", 0x0A), new("X", 0x0B), new("Y", 0x0C), new("LB", 0x05), new("RB", 0x06), new("LT", 0x13), new("RT", 0x14) },
        new GpCell[] { new("上", 0x01), new("下", 0x02), new("左", 0x03), new("右", 0x04), new("左摇杆按下", 0x07, 2), new("右摇杆按下", 0x08, 2) },
        new GpCell[] { new("左摇杆 ↑", 0x15, 2), new("左摇杆 ↓", 0x16, 2), new("左摇杆 ←", 0x17, 2), new("左摇杆 →", 0x18, 2) },
        new GpCell[] { new("右摇杆 ↑", 0x19, 2), new("右摇杆 ↓", 0x1A, 2), new("右摇杆 ←", 0x1B, 2), new("右摇杆 →", 0x1C, 2) },
        new GpCell[] { new("View", 0x0E, 2), new("Menu", 0x0F, 2), new("Xbox", 0x0D, 2), new("截图", 0x10, 2) },
    };

    private static readonly GpCell[][] GpKeyboardGrid = BuildKeyboardGrid();

    private static GpCell[][] BuildKeyboardGrid()
    {
        GpCell[] Row(string labels, int first) => labels.Split(' ').Select((l, i) => new GpCell(l, first + i)).ToArray();
        return new[]
        {
            new[] { new GpCell("Esc", 0x32) }.Concat(Enumerable.Range(0, 12).Select(i => new GpCell($"F{i + 1}", 0x33 + i))).ToArray(),
            new[] { new GpCell("`", 0x3F) }.Concat(Row("1 2 3 4 5 6 7 8 9 0", 0x40)).ToArray(),
            Row("Q W E R T Y U I O P", 0x4E),
            Row("A S D F G H J K L", 0x5C).Concat(new[] { new GpCell("Enter", 0x67, 2) }).ToArray(),
            new[] { new GpCell("Shift", 0x68, 2) }.Concat(Row("Z X C V B N M", 0x69)).ToArray(),
            new GpCell[] { new("Ctrl", 0x74, 1.5f), new("Win", 0x75, 1.5f), new("Alt", 0x76, 1.5f), new("空格", 0x77, 4), new("右 Alt", 0x78, 1.5f), new("右 Ctrl", 0x79, 1.5f) },
            new GpCell[] { new("Ins", 0x7A), new("Home", 0x7B), new("PgUp", 0x7C), new("Del", 0x7D), new("End", 0x7E), new("PgDn", 0x7F), new("↑", 0x80), new("↓", 0x81), new("←", 0x82), new("→", 0x83) },
        };
    }

    private static readonly GpCell[][] GpMouseGrid =
    {
        new GpCell[] { new("左键", 0xC8, 2), new("中键", 0xC9, 2), new("右键", 0xCA, 2) },
        new GpCell[] { new("后退", 0xCB, 2), new("前进", 0xCC, 2) },
        new GpCell[] { new("滚轮上", 0xCD, 2), new("滚轮下", 0xCE, 2) },
    };

    private static readonly int[] GpModifiers = { 0x68, 0x74, 0x75, 0x76, 0x78, 0x79 };

    private static GpCell[][]? GpPickGrid(int type) => type switch { 1 => GpPadGrid, 2 => GpKeyboardGrid, 3 => GpMouseGrid, _ => null };

    /// <summary>键码 → 显示名（映射说明里用）。</summary>
    private static string GpCodeName(int code)
    {
        foreach (var grid in new[] { GpPadGrid, GpKeyboardGrid, GpMouseGrid })
            foreach (var row in grid)
                foreach (var c in row)
                    if (c.Code == code) return grid == GpPadGrid ? $"手柄 {c.Label}" : grid == GpMouseGrid ? $"鼠标{c.Label}" : c.Label;
        return code switch { 0x11 => "M1", 0x12 => "M2", 0x9C => "F23", 0x9D => "F22", 0xFF => "不映射", _ => $"0x{code:X2}" };
    }

    // ---------------- 开 / 关 ----------------

    private void GpOpenPicker(string source, bool desktop)
    {
        JsonNode? codes = source switch
        {
            "M1" => _gpFw?["backKeys"]?["m1"]?["codes"], "M2" => _gpFw?["backKeys"]?["m2"]?["codes"],
            "LT" => _gpFw?["triggers"]?["left"]?["codes"], "RT" => _gpFw?["triggers"]?["right"]?["codes"], _ => null,
        };
        var current = (codes as JsonArray)?.Select(n => Int(n)).Where(c => c is > 0 and < 0xFF).ToArray() ?? Array.Empty<int>();
        int type = current.Length == 0 || current.Length == 1 && current[0] == GpNativeCode(source) ? 0
            : current.Any(c => c >= 0xC8) ? 3 : current.Any(c => c >= 0x32) ? 2 : 1;
        _gpPicker = new GpPickerState { Source = source, Desktop = desktop, Type = desktop && type < 2 ? 2 : type, Current = current };
        PaintGpBody();
    }

    private void GpPickerBack()
    {
        var p = _gpPicker!;
        if (p.InGrid) { p.InGrid = false; p.Chord.Clear(); PaintGpBody(); return; }
        _gpPicker = null;
        PaintGpBody();
    }

    // ---------------- 输入 ----------------

    private void GpPickerMove(Direction dir)
    {
        var p = _gpPicker!;
        if (!p.InGrid)
        {
            if (dir is Direction.Up or Direction.Down)
            {
                int t = Math.Clamp(p.Type + (dir == Direction.Up ? -1 : 1), 0, GpPickTypes.Length - 1);
                if (t == p.Type) return;
                p.Type = t; p.Row = 0; p.Col = 0; p.Chord.Clear();
                PaintGpBody();
            }
            else if (dir == Direction.Right && GpPickGrid(p.Type) != null) { p.InGrid = true; PaintGpBody(); }
            return;
        }
        var grid = GpPickGrid(p.Type)!;
        switch (dir)
        {
            case Direction.Left when p.Col == 0: p.InGrid = false; break;
            case Direction.Left: p.Col--; break;
            case Direction.Right: p.Col = Math.Min(p.Col + 1, grid[p.Row].Length - 1); break;
            case Direction.Up or Direction.Down:
            {
                int nr = Math.Clamp(p.Row + (dir == Direction.Up ? -1 : 1), 0, grid.Length - 1);
                if (nr == p.Row) return;
                // 换行时按水平位置对齐到最近的那个键（键宽不一样）
                float x = GpCellX(grid[p.Row], p.Col) + GpCellW(grid[p.Row][p.Col]) / 2;
                int best = 0; float bestD = float.MaxValue;
                for (int c = 0; c < grid[nr].Length; c++)
                {
                    float d = Math.Abs(GpCellX(grid[nr], c) + GpCellW(grid[nr][c]) / 2 - x);
                    if (d < bestD) { bestD = d; best = c; }
                }
                p.Row = nr; p.Col = best;
                break;
            }
        }
        PaintGpBody();
    }

    private void GpPickerActivate()
    {
        var p = _gpPicker!;
        if (!p.InGrid)
        {
            switch (p.Type)
            {
                case 0: GpApplyMapping(new[] { GpNativeCode(p.Source) }); return;
                case 4: GpApplyMapping(new[] { 0xFF }); return;
                case 5: Toast("宏之后做：录制、步骤间隔、按住 / 切换方式"); return;
                default: p.InGrid = true; PaintGpBody(); return;
            }
        }
        var cell = GpPickGrid(p.Type)![p.Row][p.Col];
        if (p.Type == 2 && Array.IndexOf(GpModifiers, cell.Code) >= 0)
        {
            // 修饰键：叠加 / 取消，等普通键来完成组合
            if (!p.Chord.Remove(cell.Code) && p.Chord.Count < 4) p.Chord.Add(cell.Code);
            PaintGpBody();
            return;
        }
        GpApplyMapping(p.Chord.Append(cell.Code).Take(5).ToArray());
    }

    /// <summary>写映射：背键走 set_back_key（后台已有方案），其余键走 set_key_map / set_desktop_key_map；后台没有这些方法时提示。</summary>
    private void GpApplyMapping(int[] codes)
    {
        var p = _gpPicker!;
        _gpPicker = null;
        var arr = new JsonArray(codes.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        if (p.Desktop) _ = GpWrite("set_desktop_key_map", JsonValue.Create(p.Source), arr);
        else if (p.Source is "M1" or "M2") _ = GpWrite("set_back_key", JsonValue.Create(p.Source.ToLowerInvariant()), arr);
        else _ = GpWrite("set_key_map", JsonValue.Create(p.Source), arr);
        PaintGpBody();
    }

    // ---------------- 绘制 ----------------

    private const float GpPickX = 250, GpGridX = 430, GpPickTop = 196, GpCellUnit = 52, GpCellH = 46, GpCellGap = 8;

    private static float GpCellW(GpCell c) => c.Units * GpCellUnit + (c.Units - 1) * GpCellGap;
    private static float GpCellX(GpCell[] row, int col)
    {
        float x = 0;
        for (int i = 0; i < col; i++) x += GpCellW(row[i]) + GpCellGap;
        return x;
    }

    private void PaintGpPicker(CanvasDrawingSession ds, Color accent)
    {
        var p = _gpPicker!;
        var ui = TextKit.Ui(400);
        var muted = Gfx.WithAlpha(CcText, 0.5);
        // 标题：哪个键 → 映射到；下面一行是现在的映射
        PText(ds, $"{GpSourceName(p.Source)}{(p.Desktop ? "（桌面模式）" : "")}  映射到", TextKit.Ui(600), 22, CcText, GpPickX, 140, 900);
        string now = p.Current.Length == 0 || p.Current.Length == 1 && p.Current[0] == GpNativeCode(p.Source) ? "原键位" : string.Join(" + ", p.Current.Select(GpCodeName));
        PText(ds, $"现在：{now}", ui, 13, muted, GpPickX, 168, 700);

        // 类型：一列字，当前类白字；焦点在类型上时加 ▸
        for (int i = 0; i < GpPickTypes.Length; i++)
        {
            float cy = GpPickTop + 20 + i * 46;
            bool cur = i == p.Type, later = i == 5;
            var col = cur ? (p.InGrid ? Gfx.WithAlpha(CcText, 0.92f) : CcText) : Gfx.WithAlpha(CcText, later ? 0.28f : 0.48f);
            PText(ds, GpPickTypes[i] + (later ? "  之后" : ""), cur ? TextKit.Ui(600) : ui, 17, col, GpPickX + 16, cy, 160);
            if (cur && !p.InGrid)
            {
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(GpPickX, cy - 6); pb.AddLine(GpPickX + 8, cy); pb.AddLine(GpPickX, cy + 6); pb.EndFigure(CanvasFigureLoop.Closed);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(g, CcText);
            }
        }

        var grid = GpPickGrid(p.Type);
        if (grid == null)
        {
            string text = p.Type switch
            {
                0 => $"按 A 把它还原成{GpSourceName(p.Source)}自己",
                4 => "按 A 让这个键什么都不做",
                _ => "宏（一串按键、步骤间隔、按住 / 切换）之后做",
            };
            PText(ds, text, ui, 15, Gfx.WithAlpha(CcText, 0.7f), GpGridX, GpPickTop + 20, 700);
            return;
        }
        // 键格：细边框；当前映射的键描强调色边，焦点键填强调色，叠加中的修饰键淡填
        using var font = new CanvasTextFormat { FontFamily = TextKit.Ui(400).Family, FontSize = 14, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
        for (int r = 0; r < grid.Length; r++)
            for (int c = 0; c < grid[r].Length; c++)
            {
                var cell = grid[r][c];
                var rect = new Rect(GpGridX + GpCellX(grid[r], c), GpPickTop + r * (GpCellH + GpCellGap), GpCellW(cell), GpCellH);
                bool focused = p.InGrid && r == p.Row && c == p.Col;
                bool current = Array.IndexOf(p.Current, cell.Code) >= 0;
                bool chord = p.Chord.Contains(cell.Code);
                if (focused) ds.FillRoundedRectangle(rect, 7, 7, Gfx.WithAlpha(accent, 0.45f));
                else if (chord) ds.FillRoundedRectangle(rect, 7, 7, Gfx.WithAlpha(accent, 0.22f));
                ds.DrawRoundedRectangle(rect, 7, 7, current || chord ? Gfx.WithAlpha(accent, 0.95f) : Gfx.WithAlpha(CcText, focused ? 0.6f : 0.16f), current ? 1.6f : 1f);
                ds.DrawText(cell.Label, rect, focused ? CcText : Gfx.WithAlpha(CcText, 0.8f), font);
            }
        float hintY = GpPickTop + grid.Length * (GpCellH + GpCellGap) + 22;
        string hint = p.Type == 2
            ? (p.Chord.Count > 0 ? $"组合：{string.Join(" + ", p.Chord.Select(GpCodeName))} + …  再按一个普通键完成" : "Ctrl / Shift / Alt / Win 按一下是叠加，可以组合；再按一个普通键完成")
            : "A 选定  ·  B 返回";
        PText(ds, hint, ui, 13, muted, GpGridX, hintY, 800);
    }
}
