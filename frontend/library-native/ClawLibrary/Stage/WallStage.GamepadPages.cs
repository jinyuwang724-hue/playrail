// 控制中心 → 手柄：每个分类挂在线稿两侧的设置（按键映射是紧凑的一键一行，按手柄上的位置从上到下），
// 和这一页在线稿上额外画的东西（摇杆死区圈、震动握把、扳机行程刻度）。
// 当前值从固件读（get_gamepad_state，见 docs/gamepad-firmware-protocol.md）；读不到写「—」。
// 范围照 MSI Center M：摇杆死区 0–50 %、外圈 75–100 %，扳机行程 0–100 %。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    /// <summary>快速设置键 / Claw 键在 Steam 大屏幕里做什么（工具箱后台的按键接管，settings.qamAction / msiAction）。</summary>
    private static readonly string[] GpActions = { "MainMenu", "QuickAccess", "MagpieToggle", "None" };
    private static readonly string[] GpSpecialChoices = { "原功能", "主菜单", "快速访问", "超分开关", "不做事" };
    private const string GpLater = "后台的手柄固件写入还没上线，接上后这里就能改";

    /// <summary>手柄模式的映射：左右两列，各自按手柄上从上到下（扳机、肩键、小按键、摇杆、十字键 / ABXY、背键）。</summary>
    private static readonly string[] GpMapLeft = { "LT", "LB", "Back", "QS", "LStick", "LS", "Up", "Down", "Left", "Right", "M1" };
    private static readonly string[] GpMapRight = { "RT", "RB", "Start", "Claw", "Y", "X", "B", "A", "RStick", "RS", "M2" };
    /// <summary>桌面模式的映射：只有固件管得到的键（桌面模式下固件直接发键鼠，软件输入层不参与）。</summary>
    private static readonly string[] GpDesktopLeft = { "LT", "LB", "LS", "Up", "Down", "Left", "Right" };
    private static readonly string[] GpDesktopRight = { "RT", "RB", "Y", "X", "B", "A", "RS" };

    private void BuildGpItems()
    {
        _gpItems.Clear();
        var left = new List<GpItem>();
        var right = new List<GpItem>();
        GpItem? center = null;
        string loading = _gpError ?? "正在读取…";
        string fwMissing = _gpFwError ?? "正在读取…";
        GpItem Item(string title, string key, string value, string? why = null) => new() { Title = title, Key = key, Value = value, Disabled = why != null, Why = why };

        switch (GpCatId)
        {
            case "gpmap" or "dkmap":
            {
                bool desktop = GpCatId == "dkmap";
                foreach (var src in desktop ? GpDesktopLeft : GpMapLeft) left.Add(GpMapCell(src, desktop));
                foreach (var src in desktop ? GpDesktopRight : GpMapRight) right.Add(GpMapCell(src, desktop));
                break;
            }
            case "stick":
            {
                foreach (var (side, list, key) in new[] { ("left", left, "LS"), ("right", right, "RS") })
                {
                    var st = _gpFw?["sticks"]?[side];
                    string name = side == "left" ? "左摇杆" : "右摇杆";
                    int dz = Int(st?["deadzone"], -1), outer = Int(st?["outer"], -1);
                    var d = Item($"{name}死区", key, dz < 0 ? "—" : $"{dz} %", dz < 0 ? fwMissing : null);
                    if (dz >= 0)
                    {
                        d.Number = Math.Min(dz, 50); d.Min = 0; d.Max = 50; d.Format = v => $"{v} %";
                        d.SetNumber = v => { st!["deadzone"] = v; GpStickChanged(side); };
                    }
                    var o = Item($"{name}外圈", key, outer < 0 ? "—" : $"{outer} %", outer < 0 ? fwMissing : null);
                    if (outer >= 0)
                    {
                        o.Number = Math.Max(outer, 75); o.Min = 75; o.Max = 100; o.Format = v => $"{v} %";
                        o.SetNumber = v => { st!["outer"] = v; GpStickChanged(side); };
                    }
                    list.Add(d);
                    list.Add(o);
                }
                break;
            }
            case "trigger":
            {
                // 快速触发 = 扳机当按键用（轻按即满），关 = 线性；左右一起开关
                var tl = _gpFw?["triggers"]?["left"];
                bool quick = Bool(tl?["quickTrigger"]) || Bool(_gpFw?["triggers"]?["right"]?["quickTrigger"]);
                var q = Item("快速触发", "", tl == null ? "—" : quick ? "开 · 轻按即满" : "关 · 线性", tl == null ? fwMissing : null);
                q.Side = 0;
                q.Lights = new[] { "TL", "TR" };
                q.Press = () => _ = GpSetQuickTrigger(!quick);
                center = q;
                foreach (var (side, list, key) in new[] { ("left", left, "TL"), ("right", right, "TR") })
                {
                    var t = _gpFw?["triggers"]?[side];
                    string name = side == "left" ? "左扳机" : "右扳机";
                    int a = Int(t?["deadzone"], -1), b = Int(t?["outer"], -1);
                    var start = Item($"{name}起点", key, a < 0 ? "—" : $"{a} %", a < 0 ? fwMissing : null);
                    var end = Item($"{name}终点", key, b < 0 ? "—" : $"{b} %", b < 0 ? fwMissing : null);
                    if (a >= 0 && b >= 0)
                    {
                        start.Number = a; start.Min = 0; start.Max = Math.Max(0, b - 5); start.StepBy = 5; start.Format = v => $"{v} %";
                        start.SetNumber = v => { t!["deadzone"] = v; GpTriggerChanged(side); };
                        end.Number = b; end.Min = Math.Min(100, a + 5); end.Max = 100; end.StepBy = 5; end.Format = v => $"{v} %";
                        end.SetNumber = v => { t!["outer"] = v; GpTriggerChanged(side); };
                    }
                    list.Add(start);
                    list.Add(end);
                }
                break;
            }
            case "rumble":
            {
                // 照 MSI 振动页：左右马达分开调；测试（X）、恢复原来的强度（Y）是页面底部的按键提示
                GpItem Motor(string title, string grip, int? value, bool isLeft)
                {
                    var it = Item(title, grip, value is int v ? (v == 0 ? "关" : $"{v} %") : loading, value == null ? loading : null);
                    if (value is int cur)
                    {
                        it.Number = cur; it.Min = 0; it.Max = 100; it.StepBy = 5;
                        it.Format = v => v == 0 ? "关" : $"{v} %";
                        it.SetNumber = v => GpRumbleChanged(isLeft ? v : _gpRumbleL ?? v, isLeft ? _gpRumbleR ?? v : v);
                    }
                    return it;
                }
                left.Add(Motor("左侧震动", "GripL", _gpRumbleL, true));
                right.Add(Motor("右侧震动", "GripR", _gpRumbleR, false));
                break;
            }
            default:   // mouse
                left.Add(Item("移动光标", "LS", "左摇杆", GpLater));
                left.Add(Item("光标速度", "LS", "—", GpLater));
                right.Add(Item("滚动", "RS", "右摇杆", GpLater));
                right.Add(Item("左键 / 右键", "RB", "RB / LB", GpLater));
                break;
        }

        // 两侧按线稿竖向居中排开（映射页紧凑），居中那项放在线稿正下方
        void Column(List<GpItem> items, int side)
        {
            bool compact = GpIsMapPage;
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Side = side;
                items[i].Row = i;
                items[i].Compact = compact;
                items[i].X = side < 0 ? GpLeftEdge : GpRightEdge;
                items[i].Y = GpCy - (compact ? 10 : 13) + (i - (items.Count - 1) / 2f) * (compact ? GpMapRowStep : GpRowStep);
            }
        }
        Column(left, -1);
        Column(right, 1);
        _gpItems.AddRange(left);
        _gpItems.AddRange(right);
        if (center != null)
        {
            center.X = GpCx;
            center.Y = GpCy + GpSideH / 2 + 34;
            _gpItems.Add(center);
        }
        if (_gpFocus >= _gpItems.Count) _gpFocus = 0;
    }

    /// <summary>快速触发左右一起写：先左后右，写完读回。</summary>
    private async Task GpSetQuickTrigger(bool on)
    {
        try
        {
            _agent ??= new AgentClient();
            await _agent.Call("set_quick_trigger", JsonValue.Create("left"), JsonValue.Create(on));
            await _agent.Call("set_quick_trigger", JsonValue.Create("right"), JsonValue.Create(on));
        }
        catch (Exception ex) { Toast($"没有写进手柄：{ex.Message}"); }
        _ = GpRefresh();
    }

    /// <summary>
    /// 映射表的一格。普通键：值是它现在映射到什么，A 打开「映射到」（触发方式、键盘鼠标……）；
    /// 摇杆：原样 / 当另一边的摇杆用（固件的左右互换）；快速设置键 / Claw 键：在 Steam 大屏幕里做什么（← → 选）。
    /// </summary>
    private GpItem GpMapCell(string src, bool desktop)
    {
        var it = new GpItem { Title = GpSourceName(src), Key = src };
        switch (src)
        {
            case "LStick" or "RStick":
            {
                bool isLeft = src == "LStick", swap = Bool(_gpFw?["sticks"]?["swap"]);
                it.Key = "";
                it.Lights = new[] { isLeft ? "LS" : "RS" };
                it.Choices = isLeft ? new[] { "左摇杆", "右摇杆" } : new[] { "右摇杆", "左摇杆" };
                it.Choice = swap ? 1 : 0;
                it.Value = _gpFw == null ? "—" : it.Choices[it.Choice];
                it.Stock = !swap;
                it.Disabled = _gpFw == null;
                it.Why = _gpFwError ?? "正在读取…";
                // 固件只有「左右互换」一个开关：改任意一边，两边一起换
                it.SetChoice = i => { if (_gpFw?["sticks"] is JsonObject s) s["swap"] = i == 1; _ = GpWrite("set_stick_swap", JsonValue.Create(i == 1)); };
                break;
            }
            case "QS" or "Claw":
            {
                var s = _gpSettings;
                string field = src == "QS" ? "qamAction" : "msiAction";
                bool on = s != null && Bool(s["enabled"]);
                int idx = on ? 1 + Math.Max(0, Array.IndexOf(GpActions, Str(s![field]) ?? "None")) : 0;
                // 第一项是不接管时 MSI 原来的功能
                it.Choices = GpSpecialChoices.Select((c, i) => i == 0 ? (src == "QS" ? "MSI 快速设置" : "MSI Center M") : c).ToArray();
                it.Choice = idx;
                it.Value = s == null ? "—" : it.Choices[idx];
                it.Stock = idx == 0;
                it.Disabled = s == null;
                it.Why = _gpError ?? "正在读取…";
                it.SetChoice = i =>
                {
                    if (s == null) return;
                    if (i == 0)
                    {
                        // 接管是两个键一起的：一个放回原功能，另一个也放回
                        s["enabled"] = false;
                        Toast("快速设置键和 Claw 键一起放回 MSI 原来的功能");
                    }
                    else { s["enabled"] = true; s[field] = GpActions[i - 1]; }
                    _ = GpSaveSettings();
                };
                break;
            }
            default:
                it.Value = desktop ? GpDesktopSummary(src) : GpKeySummary(src);
                it.Stock = desktop ? it.Value == "默认" : GpActs(src) == null && GpFirmwareCodes(src) is var c && (c.Length == 0 || c.Length == 1 && c[0] == GpNativeCode(src));
                it.Press = () => GpOpenPicker(src, desktop);
                break;
        }
        return it;
    }

    private static string GpSourceName(string src) => src switch
    {
        "LT" => "LT 左扳机", "RT" => "RT 右扳机", "LB" => "LB 左肩键", "RB" => "RB 右肩键",
        "LStick" => "左摇杆", "RStick" => "右摇杆",
        "LS" => "左摇杆按下", "RS" => "右摇杆按下", "Up" => "十字键 上", "Down" => "十字键 下", "Left" => "十字键 左", "Right" => "十字键 右",
        "Back" => "View 键", "Start" => "Menu 键", "QS" => "快速设置键", "Claw" => "Claw 键",
        "M1" => "M1 背键", "M2" => "M2 背键", _ => $"{src} 键",
    };

    /// <summary>桌面模式下这个键映射到什么：我们写过的从 desktopKeys 读，没写过是固件的桌面默认。</summary>
    private string GpDesktopSummary(string src)
    {
        if (_gpFw == null) return "—";
        if (_gpFw["desktopKeys"]?[src] is not JsonArray arr) return "默认";
        var codes = arr.Select(n => Int(n)).Where(c => c is > 0 and <= 0xFF).ToArray();
        return codes.Length == 0 ? "默认" : GpCodesText(codes);
    }

    private static int GpNativeCode(string src) => src switch
    {
        "Up" => 0x01, "Down" => 0x02, "Left" => 0x03, "Right" => 0x04, "LB" => 0x05, "RB" => 0x06, "LS" => 0x07, "RS" => 0x08,
        "A" => 0x09, "B" => 0x0A, "X" => 0x0B, "Y" => 0x0C, "M1" => 0x11, "M2" => 0x12, "LT" => 0x13, "RT" => 0x14, _ => 0,
    };

    /// <summary>固件管得到的源键（set_key_map 的 14 个 + 背键）；View / Menu 等只能交给软件输入层。</summary>
    private static bool GpFirmwareSource(string src) => GpNativeCode(src) != 0;

    /// <summary>摇杆死区 / 外圈：本地先改（圈立刻跟着变），停手 600 ms 后写一次。</summary>
    private void GpStickChanged(string side)
    {
        int v = ++_gpStickEdits;
        After(600, () =>
        {
            if (v != _gpStickEdits) return;
            var st = _gpFw?["sticks"]?[side];
            _ = GpWrite("set_stick_deadzone", JsonValue.Create(side), JsonValue.Create(Int(st?["deadzone"])), JsonValue.Create(Int(st?["outer"], 100)));
        });
    }
    private int _gpStickEdits, _gpTriggerEdits;

    private void GpTriggerChanged(string side)
    {
        int v = ++_gpTriggerEdits;
        After(600, () =>
        {
            if (v != _gpTriggerEdits) return;
            var t = _gpFw?["triggers"]?[side];
            _ = GpWrite("set_trigger_range", JsonValue.Create(side), JsonValue.Create(Int(t?["deadzone"])), JsonValue.Create(Int(t?["outer"], 100)));
        });
    }

    /// <summary>每页在线稿上额外画的：摇杆页的死区 / 外圈、震动页的握把强度、扳机页的行程刻度。</summary>
    private void PaintGpPageArt(CanvasDrawingSession ds, Color accent, GpItem? focus)
    {
        switch (GpCatId)
        {
            case "stick":
                foreach (var (side, key) in new[] { ("left", "LS"), ("right", "RS") })
                {
                    var st = _gpFw?["sticks"]?[side];
                    if (st == null) continue;
                    var c = GpMap("front", GpKeys[key].X, GpKeys[key].Y);
                    float r = GpStickDisc * GpS;
                    using var dash = new CanvasStrokeStyle { CustomDashStyle = new[] { 2f, 2.5f } };
                    ds.DrawCircle(c, r, Gfx.WithAlpha(CcText, 0.22f), 1);
                    ds.FillCircle(c, r * Int(st["deadzone"]) / 100f, Gfx.WithAlpha(accent, 0.35f));
                    ds.DrawCircle(c, r * Int(st["outer"], 100) / 100f, Gfx.WithAlpha(accent, 0.8f), 1.3f, dash);
                }
                break;
            case "rumble":
                // 没在调某一边时，两边握把按各自强度常亮（动效在 GamepadFx：抖动、握把发光、描边扩散）
                if (focus == null || focus.Lights?.Length == 2)
                {
                    if ((_gpRumbleL ?? 0) > 0) GpFill(ds, "GripL", 0.08f + 0.3f * (_gpRumbleL ?? 0) / 100f, accent);
                    if ((_gpRumbleR ?? 0) > 0) GpFill(ds, "GripR", 0.08f + 0.3f * (_gpRumbleR ?? 0) / 100f, accent);
                }
                break;
            case "trigger":
                // 行程刻度：扳机那块旁边两条短线标出起点、终点
                foreach (var (side, region) in new[] { ("left", "TL"), ("right", "TR") })
                {
                    var t = _gpFw?["triggers"]?[side];
                    if (t == null || !_gpRegions.TryGetValue(region, out var g)) continue;
                    var b = g.ComputeBounds();
                    float x0 = side == "left" ? (float)b.X - 14 : (float)(b.X + b.Width) + 14;
                    foreach (var (v, label) in new[] { (Int(t["deadzone"]), "起"), (Int(t["outer"], 100), "止") })
                    {
                        float y = (float)(b.Y + b.Height * v / 100.0);
                        ds.DrawLine(x0 - 6, y, x0 + 6, y, Gfx.WithAlpha(accent, 0.9f), 2f);
                        PText(ds, label, TextKit.Ui(400), 11, Gfx.WithAlpha(CcText, 0.55f), side == "left" ? x0 - 22 : x0 + 10, y, 20);
                    }
                }
                break;
        }
    }
}
