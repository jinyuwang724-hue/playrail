// 控制中心 → 手柄 → 按键映射里，选中线稿上一个键按 A 之后的几层（结构照 Steam 输入，样式是我们自己的设计系统）：
//   按键页：这个键的触发方式（每个一组：触发方式 / 指令 / 设置），添加触发方式、禁用这个键、恢复原键位。
//   选择指令：顶上一排页签（手柄 / 鼠标 / 键盘 / 数字键盘 / 系统，LB / RB 或在页签上 ← → 切），下面按实物布局摆键；
//            键盘里 Ctrl / Shift / Alt / Win 按一下是叠加（组合键），再按一个普通键完成。
//   触发设置：长按时间、双击间隔、连发、切换、启动 / 结束延迟、可中断（触感、指令循环、反转输入第二批）。
// 混合映射，每个键只归一层管（见 docs/input/gamepad-input-layer.md），界面上不分层、不露开关：
//   只用「普通按压 + 固件发得出的键」→ 写固件（set_back_key / set_key_map），零延迟；
//   用到长按 / 双击 / 组合触发、触发设置、系统动作，或者固件管不到的键（View / Menu）→ 这个键进软件输入层的全局布局（save_input_layout），
//   后台按布局自己开关输入层，并把这个键在固件里放回原键、交给软件层。恢复原键位 = 从布局里拿掉 + 固件放回原键。
// 桌面模式只有固件一层（set_desktop_key_map）：普通按压、固件发得出的键。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Rect = Windows.Foundation.Rect;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum GpSubLevel { Key, Chooser, Settings, Group }

    /// <summary>
    /// 键格：名字、固件键码（0 = 固件发不出）、软件层指令（"pad:B" "key:Tab" "mouse:Left" "wheel:Up" "sys:screenshot"，空 = 软件层也没有）、
    /// 位置、宽、高。位置的单位每页不同（见 GpCellRect）：手柄、鼠标页是格子中心的设计 px；键盘、数字键盘页是键位单位（1u = 一个字母键）；系统页是第几列、第几行。
    /// </summary>
    private readonly record struct GpCell(string Label, int Code, string Cmd, float Col, float Row, float Units = 1, float Tall = 1);

    private sealed class GpPickerState
    {
        public string Source = "";
        public bool Desktop;
        public GpSubLevel Level = GpSubLevel.Key;
        public int KeyFocus = 1, SettingsFocus = 1, Tab, Cell, Act;
        public bool InTabs;
        public bool SteamMapping;
        /// <summary>分组页（WallStage.GamepadGroups.cs）：哪一组、焦点在哪一行；从分组页直接进的选择指令 / 按键页，退回分组页。</summary>
        public int Group, GroupFocus = 1;
        public bool FromGroup, DirectChooser;
        public readonly List<GpCell> Chord = new();
    }

    private GpPickerState? _gpPicker;
    private int _gpLayoutEdits;

    private static readonly string[] GpActTypes = { "普通按压", "长按", "双击", "开始按压", "释放按压", "组合键" };
    private static readonly string[] GpActIds = { "regular", "long", "double", "start", "release", "chord" };
    private static readonly string[] GpChooserTabs = { "手柄", "鼠标", "键盘", "数字键盘", "系统" };
    private static readonly string[] GpLayoutSources = { "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "Back", "Start", "Guide", "LS", "RS", "Up", "Down", "Left", "Right", "M1", "M2" };

    // ---------------- 键表 ----------------

    /// <summary>
    /// 手柄页（照 Steam）：中间是 C8EX 线稿，左边肩键、左摇杆、十字键，右边肩键、ABXY、右摇杆，线稿下面 View / Xbox / Menu / 截图。
    /// 每格画键的图形（GpDrawGlyph），不写字；位置是格子中心（设计 px）。
    /// </summary>
    private static readonly GpCell[] GpPadCells =
    {
        new("LT", 0x13, "pad:LT", 318, 236), new("LB", 0x05, "pad:LB", 378, 236),
        new("↑", 0x15, "pad:LStickUp", 330, 318), new("←", 0x17, "pad:LStickLeft", 278, 370), new("L 按下", 0x07, "pad:LS", 330, 370), new("→", 0x18, "pad:LStickRight", 382, 370), new("↓", 0x16, "pad:LStickDown", 330, 422),
        new("上", 0x01, "pad:Up", 330, 500), new("左", 0x03, "pad:Left", 278, 552), new("右", 0x04, "pad:Right", 382, 552), new("下", 0x02, "pad:Down", 330, 604),
        new("View", 0x0E, "pad:Back", 652, 632), new("Xbox", 0x0D, "pad:Guide", 704, 632), new("Menu", 0x0F, "pad:Start", 756, 632), new("截图", 0x10, "", 808, 632),
        new("RB", 0x06, "pad:RB", 1082, 236), new("RT", 0x14, "pad:RT", 1142, 236),
        new("Y", 0x0C, "pad:Y", 1130, 318), new("X", 0x0B, "pad:X", 1078, 370), new("B", 0x0A, "pad:B", 1182, 370), new("A", 0x09, "pad:A", 1130, 422),
        new("↑", 0x19, "pad:RStickUp", 1130, 500), new("←", 0x1B, "pad:RStickLeft", 1078, 552), new("R 按下", 0x08, "pad:RS", 1130, 552), new("→", 0x1C, "pad:RStickRight", 1182, 552), new("↓", 0x1A, "pad:RStickDown", 1130, 604),
    };

    /// <summary>鼠标页（照 Steam）：中间画一只鼠标，按键分在两边、引线连到鼠标上对应的位置；位置是格子中心（设计 px）。</summary>
    private static readonly GpCell[] GpMouseCells =
    {
        new("左键", 0xC8, "mouse:Left", 520, 318), new("前进键", 0xCC, "mouse:Forward", 520, 430), new("后退键", 0xCB, "mouse:Back", 520, 482),
        new("右键", 0xCA, "mouse:Right", 940, 318), new("中键", 0xC9, "mouse:Middle", 940, 378), new("滚轮上", 0xCD, "wheel:Up", 940, 430), new("滚轮下", 0xCE, "wheel:Down", 940, 482),
        new("滚轮左", 0, "wheel:Left", 660, 600), new("滚轮右", 0, "wheel:Right", 800, 600),
    };

    private static readonly GpCell[] GpKeyboardCells = BuildKeyboardCells();

    /// <summary>
    /// 键盘页：标准 87 键主区，每行正好 15u，左右两边对齐（照 Steam）。功能键行 Esc 后空 1u，F1–F4 / F5–F8 / F9–F12 之间各空 0.5u。
    /// </summary>
    private static GpCell[] BuildKeyboardCells()
    {
        var cells = new List<GpCell>();
        void Row(int row, params (string Label, int Code, string Name, float Units, float Gap)[] keys)
        {
            float col = 0;
            foreach (var (label, code, name, units, gap) in keys) { col += gap; cells.Add(new(label, code, "key:" + name, col, row, units)); col += units; }
        }
        (string, int, string, float, float) K(string l, int c, string name, float u = 1, float gap = 0) => (l, c, name, u, gap);
        Row(0, new[] { K("Esc", 0x32, "Esc") }.Concat(Enumerable.Range(0, 12).Select(i => K($"F{i + 1}", 0x33 + i, $"F{i + 1}", 1, i == 0 ? 1 : i is 4 or 8 ? 0.5f : 0))).ToArray());
        Row(1, new[] { K("`", 0x3F, "Grave") }.Concat("1 2 3 4 5 6 7 8 9 0".Split(' ').Select((l, i) => K(l, 0x40 + i, l)))
            .Concat(new[] { K("-", 0x4A, "Minus"), K("=", 0x4B, "Equals"), K("Backspace", 0x4C, "Backspace", 2) }).ToArray());
        Row(2, new[] { K("Tab", 0x4D, "Tab", 1.5f) }.Concat("Q W E R T Y U I O P".Split(' ').Select((l, i) => K(l, 0x4E + i, l)))
            .Concat(new[] { K("[", 0x58, "LeftBracket"), K("]", 0x59, "RightBracket"), K("\\", 0x5A, "Backslash", 1.5f) }).ToArray());
        Row(3, new[] { K("Caps", 0x5B, "CapsLock", 1.75f) }.Concat("A S D F G H J K L".Split(' ').Select((l, i) => K(l, 0x5C + i, l)))
            .Concat(new[] { K(";", 0x65, "Semicolon"), K("'", 0x66, "Quote"), K("Enter", 0x67, "Enter", 2.25f) }).ToArray());
        Row(4, new[] { K("Shift", 0x68, "LShift", 2.25f) }.Concat("Z X C V B N M".Split(' ').Select((l, i) => K(l, 0x69 + i, l)))
            .Concat(new[] { K(",", 0x70, "Comma"), K(".", 0x71, "Period"), K("/", 0x72, "Slash"), K("Shift", 0x73, "RShift", 2.75f) }).ToArray());
        Row(5, K("Ctrl", 0x74, "LCtrl", 1.5f), K("Win", 0x75, "LWin", 1.5f), K("Alt", 0x76, "LAlt", 1.5f), K("空格", 0x77, "Space", 7.5f), K("Alt", 0x78, "RAlt", 1.5f), K("Ctrl", 0x79, "RCtrl", 1.5f));
        return cells.ToArray();
    }

    /// <summary>
    /// 数字键盘页（照 Steam / 全尺寸键盘右半边）：最上一排媒体键，下面左边编辑键 3×2、方向键倒 T，右边小键盘（+ 和 Enter 两格高，0 两格宽）。
    /// 单位 1u，左块 0–2 列，右块 4–7 列。
    /// </summary>
    private static readonly GpCell[] GpNumpadCells =
    {
        new("播放", 0x95, "key:MediaPlayPause", 0, 0, 2), new("停止", 0x96, "key:MediaStop", 2, 0, 2), new("上一首", 0x98, "key:MediaPrev", 4, 0, 2), new("下一首", 0x97, "key:MediaNext", 6, 0, 2),
        new("Insert", 0x7A, "key:Insert", 0, 1.25f), new("Home", 0x7B, "key:Home", 1, 1.25f), new("PgUp", 0x7C, "key:PageUp", 2, 1.25f),
        new("Delete", 0x7D, "key:Delete", 0, 2.25f), new("End", 0x7E, "key:End", 1, 2.25f), new("PgDn", 0x7F, "key:PageDown", 2, 2.25f),
        new("↑", 0x80, "key:Up", 1, 4.25f), new("←", 0x82, "key:Left", 0, 5.25f), new("↓", 0x81, "key:Down", 1, 5.25f), new("→", 0x83, "key:Right", 2, 5.25f),
        new("Num", 0x84, "key:NumLock", 4, 1.25f), new("/", 0x85, "key:NumDivide", 5, 1.25f), new("*", 0x86, "key:NumMultiply", 6, 1.25f), new("-", 0x87, "key:NumSubtract", 7, 1.25f),
        new("7", 0x92, "key:Num7", 4, 2.25f), new("8", 0x93, "key:Num8", 5, 2.25f), new("9", 0x94, "key:Num9", 6, 2.25f), new("+", 0x88, "key:NumAdd", 7, 2.25f, 1, 2),
        new("4", 0x8F, "key:Num4", 4, 3.25f), new("5", 0x90, "key:Num5", 5, 3.25f), new("6", 0x91, "key:Num6", 6, 3.25f),
        new("1", 0x8C, "key:Num1", 4, 4.25f), new("2", 0x8D, "key:Num2", 5, 4.25f), new("3", 0x8E, "key:Num3", 6, 4.25f), new("Enter", 0x8A, "key:NumEnter", 7, 4.25f, 1, 2),
        new("0", 0x8B, "key:Num0", 4, 5.25f, 2), new(".", 0x89, "key:NumDecimal", 6, 5.25f),
    };

    /// <summary>系统页：三列（Claw 和 Windows / Steam / 声音和超分），与专用键共用。两种截图：Game Bar 的存进「捕获」，Steam 的进 Steam 截图。</summary>
    private static readonly GpCell[] GpSystemCells =
    {
        new("Claw 游戏库", 0, "sys:library", 0, 0), new("Claw 控制中心", 0, "sys:controlCenter", 0, 1),
        new("Xbox Game Bar", 0, "sys:gamebar", 0, 2), new("Game Bar 截图", 0, "sys:screenshot", 0, 3), new("显示键盘", 0, "sys:keyboard", 0, 4),
        new("Steam 左侧主菜单", 0, "sys:steamMainMenu", 1, 0), new("Steam 右侧快速访问", 0, "sys:steamQuickAccess", 1, 1),
        new("Steam 游戏库", 0, "sys:steamLibrary", 1, 2), new("Steam 截图", 0, "sys:steamScreenshot", 1, 3),
        new("音量 +", 0x99, "sys:volumeUp", 2, 0), new("音量 −", 0x9A, "sys:volumeDown", 2, 1),
        new("静音", 0x9B, "sys:mute", 2, 2), new("切换超分", 0, "sys:magpieToggle", 2, 3),
    };

    private static GpCell[] GpChooserCells(int tab) => tab switch { 0 => GpPadCells, 1 => GpMouseCells, 2 => GpKeyboardCells, 3 => GpNumpadCells, _ => GpSystemCells };
    private static IEnumerable<GpCell> GpAllCells => GpPadCells.Concat(GpMouseCells).Concat(GpKeyboardCells).Concat(GpNumpadCells).Concat(GpSystemCells);
    private static bool GpIsModifier(GpCell c) => c.Cmd is "key:LShift" or "key:RShift" or "key:LCtrl" or "key:RCtrl" or "key:LWin" or "key:LAlt" or "key:RAlt";

    /// <summary>固件键码 → 显示名（映射说明里用）。</summary>
    private static string GpCodeName(int code)
    {
        foreach (var c in GpAllCells)
            if (c.Code == code) return GpCellName(c);
        return code switch { 0x11 => "M1", 0x12 => "M2", 0x9C => "F23", 0x9D => "F22", 0xFF => "不映射", _ => $"0x{code:X2}" };
    }

    /// <summary>
    /// 一个键格的显示名：手柄键直接写键名（A、LB、十字 ↑…），键盘上单个字符的键前面加「键盘」免得和手柄键混，
    /// 鼠标、小键盘同理。
    /// </summary>
    private static string GpCellName(GpCell c) => c.Cmd switch
    {
        var s when s.StartsWith("pad:LStick") => $"左摇杆 {c.Label}",
        var s when s.StartsWith("pad:RStick") => $"右摇杆 {c.Label}",
        "pad:Up" => "十字 ↑", "pad:Down" => "十字 ↓", "pad:Left" => "十字 ←", "pad:Right" => "十字 →",
        "pad:LS" => "左摇杆按下", "pad:RS" => "右摇杆按下",
        var s when s.StartsWith("pad:") => c.Label,
        var s when s.StartsWith("mouse:") => $"鼠标{c.Label}",
        var s when s.StartsWith("key:Num") && s != "key:NumLock" || s is "key:NumLock" => $"小键盘 {c.Label}",
        var s when s.StartsWith("key:") && c.Label.Length == 1 => $"键盘 {c.Label}",
        _ => c.Label,
    };

    /// <summary>几个键码连成一个组合：多于一个时用键帽上的字（Ctrl + C），一个时用显示名。</summary>
    private static string GpCodesText(int[] codes) => codes.Length == 1 ? GpCodeName(codes[0])
        : string.Join(" + ", codes.Select(c => GpAllCells.FirstOrDefault(x => x.Code == c) is { Label: not null } cell ? cell.Label : GpCodeName(c)));

    /// <summary>键自己原来发的东西（原键位时的值）。</summary>
    private static string GpNativeName(string src) => src switch
    {
        "Back" => "View", "Start" => "Menu", "Guide" => "Xbox",
        _ => GpNativeCode(src) is int code and > 0 ? GpCodeName(code) : src,
    };

    // ---------------- 软件层布局的读写 ----------------

    /// <summary>这个键在软件层布局里的触发方式列表；不在布局里（归固件管）是 null。</summary>
    private JsonArray? GpActs(string src) => _gpLayout?["buttons"]?[src] as JsonArray;

    /// <summary>把这个键搬进布局：现在的固件映射变成一个「普通按压」触发方式（原键就是发它自己）。</summary>
    private JsonArray GpEnsureActs(string src)
    {
        if (GpActs(src) is JsonArray acts) return acts;
        _gpLayout ??= new JsonObject();
        if (_gpLayout["buttons"] is not JsonObject buttons) _gpLayout["buttons"] = buttons = new JsonObject();
        var codes = GpFirmwareCodes(src);
        var cmd = codes.Length == 0 || codes.Length == 1 && codes[0] == GpNativeCode(src) ? GpPadCommand(src) : GpCommandFromCodes(codes);
        var list = new JsonArray(new JsonObject { ["trigger"] = "regular", ["command"] = cmd });
        buttons[src] = list;
        return list;
    }

    private static JsonObject GpPadCommand(string src) => src is "M1" or "M2"
        ? new JsonObject { ["type"] = "none" }
        : new JsonObject { ["type"] = "gamepad", ["button"] = src };

    /// <summary>固件键码 → 软件层指令：全是键盘键 → keyboard（组合）；一个手柄键 / 鼠标键 → 对应指令。</summary>
    private static JsonObject GpCommandFromCodes(int[] codes)
    {
        var cells = codes.Select(c => GpAllCells.FirstOrDefault(x => x.Code == c)).Where(c => c.Cmd?.Length > 0).ToList();
        if (cells.Count == 0) return new JsonObject { ["type"] = "none" };
        if (cells.All(c => c.Cmd.StartsWith("key:")) && cells.Count <= 4)
            return new JsonObject { ["type"] = "keyboard", ["keys"] = new JsonArray(cells.Select(c => (JsonNode?)c.Cmd[4..]).ToArray()) };
        return GpCommandFromCell(cells[0], new List<GpCell>());
    }

    private static JsonObject GpCommandFromCell(GpCell cell, List<GpCell> chord)
    {
        var parts = cell.Cmd.Split(':', 2);
        return parts[0] switch
        {
            "pad" => new JsonObject { ["type"] = "gamepad", ["button"] = parts[1] },
            "key" => new JsonObject { ["type"] = "keyboard", ["keys"] = new JsonArray(chord.Append(cell).Take(4).Select(c => (JsonNode?)c.Cmd[4..]).ToArray()) },
            "mouse" => new JsonObject { ["type"] = "mouse", ["button"] = parts[1] },
            "wheel" => new JsonObject { ["type"] = "mouse", ["wheel"] = parts[1] },
            "sys" => new JsonObject { ["type"] = "system", ["action"] = parts[1] },
            _ => new JsonObject { ["type"] = "none" },
        };
    }

    private static string GpCommandText(JsonNode? cmd)
    {
        string? Find(string c) => GpAllCells.Where(x => x.Cmd == c).Select(GpCellName).FirstOrDefault();
        string? Cap(string c) => GpAllCells.Where(x => x.Cmd == c).Select(x => x.Label).FirstOrDefault();
        var keys = (cmd?["keys"] as JsonArray ?? new JsonArray()).Select(k => Str(k) ?? "").ToArray();
        return Str(cmd?["type"]) switch
        {
            "gamepad" => Find("pad:" + Str(cmd!["button"])) ?? Str(cmd!["button"]) ?? "手柄",
            // 一个键用显示名（键盘 C），组合键用键帽上的字（Ctrl + C）
            "keyboard" => keys.Length == 1 ? Find("key:" + keys[0]) ?? keys[0] : string.Join(" + ", keys.Select(k => Cap("key:" + k) ?? k)),
            "mouse" => cmd!["wheel"] != null ? Find("wheel:" + Str(cmd["wheel"])) ?? "滚轮" : Find("mouse:" + Str(cmd["button"])) ?? "鼠标",
            "system" => Find("sys:" + Str(cmd!["action"])) ?? Str(cmd!["action"]) ?? "系统",
            _ => "无",
        };
    }

    private int[] GpFirmwareCodes(string src)
    {
        JsonNode? codes = src switch
        {
            "M1" => _gpFw?["backKeys"]?["m1"]?["codes"], "M2" => _gpFw?["backKeys"]?["m2"]?["codes"],
            _ => _gpFw?["keys"]?[src]?["codes"],
        };
        return (codes as JsonArray)?.Select(n => Int(n)).Where(c => c is > 0 and <= 0xFF).ToArray() ?? Array.Empty<int>();
    }

    /// <summary>存布局：本地先改（界面立刻跟着变），停手 400 ms 后存一次；后台校验不过会回中文错误，然后读回真实值。</summary>
    private void GpSaveLayout()
    {
        int v = ++_gpLayoutEdits;
        if (_gpOpen) BuildGpItems();
        PaintGpBody();
        After(400, async () =>
        {
            if (v != _gpLayoutEdits || _gpLayout == null) return;
            if (_gpGame != null) await GpWrite("save_input_layout", _gpLayout.DeepClone(), JsonValue.Create(_gpGame));
            else await GpWrite("save_input_layout", _gpLayout.DeepClone());
            if (v == _gpLayoutEdits) _gpLayoutEdits = 0;
            await GpSyncLayer();
        });
    }

    private bool GpLayerOn => Str(_gpLayer?["owner"]) == "toolbox";

    // ---------------- 开 / 关 ----------------

    private void GpOpenPicker(string source, bool desktop)
    {
        SettingsResetPointer();
        _gpPickScroll = 0;
        _gpPicker = new GpPickerState { Source = source, Desktop = desktop, Tab = GpIsSpecial(source) ? 4 : 0 };
        PaintGpBody();
    }

    private void GpPickerBack()
    {
        if (SettingsFinishEdit()) return;
        SettingsResetPointer();
        var p = _gpPicker!;
        if (p.Level == GpSubLevel.Chooser && p.DirectChooser || p.Level == GpSubLevel.Key && p.FromGroup)
        {
            p.Level = GpSubLevel.Group; p.DirectChooser = false; p.Chord.Clear(); p.InTabs = false; PaintGpBody(); return;
        }
        if (p.Level is not (GpSubLevel.Key or GpSubLevel.Group)) { p.Level = GpSubLevel.Key; p.Chord.Clear(); p.InTabs = false; PaintGpBody(); return; }
        _gpPicker = null;
        if (_gpOpen) BuildGpItems();
        PaintGpBody();
    }

    /// <summary>LB / RB：选择指令里换页签。</summary>
    private void GpSwitchTab(int delta)
    {
        var p = _gpPicker;
        if (p?.Level == GpSubLevel.Group) { GpGroupSwitch(p, delta); return; }
        if (p?.Level != GpSubLevel.Chooser) return;
        p.Tab = (p.Tab + delta + GpChooserTabs.Length) % GpChooserTabs.Length;
        p.Cell = 0;
        p.Chord.Clear();
        PaintGpBody();
    }

    // ---------------- 按键页的行 ----------------

    /// <summary>这个键现在映射到什么（线稿旁的说明、按键页标题下）。</summary>
    private string GpKeySummary(string src)
    {
        if (GpActs(src) is JsonArray acts)
        {
            if (acts.Count == 0) return "禁用";
            return string.Join("  ·  ", acts.Take(3).Select(a =>
            {
                int t = Math.Max(0, Array.IndexOf(GpActIds, Str(a?["trigger"]) ?? "regular"));
                return (t == 0 && acts.Count == 1 ? "" : GpActTypes[t] + " ") + GpCommandText(a?["command"]);
            })) + (acts.Count > 3 ? " …" : "");
        }
        var codes = GpFirmwareCodes(src);
        if (codes.Length == 0) return _gpFw == null && GpFirmwareSource(src) ? "—" : GpNativeName(src);
        if (codes.Length == 1 && codes[0] == GpNativeCode(src)) return GpNativeName(src);
        return GpCodesText(codes);
    }

    private List<PerfRow> GpKeyRows(GpPickerState p)
    {
        string src = p.Source;
        var rows = new List<PerfRow>();
        if (GpIsSpecial(src)) return GpSpecialRows(p);
        if (p.Desktop && src is "LStick" or "RStick") return GpDesktopStickRows(src);
        if (p.Desktop)
        {
            // 桌面模式：固件一层，只有「按下发什么」
            rows.Add(new PerfRow { Section = true, Title = "桌面模式" });
            rows.Add(new PerfRow { Title = "指令", Value = GpDesktopSummary(src) + "  ›", Press = () => { p.Act = 0; p.Level = GpSubLevel.Chooser; p.Cell = 0; p.InTabs = false; p.Chord.Clear(); PaintGpBody(); } });
            rows.Add(new PerfRow { Title = "禁用这个键", Value = "›", Press = () => GpWriteFirmware(p, new[] { 0xFF }) });
            rows.Add(new PerfRow { Title = "恢复原键位", Value = GpSourceName(src) + "  ›", Press = () => { _ = GpWrite("restore_gamepad", JsonValue.Create("dkey." + src)); p.KeyFocus = 1; } });
            return rows;
        }
        var acts = GpActs(src);
        int count = acts?.Count ?? 1;
        for (int i = 0; i < count; i++)
        {
            int idx = i;
            var act = acts?[i];
            int t = Math.Max(0, Array.IndexOf(GpActIds, Str(act?["trigger"]) ?? "regular"));
            string cmdText = acts != null ? GpCommandText(act?["command"]) : GpKeySummary(src);
            rows.Add(new PerfRow { Section = true, Title = count > 1 ? $"触发 {i + 1}" : "触发" });
            rows.Add(new PerfRow
            {
                Title = "触发方式", Choices = GpActTypes, Choice = t,
                Select = nt =>
                {
                    var a = (JsonObject)GpEnsureActs(src)[idx]!;
                    a["trigger"] = GpActIds[nt];
                    if (GpActIds[nt] == "chord" && a["chordWith"] == null) a["chordWith"] = src == "LB" ? "RB" : "LB";
                    GpSaveLayout();
                },
                Step = d =>
                {
                    int nt = Math.Clamp(t + d, 0, GpActIds.Length - 1);
                    if (nt == t) return;
                    var list = GpEnsureActs(src);
                    var a = (JsonObject)list[idx]!;
                    a["trigger"] = GpActIds[nt];
                    if (GpActIds[nt] == "chord" && a["chordWith"] == null) a["chordWith"] = src == "LB" ? "RB" : "LB";
                    GpSaveLayout();
                },
            });
            if (t == 5)
            {
                string with = Str(act?["chordWith"]) ?? "LB";
                var others = GpLayoutSources.Where(s => s != src).ToArray();
                int wi = Math.Max(0, Array.IndexOf(others, with));
                rows.Add(new PerfRow
                {
                    Title = "按住哪个键时触发", Value = GpSourceName(with), Numeric = true,
                    Step = d => { var a = (JsonObject)GpEnsureActs(src)[idx]!; a["chordWith"] = others[(wi + d + others.Length) % others.Length]; GpSaveLayout(); },
                });
            }
            rows.Add(new PerfRow { Title = "指令", Value = cmdText + "  ›", Press = () => { p.Act = idx; p.Level = GpSubLevel.Chooser; p.Cell = 0; p.InTabs = false; p.Chord.Clear(); PaintGpBody(); } });
            rows.Add(new PerfRow { Title = "设置", Value = GpActSettingsText(act) + "  ›", Press = () => { p.Act = idx; p.Level = GpSubLevel.Settings; p.SettingsFocus = 1; PaintGpBody(); } });
            if (count > 1)
                rows.Add(new PerfRow { Title = "删除这个触发方式", Value = "›", Press = () => { GpEnsureActs(src).RemoveAt(idx); GpSaveLayout(); p.KeyFocus = 1; } });
        }
        rows.Add(new PerfRow { Section = true, Title = "这个键" });
        rows.Add(new PerfRow
        {
            Title = "添加触发方式", Value = "›",
            Press = () =>
            {
                var list = GpEnsureActs(src);
                if (list.Count >= 8) { Toast("一个键最多 8 个触发方式"); return; }
                list.Add(new JsonObject { ["trigger"] = "long", ["command"] = new JsonObject { ["type"] = "none" } });
                GpSaveLayout();
            },
        });
        rows.Add(new PerfRow
        {
            Title = "禁用这个键", Value = "›",
            Press = () =>
            {
                if (GpActs(src) != null || !GpFirmwareSource(src)) { GpEnsureActs(src).Clear(); GpSaveLayout(); }
                else GpWriteFirmware(p, new[] { 0xFF });
            },
        });
        rows.Add(new PerfRow { Title = "恢复原键位", Value = GpSourceName(src) + "  ›", Press = () => GpRestoreKey(p) });
        return rows;
    }

    private static string GpActSettingsText(JsonNode? act)
    {
        if (act == null) return "默认";
        var parts = new List<string>();
        string t = Str(act["trigger"]) ?? "regular";
        if (t == "long") parts.Add($"长按 {Int(act["longPressMs"], 300)} ms");
        if (t == "double") parts.Add($"间隔 {Int(act["doublePressMs"], 250)} ms");
        int hz = Int(act["repeatHz"]);
        if (hz > 0) parts.Add($"连发 {hz} 次/秒");
        if (Bool(act["toggle"])) parts.Add("切换");
        int sd = Int(act["startDelayMs"]), ed = Int(act["endDelayMs"]);
        if (sd > 0 || ed > 0) parts.Add($"延迟 {sd}/{ed} ms");
        return parts.Count == 0 ? "默认" : string.Join(" · ", parts);
    }

    /// <summary>触发设置：改任何一项都会把这个键搬进软件层布局。</summary>
    private List<PerfRow> GpSettingsRows(GpPickerState p)
    {
        string src = p.Source;
        var act = GpActs(src)?[Math.Min(p.Act, Math.Max(0, (GpActs(src)?.Count ?? 1) - 1))];
        string t = Str(act?["trigger"]) ?? "regular";
        PerfRow Num(string title, string field, int def, int min, int max, int step, Func<int, string> fmt)
        {
            int v = Int(act?[field], def);
            return new PerfRow
            {
                Title = title, Value = fmt(v), Numeric = true,
                Number = v, Minimum = min, Maximum = max, Increment = step,
                SetNumber = nv => { GpActObj(p)[field] = nv; GpSaveLayout(); },
                Step = d => { int nv = Math.Clamp(v + d * step, min, max); if (nv == v) return; GpActObj(p)[field] = nv; GpSaveLayout(); },
            };
        }
        PerfRow Toggle(string title, string field, bool def)
        {
            bool v = act?[field] is JsonValue jv && jv.TryGetValue(out bool b) ? b : def;
            return new PerfRow
            {
                Title = title, Choices = new[] { "关", "开" }, Choice = v ? 1 : 0,
                Select = i => { GpActObj(p)[field] = i == 1; GpSaveLayout(); },
                Step = d => { bool nv = d > 0; if (nv == v) return; GpActObj(p)[field] = nv; GpSaveLayout(); },
                Press = () => { GpActObj(p)[field] = !v; GpSaveLayout(); },
            };
        }
        PerfRow Later(string title, string value) => new() { Title = title, Value = "暂不可用", Disabled = true, Press = () => Toast("此功能暂不可用") };
        var rows = new List<PerfRow> { new() { Section = true, Title = $"{GpActTypes[Math.Max(0, Array.IndexOf(GpActIds, t))]}设置" } };
        if (t == "long") rows.Add(Num("长按时间", "longPressMs", 300, 50, 5000, 50, v => $"{v} ms"));
        if (t == "double") rows.Add(Num("双击间隔", "doublePressMs", 250, 50, 2000, 25, v => $"{v} ms"));
        rows.Add(Num("按住连发", "repeatHz", 0, 0, 60, 5, v => v == 0 ? "关" : $"{v} 次/秒"));
        rows.Add(Toggle("切换", "toggle", false));
        rows.Add(Num("触发启动延迟", "startDelayMs", 0, 0, 5000, 50, v => $"{v} ms"));
        rows.Add(Num("触发结束延迟", "endDelayMs", 0, 0, 5000, 50, v => $"{v} ms"));
        rows.Add(Toggle("可中断", "interruptible", true));
        rows.Add(Later("触感强度", "关闭"));
        rows.Add(Later("指令循环", "关"));
        rows.Add(Later("反转输入", "关"));
        return rows;
    }

    private JsonObject GpActObj(GpPickerState p)
    {
        var list = GpEnsureActs(p.Source);
        return (JsonObject)list[Math.Clamp(p.Act, 0, list.Count - 1)]!;
    }

    // ---------------- 输入 ----------------

    private void GpPickerMove(Direction dir)
    {
        SettingsResumeKeyboard();
        var p = _gpPicker!;
        switch (p.Level)
        {
            case GpSubLevel.Group:
                GpGroupMove(p, dir);
                return;
            case GpSubLevel.Key or GpSubLevel.Settings:
            {
                var rows = p.Level == GpSubLevel.Key ? GpKeyRows(p) : GpSettingsRows(p);
                int focus = Math.Clamp(p.Level == GpSubLevel.Key ? p.KeyFocus : p.SettingsFocus, 0, rows.Count - 1);
                if (dir is Direction.Up or Direction.Down)
                {
                    int d = dir == Direction.Up ? -1 : 1;
                    for (int i = focus + d; i >= 0 && i < rows.Count; i += d)
                        if (!rows[i].Section) { focus = i; break; }
                }
                else if (focus >= 0 && focus < rows.Count) RowStep(rows[focus], dir == Direction.Left ? -1 : 1);
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
                var c0 = GpCellRect(p.Tab, cells[p.Cell]);
                var from = new Vector2((float)(c0.X + c0.Width / 2), (float)(c0.Y + c0.Height / 2));
                int best = -1;
                double bestScore = double.MaxValue;
                for (int pass = 0; pass < 2 && best < 0; pass++)
                    for (int i = 0; i < cells.Length; i++)
                    {
                        if (i == p.Cell) continue;
                        var r = GpCellRect(p.Tab, cells[i]);
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
        if (p.Level == GpSubLevel.Group)
        {
            var grows = GpGroupRows(p);
            if (p.GroupFocus >= 0 && p.GroupFocus < grows.Count && !grows[p.GroupFocus].Disabled) RowPress(grows[p.GroupFocus]);
            return;
        }
        if (p.Level is GpSubLevel.Key or GpSubLevel.Settings)
        {
            var rows = p.Level == GpSubLevel.Key ? GpKeyRows(p) : GpSettingsRows(p);
            int focus = p.Level == GpSubLevel.Key ? p.KeyFocus : p.SettingsFocus;
            if (focus >= 0 && focus < rows.Count) RowPress(rows[focus]);
            return;
        }
        if (p.InTabs) { p.InTabs = false; PaintGpBody(); return; }
        var cell = GpChooserCells(p.Tab)[p.Cell];
        if (GpIsSpecial(p.Source) && cell.Cmd.StartsWith("pad:")) { Toast("专用功能键使用系统动作或键盘鼠标指令"); return; }
        if (cell.Cmd.Length == 0 && cell.Code == 0) { Toast($"「{cell.Label}」之后做"); return; }
        if (p.Tab == 2 && GpIsModifier(cell))
        {
            // 修饰键：叠加 / 取消，等普通键来完成组合
            if (!p.Chord.Remove(cell) && p.Chord.Count < 3) p.Chord.Add(cell);
            PaintGpBody();
            return;
        }
        GpChooseCommand(p, cell);
    }

    /// <summary>
    /// 选定一个指令：这个键已经在软件层布局里 → 改它那个触发方式的指令；
    /// 不在布局里、而且固件发得出（普通按压，键码 ≤ 5 个）→ 写固件；固件发不出（截图、滚轮左右……）→ 搬进布局。
    /// </summary>
    private void GpChooseCommand(GpPickerState p, GpCell cell)
    {
        if (GpIsSpecial(p.Source))
        {
            if (cell.Cmd.StartsWith("pad:") || cell.Cmd.Length == 0) { Toast("专用功能键使用系统动作或键盘鼠标指令"); return; }
            GpSetSpecial(p, GpCommandFromCell(cell, p.Chord));
            p.Chord.Clear(); p.Level = p.DirectChooser ? GpSubLevel.Group : GpSubLevel.Key; p.DirectChooser = false; PaintGpBody(); return;
        }
        var chord = p.Chord.ToList();
        p.Chord.Clear();
        p.Level = p.DirectChooser ? GpSubLevel.Group : GpSubLevel.Key;
        p.DirectChooser = false;
        bool firmwareOk = cell.Code > 0 && chord.All(c => c.Code > 0);
        if (p.Desktop)
        {
            if (firmwareOk) GpWriteFirmware(p, chord.Select(c => c.Code).Append(cell.Code).Take(5).ToArray());
            else { Toast($"桌面模式下手柄发不出「{cell.Label}」"); PaintGpBody(); }
            return;
        }
        if (_gpGame == null && GpActs(p.Source) == null && firmwareOk && GpFirmwareSource(p.Source))
        {
            GpWriteFirmware(p, chord.Select(c => c.Code).Append(cell.Code).Take(5).ToArray());
            return;
        }
        if (cell.Cmd.Length == 0) { Toast($"「{cell.Label}」之后做"); PaintGpBody(); return; }
        GpActObj(p)["command"] = GpCommandFromCell(cell, chord);
        GpSaveLayout();
    }

    /// <summary>写固件映射：背键 set_back_key，其余 set_key_map / set_desktop_key_map；后台读回后界面跟着变。</summary>
    private void GpWriteFirmware(GpPickerState p, int[] codes)
    {
        var arr = new JsonArray(codes.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        if (p.Desktop) _ = GpWrite("set_desktop_key_map", JsonValue.Create(p.Source), arr);
        else if (p.Source is "M1" or "M2") _ = GpWrite("set_back_key", JsonValue.Create(p.Source.ToLowerInvariant()), arr);
        else _ = GpWrite("set_key_map", JsonValue.Create(p.Source), arr);
        PaintGpBody();
    }

    /// <summary>恢复原键位：从软件层布局里拿掉这个键，固件也放回原键。</summary>
    private void GpRestoreKey(GpPickerState p)
    {
        if (_gpLayout?["buttons"] is JsonObject buttons && buttons.ContainsKey(p.Source)) { buttons.Remove(p.Source); GpSaveLayout(); }
        int native = GpNativeCode(p.Source);
        if (_gpGame == null && native != 0 && !(GpFirmwareCodes(p.Source) is { Length: 1 } c && c[0] == native) && !p.Desktop)
            GpWriteFirmware(p, new[] { native });
        p.KeyFocus = 1;
        PaintGpBody();
    }

    // ---------------- 绘制 ----------------

    /// <summary>按键页 / 触发设置的左边距；选择指令页内容以 GpCx（分类右边那块的中线）居中。</summary>
    private const float GpPickX = 250;
    /// <summary>选择指令各页的几何（设计 px）：页签行、手柄页格子、键盘 / 数字键盘的键位单位、系统页格子。</summary>
    private const float GpTabY = 160, GpPadCell = 44;
    private const float GpKbU = 52, GpKbX = GpCx - 7.5f * GpKbU, GpKbY = 236, GpKbFnGap = 14;
    private const float GpNpU = 60, GpNpX = GpCx - 4 * GpNpU, GpNpY = 232;
    private const float GpSysW = 200, GpSysH = 52, GpSysPitchX = 216, GpSysPitchY = 64, GpSysX = GpCx - (3 * GpSysPitchX - 16) / 2, GpSysY = 280;
    /// <summary>手柄页的线稿：580 宽，中心 (GpCx, 420)；鼠标页画的鼠标中心 (GpCx, 410)。</summary>
    private const float GpPadArtW = 580, GpPadArtCy = 420, GpMouseCy = 410;

    private static Rect GpCellRect(int tab, GpCell c) => tab switch
    {
        0 => new(c.Col - GpPadCell / 2, c.Row - GpPadCell / 2, GpPadCell, GpPadCell),
        1 => new(c.Col - 60, c.Row - 20, 120, 40),
        2 => new(GpKbX + c.Col * GpKbU, GpKbY + c.Row * GpKbU + (c.Row > 0 ? GpKbFnGap : 0), c.Units * GpKbU - 6, GpKbU - 6),
        3 => new(GpNpX + c.Col * GpNpU, GpNpY + c.Row * GpNpU, c.Units * GpNpU - 6, c.Tall * GpNpU - 6),
        _ => new(GpSysX + c.Col * GpSysPitchX, GpSysY + c.Row * GpSysPitchY, GpSysW, GpSysH),
    };

    private GpPickerState? _gpPaintPicker;
    private GpSubLevel _gpPaintLevel;

    private void PaintGpPicker(CanvasDrawingSession ds, Color accent)
    {
        var p = _gpPicker!;
        if (_gpPaintPicker != p || _gpPaintLevel != p.Level)
        {
            _gpPaintPicker = p; _gpPaintLevel = p.Level;
            _gpPickScroll = 0; _settingsManualScroll = false;
        }
        var ui = TextKit.Ui(400);
        var muted = Gfx.WithAlpha(CcText, 0.5);
        string where = GpIsSpecial(p.Source) ? (p.SteamMapping ? "（Steam 大屏接管）" : "（常规场景）") : p.Desktop ? "（桌面模式）" : "";
        switch (p.Level)
        {
            case GpSubLevel.Key:
            {
                var rows = GpKeyRows(p);
                p.KeyFocus = Math.Clamp(p.KeyFocus, 0, rows.Count - 1);
                PText(ds, GpSourceName(p.Source) + where, TextKit.Ui(700), 22, CcText, GpPickX, 140, 900);
                string now = GpIsSpecial(p.Source) ? GpSpecialSummary(p.Source, p.SteamMapping) : p.Desktop ? GpDesktopSummary(p.Source) : GpKeySummary(p.Source);
                PText(ds, $"当前：{now}", ui, 13, muted, GpPickX, 170, 900);
                PaintGpRows(ds, rows, p.KeyFocus);
                GpHints(ds, ("B", "返回"));
                break;
            }
            case GpSubLevel.Group:
                PaintGpGroup(ds, p);
                break;
            case GpSubLevel.Settings:
            {
                var rows = GpSettingsRows(p);
                p.SettingsFocus = Math.Clamp(p.SettingsFocus, 0, rows.Count - 1);
                PText(ds, $"{GpSourceName(p.Source)}{where} · 触发设置", TextKit.Ui(700), 22, CcText, GpPickX, 140, 900);
                PaintGpRows(ds, rows, p.SettingsFocus);
                GpHints(ds, ("B", "返回"));
                break;
            }
            default:
                PaintGpChooser(ds, p, accent);
                break;
        }
    }

    /// <summary>按键页 / 触发设置：和性能页同一套行列表（行号、▸ 焦点、几选一下划线），放在分类右边。</summary>
    private void PaintGpRows(CanvasDrawingSession ds, List<PerfRow> rows, int focus)
    {
        _gpPickRows = rows;
        _gpPickScroll = SettingsPointerGesture.Scroll(_gpPickScroll, RowsHeight(rows), 520);
        if (!_settingsManualScroll && focus >= 0 && focus < rows.Count)
        {
            float top = rows.Take(focus).Sum(r => r.Section ? PerfSecH : PerfRowH);
            if (top < _gpPickScroll) _gpPickScroll = top;
            if (top + PerfRowH > _gpPickScroll + 520) _gpPickScroll = top + PerfRowH - 520;
        }
        using var clip = ds.CreateLayer(1, new Rect(GpPickX, 196, PerfRightW, 520));
        var old = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(GpPickX, 196 - _gpPickScroll) * old;
        _settingsTargets.Clear();
        PaintRowList(ds, rows, focus, _settingsTargets);
        ds.Transform = old;
    }

    /// <summary>
    /// 选择指令（照 Steam）：一排居中的页签（LB / RB 图标在两头），下面按实物布局摆键 ——
    /// 手柄页线稿 + 键的图形，鼠标页一只鼠标 + 引线，键盘 / 数字键盘是整齐的键位网格；底部按键提示。
    /// </summary>
    private void PaintGpChooser(CanvasDrawingSession ds, GpPickerState p, Color accent)
    {
        var ui = TextKit.Ui(400);
        var muted = Gfx.WithAlpha(CcText, 0.5);
        var acts = GpActs(p.Source);
        var act = acts != null && acts.Count > 0 ? acts[Math.Clamp(p.Act, 0, acts.Count - 1)] : null;
        if (GpIsSpecial(p.Source)) act = new JsonObject { ["command"] = GpSpecialCommand(p.Source, p.SteamMapping).DeepClone() };

        // 页签：居中一排
        var tabFont = TextKit.Ui(600);
        var widths = GpChooserTabs.Select(t => PWidth(ds, t, tabFont, 17)).ToArray();
        const float tabGap = 34, padGlyph = 28, padGap = 22;
        float total = widths.Sum() + tabGap * (widths.Length - 1) + 2 * (padGlyph + padGap);
        float x = GpCx - total / 2;
        var dim = Gfx.WithAlpha(CcText, 0.4f);
        GpDrawGlyph(ds, "LB", x, GpTabY, dim, false);
        x += padGlyph + padGap;
        for (int i = 0; i < GpChooserTabs.Length; i++)
        {
            bool on = i == p.Tab;
            var col = on ? CcText : Gfx.WithAlpha(CcText, 0.42f);
            PText(ds, GpChooserTabs[i], on ? tabFont : ui, 17, col, x, GpTabY, widths[i] + 4);
            _gpTargets.Add(new(i, SettingsTargetKind.Tab, x - 12, GpTabY - 22, widths[i] + 24, 44));
            if (on) ds.FillRectangle(x, GpTabY + 15, widths[i], 2, p.InTabs ? accent : col);
            x += widths[i] + tabGap;
        }
        GpDrawGlyph(ds, "RB", x - tabGap + padGap, GpTabY, dim, false);

        // 当前指令：软件层看指令名，固件看键码
        var currentCmds = new HashSet<string>();
        var currentCodes = new HashSet<int>();
        if (act != null)
        {
            var cmd = act["command"];
            switch (Str(cmd?["type"]))
            {
                case "gamepad": currentCmds.Add("pad:" + Str(cmd!["button"])); break;
                case "keyboard": foreach (var k in cmd!["keys"] as JsonArray ?? new JsonArray()) currentCmds.Add("key:" + Str(k)); break;
                case "mouse": currentCmds.Add(cmd!["wheel"] != null ? "wheel:" + Str(cmd["wheel"]) : "mouse:" + Str(cmd["button"])); break;
                case "system": currentCmds.Add("sys:" + Str(cmd!["action"])); break;
            }
        }
        else if (p.Desktop)
        {
            foreach (var c in GpDesktopCodes(p.Source) ?? Array.Empty<int>()) currentCodes.Add(c);
        }
        else foreach (var c in GpFirmwareCodes(p.Source)) currentCodes.Add(c);

        var cells = GpChooserCells(p.Tab);
        int focusCell = p.InTabs ? -1 : p.Cell;
        if (p.Tab == 0) PaintGpChooserPadArt(ds);
        if (p.Tab == 1) PaintGpChooserMouse(ds, focusCell >= 0 ? cells[focusCell].Cmd : "", accent);

        float radius = p.Tab == 0 ? 12 : 7;
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            var rect = GpCellRect(p.Tab, cell);
            _gpTargets.Add(new(i, SettingsTargetKind.Cell, rect.X, rect.Y, rect.Width, rect.Height));
            bool focused = i == focusCell;
            bool current = currentCmds.Contains(cell.Cmd) || cell.Code > 0 && currentCodes.Contains(cell.Code);
            bool chord = p.Chord.Contains(cell);
            bool later = cell.Cmd.Length == 0 && cell.Code == 0 || GpIsSpecial(p.Source) && cell.Cmd.StartsWith("pad:");
            if (focused) ds.FillRoundedRectangle(rect, radius, radius, Gfx.WithAlpha(accent, 0.45f));
            else if (chord) ds.FillRoundedRectangle(rect, radius, radius, Gfx.WithAlpha(accent, 0.22f));
            ds.DrawRoundedRectangle(rect, radius, radius, current || chord ? Gfx.WithAlpha(accent, 0.95f) : Gfx.WithAlpha(CcText, focused ? 0.6f : later ? 0.08f : 0.16f), current ? 1.6f : 1f);
            var ink = focused ? CcText : Gfx.WithAlpha(CcText, later ? 0.35f : 0.8f);
            if (p.Tab == 0)
            {
                // 手柄页画键的图形（放大 1.3 倍），不写字
                string id = cell.Cmd.StartsWith("pad:") ? cell.Cmd[4..] : "Share";
                float cx = (float)(rect.X + rect.Width / 2), cy = (float)(rect.Y + rect.Height / 2);
                var old = ds.Transform;
                ds.Transform = Matrix3x2.CreateScale(1.3f, new Vector2(cx, cy)) * old;
                GpDrawGlyph(ds, id, cx - GpGlyphWidth(id) / 2, cy, ink, false);
                ds.Transform = old;
            }
            else
            {
                using var l = TextKit.Layout(ds, cell.Label, TextKit.Ui(400), 14, (float)rect.Width, CanvasHorizontalAlignment.Center);
                ds.DrawTextLayout(l, (float)rect.X, (float)(rect.Y + (rect.Height - l.LayoutBounds.Height) / 2 - l.LayoutBounds.Y), ink);
            }
        }

        // 底部：键盘组合的状态 / 这一页的说明（一行小字），再下面按键提示
        string? note = p.Tab == 2
            ? (p.Chord.Count > 0 ? $"组合：{string.Join(" + ", p.Chord.Select(c => c.Label))} + …  选择普通键完成" : "先选修饰键，再选普通键完成组合")
            : null;
        if (note != null)
        {
            float nw = PWidth(ds, note, ui, 13);
            PText(ds, note, ui, 13, muted, GpCx - nw / 2, 690, nw + 4);
        }
        GpHints(ds, ("A", "选定"), ("LB / RB", "换页"), ("B", "返回"));
    }

    /// <summary>手柄页中间的 C8EX 正面线稿（直接画已载入的位图，淡一点，不抢键格）。</summary>
    private void PaintGpChooserPadArt(CanvasDrawingSession ds)
    {
        if (!_gpBitmaps.TryGetValue("front", out var bmp)) return;
        float w = GpPadArtW, h = w * (float)(bmp.Size.Height / bmp.Size.Width);
        ds.DrawImage(bmp, new Rect(GpCx - w / 2, GpPadArtCy - h / 2, w, h), bmp.Bounds, 0.85f, CanvasImageInterpolation.HighQualityCubic);
    }

    /// <summary>
    /// 鼠标页中间画一只鼠标（线稿风格，和 C8EX 线稿同样的细线）：左右键分线、滚轮、侧边两个键。
    /// 每个键格拉一根淡淡的引线到鼠标上对应的位置；焦点那个键的位置点亮。
    /// </summary>
    private void PaintGpChooserMouse(CanvasDrawingSession ds, string focusCmd, Color accent)
    {
        float cx = GpCx, cy = GpMouseCy, w = 150, h = 230, top = cy - h / 2, left = cx - w / 2;
        var line = Gfx.WithAlpha(CcText, 0.55f);
        using (var body = CanvasGeometry.CreateRoundedRectangle(ds, left, top, w, h, 72, 82))
            ds.DrawGeometry(body, line, 1.6f);
        float split = top + 92;
        ds.DrawLine(cx, top, cx, split, line, 1.4f);
        ds.DrawLine(left + 3, split, left + w - 3, split, line, 1.4f);
        ds.DrawRoundedRectangle(cx - 7, top + 24, 14, 38, 7, 7, line, 1.4f);
        ds.DrawRoundedRectangle(left - 6, cy - 12, 8, 28, 3, 3, line, 1.3f);
        ds.DrawRoundedRectangle(left - 6, cy + 26, 8, 28, 3, 3, line, 1.3f);

        // 键格 → 鼠标上的位置
        var anchors = new Dictionary<string, Vector2>
        {
            ["mouse:Left"] = new(cx - 36, top + 46), ["mouse:Right"] = new(cx + 36, top + 46), ["mouse:Middle"] = new(cx, top + 43),
            ["wheel:Up"] = new(cx + 7, top + 30), ["wheel:Down"] = new(cx + 7, top + 56),
            ["mouse:Forward"] = new(left - 6, cy + 2), ["mouse:Back"] = new(left - 6, cy + 40),
            ["wheel:Left"] = new(cx - 7, top + 43), ["wheel:Right"] = new(cx + 7, top + 43),
        };
        var lead = Gfx.WithAlpha(CcText, 0.2f);
        foreach (var cell in GpMouseCells)
        {
            if (!anchors.TryGetValue(cell.Cmd, out var a)) continue;
            var r = GpCellRect(1, cell);
            var from = cell.Row > 560 ? new Vector2(cell.Col, (float)r.Y) : new Vector2(cell.Col < cx ? (float)(r.X + r.Width) : (float)r.X, cell.Row);
            bool on = cell.Cmd == focusCmd;
            ds.DrawLine(from, a, on ? Gfx.WithAlpha(accent, 0.9f) : lead, on ? 1.4f : 1f);
            ds.FillCircle(a, on ? 4 : 2.5f, on ? accent : Gfx.WithAlpha(CcText, 0.45f));
        }
    }
}
