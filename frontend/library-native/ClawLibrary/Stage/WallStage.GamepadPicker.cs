// 控制中心 → 手柄 → 按键映射里，选中线稿上一个键按 A 之后的几层（结构照 Steam 输入，样式是我们自己的设计系统）：
//   按键页：这个键的触发方式（每个一组：触发方式 / 指令 / 设置），添加触发方式、禁用这个键、恢复原键位。
//   选择指令：顶上一排页签（手柄 / 鼠标 / 键盘 / 数字键盘 / 系统，LB / RB 或在页签上 ← → 切），下面按实物布局摆键；
//            键盘里 Ctrl / Shift / Alt / Win 按一下是叠加（组合键），再按一个普通键完成。
//   触发设置：长按时间、双击间隔、连发、切换、启动 / 结束延迟、可中断（触感、指令循环、反转输入第二批）。
// 混合映射，每个键只归一层管（见 docs/gamepad-input-layer.md），界面上不分层、不露开关：
//   只用「普通按压 + 固件发得出的键」→ 写固件（set_back_key / set_key_map），零延迟；
//   用到长按 / 双击 / 组合触发、触发设置、系统动作，或者固件管不到的键（View / Menu）→ 这个键进软件输入层的全局布局（save_input_layout），
//   后台按布局自己开关输入层，并把这个键在固件里放回原键、交给软件层。恢复原键位 = 从布局里拿掉 + 固件放回原键。
// 桌面模式只有固件一层（set_desktop_key_map）：普通按压、固件发得出的键。
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

    /// <summary>
    /// 键格：名字、固件键码（0 = 固件发不出）、软件层指令（"pad:B" "key:Tab" "mouse:Left" "wheel:Up" "sys:screenshot"，空 = 软件层也没有）、
    /// 位置（以格为单位的列、行）、宽度（格）。
    /// </summary>
    private readonly record struct GpCell(string Label, int Code, string Cmd, float Col, float Row, float Units = 1);

    private sealed class GpPickerState
    {
        public string Source = "";
        public bool Desktop;
        public GpSubLevel Level = GpSubLevel.Key;
        public int KeyFocus = 1, SettingsFocus = 1, Tab, Cell, Act;
        public bool InTabs;
        public readonly List<GpCell> Chord = new();
    }

    private GpPickerState? _gpPicker;
    private int _gpLayoutEdits;

    private static readonly string[] GpActTypes = { "普通按压", "长按", "双击", "开始按压", "释放按压", "组合键" };
    private static readonly string[] GpActIds = { "regular", "long", "double", "start", "release", "chord" };
    private static readonly string[] GpChooserTabs = { "手柄", "鼠标", "键盘", "数字键盘", "系统" };
    private static readonly string[] GpLayoutSources = { "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "Back", "Start", "Guide", "LS", "RS", "Up", "Down", "Left", "Right", "M1", "M2" };

    // ---------------- 键表（按实物布局摆，列 / 行以格为单位） ----------------

    private static readonly GpCell[] GpPadCells =
    {
        new("LT", 0x13, "pad:LT", 0, 0, 2), new("LB", 0x05, "pad:LB", 0, 1, 2),
        new("↑", 0x15, "pad:LStickUp", 1, 2.6f), new("←", 0x17, "pad:LStickLeft", 0, 3.6f), new("L 按下", 0x07, "pad:LS", 1, 3.6f), new("→", 0x18, "pad:LStickRight", 2, 3.6f), new("↓", 0x16, "pad:LStickDown", 1, 4.6f),
        new("上", 0x01, "pad:Up", 1, 6), new("左", 0x03, "pad:Left", 0, 7), new("右", 0x04, "pad:Right", 2, 7), new("下", 0x02, "pad:Down", 1, 8),
        new("View", 0x0E, "pad:Back", 5.5f, 0, 1.5f), new("Menu", 0x0F, "pad:Start", 7.5f, 0, 1.5f), new("Xbox", 0x0D, "pad:Guide", 5.5f, 1, 1.5f), new("截图", 0x10, "", 7.5f, 1, 1.5f),
        new("RT", 0x14, "pad:RT", 12, 0, 2), new("RB", 0x06, "pad:RB", 12, 1, 2),
        new("Y", 0x0C, "pad:Y", 13, 2.6f), new("X", 0x0B, "pad:X", 12, 3.6f), new("B", 0x0A, "pad:B", 14, 3.6f), new("A", 0x09, "pad:A", 13, 4.6f),
        new("↑", 0x19, "pad:RStickUp", 13, 6), new("←", 0x1B, "pad:RStickLeft", 12, 7), new("R 按下", 0x08, "pad:RS", 13, 7), new("→", 0x1C, "pad:RStickRight", 14, 7), new("↓", 0x1A, "pad:RStickDown", 13, 8),
    };

    private static readonly GpCell[] GpMouseCells =
    {
        new("左键", 0xC8, "mouse:Left", 0, 0, 3), new("中键", 0xC9, "mouse:Middle", 0, 1, 3), new("右键", 0xCA, "mouse:Right", 0, 2, 3),
        new("前进键", 0xCC, "mouse:Forward", 0, 3.4f, 3), new("后退键", 0xCB, "mouse:Back", 0, 4.4f, 3),
        new("滚轮上", 0xCD, "wheel:Up", 5, 0, 3), new("滚轮下", 0xCE, "wheel:Down", 5, 1, 3), new("滚轮左", 0, "wheel:Left", 5, 2, 3), new("滚轮右", 0, "wheel:Right", 5, 3, 3),
    };

    private static readonly GpCell[] GpKeyboardCells = BuildKeyboardCells();

    private static GpCell[] BuildKeyboardCells()
    {
        var cells = new List<GpCell>();
        void Row(float row, params (string Label, int Code, string Name, float Units)[] keys)
        {
            float col = 0;
            foreach (var (label, code, name, units) in keys) { cells.Add(new(label, code, "key:" + name, col, row, units)); col += units + 0.12f; }
        }
        (string, int, string, float) K(string l, int c, string name, float u = 1) => (l, c, name, u);
        Row(0, new[] { K("Esc", 0x32, "Esc") }.Concat(Enumerable.Range(0, 12).Select(i => K($"F{i + 1}", 0x33 + i, $"F{i + 1}"))).ToArray());
        Row(1.1f, new[] { K("`", 0x3F, "Grave") }.Concat("1 2 3 4 5 6 7 8 9 0".Split(' ').Select((l, i) => K(l, 0x40 + i, l)))
            .Concat(new[] { K("-", 0x4A, "Minus"), K("=", 0x4B, "Equals"), K("Backspace", 0x4C, "Backspace", 2) }).ToArray());
        Row(2.2f, new[] { K("Tab", 0x4D, "Tab", 1.5f) }.Concat("Q W E R T Y U I O P".Split(' ').Select((l, i) => K(l, 0x4E + i, l)))
            .Concat(new[] { K("[", 0x58, "LeftBracket"), K("]", 0x59, "RightBracket"), K("\\", 0x5A, "Backslash", 1.5f) }).ToArray());
        Row(3.3f, new[] { K("Caps", 0x5B, "CapsLock", 1.75f) }.Concat("A S D F G H J K L".Split(' ').Select((l, i) => K(l, 0x5C + i, l)))
            .Concat(new[] { K(";", 0x65, "Semicolon"), K("'", 0x66, "Quote"), K("Enter", 0x67, "Enter", 2.4f) }).ToArray());
        Row(4.4f, new[] { K("Shift", 0x68, "LShift", 2.3f) }.Concat("Z X C V B N M".Split(' ').Select((l, i) => K(l, 0x69 + i, l)))
            .Concat(new[] { K(",", 0x70, "Comma"), K(".", 0x71, "Period"), K("/", 0x72, "Slash"), K("Shift", 0x73, "RShift", 2.9f) }).ToArray());
        Row(5.5f, K("Ctrl", 0x74, "LCtrl", 1.5f), K("Win", 0x75, "LWin", 1.5f), K("Alt", 0x76, "LAlt", 1.5f), K("空格", 0x77, "Space", 6.2f), K("Alt", 0x78, "RAlt", 1.5f), K("Ctrl", 0x79, "RCtrl", 1.5f));
        return cells.ToArray();
    }

    private static readonly GpCell[] GpNumpadCells =
    {
        new("Insert", 0x7A, "key:Insert", 0, 0, 1.3f), new("Home", 0x7B, "key:Home", 1.42f, 0, 1.3f), new("PgUp", 0x7C, "key:PageUp", 2.84f, 0, 1.3f),
        new("Delete", 0x7D, "key:Delete", 0, 1.1f, 1.3f), new("End", 0x7E, "key:End", 1.42f, 1.1f, 1.3f), new("PgDn", 0x7F, "key:PageDown", 2.84f, 1.1f, 1.3f),
        new("↑", 0x80, "key:Up", 1.42f, 3.3f, 1.3f), new("←", 0x82, "key:Left", 0, 4.4f, 1.3f), new("↓", 0x81, "key:Down", 1.42f, 4.4f, 1.3f), new("→", 0x83, "key:Right", 2.84f, 4.4f, 1.3f),
        new("播放", 0x95, "key:MediaPlayPause", 5, 0, 1.4f), new("停止", 0x96, "key:MediaStop", 6.52f, 0, 1.4f),
        new("上一首", 0x98, "key:MediaPrev", 5, 1.1f, 1.4f), new("下一首", 0x97, "key:MediaNext", 6.52f, 1.1f, 1.4f),
        new("Num", 0x84, "key:NumLock", 9.5f, 0), new("/", 0x85, "key:NumDivide", 10.62f, 0), new("*", 0x86, "key:NumMultiply", 11.74f, 0), new("-", 0x87, "key:NumSubtract", 12.86f, 0),
        new("7", 0x92, "key:Num7", 9.5f, 1.1f), new("8", 0x93, "key:Num8", 10.62f, 1.1f), new("9", 0x94, "key:Num9", 11.74f, 1.1f), new("+", 0x88, "key:NumAdd", 12.86f, 1.1f),
        new("4", 0x8F, "key:Num4", 9.5f, 2.2f), new("5", 0x90, "key:Num5", 10.62f, 2.2f), new("6", 0x91, "key:Num6", 11.74f, 2.2f),
        new("1", 0x8C, "key:Num1", 9.5f, 3.3f), new("2", 0x8D, "key:Num2", 10.62f, 3.3f), new("3", 0x8E, "key:Num3", 11.74f, 3.3f), new("Enter", 0x8A, "key:NumEnter", 12.86f, 3.3f),
        new("0", 0x8B, "key:Num0", 9.5f, 4.4f, 2.12f), new(".", 0x89, "key:NumDecimal", 11.74f, 4.4f),
    };

    private static readonly GpCell[] GpSystemCells =
    {
        new("音量 +", 0x99, "sys:volumeUp", 0, 0, 2.5f), new("音量 −", 0x9A, "sys:volumeDown", 0, 1.1f, 2.5f), new("静音", 0x9B, "sys:mute", 0, 2.2f, 2.5f),
        new("截图", 0, "sys:screenshot", 3.2f, 0, 3), new("显示键盘", 0, "sys:keyboard", 3.2f, 1.1f, 3), new("强制关闭游戏", 0, "", 3.2f, 2.2f, 3),
        new("打开控制中心", 0, "", 6.9f, 0, 3), new("Steam 快速菜单", 0, "", 6.9f, 1.1f, 3), new("切换超分", 0, "", 6.9f, 2.2f, 3),
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
        BuildGpItems();
        PaintGpBody();
        After(400, async () =>
        {
            if (v != _gpLayoutEdits || _gpLayout == null) return;
            await GpWrite("save_input_layout", _gpLayout.DeepClone());
            if (v == _gpLayoutEdits) _gpLayoutEdits = 0;
            await GpSyncLayer();
        });
    }

    private bool GpLayerOn => Str(_gpLayer?["owner"]) == "toolbox";

    // ---------------- 开 / 关 ----------------

    private void GpOpenPicker(string source, bool desktop)
    {
        _gpPicker = new GpPickerState { Source = source, Desktop = desktop };
        PaintGpBody();
    }

    private void GpPickerBack()
    {
        var p = _gpPicker!;
        if (p.Level != GpSubLevel.Key) { p.Level = GpSubLevel.Key; p.Chord.Clear(); p.InTabs = false; PaintGpBody(); return; }
        _gpPicker = null;
        BuildGpItems();
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
        if (p.Desktop)
        {
            // 桌面模式：固件一层，只有「按下发什么」
            rows.Add(new PerfRow { Section = true, Title = "桌面模式" });
            rows.Add(new PerfRow { Title = "指令", Value = GpDesktopSummary(src) + "  ›", Press = () => { p.Act = 0; p.Level = GpSubLevel.Chooser; p.Cell = 0; p.InTabs = false; p.Chord.Clear(); PaintGpBody(); } });
            rows.Add(new PerfRow { Title = "禁用这个键", Value = "按下不起作用  ›", Press = () => GpWriteFirmware(p, new[] { 0xFF }) });
            rows.Add(new PerfRow { Title = "恢复默认", Value = "固件的桌面默认  ›", Press = () => { _ = GpWrite("restore_gamepad", JsonValue.Create("dkey." + src)); p.KeyFocus = 1; } });
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
            Title = "添加触发方式", Value = "长按、双击、组合键……  ›",
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
            Title = "禁用这个键", Value = "按下不起作用  ›",
            Press = () =>
            {
                if (GpActs(src) != null || !GpFirmwareSource(src)) { GpEnsureActs(src).Clear(); GpSaveLayout(); }
                else GpWriteFirmware(p, new[] { 0xFF });
            },
        });
        rows.Add(new PerfRow { Title = "恢复原键位", Value = $"{GpSourceName(src)}自己  ›", Press = () => GpRestoreKey(p) });
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
                Step = d => { int nv = Math.Clamp(v + d * step, min, max); if (nv == v) return; GpActObj(p)[field] = nv; GpSaveLayout(); },
            };
        }
        PerfRow Toggle(string title, string field, bool def)
        {
            bool v = act?[field] is JsonValue jv && jv.TryGetValue(out bool b) ? b : def;
            return new PerfRow
            {
                Title = title, Choices = new[] { "关", "开" }, Choice = v ? 1 : 0,
                Step = d => { bool nv = d > 0; if (nv == v) return; GpActObj(p)[field] = nv; GpSaveLayout(); },
                Press = () => { GpActObj(p)[field] = !v; GpSaveLayout(); },
            };
        }
        PerfRow Later(string title, string value) => new() { Title = title, Value = value + " · 第二批", Disabled = true, Press = () => Toast("这一项在软件输入层的第二批里做") };
        var rows = new List<PerfRow> { new() { Section = true, Title = $"{GpActTypes[Math.Max(0, Array.IndexOf(GpActIds, t))]}设置" } };
        if (t == "long") rows.Add(Num("长按时间", "longPressMs", 300, 50, 5000, 50, v => $"{v} ms"));
        if (t == "double") rows.Add(Num("双击间隔", "doublePressMs", 250, 50, 2000, 25, v => $"{v} ms"));
        rows.Add(Num("按住以重复（连发）", "repeatHz", 0, 0, 60, 5, v => v == 0 ? "关" : $"{v} 次/秒"));
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
        var p = _gpPicker!;
        switch (p.Level)
        {
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
            var rows = p.Level == GpSubLevel.Key ? GpKeyRows(p) : GpSettingsRows(p);
            int focus = p.Level == GpSubLevel.Key ? p.KeyFocus : p.SettingsFocus;
            if (focus >= 0 && focus < rows.Count) rows[focus].Press?.Invoke();
            return;
        }
        if (p.InTabs) { p.InTabs = false; PaintGpBody(); return; }
        var cell = GpChooserCells(p.Tab)[p.Cell];
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
        var chord = p.Chord.ToList();
        p.Chord.Clear();
        p.Level = GpSubLevel.Key;
        bool firmwareOk = cell.Code > 0 && chord.All(c => c.Code > 0);
        if (p.Desktop)
        {
            if (firmwareOk) GpWriteFirmware(p, chord.Select(c => c.Code).Append(cell.Code).Take(5).ToArray());
            else { Toast($"桌面模式下手柄发不出「{cell.Label}」"); PaintGpBody(); }
            return;
        }
        if (GpActs(p.Source) == null && firmwareOk && GpFirmwareSource(p.Source))
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
        if (native != 0 && !(GpFirmwareCodes(p.Source) is { Length: 1 } c && c[0] == native) && !p.Desktop)
            GpWriteFirmware(p, new[] { native });
        p.KeyFocus = 1;
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
            {
                var rows = GpKeyRows(p);
                p.KeyFocus = Math.Clamp(p.KeyFocus, 0, rows.Count - 1);
                PText(ds, GpSourceName(p.Source) + where, TextKit.Ui(700), 22, CcText, GpPickX, 140, 900);
                string now = p.Desktop ? GpDesktopSummary(p.Source) : GpKeySummary(p.Source);
                PText(ds, $"现在：{now}  ·  改了立刻生效", ui, 13, muted, GpPickX, 170, 900);
                PaintGpRows(ds, rows, p.KeyFocus);
                break;
            }
            case GpSubLevel.Settings:
            {
                var rows = GpSettingsRows(p);
                p.SettingsFocus = Math.Clamp(p.SettingsFocus, 0, rows.Count - 1);
                PText(ds, $"{GpSourceName(p.Source)}{where} · 触发设置", TextKit.Ui(700), 22, CcText, GpPickX, 140, 900);
                PText(ds, "照 Steam 输入", ui, 13, muted, GpPickX, 170, 900);
                PaintGpRows(ds, rows, p.SettingsFocus);
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
        var acts = GpActs(p.Source);
        var act = acts != null && acts.Count > 0 ? acts[Math.Clamp(p.Act, 0, acts.Count - 1)] : null;
        string actName = GpActTypes[Math.Max(0, Array.IndexOf(GpActIds, Str(act?["trigger"]) ?? "regular"))];
        PText(ds, $"选择一个{GpChooserTabs[p.Tab]}指令：{GpSourceName(p.Source)} → {actName}", TextKit.Ui(700), 22, CcText, GpPickX, 140, 900);
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
            foreach (var c in _gpFw?["desktopKeys"]?[p.Source] as JsonArray ?? new JsonArray()) currentCodes.Add(Int(c));
        }
        else foreach (var c in GpFirmwareCodes(p.Source)) currentCodes.Add(c);

        using var font = new CanvasTextFormat { FontFamily = TextKit.Ui(400).Family, FontSize = 14, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
        var cells = GpChooserCells(p.Tab);
        float bottom = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            var rect = GpCellRect(cell);
            bottom = Math.Max(bottom, (float)(rect.Y + rect.Height));
            bool focused = !p.InTabs && i == p.Cell;
            bool current = currentCmds.Contains(cell.Cmd) || cell.Code > 0 && currentCodes.Contains(cell.Code);
            bool chord = p.Chord.Contains(cell);
            bool later = cell.Cmd.Length == 0 && cell.Code == 0;
            if (focused) ds.FillRoundedRectangle(rect, 7, 7, Gfx.WithAlpha(accent, 0.45f));
            else if (chord) ds.FillRoundedRectangle(rect, 7, 7, Gfx.WithAlpha(accent, 0.22f));
            ds.DrawRoundedRectangle(rect, 7, 7, current || chord ? Gfx.WithAlpha(accent, 0.95f) : Gfx.WithAlpha(CcText, focused ? 0.6f : later ? 0.08f : 0.16f), current ? 1.6f : 1f);
            ds.DrawText(cell.Label, rect, focused ? CcText : Gfx.WithAlpha(CcText, later ? 0.35f : 0.8f), font);
        }
        if (p.Tab == 0)
            foreach (var (label, col, row) in new[] { ("左摇杆", 0f, 2.6f), ("十字键", 0f, 6f), ("右摇杆", 12f, 6f) })
                PText(ds, label, ui, 11, Gfx.WithAlpha(CcText, 0.38f), GpPickX + col * GpCellStepX + 140, GpChooserTop + row * GpCellStepY + 22, 80);
        string hint = p.Tab == 2
            ? (p.Chord.Count > 0 ? $"组合：{string.Join(" + ", p.Chord.Select(GpCellName))} + …  再按一个普通键完成" : "Ctrl / Shift / Alt / Win 按一下是叠加，可以组合；再按一个普通键完成")
            : p.Tab == 4 ? "强制关闭游戏、打开控制中心、Steam 快速菜单、切换超分之后做" : "A 选定  ·  LB / RB 换页签  ·  B 返回";
        PText(ds, hint, ui, 13, muted, GpPickX, bottom + 30, 900);
    }
}
