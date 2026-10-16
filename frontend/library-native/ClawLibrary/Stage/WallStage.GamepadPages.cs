// 控制中心 → 手柄：每个分类挂在线稿两侧的设置，和这一页在线稿上额外画的东西（摇杆死区圈、震动波纹、灯光预览）。
// 当前值从固件读（get_gamepad_state，见 docs/gamepad-firmware-protocol.md）；读不到写「—」。
// 范围照 MSI Center M：摇杆死区 0–50 %、外圈 75–100 %，扳机行程 0–100 %。
using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private static readonly string[] GpActions = { "MainMenu", "QuickAccess", "MagpieToggle", "None" };
    private static readonly string[] GpActionNames = { "主菜单", "快速访问", "超分开关", "不做事" };
    private static readonly string[] GpLightEffects = { "off", "monocolor", "breathe", "rainbow", "ripple", "modeinfo" };
    private static readonly string[] GpLightEffectNames = { "关闭", "常亮", "呼吸", "幻彩", "水波", "模式指示" };
    private static readonly string[] GpLightSpeedNames = { "慢", "中", "快" };
    private static readonly string[] GpLightColorNames = { "白", "红", "橙", "黄", "绿", "青", "蓝", "紫" };
    private static readonly Color[] GpLightColors =
    {
        Gfx.Rgba(0xff, 0xff, 0xff), Gfx.Rgba(0xff, 0x2d, 0x2d), Gfx.Rgba(0xff, 0x8a, 0x1f), Gfx.Rgba(0xff, 0xe0, 0x2a),
        Gfx.Rgba(0x3c, 0xe0, 0x5a), Gfx.Rgba(0x2a, 0xe0, 0xe0), Gfx.Rgba(0x2f, 0x6b, 0xff), Gfx.Rgba(0xa8, 0x4b, 0xff),
    };
    /// <summary>桌面模式下由固件改成键鼠的键（MSI 桌面模式示意图里涂红的那些）。</summary>
    private static readonly string[] GpDesktopKeys = { "LB", "RB", "LS", "RS", "Dpad", "QS", "Claw" };
    private const string GpLater = "后台的手柄固件写入还没上线，接上后这里就能改";

    private void BuildGpItems()
    {
        _gpItems.Clear();
        var left = new List<GpItem>();
        var right = new List<GpItem>();
        string loading = _gpError ?? "正在读取…";
        string fwMissing = _gpFwError ?? "正在读取…";
        GpItem Item(string title, string key, string value, string? why = null) => new() { Title = title, Key = key, Value = value, Disabled = why != null, Why = why };

        switch (GpCatId)
        {
            case "mode":
            {
                string cur = Str(_gpFw?["mode"]) ?? "";
                int idx = cur == "desktop" ? 1 : 0;
                var mode = Item("当前模式", "", _gpFw == null ? "—" : idx == 1 ? "桌面模式" : "手柄模式", _gpFw == null ? fwMissing : null);
                mode.Choices = new[] { "手柄模式", "桌面模式" };
                mode.Choice = idx;
                mode.SetChoice = i => _ = GpWrite("set_gamepad_mode", JsonValue.Create(i == 1 ? "desktop" : "xinput"));
                mode.Lights = idx == 1 ? GpDesktopKeys : new[] { "ABXY", "LS", "RS", "Dpad", "LB", "RB" };
                left.Add(mode);
                right.Add(Item("长按 ≡ 两秒", "Menu", "手柄 / 桌面互切", "这是手柄固件自带的组合键，不能改"));
                right.Add(Item("自动切换", "", "之后", "之后做：进游戏切手柄模式、回到桌面切桌面模式"));
                break;
            }
            case "gpmap":
                foreach (var src in new[] { "LT", "LB", "LS", "Up", "Left", "Right", "Down", "M1", "RT", "RB", "Y", "X", "B", "A", "RS", "M2" })
                    _gpItems.Add(GpMapItem(src, desktop: false));
                return;
            case "dkmap":
                foreach (var src in new[] { "LB", "LS", "Up", "Left", "Right", "Down", "RB", "RS" })
                    _gpItems.Add(GpMapItem(src, desktop: true));
                return;
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
                bool swap = Bool(_gpFw?["sticks"]?["swap"]);
                var sw = Item("交换左右摇杆", "", _gpFw == null ? "—" : swap ? "开" : "关", _gpFw == null ? fwMissing : null);
                sw.Lights = new[] { "LS", "RS" };
                sw.Press = () => _ = GpWrite("set_stick_swap", JsonValue.Create(!swap));
                right.Add(sw);
                break;
            }
            case "trigger":
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
                    bool quick = Bool(t?["quickTrigger"]);
                    var q = Item($"{name}快速触发", key, t == null ? "—" : quick ? "开" : "关", t == null ? fwMissing : null);
                    q.Press = () => _ = GpWrite("set_quick_trigger", JsonValue.Create(side), JsonValue.Create(!quick));
                    list.Add(start);
                    list.Add(end);
                    list.Add(q);
                }
                break;
            case "rumble":
            {
                // 照 MSI 振动页：左右马达分开调，测试、恢复；另加我们的 Steam 导航震动
                var s = _gpSettings;
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
                var nav = Item("Steam 导航震动", "GripL", s == null ? loading : Bool(s["hapticsEnabled"]) ? "开" : "关", s == null ? loading : null);
                nav.Lights = new[] { "GripL", "GripR" };
                nav.Press = () => { if (s == null) return; s["hapticsEnabled"] = !Bool(s["hapticsEnabled"]); BuildGpItems(); PaintGpBody(); _ = GpSaveSettings(); };
                left.Add(nav);
                right.Add(Motor("右侧震动", "GripR", _gpRumbleR, false));
                var test = Item("测试震动", "GripR", "按 A 震一下", _gpRumbleL == null ? loading : null);
                test.Lights = new[] { "GripL", "GripR" };
                test.Press = () => _ = GpTestRumble();
                right.Add(test);
                var reset = Item("恢复原来的强度", "GripR", "按 A", _gpRumbleL == null ? loading : null);
                reset.Lights = new[] { "GripL", "GripR" };
                reset.Press = () => _ = GpWrite("restore_rumble");
                right.Add(reset);
                break;
            }
            case "mouse":
                left.Add(Item("移动光标", "LS", "左摇杆", GpLater));
                left.Add(Item("光标速度", "LS", "—", GpLater));
                right.Add(Item("滚动", "RS", "右摇杆", GpLater));
                right.Add(Item("左键 / 右键", "RB", "RB / LB", GpLater));
                break;
            case "special":
            {
                var s = _gpSettings;
                bool on = s != null && Bool(s["enabled"]);
                GpItem KeyAction(string title, string key, string field)
                {
                    if (s == null) return Item(title, key, loading, loading);
                    int idx = Math.Max(0, Array.IndexOf(GpActions, Str(s[field]) ?? "None"));
                    var it = Item(title, key, on ? GpActionNames[idx] : "未接管", on ? null : "先打开「接管（Steam 大屏幕里）」");
                    it.Choices = GpActionNames;
                    it.Choice = idx;
                    it.SetChoice = i => { s[field] = GpActions[i]; _ = GpSaveSettings(); };
                    return it;
                }
                left.Add(KeyAction("快速设置键", "QS", "qamAction"));
                var takeover = Item("接管（Steam 大屏幕里）", "QS", s == null ? loading : on ? "开" : "关", s == null ? loading : null);
                takeover.Lights = new[] { "QS", "Claw" };
                takeover.Press = () => { if (s == null) return; s["enabled"] = !on; BuildGpItems(); PaintGpBody(); _ = GpSaveSettings(); };
                left.Add(takeover);
                right.Add(KeyAction("Claw 键", "Claw", "msiAction"));
                break;
            }
            default:   // light
            {
                // 照 MSI Mystic Light：效果（关闭 / 常亮 / 呼吸 / 幻彩 / 水波 / 模式指示）、颜色、亮度、速度（慢 / 中 / 快）
                var l = _gpFw?["light"];
                int effect = Math.Max(0, Array.IndexOf(GpLightEffects, Str(l?["effect"]) ?? "off"));
                int color = GpLightColorIndex(l), bright = Int(l?["brightness"], -1), speed = Math.Clamp(Int(l?["speedLevel"], 1), 0, 2);
                bool hasColor = effect is 1 or 2 or 4, hasSpeed = effect is 2 or 3 or 4;
                void Write(int e, int c, int b, int sp) => _ = GpWrite("set_light", JsonValue.Create(GpLightEffects[e]), JsonValue.Create(GpLightHex(c)), JsonValue.Create(Math.Max(0, b)), JsonValue.Create(sp));
                var e = Item("灯光效果", "LS", l == null ? "—" : GpLightEffectNames[effect], l == null ? fwMissing : null);
                e.Lights = new[] { "LS", "RS" };
                e.Choices = GpLightEffectNames;
                e.Choice = effect;
                e.SetChoice = i => Write(i, color, bright < 0 ? 100 : bright, speed);
                left.Add(e);
                var sp = Item("速度", "LS", l == null ? "—" : hasSpeed ? GpLightSpeedNames[speed] : "这个效果没有速度", l == null ? fwMissing : hasSpeed ? null : "常亮、模式指示不分快慢");
                sp.Lights = new[] { "LS", "RS" };
                sp.Choices = GpLightSpeedNames;
                sp.Choice = speed;
                sp.SetChoice = i => Write(effect, color, bright, i);
                left.Add(sp);
                var c = Item("颜色", "RS", l == null ? "—" : hasColor ? GpLightColorNames[color] : "跟着效果走", l == null ? fwMissing : hasColor ? null : "幻彩、模式指示的颜色由效果决定");
                c.Lights = new[] { "LS", "RS" };
                c.Choices = GpLightColorNames;
                c.Choice = color;
                if (hasColor) c.Swatches = GpLightColors;
                c.SetChoice = i => Write(effect, i, bright, speed);
                right.Add(c);
                var b = Item("亮度", "RS", bright < 0 ? "—" : $"{bright} %", bright < 0 ? fwMissing : null);
                b.Lights = new[] { "LS", "RS" };
                if (bright >= 0)
                {
                    b.Number = bright; b.Min = 0; b.Max = 100; b.StepBy = 10; b.Format = v => $"{v} %";
                    b.SetNumber = v => Write(effect, color, v, speed);
                }
                right.Add(b);
                break;
            }
        }

        // 两侧按线稿竖向居中排开
        void Column(List<GpItem> items, int side)
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Side = side;
                items[i].Row = i;
                items[i].X = side < 0 ? GpLeftEdge : GpRightEdge;
                items[i].Y = GpCy - 13 + (i - (items.Count - 1) / 2f) * GpRowStep;
            }
        }
        Column(left, -1);
        Column(right, 1);
        _gpItems.AddRange(left);
        _gpItems.AddRange(right);
        if (_gpFocus >= _gpItems.Count) _gpFocus = 0;
        GpUpdateLightPreview();
    }

    /// <summary>按键映射页的一个键：焦点在线稿上这个键时，说明画在它那一侧、和它同高。</summary>
    private GpItem GpMapItem(string src, bool desktop)
    {
        var key = GpKeys[src];
        var pos = key.Face == "back" ? GpMap("front", key.X < 350 ? 70 : 830, 290) : GpMap(key.Face, key.X, key.Y);
        bool left = pos.X < GpCx;
        var it = new GpItem
        {
            Hotspot = true, Key = src, Side = left ? -1 : 1, X = left ? GpLeftEdge : GpRightEdge, Y = Math.Clamp(pos.Y - 13, GpY - 10, GpY + GpH - 30),
            Title = $"{GpSourceName(src)}  →", Value = desktop ? "—" : GpMappingText(src),
        };
        it.Press = () => GpOpenPicker(src, desktop);
        return it;
    }

    private static string GpSourceName(string src) => src switch
    {
        "LT" => "LT 左扳机", "RT" => "RT 右扳机", "LB" => "LB 左肩键", "RB" => "RB 右肩键",
        "LS" => "左摇杆按下", "RS" => "右摇杆按下", "Up" => "十字键 上", "Down" => "十字键 下", "Left" => "十字键 左", "Right" => "十字键 右",
        "M1" => "M1 背键", "M2" => "M2 背键", _ => $"{src} 键",
    };

    /// <summary>这个键现在映射到什么：背键、扳机从固件读；其余固件里没读这部分，写「原键位」。</summary>
    private string GpMappingText(string src)
    {
        JsonNode? codes = src switch
        {
            "M1" => _gpFw?["backKeys"]?["m1"]?["codes"], "M2" => _gpFw?["backKeys"]?["m2"]?["codes"],
            _ => _gpFw?["keys"]?[src]?["codes"],
        };
        if (codes is not JsonArray arr || arr.Count == 0) return _gpFw == null ? "—" : "原键位";
        var list = arr.Select(n => Int(n)).Where(c => c is > 0 and < 0xFF).ToArray();
        if (list.Length == 0) return "不映射";
        if (list.Length == 1 && GpNativeCode(src) == list[0]) return "原键位";
        return string.Join(" + ", list.Select(GpCodeName));
    }

    private static int GpNativeCode(string src) => src switch
    {
        "Up" => 0x01, "Down" => 0x02, "Left" => 0x03, "Right" => 0x04, "LB" => 0x05, "RB" => 0x06, "LS" => 0x07, "RS" => 0x08,
        "A" => 0x09, "B" => 0x0A, "X" => 0x0B, "Y" => 0x0C, "M1" => 0x11, "M2" => 0x12, "LT" => 0x13, "RT" => 0x14, _ => 0,
    };

    private static int GpLightColorIndex(JsonNode? light)
    {
        if (light?["color"] is not JsonArray c || c.Count < 3) return 0;
        var rgb = new Vector3(Int(c[0]), Int(c[1]), Int(c[2]));
        int best = 0; float bestD = float.MaxValue;
        for (int i = 0; i < GpLightColors.Length; i++)
        {
            float d = Vector3.DistanceSquared(rgb, new Vector3(GpLightColors[i].R, GpLightColors[i].G, GpLightColors[i].B));
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private static string GpLightHex(int i) => $"#{GpLightColors[i].R:x2}{GpLightColors[i].G:x2}{GpLightColors[i].B:x2}";

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

    /// <summary>每页在线稿上额外画的：摇杆页的死区 / 外圈、震动页的握把强度和波纹、灯光页的灯色预览、扳机页的行程刻度。</summary>
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
