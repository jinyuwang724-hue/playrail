// 从 Cocoon 左侧栏进入的全屏「手柄」页及 ModalSheet 子页面：
//   主页面（1920×1200）：左上「B ←」返回胶囊 +「手柄」标题；
//   左列上方 5 个分类（按键映射 / 摇杆 / 扳机 / 震动 / 桌面模式），下方立体卡片内嵌实时硬件视窗（Claw 8 EX 陶瓷正反面线稿 / 摇杆死区同心圆 / 扳机 270° 圆环仪表 + 底部状态与模式胶囊）；
//   右列立体分组卡片（控制模式下拉、背键与专用键、ABXY、肩键与扳机、十字键、摇杆按下与功能键，以及摇杆 / 扳机 / 震动 / 桌面模式的开关、下拉与双层滑块）；
//   子页面 ModalSheet（1728×1060 圆角面板）：
//     1. 选择指令（Chooser）：完整 5 页签（手柄 26 键位 + 线稿联动 / 鼠标 9 键位 + 引线图 / 键盘 87 键 3D 键帽 + 组合预览 / 数字键盘媒体与小键盘 / 系统动作三栏卡片）；
//     2. 单键触发与专用键页（Key）：多触发方式（普通按压 / 长按 / 双击 / 开始 / 释放 / 组合键）、生效场景、添加或删除触发、禁用与恢复原键位；
//     3. 触发设置页（Settings）：长按时间、双击间隔、按住连发、切换、启动 / 结束延迟、可中断。
//   严格仅在 _ccLight && _ccOpen && _gpOpen 时接管，不影响深色主题（Folia / Cases）及游戏内嵌手柄设置。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Input;
using Windows.UI;
using Colors = Microsoft.UI.Colors;
using Rect = Windows.Foundation.Rect;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum CgpRowKind { Dropdown, Toggle, Slider, Stepper, KeyMap, Action }

    private sealed class CgpRow
    {
        public CgpRowKind Kind;
        public bool Break;
        public string Id = "";
        public string Title = "";
        public string? Sub;
        public string Value = "";
        public bool AccentValue, Danger, Nav, Disabled, Changed;
        public string? Why;

        // Dropdown / Toggle
        public string[]? Options;
        public int Choice;
        public Action<int>? Select;
        public Action? Press;
        public Action? Secondary;

        // Slider / Stepper
        public int Number, Min, Max = 100, Step = 1;
        public Action<int>? SetNumber;
        public Action<int>? StepBy;

        // KeyMap
        public string KeySource = "";
        public string KeyBadge = "";
        public bool DesktopKey;
        public bool Stock = true;
        public string? TriggerTag;
        public List<string>? Caps;
    }

    private sealed class CgpView
    {
        public required ContainerVisual Root, PanelWell, Sel, ListBox, ModalRoot, ModalBox, ModalListClip;
        public required SpriteVisual Bg, Head, Left, PanelLive, List, Press, ModalScrim, ModalSheet, ModalList, ModalPress, ModalBody, ModalHead, Toast;
        public readonly List<SpriteVisual> CatItems = new();
        public readonly List<CompositionDrawingSurface> CatSurfs = new();
        public required CurSet Cur, ModalCur;
        public CompositionDrawingSurface? HeadS, LeftS, PanelLiveS, ListS, ModalSheetS, ModalListS, ModalBodyS, ModalHeadS, ToastS;
        public int SelOn = -1, ToastGen, ModalSeq;
        public string HeadKey = "", LeftKey = "", PanelLiveKey = "", SelKey = "", ListKey = "", ModalHeadKey = "", ModalBodyKey = "", ModalListKey = "";
        public float Scroll, ListH, ModalScroll, ModalListH, ModalSheetRaster;
        public bool Manual, OffsetInit, ModalManual, ModalOffsetInit, ModalShown;
        public List<(int I, float Y, float H)> Lay = new();
        public List<(int I, float Y, float H)> ModalLay = new();
    }

    private static readonly (string Id, string Name, string Icon)[] CgpCats =
    {
        ("map", "按键映射", "pad"),
        ("stick", "摇杆", "stick"),
        ("trigger", "扳机", "trigger"),
        ("rumble", "震动", "rumble"),
        ("desktop", "桌面模式", "mouse"),
    };

    private const float CgpSlot = 112f;
    private const float CgpCardTop = CpTop + 5 * CgpSlot + 24f; // 734
    private const float CgpCardH = CpBottom - CgpCardTop;       // 452
    private const float CgpWellX = CpLeftX + 28f, CgpWellY = CgpCardTop + 28f, CgpWellW = CpLeftW - 56f, CgpWellH = CgpCardH - 138f; // 72, 762, 506, 314
    private const float CgpModalListX = 156f, CgpModalListW = 1608f, CgpModalListTop = 212f, CgpModalListBottom = 1094f;

    private CgpView? _cgp;
    private int _cgpCat, _cgpZone, _cgpFocus;
    private int _cgpDrop = -1, _cgpDropSel;
    private int _cgpModalDrop = -1, _cgpModalDropSel;
    private string _cgpActiveKey = "M1";
    private bool _cgpTouchMode, _cgpRumblePulse;
    private List<CgpRow> _cgpRows = new();
    private List<PerfRow> _cgpModalRows = new();

    private bool CgpActive => _ccLight && _ccOpen && _gpOpen;
    private bool CgpModalOpen => CgpActive && _gpPicker != null;

    // ---------------- 生命周期 ----------------

    private void CgpOnOpen()
    {
        _cgpCat = 0;
        _cgpZone = 0;
        _cgpFocus = 0;
        _cgpDrop = -1;
        _cgpModalDrop = -1;
        _cgpActiveKey = "M1";
        _cgpTouchMode = false;
        _cgpRumblePulse = false;
        _cgpT = null;
        if (_cgp is { } v)
        {
            v.Scroll = v.ModalScroll = 0;
            v.Manual = v.ModalManual = false;
            v.OffsetInit = v.ModalOffsetInit = false;
            v.HeadKey = v.LeftKey = v.PanelLiveKey = v.SelKey = v.ListKey = v.ModalHeadKey = v.ModalBodyKey = v.ModalListKey = "";
            v.SelOn = -1;
            v.Sel.IsVisible = false;
            v.ModalShown = false;
            v.ModalRoot.IsVisible = false;
            v.Cur.Shown = false;
            foreach (var c in v.Cur.All) { c.StopAnimation("Opacity"); c.Opacity = 0; }
            v.ModalCur.Shown = false;
            foreach (var c in v.ModalCur.All) { c.StopAnimation("Opacity"); c.Opacity = 0; }
        }
    }

    private void CgpOnClose(bool instant)
    {
        _cgpT = null;
        _cgpDrop = -1;
        _cgpModalDrop = -1;
        if (instant && _cgp != null) _cgp.Root.IsVisible = false;
    }

    private void CgpHide()
    {
        if (_cgp != null) _cgp.Root.IsVisible = false;
    }

    private CgpView CgpEnsure()
    {
        if (_cgp is { } existing)
        {
            if (existing.Root.Parent != _gp && _gp != null)
            {
                (existing.Root.Parent as ContainerVisual)?.Children.Remove(existing.Root);
                _gp.Children.InsertAtTop(existing.Root);
            }
            return existing;
        }

        SpriteVisual Sp() => G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);

        var g = G.C.CreateLinearGradientBrush();
        g.StartPoint = new Vector2(0, 0);
        g.EndPoint = new Vector2(0, 1);
        g.ColorStops.Add(G.C.CreateColorGradientStop(0, Color.FromArgb(255, 0xEF, 0xEF, 0xF4)));
        g.ColorStops.Add(G.C.CreateColorGradientStop(1, Color.FromArgb(255, 0xE2, 0xE3, 0xEA)));
        var bg = G.Sprite(g, relative: false);
        bg.Size = new Vector2(CsW, CsH);

        var panelWell = G.Container(relative: false);
        panelWell.Size = new Vector2(CgpWellW, CgpWellH);
        panelWell.Offset = new Vector3(CgpWellX, CgpWellY, 0);
        var panelLive = Sp();
        panelWell.Children.InsertAtTop(panelLive);

        var listBox = G.Container(relative: false);
        listBox.Size = new Vector2(CsW, CsH);
        listBox.Clip = G.C.CreateInsetClip(CpListX - 40, 0, 0, CsH - CpBottom);
        var list = Sp();
        var press = G.Sprite(G.Solid(Color.FromArgb(18, 30, 30, 60)), relative: false);
        press.Opacity = 0;
        list.Children.InsertAtTop(press);
        listBox.Children.InsertAtTop(list);
        {
            var fg = G.C.CreateLinearGradientBrush();
            fg.StartPoint = new Vector2(0, 0); fg.EndPoint = new Vector2(0, 1);
            var topCol = Color.FromArgb(255, 0xEF, 0xEF, 0xF4);
            fg.ColorStops.Add(G.C.CreateColorGradientStop(0, topCol));
            fg.ColorStops.Add(G.C.CreateColorGradientStop(0.35f, Color.FromArgb(235, topCol.R, topCol.G, topCol.B)));
            fg.ColorStops.Add(G.C.CreateColorGradientStop(1, Color.FromArgb(0, topCol.R, topCol.G, topCol.B)));
            var fade = G.Sprite(fg, relative: false);
            fade.Size = new Vector2(CsW - (CpListX - 40), CpTop);
            fade.Offset = new Vector3(CpListX - 40, 0, 0);
            listBox.Children.InsertAtTop(fade);
        }

        // ModalSheet 层
        var modalRoot = G.Container(relative: false);
        modalRoot.Size = new Vector2(CsW, CsH);
        modalRoot.IsVisible = false;
        var modalScrim = G.Sprite(G.Solid(Color.FromArgb(194, 35, 35, 37)), relative: false);
        modalScrim.Size = new Vector2(CsW, CsH);

        var modalBox = G.Container(relative: false);
        modalBox.Size = new Vector2(CsW, CsH);
        modalBox.CenterPoint = new Vector3(RsX + RsW / 2, RsY + RsH / 2, 0);
        var modalSheet = Sp();

        var modalListClip = G.Container(relative: false);
        modalListClip.Size = new Vector2(CsW, CsH);
        var clipGeo = G.C.CreateRoundedRectangleGeometry();
        clipGeo.Offset = new Vector2(RsX, CgpModalListTop - 14);
        clipGeo.Size = new Vector2(RsW, RsY + RsH - (CgpModalListTop - 14) - 6);
        clipGeo.CornerRadius = new Vector2(48, 48);
        modalListClip.Clip = G.C.CreateGeometricClip(clipGeo);
        var modalList = Sp();
        var modalPress = G.Sprite(G.Solid(Color.FromArgb(18, 30, 30, 60)), relative: false);
        modalPress.Opacity = 0;
        modalList.Children.InsertAtTop(modalPress);
        modalListClip.Children.InsertAtTop(modalList);

        var modalBody = Sp();
        var modalHead = Sp();
        foreach (var mv in new Visual[] { modalSheet, modalListClip, modalBody, modalHead }) modalBox.Children.InsertAtTop(mv);
        modalRoot.Children.InsertAtTop(modalScrim);
        modalRoot.Children.InsertAtTop(modalBox);
        var modalCur = CurNew(modalRoot);

        var head = Sp();
        var left = Sp();
        var sel = G.Container(relative: false);
        var toast = Sp();
        toast.Opacity = 0;

        foreach (var x in new Visual[] { bg, head, left, panelWell, sel, listBox }) root.Children.InsertAtTop(x);
        var cur = CurNew(root);
        root.Children.InsertAtTop(modalRoot);
        root.Children.InsertAtTop(toast);

        _gp!.Children.InsertAtTop(root);

        var v = new CgpView
        {
            Root = root,
            Bg = bg,
            Head = head,
            Left = left,
            PanelWell = panelWell,
            PanelLive = panelLive,
            Sel = sel,
            ListBox = listBox,
            List = list,
            Press = press,
            Cur = cur,
            ModalRoot = modalRoot,
            ModalScrim = modalScrim,
            ModalBox = modalBox,
            ModalSheet = modalSheet,
            ModalListClip = modalListClip,
            ModalList = modalList,
            ModalPress = modalPress,
            ModalBody = modalBody,
            ModalHead = modalHead,
            ModalCur = modalCur,
            Toast = toast,
        };
        CgpPaintModalSheetBg(v);
        _cgp = v;
        return v;
    }

    // ---------------- 主绘制入口 ----------------

    private bool CgpPaint()
    {
        if (!CgpActive || _gp == null)
        {
            if (_cgp != null) _cgp.Root.IsVisible = false;
            return false;
        }
        _gpArtPage.IsVisible = false;
        _gpBody.IsVisible = false;

        // DirectChooser 完成选键后 GpChooseCommand 会把 Level 置成 Group，Cocoon 主页面本身即分组列表，直接收起子页
        if (_gpPicker is { Level: GpSubLevel.Group }) _gpPicker = null;

        var v = CgpEnsure();
        v.Root.IsVisible = true;
        v.Root.Scale = new Vector3(_ccK / 1.5f, _ccK / 1.5f, 1);

        _cgpRows = CgpBuildRows(_cgpCat);
        if (_cgpRows.Count > 0) _cgpFocus = Math.Clamp(_cgpFocus, 0, _cgpRows.Count - 1);
        if (_cgpDrop >= _cgpRows.Count || _cgpDrop >= 0 && _cgpRows[_cgpDrop].Options == null) _cgpDrop = -1;

        if (_cgpZone == 1 && _cgpFocus >= 0 && _cgpFocus < _cgpRows.Count && _cgpRows[_cgpFocus].KeySource is { Length: > 0 } ks)
            _cgpActiveKey = ks;

        CgpPaintHead(v);
        CgpPaintLeft(v);
        CgpPaintSel(v);
        CgpPaintPanelInner(v);

        string listKey = CgpComputeListKey(_cgpRows);
        if (listKey != v.ListKey)
        {
            v.Lay = CgpLayoutRows(_cgpRows, _cgpDrop, CpListW, out var groups);
            v.ListKey = listKey;
            CgpPaintList(v, _cgpRows, groups);
        }
        else v.Lay = CgpLayoutRows(_cgpRows, _cgpDrop, CpListW, out _);

        CgpScrollAndFocus(v);
        CgpSyncModal(v);
        return true;
    }

    private bool CgpPaintLive()
    {
        if (!CgpActive || _gp == null) return false;
        if (_cgp is { } v && v.Root.IsVisible && !CgpModalOpen) CgpPaintPanelInner(v);
        return true;
    }

    private bool CgpPlayRumble(float left, float right)
    {
        if (!CgpActive || _cgp is not { } v) return false;
        _cgpRumblePulse = true;
        v.PanelLiveKey = "";
        CgpPaintPanelInner(v);
        if (!Reduced)
        {
            float amp = 2.4f + 4.8f * Math.Max(left, right);
            var shake = G.C.CreateVector3KeyFrameAnimation();
            var linear = G.C.CreateLinearEasingFunction();
            const int steps = 12;
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps, decay = (1 - t) * (1 - t);
                float dx = i == steps ? 0 : (i % 2 == 0 ? 1 : -1) * amp * decay;
                float dy = i == steps ? 0 : (i % 4 < 2 ? 0.4f : -0.4f) * amp * decay;
                shake.InsertKeyFrame(t, new Vector3(CgpWellX + dx, CgpWellY + dy, 0), linear);
            }
            shake.Duration = TimeSpan.FromMilliseconds(380);
            v.PanelWell.StartAnimation("Offset", shake);
        }
        After(620, () =>
        {
            _cgpRumblePulse = false;
            if (CgpActive && _cgp != null) { _cgp.PanelLiveKey = ""; CgpPaintPanelInner(_cgp); }
        });
        return true;
    }

    // ---------------- 主页面数据构建 ----------------

    private List<CgpRow> CgpBuildRows(int cat)
    {
        var rows = new List<CgpRow>();
        string fwMissing = _gpFwError ?? "正在读取…";
        string loading = _gpError ?? "正在读取…";

        switch (cat)
        {
            case 0: // 按键映射
            case 4: // 桌面模式
            {
                bool desk = cat == 4;
                int modeIdx = GpWantDesktop ? 1 : 0;
                // 11-27 jinyu：桌面模式页不放「当前控制模式」，只在按键映射页
                if (!desk) rows.Add(new CgpRow
                {
                    Break = true,
                    Id = "mode",
                    Kind = CgpRowKind.Dropdown,
                    Title = "当前控制模式",
                    Sub = desk
                        ? (GpWantDesktop ? "游戏库在前台时保持手柄导航，离开后自动切为键鼠" : "切换到桌面模式后，以下键鼠映射将在桌面生效")
                        : (GpWantDesktop ? "游戏库在前台时保持手柄导航，离开后自动切为键鼠" : "简单改键直写固件零延迟，多触发自动启用输入层"),
                    Options = new[] { "手柄模式（XInput）", "桌面模式（键鼠）" },
                    Choice = modeIdx,
                    Disabled = _gpFw == null,
                    Why = fwMissing,
                    Select = i =>
                    {
                        if (i != modeIdx) _ = GpToggleMode();
                    },
                });

                if (desk)
                {
                    rows.Add(CgpMakeDeskStickRow("LStick", "LS", "左摇杆桌面功能", true));
                    rows.Add(CgpMakeDeskStickRow("RStick", "RS", "右摇杆桌面功能", false));
                }

                rows.Add(CgpMakeKeyRow("M1", "M1", "M1 背键", desk, firstInCard: true));
                rows.Add(CgpMakeKeyRow("M2", "M2", "M2 背键", desk));
                if (!desk)
                {
                    rows.Add(CgpMakeKeyRow("QS", "QS", "快速设置键", false));
                    rows.Add(CgpMakeKeyRow("Claw", "Claw", "Claw 专用键", false));
                }

                rows.Add(CgpMakeKeyRow("A", "A", "A 键", desk, firstInCard: true));
                rows.Add(CgpMakeKeyRow("B", "B", "B 键", desk));
                rows.Add(CgpMakeKeyRow("X", "X", "X 键", desk));
                rows.Add(CgpMakeKeyRow("Y", "Y", "Y 键", desk));

                rows.Add(CgpMakeKeyRow("LB", "LB", "LB 左肩键", desk, firstInCard: true));
                rows.Add(CgpMakeKeyRow("RB", "RB", "RB 右肩键", desk));
                rows.Add(CgpMakeKeyRow("LT", "LT", "LT 左扳机", desk));
                rows.Add(CgpMakeKeyRow("RT", "RT", "RT 右扳机", desk));

                rows.Add(CgpMakeKeyRow("Up", "↑", "十字键 上", desk, firstInCard: true));
                rows.Add(CgpMakeKeyRow("Down", "↓", "十字键 下", desk));
                rows.Add(CgpMakeKeyRow("Left", "←", "十字键 左", desk));
                rows.Add(CgpMakeKeyRow("Right", "→", "十字键 右", desk));

                rows.Add(CgpMakeKeyRow("LS", "LS", "左摇杆按下", desk, firstInCard: true));
                rows.Add(CgpMakeKeyRow("RS", "RS", "右摇杆按下", desk));
                if (!desk)
                {
                    rows.Add(CgpMakeKeyRow("Back", "View", "View 键", false));
                    rows.Add(CgpMakeKeyRow("Start", "Menu", "Menu 键", false));
                }
                break;
            }

            case 1: // 摇杆
            {
                bool swap = Bool(_gpFw?["sticks"]?["swap"]);
                rows.Add(new CgpRow
                {
                    Break = true,
                    Id = "swap",
                    Kind = CgpRowKind.Toggle,
                    Title = "左右摇杆互换",
                    Sub = "直接写入手柄固件，全局生效",
                    Choice = swap ? 1 : 0,
                    Changed = swap,
                    Disabled = _gpFw == null,
                    Why = fwMissing,
                    Press = () =>
                    {
                        bool next = !swap;
                        if (_gpFw?["sticks"] is JsonObject s) s["swap"] = next;
                        _ = GpWrite("set_stick_swap", JsonValue.Create(next));
                        CgpPaint();
                    },
                });

                var modeNames = GpStickModes.Select(GpStickModeName).ToArray();
                foreach (var (side, label, keyId) in new[] { ("left", "左摇杆", "LS"), ("right", "右摇杆", "RS") })
                {
                    string mode = GpStickMode(side, false);
                    int mi = Math.Max(0, Array.IndexOf(GpStickModes, mode));
                    rows.Add(new CgpRow
                    {
                        Break = true,
                        Id = $"{side}_mode",
                        Kind = CgpRowKind.Dropdown,
                        KeySource = keyId,
                        Title = $"{label}行为",
                        Options = modeNames,
                        Choice = mi,
                        Changed = mode != "stick",
                        Disabled = _gpLayout == null,
                        Why = loading,
                        Select = i =>
                        {
                            if (i != mi) GpSetStick(side, "mode", JsonValue.Create(GpStickModes[i]), false);
                        },
                    });

                    if (mode is "mouse" or "scroll")
                    {
                        int sens = GpStickSens(side, false);
                        rows.Add(new CgpRow
                        {
                            Id = $"{side}_sens",
                            Kind = CgpRowKind.Slider,
                            KeySource = keyId,
                            Title = $"{label}灵敏度",
                            Value = $"{sens} %",
                            Number = sens,
                            Min = 1,
                            Max = 100,
                            Step = 5,
                            SetNumber = n => GpSetStick(side, "sensitivity", JsonValue.Create(Math.Clamp(n, 1, 100)), false),
                        });
                    }

                    var st = _gpFw?["sticks"]?[side];
                    int dz = Int(st?["deadzone"], -1), outer = Int(st?["outer"], -1);
                    rows.Add(new CgpRow
                    {
                        Id = $"{side}_dz",
                        Kind = CgpRowKind.Slider,
                        KeySource = keyId,
                        Title = $"{label}死区",
                        Value = dz < 0 ? "—" : $"{dz} %",
                        Number = Math.Clamp(dz < 0 ? 10 : dz, 0, 50),
                        Min = 0,
                        Max = 50,
                        Step = 2,
                        Disabled = dz < 0,
                        Why = fwMissing,
                        SetNumber = v => { if (st != null) { st["deadzone"] = v; GpStickChanged(side); CgpPaint(); } },
                    });
                    rows.Add(new CgpRow
                    {
                        Id = $"{side}_outer",
                        Kind = CgpRowKind.Slider,
                        KeySource = keyId,
                        Title = $"{label}外圈阈值",
                        Value = outer < 0 ? "—" : $"{outer} %",
                        Number = Math.Clamp(outer < 0 ? 95 : outer, 75, 100),
                        Min = 75,
                        Max = 100,
                        Step = 1,
                        Disabled = outer < 0,
                        Why = fwMissing,
                        SetNumber = v => { if (st != null) { st["outer"] = v; GpStickChanged(side); CgpPaint(); } },
                    });
                }
                break;
            }

            case 2: // 扳机
            {
                var tl = _gpFw?["triggers"]?["left"];
                var tr = _gpFw?["triggers"]?["right"];
                bool quick = Bool(tl?["quickTrigger"]) || Bool(tr?["quickTrigger"]);
                rows.Add(new CgpRow
                {
                    Break = true,
                    Id = "qt",
                    Kind = CgpRowKind.Toggle,
                    Title = "快速触发",
                    Sub = tl == null ? fwMissing : quick ? "开 · 轻按即触发满行程" : "关 · 保持线性模拟行程",
                    Choice = quick ? 1 : 0,
                    Changed = quick,
                    Disabled = tl == null,
                    Why = fwMissing,
                    Press = () => _ = GpSetQuickTrigger(!quick),
                });

                foreach (var (side, node, label, keyId) in new[] { ("left", tl, "左扳机", "LT"), ("right", tr, "右扳机", "RT") })
                {
                    int a = Int(node?["deadzone"], -1), b = Int(node?["outer"], -1);
                    bool ok = a >= 0 && b >= 0;
                    int sv = ok ? a : 5, ev = ok ? b : 100;
                    rows.Add(new CgpRow
                    {
                        Break = true,
                        Id = $"{side}_start",
                        Kind = CgpRowKind.Slider,
                        KeySource = keyId,
                        Title = $"{label}起点",
                        Value = ok ? $"{sv} %" : "—",
                        Number = sv,
                        Min = 0,
                        Max = Math.Max(0, ev - 5),
                        Step = 5,
                        Disabled = !ok,
                        Why = fwMissing,
                        SetNumber = v => { if (node != null) { node["deadzone"] = v; GpTriggerChanged(side); CgpPaint(); } },
                    });
                    rows.Add(new CgpRow
                    {
                        Id = $"{side}_end",
                        Kind = CgpRowKind.Slider,
                        KeySource = keyId,
                        Title = $"{label}终点",
                        Value = ok ? $"{ev} %" : "—",
                        Number = ev,
                        Min = Math.Min(100, sv + 5),
                        Max = 100,
                        Step = 5,
                        Disabled = !ok,
                        Why = fwMissing,
                        SetNumber = v => { if (node != null) { node["outer"] = v; GpTriggerChanged(side); CgpPaint(); } },
                    });
                }
                break;
            }

            case 3: // 震动
            {
                int rl = _gpRumbleL ?? -1, rr = _gpRumbleR ?? -1;
                rows.Add(new CgpRow
                {
                    Break = true,
                    Id = "rumble_l",
                    Kind = CgpRowKind.Slider,
                    Title = "左侧握把震动",
                    Value = rl < 0 ? loading : rl == 0 ? "关" : $"{rl} %",
                    Number = Math.Clamp(rl < 0 ? 80 : rl, 0, 100),
                    Min = 0,
                    Max = 100,
                    Step = 5,
                    Disabled = rl < 0,
                    Why = loading,
                    SetNumber = v => { GpRumbleChanged(v, _gpRumbleR ?? v); CgpPaint(); },
                });
                rows.Add(new CgpRow
                {
                    Id = "rumble_r",
                    Kind = CgpRowKind.Slider,
                    Title = "右侧握把震动",
                    Value = rr < 0 ? loading : rr == 0 ? "关" : $"{rr} %",
                    Number = Math.Clamp(rr < 0 ? 80 : rr, 0, 100),
                    Min = 0,
                    Max = 100,
                    Step = 5,
                    Disabled = rr < 0,
                    Why = loading,
                    SetNumber = v => { GpRumbleChanged(_gpRumbleL ?? v, v); CgpPaint(); },
                });

                rows.Add(new CgpRow
                {
                    Break = true,
                    Id = "rumble_test",
                    Kind = CgpRowKind.Action,
                    Title = "测试震动",
                    Value = "立即震动一下",
                    AccentValue = true,
                    Press = () => _ = GpTestRumble(),
                });
                rows.Add(new CgpRow
                {
                    Id = "rumble_reset",
                    Kind = CgpRowKind.Action,
                    Title = "恢复默认强度",
                    Value = "左右 100 %",
                    Press = () =>
                    {
                        _gpRumbleL = _gpRumbleR = 100;
                        CgpPaint();
                        _ = GpWrite("restore_rumble");
                    },
                });
                break;
            }
        }
        return rows;
    }

    private CgpRow CgpMakeDeskStickRow(string src, string badge, string title, bool firstInCard)
    {
        string side = src == "LStick" ? "left" : "right";
        var s = _gpFw?["desktopStickMappings"]?[side];
        string mode = GpStickMode(side, true);
        bool stock = mode == (side == "left" ? "mouse" : "scroll") && !Bool(s?["flipX"]) && !Bool(s?["flipY"]);
        return new CgpRow
        {
            Break = firstInCard,
            Id = $"dstick_{src}",
            Kind = CgpRowKind.KeyMap,
            KeySource = badge,
            KeyBadge = badge,
            DesktopKey = true,
            Title = title,
            Stock = stock,
            Changed = !stock,
            Value = _gpDesktopLayout == null ? "—" : GpStickModeName(mode),
            Disabled = _gpDesktopLayout == null,
            Why = _gpFwError ?? "正在读取…",
            Nav = true,
            Press = () => CgpOpenModal(src, desktop: true, directChooser: false),
            Secondary = () => CgpOpenModal(src, desktop: true, directChooser: false),
        };
    }

    private CgpRow CgpMakeKeyRow(string src, string badge, string title, bool desktop, bool firstInCard = false)
    {
        var row = new CgpRow
        {
            Break = firstInCard,
            Id = $"key_{(desktop ? "d" : "g")}_{src}",
            Kind = CgpRowKind.KeyMap,
            KeySource = src,
            KeyBadge = badge,
            DesktopKey = desktop,
            Title = title,
            Nav = true,
        };

        if (GpIsSpecial(src))
        {
            bool stock = _gpSettings?["buttonMappings"]?[src] == null;
            row.Stock = stock;
            row.Changed = !stock;
            row.Disabled = _gpSettings == null;
            row.Why = _gpError ?? "正在读取…";
            if (_gpSettings == null) row.Value = "—";
            else if (stock) row.Value = $"原功能 · {(src == "QS" ? "Xbox Game Bar" : "Claw 游戏库")}";
            else CgpFillCmdDisplay(row, GpSpecialCommand(src, false));
            row.Press = () => CgpOpenModal(src, desktop: false, directChooser: false);
            row.Secondary = () => CgpOpenModal(src, desktop: false, directChooser: false);
            return row;
        }

        if (desktop)
        {
            if (_gpFw == null || _gpFw["desktopKeys"] == null)
            {
                row.Stock = true;
                row.Value = "—";
                row.Disabled = true;
                row.Why = _gpFwError ?? "正在读取…";
            }
            else if (GpDesktopStock(src))
            {
                row.Stock = true;
                row.Value = GpNativeName(src);   // 11-27 jinyu：原键只写键名，不加「原键 ·」
            }
            else
            {
                row.Stock = false;
                row.Changed = true;
                CgpFillCodesDisplay(row, GpDesktopCodes(src)!);
            }
            row.Press = () => CgpOpenModal(src, desktop: true, directChooser: true);
            row.Secondary = () => CgpOpenModal(src, desktop: true, directChooser: false);
            return row;
        }

        var acts = GpActs(src);
        if (acts != null)
        {
            row.Stock = false;
            row.Changed = true;
            if (acts.Count == 0) row.Value = "禁用";
            else if (acts.Count == 1)
            {
                int t = Math.Max(0, Array.IndexOf(GpActIds, Str(acts[0]?["trigger"]) ?? "regular"));
                if (t != 0) row.TriggerTag = GpActTypes[t];
                CgpFillCmdDisplay(row, acts[0]?["command"]);
            }
            else
            {
                row.TriggerTag = $"{acts.Count} 组触发";
                CgpFillCmdDisplay(row, acts[0]?["command"]);
            }
            bool multi = acts.Count != 1 || (Str(acts[0]?["trigger"]) ?? "regular") != "regular";
            row.Press = () => CgpOpenModal(src, desktop: false, directChooser: !multi);
            row.Secondary = () => CgpOpenModal(src, desktop: false, directChooser: false);
            return row;
        }

        var codes = GpFirmwareCodes(src);
        bool isNative = codes.Length == 0 || codes.Length == 1 && codes[0] == GpNativeCode(src);
        if (codes.Length == 0 && _gpFw == null && GpFirmwareSource(src))
        {
            row.Stock = true;
            row.Value = "—";
        }
        else if (isNative)
        {
            row.Stock = true;
            row.Value = src is "M1" or "M2" ? "未映射" : GpNativeName(src);
        }
        else
        {
            row.Stock = false;
            row.Changed = true;
            CgpFillCodesDisplay(row, codes);
        }
        row.Press = () => CgpOpenModal(src, desktop: false, directChooser: true);
        row.Secondary = () => CgpOpenModal(src, desktop: false, directChooser: false);
        return row;
    }

    private static void CgpFillCodesDisplay(CgpRow row, int[] codes)
    {
        if (codes.Length == 1 && codes[0] == 0xFF) { row.Value = "禁用"; return; }
        var caps = new List<string>();
        foreach (int code in codes)
        {
            var cell = GpAllCells.FirstOrDefault(c => c.Code == code);
            if (cell.Cmd is { Length: > 0 })
            {
                if (cell.Cmd.StartsWith("sys:")) { row.Value = cell.Label; return; }
                if (cell.Cmd.StartsWith("mouse:") || cell.Cmd.StartsWith("wheel:")) { caps.Add(GpCellName(cell)); }
                else caps.Add(cell.Label);
            }
            else caps.Add(GpCodeName(code));
        }
        if (caps.Count > 0) row.Caps = caps;
        else row.Value = GpCodesText(codes);
    }

    private static void CgpFillCmdDisplay(CgpRow row, JsonNode? cmd)
    {
        string type = Str(cmd?["type"]) ?? "none";
        if (type == "keyboard")
        {
            var keys = (cmd?["keys"] as JsonArray ?? new JsonArray())
                .Select(k => Str(k) ?? "")
                .Where(k => k.Length > 0)
                .Select(k => GpAllCells.FirstOrDefault(c => c.Cmd == "key:" + k) is { Label: { Length: > 0 } l } ? l : k)
                .ToList();
            if (keys.Count > 0) { row.Caps = keys; return; }
        }
        if (type == "gamepad")
        {
            string btn = Str(cmd?["button"]) ?? "";
            var cell = GpAllCells.FirstOrDefault(c => c.Cmd == "pad:" + btn);
            if (cell.Label is { Length: > 0 }) { row.Caps = new List<string> { GpCellName(cell) }; return; }
        }
        row.Value = GpCommandText(cmd);
    }

    // ---------------- 主页面左栏与动态硬件视窗绘制 ----------------

    private void CgpPaintHead(CgpView v)
    {
        const string title = "手柄";
        if (v.HeadKey == title) return;
        v.HeadKey = title;
        float x0 = 20, y0 = 20, w = 800, h = 120;
        using var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.HeadS, v.Head, x0, y0, w, h));
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(40 - 134 - x0, 36 - 104 - y0) * Matrix3x2.CreateScale(CsRaster);
        RsPaintPills(ds, noY: true);
        ds.Transform = Matrix3x2.CreateTranslation(-x0, -y0) * Matrix3x2.CreateScale(CsRaster);
        using var f = new CanvasTextFormat { FontFamily = RmFont(title), FontSize = 48, FontWeight = RsWt(title), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
        ds.DrawText(title, new Rect(198, 36, 560, 74), RmInk, f);
    }

    private void CgpPaintLeft(CgpView v)
    {
        bool connected = _pad?.Connected ?? false;
        string modeText = _gpFw == null ? "读取中" : !GpWantDesktop ? "手柄模式" : _gpDesktopHeld ? "桌面模式 · 待生效" : "桌面模式";
        string key = $"{connected}|{modeText}|{CsRaster}";
        if (v.LeftKey == key) return;
        v.LeftKey = key;

        float x0 = 0, y0 = CpTop - 10, w = CpLeftX + CpLeftW + 30, h = CsH - y0;
        using var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.LeftS, v.Left, x0, y0, w, h));
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(-x0, -y0) * Matrix3x2.CreateScale(CsRaster);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;

        for (int i = 0; i < CgpCats.Length; i++)
        {
            float y = CpTop + i * CgpSlot, cy = y + CgpSlot / 2;
            CgpDrawCatIcon(ds, CgpCats[i].Icon, CpLeftX + 66, cy, RmInk);
            string name = CgpCats[i].Name;
            using var f = new CanvasTextFormat { FontFamily = RmFont(name), FontSize = 38, FontWeight = RsWt(name), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(name, new Rect(CpLeftX + 118, y, CpLeftW - 140, CgpSlot), RmInk, f);
        }

        // 左下动态视窗外框 Card3D + 底部双状态胶囊
        CpCard(ds, CpLeftX, CgpCardTop, CpLeftW, CgpCardH, null, 30);
        float chipY = CgpCardTop + CgpCardH - 28 - 62, gap = 14;
        float w1 = (CgpWellW - gap) * 0.46f, w2 = CgpWellW - gap - w1;
        CgpDrawStatusChip(ds, CgpWellX, chipY, w1, 62, "状态", connected ? "已连接" : "未连接", connected ? RmInk : RsSub);
        CgpDrawStatusChip(ds, CgpWellX + w1 + gap, chipY, w2, 62, "模式", modeText, CpAccentInk);
    }

    private static void CgpDrawStatusChip(CanvasDrawingSession ds, float x, float y, float w, float h, string label, string value, Color valCol)
    {
        ds.FillRoundedRectangle(x, y, w, h, h / 2, h / 2, CpTile);
        using (var lf = new CanvasTextFormat { FontFamily = RmFont(label), FontSize = 24, FontWeight = RsWt(label, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(label, new Rect(x + 24, y, w * 0.45f, h), RsSub, lf);
        float lw = CpTextW(ds, label, 24);
        using var vf = new CanvasTextFormat { FontFamily = RmFont(value), FontSize = 25, FontWeight = RsWt(value), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
        ds.DrawText(value, new Rect(x + 24 + lw + 12, y, Math.Max(40, w - 48 - lw - 12), h), valCol, vf);
    }

    private void CgpPaintSel(CgpView v)
    {
        int sel = Math.Clamp(_cgpCat, 0, CgpCats.Length - 1);
        float pad = 30, w = CpLeftW + pad * 2, h = CgpSlot + pad * 2, R = CsRaster;
        string key = $"{R}";
        if (key != v.SelKey)
        {
            v.SelKey = key;
            v.Sel.Children.RemoveAll();
            foreach (var s in v.CatSurfs) s.Dispose();
            v.CatSurfs.Clear();
            v.CatItems.Clear();
            int pw = (int)Math.Ceiling(w * R), ph = (int)Math.Ceiling(h * R);
            for (int i = 0; i < CgpCats.Length; i++)
            {
                var surf = G.Surface(pw, ph);
                var sp = G.Sprite(G.C.CreateSurfaceBrush(surf), relative: false);
                sp.Size = new Vector2(w, h);
                sp.Offset = new Vector3(CpLeftX - pad, CpTop + i * CgpSlot - pad, 0);
                sp.Opacity = 0;
                using (var ds = CanvasComposition.CreateDrawingSession(surf))
                {
                    ds.Clear(Colors.Transparent);
                    ds.Transform = Matrix3x2.CreateScale(R);
                    ds.Antialiasing = CanvasAntialiasing.Antialiased;
                    CpCard(ds, pad, pad + 4, CpLeftW, CgpSlot - 8, null, 30);
                    float cy = pad + CgpSlot / 2;
                    CgpDrawCatIcon(ds, CgpCats[i].Icon, pad + 66, cy, CpAccentInk);
                    string name = CgpCats[i].Name;
                    using var f = new CanvasTextFormat { FontFamily = RmFont(name), FontSize = 38, FontWeight = RsWt(name), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
                    ds.DrawText(name, new Rect(pad + 118, pad, CpLeftW - 140, CgpSlot), CpAccentInk, f);
                }
                v.Sel.Children.InsertAtTop(sp);
                v.CatItems.Add(sp);
                v.CatSurfs.Add(surf);
            }
            v.SelOn = -1;
        }

        bool snap = !v.Sel.IsVisible || v.SelOn < 0 || Reduced;
        v.Sel.IsVisible = true;
        if (sel == v.SelOn) return;
        for (int i = 0; i < v.CatItems.Count; i++)
        {
            float to = i == sel ? 1 : 0;
            var it = v.CatItems[i];
            if (snap) { it.StopAnimation("Opacity"); it.Opacity = to; }
            else if (i == sel || i == v.SelOn || it.Opacity > 0.001f) it.StartAnimation("Opacity", CsS(to, 150, Bezier.EaseOut));
        }
        v.SelOn = sel;
    }

    private static void CgpDrawCatIcon(CanvasDrawingSession ds, string kind, float cx, float cy, Color ink)
    {
        var paper = Color.FromArgb(255, 0xFA, 0xFA, 0xFA);
        switch (kind)
        {
            case "pad":
                ds.FillRoundedRectangle(cx - 21, cy - 13, 42, 26, 13, 13, ink);
                ds.FillRoundedRectangle(cx - 13, cy - 2, 11, 4, 2, 2, paper);
                ds.FillRoundedRectangle(cx - 9.5f, cy - 5.5f, 4, 11, 2, 2, paper);
                ds.FillCircle(cx + 8, cy - 3, 3.2f, paper);
                ds.FillCircle(cx + 13, cy + 2, 3.2f, paper);
                break;
            case "stick":
                ds.DrawCircle(cx, cy, 17, ink, 4.8f);
                ds.FillCircle(cx, cy, 8.5f, ink);
                break;
            case "trigger":
            {
                using var pb = new CanvasPathBuilder(ds.Device);
                pb.BeginFigure(cx - 12, cy - 14);
                pb.AddCubicBezier(new Vector2(cx - 6, cy - 18), new Vector2(cx + 6, cy - 18), new Vector2(cx + 12, cy - 14));
                pb.AddLine(cx + 15, cy + 12);
                pb.AddCubicBezier(new Vector2(cx + 15, cy + 15), new Vector2(cx + 12, cy + 17), new Vector2(cx + 9, cy + 17));
                pb.AddLine(cx - 9, cy + 17);
                pb.AddCubicBezier(new Vector2(cx - 12, cy + 17), new Vector2(cx - 15, cy + 15), new Vector2(cx - 15, cy + 12));
                pb.EndFigure(CanvasFigureLoop.Closed);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(g, ink);
                ds.FillRoundedRectangle(cx - 8, cy - 8, 16, 5, 2.5f, 2.5f, paper);
                break;
            }
            case "rumble":
            {
                ds.FillRoundedRectangle(cx - 10, cy - 14, 20, 28, 7, 7, ink);
                using var pb = new CanvasPathBuilder(ds.Device);
                pb.BeginFigure(cx - 17, cy - 8);
                pb.AddCubicBezier(new Vector2(cx - 20, cy - 3), new Vector2(cx - 20, cy + 3), new Vector2(cx - 17, cy + 8));
                pb.EndFigure(CanvasFigureLoop.Open);
                pb.BeginFigure(cx + 17, cy - 8);
                pb.AddCubicBezier(new Vector2(cx + 20, cy - 3), new Vector2(cx + 20, cy + 3), new Vector2(cx + 17, cy + 8));
                pb.EndFigure(CanvasFigureLoop.Open);
                using var waves = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(waves, ink, 4.2f, RwRoundStroke);
                break;
            }
            default: // mouse
                ds.FillRoundedRectangle(cx - 12, cy - 17, 24, 34, 12, 12, ink);
                ds.DrawLine(cx, cy - 14, cx, cy - 3, paper, 3.5f, RwRoundStroke);
                ds.DrawLine(cx - 10, cy - 3, cx + 10, cy - 3, paper, 3f);
                break;
        }
    }

    private void CgpPaintPanelInner(CgpView v)
    {
        var snap = _gpShown;
        int lDz = Int(_gpFw?["sticks"]?["left"]?["deadzone"], 10), lOut = Int(_gpFw?["sticks"]?["left"]?["outer"], 95);
        int rDz = Int(_gpFw?["sticks"]?["right"]?["deadzone"], 10), rOut = Int(_gpFw?["sticks"]?["right"]?["outer"], 100);
        int ltS = Int(_gpFw?["triggers"]?["left"]?["deadzone"], 5), ltE = Int(_gpFw?["triggers"]?["left"]?["outer"], 100);
        int rtS = Int(_gpFw?["triggers"]?["right"]?["deadzone"], 5), rtE = Int(_gpFw?["triggers"]?["right"]?["outer"], 100);
        int rumL = _gpRumbleL ?? 80, rumR = _gpRumbleR ?? 80;

        string key = $"{_cgpCat}|{_cgpActiveKey}|{_cgpRumblePulse}|{_gpArtLoaded}|{lDz},{lOut},{rDz},{rOut}|{ltS},{ltE},{rtS},{rtE}|{rumL},{rumR}|{snap.Buttons},{snap.LT},{snap.RT},{snap.LX >> 8},{snap.LY >> 8},{snap.RX >> 8},{snap.RY >> 8}|{CsRaster}";
        if (v.PanelLiveKey == key) return;
        v.PanelLiveKey = key;

        using var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.PanelLiveS, v.PanelLive, 0, 0, CgpWellW, CgpWellH));
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateScale(CsRaster);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;

        using var boxGeo = CanvasGeometry.CreateRoundedRectangle(ds.Device, 0, 0, CgpWellW, CgpWellH, 26, 26);
        ds.FillGeometry(boxGeo, CpTile);
        using var clip = ds.CreateLayer(1, boxGeo);

        if (_cgpCat == 1)
        {
            // 摇杆：左右双同心死区圆盘 + 实时遥测落点
            CgpDrawStickWell(ds, CgpWellW * 0.26f, 134, lDz, lOut, snap.LX / 32767f, -snap.LY / 32767f, (snap.Buttons & 0x0040) != 0, $"左摇杆 · 死区 {lDz}%", RmInk);
            CgpDrawStickWell(ds, CgpWellW * 0.74f, 134, rDz, rOut, snap.RX / 32767f, -snap.RY / 32767f, (snap.Buttons & 0x0080) != 0, $"右摇杆 · 外圈 {rOut}%", RsSub);
        }
        else if (_cgpCat == 2)
        {
            // 扳机：双 270° 圆环仪表 + 实时按压游标
            CgpDrawTriggerRing(ds, CgpWellW * 0.26f, 134, ltS, ltE, snap.LT / 255f, "LT 行程范围");
            CgpDrawTriggerRing(ds, CgpWellW * 0.74f, 134, rtS, rtE, snap.RT / 255f, "RT 行程范围");
        }
        else
        {
            // 按键映射 / 震动 / 桌面模式：Claw 8 EX 陶瓷正反面线稿
            bool isBack = (_cgpActiveKey is "M1" or "M2") && (_cgpCat is 0 or 4);
            // 11-27 jinyu：背面视图不挂「背面视图 · 背键高亮」标签
            string? tag = isBack || _cgpCat == 3 ? null : $"聚焦：{CgpShortKeyLabel(_cgpActiveKey)}";   // 11-27 jinyu：震动页不要「握把马达预览」
            if (tag != null)
            {
                float tw = CpTextW(ds, tag, 20) + 28;
                float tx = CgpWellW - 16 - tw, ty = 14;
                ds.FillRoundedRectangle(tx, ty, tw, 36, 18, 18, Color.FromArgb(255, 0xE4, 0xE5, 0xEC));
                using var tf = new CanvasTextFormat { FontFamily = RmFont(tag), FontSize = 20, FontWeight = RsWt(tag, 500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
                ds.DrawText(tag, new Rect(tx, ty, tw, 36), RsSub, tf);
            }

            string hiKey = _cgpCat == 3 ? "" : _cgpActiveKey;
            CgpDrawClawWireframe(ds, 22, 58, CgpWellW - 44, 240, hiKey, isBack, false, rumL, rumR, false, snap);   // 震动页只画手柄线稿，不画马达预览
        }
    }

    private static string CgpShortKeyLabel(string key) => key switch
    {
        "Up" => "十字键 ↑", "Down" => "十字键 ↓", "Left" => "十字键 ←", "Right" => "十字键 →",
        "LS" => "左摇杆键", "RS" => "右摇杆键", "Back" => "View 键", "Start" => "Menu 键",
        "QS" => "QS 键", "Claw" => "Claw 键", "" => "手柄",
        _ => $"{key} 键",
    };

    private static void CgpDrawStickWell(CanvasDrawingSession ds, float cx, float cy, int dz, int outer, float lx, float ly, bool pressed, string caption, Color capCol)
    {
        const float r = 74f;
        ds.FillCircle(cx, cy, r, Colors.White);
        ds.DrawCircle(cx, cy, r, Color.FromArgb(255, 0xD5, 0xD6, 0xDF), 3f);

        float outR = Math.Clamp(r * outer / 100f, 16f, r);
        using (var dash = new CanvasStrokeStyle { CustomDashStyle = new[] { 2.4f, 2f }, StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round })
            ds.DrawCircle(cx, cy, outR, RwC2, 3f, dash);

        float dzR = Math.Max(6f, r * dz / 100f);
        ds.FillCircle(cx, cy, dzR, Color.FromArgb(72, 0x80, 0x37, 0xFF));
        ds.DrawCircle(cx, cy, dzR, CpAccentInk, 2f);

        float mag = MathF.Sqrt(lx * lx + ly * ly);
        if (mag > 1f) { lx /= mag; ly /= mag; }
        float px = cx + lx * (r - 8f), py = cy + ly * (r - 8f);
        ds.FillCircle(px, py, pressed ? 11f : 9f, mag > 0.06f || pressed ? RwC2 : RwC1);

        using var cf = new CanvasTextFormat { FontFamily = RmFont(caption), FontSize = 24, FontWeight = RsWt(caption, 500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
        ds.DrawText(caption, new Rect(cx - 120, cy + 88, 240, 38), capCol, cf);
    }

    private void CgpDrawTriggerRing(CanvasDrawingSession ds, float cx, float cy, int startPct, int endPct, float live, string caption)
    {
        const float r = 74f, sw = 20f, startDeg = 135f, sweep = 270f;
        static float Rad(float d) => d * MathF.PI / 180f;
        CanvasGeometry ArcRange(float a0, float span)
        {
            using var pb = new CanvasPathBuilder(ds.Device);
            pb.BeginFigure(cx + r * MathF.Cos(Rad(a0)), cy + r * MathF.Sin(Rad(a0)));
            pb.AddArc(new Vector2(cx, cy), r, r, Rad(a0), Rad(span));
            pb.EndFigure(CanvasFigureLoop.Open);
            return CanvasGeometry.CreatePath(pb);
        }

        using (var track = ArcRange(startDeg, sweep))
            ds.DrawGeometry(track, CpTrack, sw, RwRoundStroke);

        float f0 = Math.Clamp(startPct / 100f, 0f, 1f);
        float f1 = Math.Clamp(endPct / 100f, f0, 1f);
        if (f1 - f0 > 0.01f)
        {
            using var arc = ArcRange(startDeg + sweep * f0, sweep * (f1 - f0));
            using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(cx - r, cy + r), EndPoint = new Vector2(cx + r, cy - r) };
            ds.DrawGeometry(arc, br, sw, RwRoundStroke);
        }

        if (live > 0.02f)
        {
            float ang = Rad(startDeg + sweep * Math.Clamp(live, 0f, 1f));
            float mx = cx + r * MathF.Cos(ang), my = cy + r * MathF.Sin(ang);
            ds.FillCircle(mx, my, 12f, Colors.White);
            ds.DrawCircle(mx, my, 10f, CpAccentInk, 4f);
        }

        string val = $"{startPct}–{endPct}";
        using (var vf = new CanvasTextFormat { FontFamily = RmFont(val), FontSize = 38, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(val, new Rect(cx - 90, cy - 34, 180, 52), RmInk, vf);
        using (var uf = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 20, FontWeight = new Windows.UI.Text.FontWeight(600), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center })
            ds.DrawText("%", new Rect(cx - 50, cy + 14, 100, 30), RsSub, uf);
        using (var cf = new CanvasTextFormat { FontFamily = RmFont(caption), FontSize = 25, FontWeight = RsWt(caption, 500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(caption, new Rect(cx - 115, cy + 88, 230, 38), RmInk, cf);
    }

    /// <summary>优先绘制软件内置的 Claw 8 EX 原生正反面高清线稿（Assets/Pad/claw8ex-front-hd.png 与 claw8ex-back.png）并叠加精确矢量热区高亮；首帧尚未载入完成时平滑回退矢量轮廓。</summary>
    private void CgpDrawClawWireframe(CanvasDrawingSession ds, float x, float y, float w, float h, string activeKey, bool backFace, bool rumbleView, int rumbleL, int rumbleR, bool pulse, Input.GamepadPoller.Snapshot snap, string stickArrow = "")
    {
        bool Live(ushort mask) => (snap.Buttons & mask) != 0;
        bool Hi(string k) => activeKey == k || k switch
        {
            "A" => Live(0x1000), "B" => Live(0x2000), "X" => Live(0x4000), "Y" => Live(0x8000),
            "LB" => Live(0x0100), "RB" => Live(0x0200), "LT" => snap.LT > 25, "RT" => snap.RT > 25,
            "LS" => Live(0x0040), "RS" => Live(0x0080), "Back" => Live(0x0020), "Start" => Live(0x0010),
            "Up" => Live(0x0001), "Down" => Live(0x0002), "Left" => Live(0x0004), "Right" => Live(0x0008),
            _ => false,
        };

        string faceKey = backFace ? "back" : "front";
        if (_gpArtLoaded && _gpBitmaps.TryGetValue(faceKey, out var bmp))
        {
            float bw = (float)bmp.Size.Width, bh = (float)bmp.Size.Height;
            float s = MathF.Min(w / bw, h / bh);
            float ox = x + (w - bw * s) / 2f, oy = y + (h - bh * s) / 2f;
            ds.DrawImage(bmp, new Rect(ox, oy, bw * s, bh * s), bmp.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic);

            // _gpRegions 中存储的是经 GpMap 映射到 1280×800 舞台的多边形，逆变换回当前视窗目标矩形
            Matrix3x2 regXf = backFace
                ? Matrix3x2.CreateTranslation(-(GpX - 5f * GpS), -(GpY - 10f * GpS)) * Matrix3x2.CreateScale(s / (1.3f * GpS)) * Matrix3x2.CreateTranslation(ox, oy)
                : Matrix3x2.CreateTranslation(-GpX, -GpY) * Matrix3x2.CreateScale(s / GpS) * Matrix3x2.CreateTranslation(ox, oy);

            void FillRegion(string regName, Color fillCol, float strokeW = 0f)
            {
                if (!_gpRegions.TryGetValue(regName, out var reg)) return;
                using var tr = reg.Transform(regXf);
                ds.FillGeometry(tr, fillCol);
                if (strokeW > 0f) ds.DrawGeometry(tr, CpAccentInk, strokeW, RwRoundStroke);
            }

            if (backFace)
            {
                if (Hi("M1")) FillRegion("M1", Color.FromArgb(190, 0x80, 0x37, 0xFF), 2.5f);
                if (Hi("M2")) FillRegion("M2", Color.FromArgb(190, 0x80, 0x37, 0xFF), 2.5f);
                return;
            }

            if (rumbleView)
            {
                foreach (var (regName, strength) in new[] { ("GripL", rumbleL), ("GripR", rumbleR) })
                {
                    if (strength <= 0) continue;
                    byte a = (byte)Math.Clamp((int)((0.12f + strength * 0.0042f) * 255), 0, 195);
                    FillRegion(regName, Color.FromArgb(a, 0x80, 0x37, 0xFF), pulse ? 3.2f : 0f);
                }
            }

            if (Hi("LB") || Hi("LT")) FillRegion("LB", Color.FromArgb(190, 0x80, 0x37, 0xFF), 2.4f);
            if (Hi("RB") || Hi("RT")) FillRegion("RB", Color.FromArgb(190, 0x80, 0x37, 0xFF), 2.4f);

            foreach (string k in new[] { "A", "B", "X", "Y", "Up", "Down", "Left", "Right", "Back", "QS", "Start", "Claw" })
            {
                if (!Hi(k) || !GpKeys.TryGetValue(k, out var gk) || gk.Face != "front") continue;
                float kw = gk.W * s + 4f, kh = gk.H * s + 4f, kr = gk.R * s + 2f;
                float kx = ox + gk.X * s - kw / 2f, ky = oy + gk.Y * s - kh / 2f;
                ds.FillRoundedRectangle(kx, ky, kw, kh, kr, kr, Color.FromArgb(185, 0x80, 0x37, 0xFF));
                ds.DrawRoundedRectangle(kx, ky, kw, kh, kr, kr, CpAccentInk, 2.2f);
            }

            foreach (var (sk, prefix) in new[] { ("LS", "LStick"), ("RS", "RStick") })
            {
                bool sOn = Hi(sk) || activeKey == prefix || stickArrow.StartsWith(prefix);
                if (!sOn || !GpKeys.TryGetValue(sk, out var gk)) continue;
                float cx = ox + gk.X * s, cy = oy + gk.Y * s, sr = gk.W * 0.52f * s;
                ds.FillCircle(cx, cy, sr, Color.FromArgb(120, 0x80, 0x37, 0xFF));
                ds.DrawCircle(cx, cy, sr, CpAccentInk, 2.8f);
                if (stickArrow.StartsWith(prefix) && stickArrow.Length > prefix.Length)
                {
                    var (dx, dy) = stickArrow[prefix.Length..] switch { "Up" => (0f, -1f), "Down" => (0f, 1f), "Left" => (-1f, 0f), _ => (1f, 0f) };
                    var tip = new Vector2(cx + dx * (sr + 10f), cy + dy * (sr + 10f));
                    using var pb = new CanvasPathBuilder(ds.Device);
                    pb.BeginFigure(tip);
                    pb.AddLine(new Vector2(cx + dx * (sr + 2f) - dy * 7f, cy + dy * (sr + 2f) + dx * 7f));
                    pb.AddLine(new Vector2(cx + dx * (sr + 2f) + dy * 7f, cy + dy * (sr + 2f) - dx * 7f));
                    pb.EndFigure(CanvasFigureLoop.Closed);
                    using var tri = CanvasGeometry.CreatePath(pb);
                    ds.FillGeometry(tri, CpAccentInk);
                }
            }
            return;
        }

        float vbW = 460f, vbH = backFace ? 210f : 215f;
        float vs = MathF.Min(w / vbW, h / vbH);
        float vox = x + (w - vbW * vs) / 2f, voy = y + (h - vbH * vs) / 2f;
        var prev = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(vs) * Matrix3x2.CreateTranslation(vox, voy) * prev;

        var stroke = Color.FromArgb(255, 0x5F, 0x61, 0x68);
        var neutralBtn = Color.FromArgb(255, 0xF0, 0xF1, 0xF5);
        var shoulderNeutral = Color.FromArgb(255, 0xE4, 0xE5, 0xEC);
        using var padGrad = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(460, 215) };

        if (backFace)
        {
            ds.FillRoundedRectangle(28, 18, 404, 174, 54, 54, Colors.White);
            ds.DrawRoundedRectangle(28, 18, 404, 174, 54, 54, stroke, 3.5f);

            using (var pb = new CanvasPathBuilder(ds.Device))
            {
                pb.BeginFigure(28, 115);
                pb.AddQuadraticBezier(new Vector2(26, 188), new Vector2(76, 192));
                pb.AddLine(112, 168); pb.AddLine(112, 145);
                pb.EndFigure(CanvasFigureLoop.Open);
                pb.BeginFigure(432, 115);
                pb.AddQuadraticBezier(new Vector2(434, 188), new Vector2(384, 192));
                pb.AddLine(348, 168); pb.AddLine(348, 145);
                pb.EndFigure(CanvasFigureLoop.Open);
                using var grips = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(grips, stroke, 3f, RwRoundStroke);
            }

            var vent = Color.FromArgb(255, 0xDF, 0xE1, 0xE8);
            foreach (float vy in new[] { 52f, 70f, 88f })
            {
                ds.FillRoundedRectangle(130, vy, 66, 10, 5, 5, vent);
                ds.FillRoundedRectangle(264, vy, 66, 10, 5, 5, vent);
            }
            ds.FillCircle(230, 96, 22, Color.FromArgb(255, 0xEC, 0xED, 0xF2));
            ds.DrawCircle(230, 96, 22, Color.FromArgb(255, 0xC4, 0xC5, 0xCC), 2f);

            foreach (var (mk, mx) in new[] { ("M1", 86f), ("M2", 346f) })
            {
                bool on = Hi(mk);
                if (on) ds.FillRoundedRectangle(mx, 82, 28, 48, 10, 10, padGrad);
                else ds.FillRoundedRectangle(mx, 82, 28, 48, 10, 10, shoulderNeutral);
                ds.DrawRoundedRectangle(mx, 82, 28, 48, 10, 10, on ? CpAccentInk : stroke, 3f);
                using var mf = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 13, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
                ds.DrawText(mk, new Rect(mx - 4, 82, 36, 48), on ? Colors.White : RmInk, mf);
            }

            ds.Transform = prev;
            return;
        }

        // 左肩键 / 左扳机
        using (var pb = new CanvasPathBuilder(ds.Device))
        {
            pb.BeginFigure(46, 24);
            pb.AddQuadraticBezier(new Vector2(74, 10), new Vector2(112, 18));
            pb.AddLine(112, 30); pb.AddLine(42, 32);
            pb.EndFigure(CanvasFigureLoop.Closed);
            using var lbGeo = CanvasGeometry.CreatePath(pb);
            bool lOn = Hi("LB") || Hi("LT");
            if (lOn) ds.FillGeometry(lbGeo, padGrad); else ds.FillGeometry(lbGeo, shoulderNeutral);
            ds.DrawGeometry(lbGeo, lOn ? CpAccentInk : stroke, 2.8f, RwRoundStroke);
        }
        // 右肩键 / 右扳机
        using (var pb = new CanvasPathBuilder(ds.Device))
        {
            pb.BeginFigure(414, 24);
            pb.AddQuadraticBezier(new Vector2(386, 10), new Vector2(348, 18));
            pb.AddLine(348, 30); pb.AddLine(418, 32);
            pb.EndFigure(CanvasFigureLoop.Closed);
            using var rbGeo = CanvasGeometry.CreatePath(pb);
            bool rOn = Hi("RB") || Hi("RT");
            if (rOn) ds.FillGeometry(rbGeo, padGrad); else ds.FillGeometry(rbGeo, shoulderNeutral);
            ds.DrawGeometry(rbGeo, rOn ? CpAccentInk : stroke, 2.8f, RwRoundStroke);
        }

        // 掌机主体外壳
        ds.FillRoundedRectangle(24, 24, 412, 168, 52, 52, Colors.White);
        ds.DrawRoundedRectangle(24, 24, 412, 168, 52, 52, stroke, 3.2f);

        // 震动握把高光与扩散环
        if (rumbleView)
        {
            foreach (var (gx, strength) in new[] { (66f, rumbleL), (394f, rumbleR) })
            {
                float gr = 24f + strength * 0.14f;
                byte a = (byte)Math.Clamp((int)((0.12f + strength * 0.003f) * 255), 0, 180);
                ds.FillCircle(gx, 146, gr, Color.FromArgb(a, 0x80, 0x37, 0xFF));
                if (pulse && strength > 0)
                    ds.DrawCircle(gx, 146, gr + 9f, Color.FromArgb(180, 0x7A, 0x4C, 0xFF), 2.8f);
            }
        }

        // 中央 16:10 屏幕
        ds.FillRoundedRectangle(122, 36, 216, 138, 12, 12, Color.FromArgb(255, 0xF2, 0xF3, 0xF8));
        ds.DrawRoundedRectangle(122, 36, 216, 138, 12, 12, Color.FromArgb(255, 0xC4, 0xC5, 0xCC), 2f);

        // 左摇杆
        bool lsOn = Hi("LStick") || Hi("LS") || stickArrow.StartsWith("LStick");
        if (lsOn) ds.FillCircle(68, 76, 22, padGrad); else ds.FillCircle(68, 76, 22, neutralBtn);
        ds.DrawCircle(68, 76, 22, lsOn ? CpAccentInk : stroke, 3f);
        ds.FillCircle(68, 76, 13, lsOn ? Color.FromArgb(230, 0xFA, 0xFA, 0xFA) : Color.FromArgb(64, 0x5F, 0x61, 0x68));

        // 十字键（支持单臂高亮）
        ds.FillRoundedRectangle(92 - 18, 124 - 6, 36, 12, 3, 3, Color.FromArgb(255, 0xE6, 0xE7, 0xEE));
        ds.FillRoundedRectangle(92 - 6, 124 - 18, 12, 36, 3, 3, Color.FromArgb(255, 0xE6, 0xE7, 0xEE));
        ds.DrawRoundedRectangle(92 - 18, 124 - 6, 36, 12, 3, 3, stroke, 2f);
        ds.DrawRoundedRectangle(92 - 6, 124 - 18, 12, 36, 3, 3, stroke, 2f);
        ds.FillRectangle(92 - 5, 124 - 5, 10, 10, Color.FromArgb(255, 0xE6, 0xE7, 0xEE));
        if (Hi("Up")) ds.FillRoundedRectangle(92 - 5.5f, 124 - 17.5f, 11, 13, 2.5f, 2.5f, RwC2);
        if (Hi("Down")) ds.FillRoundedRectangle(92 - 5.5f, 124 + 4.5f, 11, 13, 2.5f, 2.5f, RwC2);
        if (Hi("Left")) ds.FillRoundedRectangle(92 - 17.5f, 124 - 5.5f, 13, 11, 2.5f, 2.5f, RwC2);
        if (Hi("Right")) ds.FillRoundedRectangle(92 + 4.5f, 124 - 5.5f, 13, 11, 2.5f, 2.5f, RwC2);

        // 功能小键（View / QS / Menu / Claw）
        var smallOff = Color.FromArgb(255, 0xD8, 0xD9, 0xE2);
        ds.FillRoundedRectangle(104, 48, 9, 15, 4.5f, 4.5f, Hi("Back") ? RwC2 : smallOff);
        ds.FillRoundedRectangle(104, 69, 9, 15, 4.5f, 4.5f, Hi("QS") ? RwC2 : smallOff);
        ds.FillRoundedRectangle(347, 48, 9, 15, 4.5f, 4.5f, Hi("Start") ? RwC2 : smallOff);
        ds.FillRoundedRectangle(347, 69, 9, 15, 4.5f, 4.5f, Hi("Claw") ? RwC2 : smallOff);

        // ABXY
        foreach (var (bk, bx, by) in new[] { ("Y", 392f, 54f), ("X", 374f, 72f), ("B", 410f, 72f), ("A", 392f, 90f) })
        {
            bool bOn = Hi(bk);
            if (bOn) ds.FillCircle(bx, by, 9.5f, padGrad); else ds.FillCircle(bx, by, 9.5f, neutralBtn);
            ds.DrawCircle(bx, by, 9.5f, bOn ? CpAccentInk : stroke, 2.4f);
        }

        // 右摇杆
        bool rsOn = Hi("RStick") || Hi("RS") || stickArrow.StartsWith("RStick");
        if (rsOn) ds.FillCircle(370, 126, 22, padGrad); else ds.FillCircle(370, 126, 22, neutralBtn);
        ds.DrawCircle(370, 126, 22, rsOn ? CpAccentInk : stroke, 3f);
        ds.FillCircle(370, 126, 13, rsOn ? Color.FromArgb(230, 0xFA, 0xFA, 0xFA) : Color.FromArgb(64, 0x5F, 0x61, 0x68));

        ds.Transform = prev;
    }

    // ---------------- 主页面右栏卡片与行绘制 ----------------

    private static float CgpRowBaseH(CgpRow r) => r.Kind switch
    {
        CgpRowKind.Slider => CpRowSliderH, // 160
        CgpRowKind.Toggle when r.Sub != null => 136f,
        CgpRowKind.Dropdown when r.Sub != null => CpRowSubH, // 140
        _ => CpRowH, // 112
    };

    private static (float X, float Y, float W, float H) CgpOptRect(float listW, float baseH, float rowY, int k) =>
        (listW - 34f - CpPickW, rowY + baseH / 2f + 40f + 14f + k * CpOptH, CpPickW, CpOptH);

    private static List<(int I, float Y, float H)> CgpLayoutRows(List<CgpRow> rows, int openDrop, float listW, out List<(float Y0, float Y1, List<float> Seps)> groups)
    {
        var lay = new List<(int, float, float)>();
        groups = new();
        float y = 0;
        bool open = false;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Break && open) { y += CpGap; open = false; }
            float h = CgpRowBaseH(r);
            if (i == openDrop && r.Options is { Length: > 0 } o) h += o.Length * CpOptH + 34f;
            if (!open) { groups.Add((y, y + h, new List<float>())); open = true; }
            else { var g = groups[^1]; g.Seps.Add(y - CpSep); groups[^1] = (g.Y0, y + h, g.Seps); }
            lay.Add((i, y, h));
            y += h + CpSep;
        }
        return lay;
    }

    private string CgpComputeListKey(List<CgpRow> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(_cgpCat).Append('|').Append(_cgpDrop).Append('|').Append(CsRaster).Append('|');
        foreach (var r in rows)
        {
            sb.Append(r.Id).Append('\u0001').Append(r.Title).Append('\u0001').Append(r.Sub).Append('\u0001')
              .Append(r.Value).Append('\u0001').Append(r.Choice).Append('\u0001').Append(r.Number).Append('\u0001')
              .Append(r.Stock ? 's' : 'm').Append(r.Disabled ? 'd' : ' ').Append(r.TriggerTag).Append('\u0001');
            if (r.Caps != null) sb.Append(string.Join("+", r.Caps));
            sb.Append('\u0002');
        }
        return sb.ToString();
    }

    private void CgpPaintList(CgpView v, List<CgpRow> rows, List<(float Y0, float Y1, List<float> Seps)> groups)
    {
        float H = v.Lay.Count == 0 ? 200 : v.Lay[^1].Y + v.Lay[^1].H;
        v.ListH = H;
        float pad = 16, R0 = CsRaster, lw = CpListW + pad * 2, lh = H + pad * 2 + 40;
        int pw = (int)Math.Ceiling(lw * R0), ph = (int)Math.Ceiling(lh * R0);
        if (v.ListS == null || (int)v.ListS.Size.Width != pw || (int)v.ListS.Size.Height != ph)
        {
            v.ListS?.Dispose();
            v.ListS = G.Surface(pw, ph);
            ((CompositionSurfaceBrush)v.List.Brush).Surface = v.ListS;
        }
        v.List.Size = new Vector2(lw, lh);
        using var ds = CanvasComposition.CreateDrawingSession(v.ListS);
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(pad, pad) * Matrix3x2.CreateScale(CsRaster);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;

        foreach (var (y0, y1, seps) in groups) CpCard(ds, 0, y0, CpListW, y1 - y0, seps, 30);
        foreach (var (i, y, _) in v.Lay) CgpPaintRow(ds, rows[i], i, y, CpListW, _cgpDrop);
    }

    private void CgpPaintRow(CanvasDrawingSession ds, CgpRow r, int index, float y, float listW, int openDrop)
    {
        float baseH = CgpRowBaseH(r), cy = y + baseH / 2f, R = listW - 34f, tx = 44f;
        using var layer = r.Disabled ? ds.CreateLayer(0.42f) : null;

        if (r.Kind == CgpRowKind.KeyMap)
        {
            CgpPaintKeyMapRow(ds, r, y, baseH, listW);
            return;
        }

        if (r.Changed) ds.FillCircle(24, cy, 6, CpAccentInk);
        float right = r.Kind switch
        {
            CgpRowKind.Slider => R,
            CgpRowKind.Dropdown => R - 420f,
            CgpRowKind.Toggle => R - 150f,
            _ => R - 340f,
        };

        if (r.Kind == CgpRowKind.Slider)
        {
            using var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 36, FontWeight = CpTitleWt, WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(r.Title, new Rect(tx, y + 18, R - tx, 52), RmInk, tf);

            float frac = Math.Clamp((r.Number - r.Min) / (float)Math.Max(1, r.Max - r.Min), 0, 1);
            var (mx, x0, x1, px) = CpSliderX(tx, R);
            float scy = y + baseH - 44f;
            using (var vf = new CanvasTextFormat { FontFamily = RmFont(r.Value), FontSize = 30, FontWeight = RsWt(r.Value, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(r.Value, new Rect(tx, scy - 24, mx - tx - 20, 48), RsSub, vf);
            ds.FillRoundedRectangle(mx - 16, scy - 4, 32, 8, 4, 4, RmInk);
            ds.FillRoundedRectangle(x0, scy - 5, x1 - x0, 10, 5, 5, Color.FromArgb(255, 0xDC, 0xDC, 0xE3));
            if (frac > 0)
            {
                using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, Color.FromArgb(255, 0x73, 0x40, 0xFF)) { StartPoint = new Vector2(x0, 0), EndPoint = new Vector2(x1, 0) };
                ds.FillRoundedRectangle(x0, scy - 5, (x1 - x0) * frac, 10, 5, 5, br);
            }
            float kx = x0 + (x1 - x0) * frac;
            RsSoftShadow(ds, kx - 24, scy - 24 + 3, 48, 48, 24, 5, 0.3f);
            using (var kb = new CanvasLinearGradientBrush(ds.Device, Color.FromArgb(255, 0x9A, 0x9C, 0xFF), Color.FromArgb(255, 0x6F, 0x3C, 0xFF)) { StartPoint = new Vector2(kx - 16, scy - 24), EndPoint = new Vector2(kx + 16, scy + 24) })
                ds.FillCircle(kx, scy, 24, kb);
            ds.FillRoundedRectangle(px - 16, scy - 4, 32, 8, 4, 4, RmInk);
            ds.FillRoundedRectangle(px - 4, scy - 16, 8, 32, 4, 4, RmInk);
            return;
        }

        if (r.Sub != null)
        {
            using (var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 36, FontWeight = CpTitleWt, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
                ds.DrawText(r.Title, new Rect(tx, y + 20, Math.Max(200, right - tx), 52), r.Danger ? RmRed : RmInk, tf);
            using (var sf = new CanvasTextFormat { FontFamily = RmFont(r.Sub), FontSize = 26, FontWeight = RsWt(r.Sub, 500), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
                ds.DrawText(r.Sub, new Rect(tx, y + 76, Math.Max(200, right - tx), 42), RsSub, sf);
        }
        else
        {
            using var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 36, FontWeight = CpTitleWt, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
            ds.DrawText(r.Title, new Rect(tx, y, Math.Max(200, right - tx), baseH), r.Danger ? RmRed : RmInk, tf);
        }

        switch (r.Kind)
        {
            case CgpRowKind.Toggle:
            {
                bool on = r.Choice == 1;
                float x = R - 120, ty = cy - 38;
                if (on)
                {
                    using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(x, ty), EndPoint = new Vector2(x + 120, ty + 76) };
                    ds.FillRoundedRectangle(x, ty, 120, 76, 38, 38, br);
                }
                else
                {
                    ds.FillRoundedRectangle(x, ty, 120, 76, 38, 38, Color.FromArgb(255, 0xE4, 0xE5, 0xE9));
                    ds.DrawRoundedRectangle(x + 2, ty + 2, 116, 72, 36, 36, Color.FromArgb(255, 0x9A, 0x9C, 0xA3), 4);
                }
                RsSoftShadow(ds, (on ? x + 82 : x + 38) - 27, cy - 27 + 3, 54, 54, 27, 4, 0.22f);
                ds.FillCircle(on ? x + 82 : x + 38, cy, 27, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                break;
            }
            case CgpRowKind.Dropdown:
            {
                var opts = r.Options!;
                string val = r.Choice >= 0 && r.Choice < opts.Length ? opts[r.Choice] : r.Value;
                bool open = openDrop == index;
                RsPaintPickPill(ds, R - CpPickW, cy - 40, CpPickW, 80, val, open, 32);
                if (open)
                {
                    var (ox, oy, ow, oh) = CgpOptRect(listW, baseH, y, 0);
                    var tmp = new RsRow { Title = r.Title, Options = opts, Current = () => r.Choice };
                    RsPaintOptionCard(ds, tmp, ox, oy, ow, oh, 32);
                }
                break;
            }
            case CgpRowKind.Stepper:
            {
                var (px0, pw) = CpStepperX(ds, r.Value, R);
                ds.FillRoundedRectangle(px0, cy - 38, pw, 76, 38, 38, Color.FromArgb(255, 0xE6, 0xE7, 0xEC));
                ds.FillRoundedRectangle(px0 + 30, cy - 4, 28, 8, 4, 4, RmInk);
                ds.FillRoundedRectangle(px0 + pw - 58, cy - 4, 28, 8, 4, 4, RmInk);
                ds.FillRoundedRectangle(px0 + pw - 48, cy - 14, 8, 28, 4, 4, RmInk);
                using var vf = new CanvasTextFormat { FontFamily = RmFont(r.Value), FontSize = 30, FontWeight = RsWt(r.Value), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
                ds.DrawText(r.Value, new Rect(px0 + 70, cy - 38, pw - 140, 76), RmInk, vf);
                break;
            }
            default: // Action
            {
                string val = r.Value.TrimEnd('›', ' ');
                if (val.Length > 0)
                {
                    using var vf = new CanvasTextFormat { FontFamily = RmFont(val), FontSize = 30, FontWeight = RsWt(val, 500), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
                    ds.DrawText(val, new Rect(R - 580, y, 580 - (r.Nav ? 38 : 0), baseH), r.AccentValue ? CpAccentInk : RsSub, vf);
                }
                if (r.Nav) CgpDrawRightChev(ds, R, cy);
                break;
            }
        }
    }

    private void CgpPaintKeyMapRow(CanvasDrawingSession ds, CgpRow r, float y, float h, float listW)
    {
        float cy = y + h / 2f, R = listW - 34f;
        bool changed = !r.Stock;
        if (changed) ds.FillCircle(24, cy, 6, CpAccentInk);

        // 左侧键位胶囊徽章
        float bw = Math.Max(58f, CpTextW(ds, r.KeyBadge, 24) + 28f), bh = 52f, bx = 44f, by = cy - bh / 2f;
        if (changed)
        {
            using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(bx, by), EndPoint = new Vector2(bx + bw, by + bh) };
            ds.FillRoundedRectangle(bx, by, bw, bh, 26, 26, br);
        }
        else ds.FillRoundedRectangle(bx, by, bw, bh, 26, 26, Color.FromArgb(255, 0xE6, 0xE7, 0xEC));

        using (var bf = new CanvasTextFormat { FontFamily = RmFont(r.KeyBadge), FontSize = 24, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(r.KeyBadge, new Rect(bx, by, bw, bh), changed ? Color.FromArgb(255, 0xFA, 0xFA, 0xFA) : RmInk, bf);

        float titleX = bx + bw + 20f;
        using (var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 36, FontWeight = CpTitleWt, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(r.Title, new Rect(titleX, y, 420, h), RmInk, tf);

        // 右侧箭头 + 3D 键帽组或映射说明
        CgpDrawRightChev(ds, R, cy);
        float rx = R - 34f;

        if (r.Caps is { Count: > 0 } caps)
        {
            for (int i = caps.Count - 1; i >= 0; i--)
            {
                string cap = caps[i];
                float cw = Math.Max(52f, CpTextW(ds, cap, 26) + 32f), ch = 52f;
                rx -= cw;
                CgpDrawKeycap3D(ds, rx, cy - ch / 2f, cw, ch, 14f, cap, 26f, chord: false, current: false, dimmed: false);
                if (i > 0)
                {
                    rx -= 30f;
                    using var pf = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 26, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
                    ds.DrawText("+", new Rect(rx, cy - 26, 30, 52), Color.FromArgb(255, 0x8E, 0x90, 0x98), pf);
                }
            }
            if (!string.IsNullOrEmpty(r.TriggerTag))
            {
                rx -= 12f;
                float tw = CpTextW(ds, r.TriggerTag, 24) + 28f, th = 44f;
                rx -= tw;
                ds.FillRoundedRectangle(rx, cy - th / 2f, tw, th, th / 2f, th / 2f, Color.FromArgb(255, 0xEC, 0xE6, 0xFF));
                using var tgf = new CanvasTextFormat { FontFamily = RmFont(r.TriggerTag), FontSize = 24, FontWeight = RsWt(r.TriggerTag, 500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
                ds.DrawText(r.TriggerTag, new Rect(rx, cy - th / 2f, tw, th), CpAccentInk, tgf);
            }
        }
        else
        {
            string val = r.Value;
            var col = changed ? RmInk : Color.FromArgb(255, 0x8E, 0x90, 0x98);
            using var vf = new CanvasTextFormat { FontFamily = RmFont(val), FontSize = 32, FontWeight = RsWt(val, 500), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
            ds.DrawText(val, new Rect(rx - 520, y, 520, h), col, vf);
            if (!string.IsNullOrEmpty(r.TriggerTag))
            {
                float vw = CpTextW(ds, val, 32);
                float tw = CpTextW(ds, r.TriggerTag, 24) + 28f, th = 44f, tx0 = rx - vw - 16f - tw;
                ds.FillRoundedRectangle(tx0, cy - th / 2f, tw, th, th / 2f, th / 2f, Color.FromArgb(255, 0xEC, 0xE6, 0xFF));
                using var tgf = new CanvasTextFormat { FontFamily = RmFont(r.TriggerTag), FontSize = 24, FontWeight = RsWt(r.TriggerTag, 500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
                ds.DrawText(r.TriggerTag, new Rect(tx0, cy - th / 2f, tw, th), CpAccentInk, tgf);
            }
        }
    }

    private static void CgpDrawRightChev(CanvasDrawingSession ds, float rightX, float cy)
    {
        using var pb = new CanvasPathBuilder(ds.Device);
        pb.BeginFigure(rightX - 10, cy - 9);
        pb.AddLine(rightX, cy);
        pb.AddLine(rightX - 10, cy + 9);
        pb.EndFigure(CanvasFigureLoop.Closed);
        using var tri = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(tri, RmInk);
        ds.DrawGeometry(tri, RmInk, 3, RwRoundStroke);
    }

    /// <summary>Cocoon 立体 3D 键帽（普通白灰凸起 / 紫色修饰键叠加 / 淡紫当前选中）。</summary>
    private void CgpDrawKeycap3D(CanvasDrawingSession ds, float x, float y, float w, float h, float r, string label, float fontSize, bool chord, bool current, bool dimmed)
    {
        using var dimLayer = dimmed ? ds.CreateLayer(0.38f) : null;
        if (chord)
        {
            RsSoftShadow(ds, x + 2, y + 5, w - 4, h, r, 6, 0.34f);
            using var br = new CanvasLinearGradientBrush(ds.Device, Color.FromArgb(255, 0x8F, 0x8B, 0xFF), Color.FromArgb(255, 0x73, 0x40, 0xFF)) { StartPoint = new Vector2(x, y), EndPoint = new Vector2(x, y + h) };
            ds.FillRoundedRectangle(x, y, w, h, r, r, br);
            ds.DrawRoundedRectangle(x + 0.75f, y + 0.75f, w - 1.5f, h - 1.5f, r, r, Color.FromArgb(255, 0x69, 0x35, 0xF2), 1.5f);
        }
        else if (current)
        {
            RsSoftShadow(ds, x + 1, y + 3, w - 2, h, r, 4, 0.12f);
            ds.FillRoundedRectangle(x, y, w, h, r, r, Color.FromArgb(255, 0xEC, 0xE6, 0xFF));
            ds.DrawRoundedRectangle(x + 1, y + 1, w - 2, h - 2, r, r, CpAccentInk, 2.5f);
        }
        else
        {
            RsSoftShadow(ds, x + 1, y + 4, w - 2, h, r, 4, 0.11f);
            using var fill = new CanvasLinearGradientBrush(ds.Device, Color.FromArgb(255, 0xFD, 0xFD, 0xFE), Color.FromArgb(255, 0xEF, 0xF0, 0xF5)) { StartPoint = new Vector2(x, y), EndPoint = new Vector2(x, y + h) };
            ds.FillRoundedRectangle(x, y, w, h, r, r, fill);
            ds.DrawLine(x + r * 0.7f, y + 1.5f, x + w - r * 0.7f, y + 1.5f, Colors.White, 1.5f);
            using var edge = new CanvasLinearGradientBrush(ds.Device, Color.FromArgb(255, 0xF6, 0xF6, 0xF9), Color.FromArgb(255, 0xBE, 0xBF, 0xC8)) { StartPoint = new Vector2(x, y), EndPoint = new Vector2(x, y + h) };
            ds.DrawRoundedRectangle(x + 0.75f, y + 0.75f, w - 1.5f, h - 1.5f, r, r, edge, 1.5f);
        }

        var ink = chord ? Color.FromArgb(255, 0xFA, 0xFA, 0xFA) : current ? CpAccentInk : RmInk;
        using var tf = new CanvasTextFormat
        {
            FontFamily = RmFont(label),
            FontSize = fontSize,
            FontWeight = RsWt(label, 600),
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
        };
        ds.DrawText(label, new Rect(x + 6, y, Math.Max(12, w - 12), h), ink, tf);
    }

    private void CgpScrollAndFocus(CgpView v)
    {
        int f = _cgpDrop >= 0 ? _cgpDrop : _cgpFocus;
        var hit = v.Lay.FirstOrDefault(e => e.I == f);
        bool inList = _cgpZone == 1 && hit.H > 0;
        float view = CpBottom - CpTop, max = Math.Max(0, v.ListH - view + 30), target = Math.Clamp(v.Scroll, 0, max);

        if (inList && !(v.Manual && _cgpTouchMode))
        {
            float top = hit.Y - 60, bottom = hit.Y + hit.H + 60;
            if (_cgpDrop >= 0 && _cgpDrop < _cgpRows.Count)
            {
                var (_, oy, _, oh) = CgpOptRect(CpListW, CgpRowBaseH(_cgpRows[_cgpDrop]), hit.Y, _cgpDropSel);
                top = Math.Min(top, oy - 60);
                bottom = oy + oh + 80;
            }
            if (bottom > target + view) target = bottom - view;
            if (top < target) target = top;
            target = Math.Clamp(target, 0, max);
        }

        if (!v.OffsetInit) { v.OffsetInit = true; v.Scroll = target; RwJump(v.List, new Vector3(CpListX - 16, CpTop - 16 - target, 0)); }
        else if (MathF.Abs(target - v.Scroll) > 0.5f)
        {
            v.Scroll = target;
            var to = new Vector3(CpListX - 16, CpTop - 16 - target, 0);
            if (Reduced) RwJump(v.List, to); else v.List.StartAnimation("Offset", RwSpring(to));
        }

        if (_cgpTouchMode || CgpModalOpen)
        {
            if (v.Cur.Shown) CurShow(v.Cur, false, 80);
            return;
        }

        bool first = !v.Cur.Shown;
        if (inList && _cgpDrop >= 0 && _cgpDrop < _cgpRows.Count)
        {
            var (ox, oy, ow, oh) = CgpOptRect(CpListW, CgpRowBaseH(_cgpRows[_cgpDrop]), hit.Y, _cgpDropSel);
            CurToCocoon(v.Cur, CpListX + ox, CpTop + oy - target, ow, oh, 24, move: !first);
        }
        else if (inList)
        {
            float baseH = CgpRowBaseH(_cgpRows[f]);
            CurToCocoon(v.Cur, CpListX, CpTop + hit.Y - target, CpListW, baseH, 30, move: !first);
        }
        else
        {
            int cat = Math.Clamp(_cgpCat, 0, CgpCats.Length - 1);
            CurToCocoon(v.Cur, CpLeftX, CpTop + cat * CgpSlot + 4, CpLeftW, CgpSlot - 8, 30, move: !first);
        }
        if (first) CurShow(v.Cur, true, 140, 40);
    }

    // ---------------- ModalSheet 子页面（指令键帽选择器 / 单键触发 / 触发设置） ----------------

    private void CgpOpenModal(string src, bool desktop, bool directChooser)
    {
        _cgpActiveKey = src is "LStick" ? "LS" : src is "RStick" ? "RS" : src;
        _cgpDrop = -1;
        _cgpModalDrop = -1;
        if (_cgp != null) { _cgp.ModalScroll = 0; _cgp.ModalManual = false; _cgp.ModalOffsetInit = false; }

        var p = new GpPickerState
        {
            Source = src,
            Desktop = desktop,
            Act = 0,
            KeyFocus = 1,
            SettingsFocus = 1,
            DirectChooser = directChooser,
            Level = directChooser ? GpSubLevel.Chooser : GpSubLevel.Key,
        };
        if (directChooser) CgpLocateCurrentCell(p);
        else p.Tab = GpIsSpecial(src) ? 4 : 0;

        _gpPicker = p;
        Cue(Sfx.ModalShow);
        CgpPaint();
    }

    private void CgpCloseModal()
    {
        _cgpModalDrop = -1;
        _gpPicker = null;
        Cue(Sfx.ModalHide);
        CgpPaint();
    }

    /// <summary>打开选择器时默认定位到当前绑定的页签与键位。</summary>
    private void CgpLocateCurrentCell(GpPickerState p)
    {
        var (cmds, codes) = CgpCurrentBinding(p);
        for (int tab = 0; tab < 5; tab++)
        {
            var cells = GpChooserCells(tab);
            for (int i = 0; i < cells.Length; i++)
            {
                var c = cells[i];
                if (c.Cmd.Length > 0 && cmds.Contains(c.Cmd) || c.Code > 0 && codes.Contains(c.Code))
                {
                    p.Tab = tab;
                    p.Cell = i;
                    return;
                }
            }
        }
        p.Tab = GpIsSpecial(p.Source) ? 4 : p.Desktop ? 2 : 0;
        p.Cell = 0;
    }

    private (HashSet<string> Cmds, HashSet<int> Codes) CgpCurrentBinding(GpPickerState p)
    {
        var cmds = new HashSet<string>();
        var codes = new HashSet<int>();
        var acts = GpActs(p.Source);
        JsonNode? act = acts != null && acts.Count > 0 ? acts[Math.Clamp(p.Act, 0, acts.Count - 1)] : null;
        if (GpIsSpecial(p.Source)) act = new JsonObject { ["command"] = GpSpecialCommand(p.Source, p.SteamMapping).DeepClone() };
        if (act != null)
        {
            var cmd = act["command"];
            switch (Str(cmd?["type"]))
            {
                case "gamepad": cmds.Add("pad:" + Str(cmd!["button"])); break;
                case "keyboard": foreach (var k in cmd!["keys"] as JsonArray ?? new JsonArray()) cmds.Add("key:" + Str(k)); break;
                case "mouse": cmds.Add(cmd!["wheel"] != null ? "wheel:" + Str(cmd["wheel"]) : "mouse:" + Str(cmd["button"])); break;
                case "system": cmds.Add("sys:" + Str(cmd!["action"])); break;
            }
        }
        else if (p.Desktop)
        {
            foreach (var c in GpDesktopCodes(p.Source) ?? Array.Empty<int>()) codes.Add(c);
        }
        else
        {
            foreach (var c in GpFirmwareCodes(p.Source)) codes.Add(c);
        }
        return (cmds, codes);
    }

    private void CgpPaintModalSheetBg(CgpView v)
    {
        float R = CsRaster, w = RsW + RsPad * 2, h = RsH + RsPad * 2;
        v.ModalSheetRaster = R;
        v.ModalSheetS?.Dispose();
        v.ModalSheetS = G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(h * R));
        using (var ds = CanvasComposition.CreateDrawingSession(v.ModalSheetS))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(RsPad - RsX, RsPad - RsY) * Matrix3x2.CreateScale(R);
            using (var shadow = new CanvasCommandList(ds.Device))
            {
                using (var s = shadow.CreateDrawingSession()) { s.Transform = ds.Transform; s.FillRoundedRectangle(RsX, RsY + 14, RsW, RsH, RsR, RsR, Colors.Black); }
                using var blur = new GaussianBlurEffect { Source = shadow, BlurAmount = 22 * R, BorderMode = EffectBorderMode.Soft };
                using var op = new OpacityEffect { Source = blur, Opacity = 0.34f };
                var t = ds.Transform; ds.Transform = Matrix3x2.Identity; ds.DrawImage(op); ds.Transform = t;
            }
            ds.FillRoundedRectangle(RsX, RsY, RsW, RsH, RsR, RsR, RsBg);
        }
        ((CompositionSurfaceBrush)v.ModalSheet.Brush).Surface = v.ModalSheetS;
        v.ModalSheet.Size = new Vector2(w, h);
        v.ModalSheet.Offset = new Vector3(RsX - RsPad, RsY - RsPad, 0);
    }

    private void CgpSyncModal(CgpView v)
    {
        if (_gpPicker is not { } p)
        {
            if (v.ModalShown)
            {
                v.ModalShown = false;
                int seq = ++v.ModalSeq;
                if (v.ModalCur.Shown) CurShow(v.ModalCur, false, 60);
                if (Reduced) v.ModalRoot.IsVisible = false;
                else
                {
                    v.ModalScrim.StartAnimation("Opacity", CsS(0, 140, Bezier.EaseOut));
                    v.ModalBox.StartAnimation("Opacity", CsS(0, 120, Bezier.EaseOut));
                    v.ModalBox.StartAnimation("Scale", CsV3(new Vector3(0.94f, 0.94f, 1), 140, Bezier.EaseOut));
                    After(150, () => { if (v.ModalSeq == seq && _gpPicker == null) v.ModalRoot.IsVisible = false; });
                }
            }
            return;
        }

        bool opening = !v.ModalShown;
        if (MathF.Abs(v.ModalSheetRaster - CsRaster) > 0.001f) CgpPaintModalSheetBg(v);
        if (opening)
        {
            v.ModalShown = true;
            ++v.ModalSeq;
            v.ModalRoot.IsVisible = true;
            v.ModalCur.Shown = false;
            if (Reduced)
            {
                v.ModalScrim.StopAnimation("Opacity"); v.ModalScrim.Opacity = 1;
                v.ModalBox.StopAnimation("Opacity"); v.ModalBox.Opacity = 1;
                v.ModalBox.StopAnimation("Scale"); v.ModalBox.Scale = Vector3.One;
            }
            else
            {
                var outQuint = new Bezier(0.22f, 1f, 0.36f, 1f);
                v.ModalScrim.Opacity = 0;
                v.ModalScrim.StartAnimation("Opacity", CsS(1, 160, Bezier.EaseOut));
                v.ModalBox.Opacity = 0;
                v.ModalBox.Scale = new Vector3(0.92f, 0.92f, 1);
                v.ModalBox.StartAnimation("Opacity", CsS(1, 140, Bezier.EaseOut));
                v.ModalBox.StartAnimation("Scale", CsV3(Vector3.One, 210, outQuint));
            }
        }

        CgpPaintModalHead(v, p);

        if (p.Level == GpSubLevel.Chooser)
        {
            v.ModalListClip.IsVisible = false;
            v.ModalBody.IsVisible = true;
            CgpPaintModalChooser(v, p);
            CgpFocusModalChooser(v, p, opening);
        }
        else
        {
            v.ModalBody.IsVisible = false;
            v.ModalListClip.IsVisible = true;
            _cgpModalRows = p.Level == GpSubLevel.Key ? GpKeyRows(p) : GpSettingsRows(p);
            int focus = p.Level == GpSubLevel.Key ? p.KeyFocus : p.SettingsFocus;
            focus = CgpClampPerfFocus(_cgpModalRows, focus);
            if (p.Level == GpSubLevel.Key) p.KeyFocus = focus; else p.SettingsFocus = focus;
            if (_cgpModalDrop >= _cgpModalRows.Count || _cgpModalDrop >= 0 && CpOpts(_cgpModalRows[_cgpModalDrop]) == null) _cgpModalDrop = -1;

            CgpPaintModalList(v, _cgpModalRows);
            CgpFocusModalList(v, _cgpModalRows, focus, opening);
        }
    }

    private static int CgpClampPerfFocus(List<PerfRow> rows, int focus)
    {
        if (rows.Count == 0) return 0;
        focus = Math.Clamp(focus, 0, rows.Count - 1);
        if (CpFocusable(rows[focus])) return focus;
        for (int i = focus; i < rows.Count; i++) if (CpFocusable(rows[i])) return i;
        for (int i = focus - 1; i >= 0; i--) if (CpFocusable(rows[i])) return i;
        return 0;
    }

    // ---- ModalSheet 顶栏与分段页签 ----

    private static readonly float[] CgpTabWidths = { 122f, 122f, 122f, 168f, 122f };
    private const float CgpTabBarX = 742f, CgpTabBarY = 102f, CgpTabBarW = 876f, CgpTabBarH = 78f;

    private static Rect CgpChooserTabRect(int tab)
    {
        float x = CgpTabBarX + 84f;
        for (int i = 0; i < tab; i++) x += CgpTabWidths[i] + 10f;
        return new Rect(x, CgpTabBarY + 8f, CgpTabWidths[tab], CgpTabBarH - 16f);
    }

    private void CgpPaintModalHead(CgpView v, GpPickerState p)
    {
        string srcTitle = GpSourceName(p.Source);
        string title = p.Level switch
        {
            GpSubLevel.Chooser => $"{CgpShortKeyLabel(p.Source)} · 选择指令",
            GpSubLevel.Settings => $"{srcTitle} · 触发设置",
            _ => p.Desktop ? $"{srcTitle} · 桌面模式" : srcTitle,
        };
        bool showY = p.Level == GpSubLevel.Chooser;
        bool yAsGear = showY && p.DirectChooser && !p.Desktop && !GpIsSpecial(p.Source) && !(p.Tab == 2 && p.Chord.Count > 0);
        string subRight = p.Level == GpSubLevel.Key
            ? $"当前：{(GpIsSpecial(p.Source) ? GpSpecialSummary(p.Source, p.SteamMapping) : p.Desktop ? GpDesktopSummary(p.Source) : GpKeySummary(p.Source))}"
            : "";

        string key = $"{p.Level}|{title}|{p.Tab}|{p.InTabs}|{showY}|{yAsGear}|{subRight}|{CsRaster}";
        if (v.ModalHeadKey == key) return;
        v.ModalHeadKey = key;

        using var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.ModalHeadS, v.ModalHead, RsX, RsY, RsW, 136));
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(-RsX, -RsY) * Matrix3x2.CreateScale(CsRaster);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;

        RsPaintPills(ds, play: false, noY: !showY || yAsGear);
        if (yAsGear) CgpDrawYSettingsPill(ds);

        float titleMaxW = p.Level == GpSubLevel.Chooser ? 430f : 860f;
        using (var tf = new CanvasTextFormat { FontFamily = RmFont(title), FontSize = p.Level == GpSubLevel.Chooser ? 40 : 44, FontWeight = RsWt(title), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
            ds.DrawText(title, new Rect(290, 104, titleMaxW, 74), RmInk, tf);

        if (p.Level == GpSubLevel.Chooser)
        {
            ds.FillRoundedRectangle(CgpTabBarX, CgpTabBarY, CgpTabBarW, CgpTabBarH, 39, 39, Color.FromArgb(255, 0xEA, 0xEC, 0xEF));
            CgpDrawShoulderBadge(ds, CgpTabBarX + 10, CgpTabBarY + 15, 64, 48, "LB");
            for (int i = 0; i < GpChooserTabs.Length; i++)
            {
                var tr = CgpChooserTabRect(i);
                bool on = i == p.Tab;
                if (on)
                {
                    RsSoftShadow(ds, (float)tr.X, (float)tr.Y + 3, (float)tr.Width, (float)tr.Height, 31, 5, 0.13f);
                    ds.FillRoundedRectangle(tr, 31, 31, Colors.White);
                }
                using var tabF = new CanvasTextFormat { FontFamily = RmFont(GpChooserTabs[i]), FontSize = 27, FontWeight = RsWt(GpChooserTabs[i], 500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
                ds.DrawText(GpChooserTabs[i], tr, on ? CpAccentInk : RsSub, tabF);
            }
            CgpDrawShoulderBadge(ds, CgpTabBarX + CgpTabBarW - 74, CgpTabBarY + 15, 64, 48, "RB");
        }
        else if (subRight.Length > 0)
        {
            using var sf = new CanvasTextFormat { FontFamily = RmFont(subRight), FontSize = 28, FontWeight = RsWt(subRight, 500), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
            ds.DrawText(subRight, new Rect(1120, 104, 650, 74), RsSub, sf);
        }
    }

    private void CgpDrawYSettingsPill(CanvasDrawingSession ds)
    {
        var dev = ds.Device;
        RsSoftShadow(ds, 1656, 112, 126, 70, 35, 9, 0.42f);
        using (var br = new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0x8A, 0x7B, 0xFF), Color.FromArgb(255, 0x7A, 0x3C, 0xFF)) { StartPoint = new Vector2(1652, 104), EndPoint = new Vector2(1786, 178) })
            ds.FillRoundedRectangle(1652, 104, 134, 74, 37, 37, br);
        ds.FillCircle(1689, 141, 21, Color.FromArgb(255, 0xB9, 0xA9, 0xFF));
        using (var f = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center })
            ds.DrawText("Y", new Rect(1668, 120, 42, 42), CpAccentInk, f);
        ds.FillCircle(1750, 141, 21, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
        // 三道滑杆图标 = 多触发与高级触发设置
        foreach (var (ly, kx) in new[] { (-8f, 4f), (0f, -4f), (8f, 3f) })
        {
            ds.DrawLine(1740, 141 + ly, 1760, 141 + ly, CpAccentInk, 2.8f, RwRoundStroke);
            ds.FillCircle(1750 + kx, 141 + ly, 3.2f, CpAccentInk);
        }
    }

    private static void CgpDrawShoulderBadge(CanvasDrawingSession ds, float x, float y, float w, float h, string text)
    {
        ds.FillRoundedRectangle(x, y, w, h, h / 2, h / 2, Color.FromArgb(255, 0xD8, 0xDA, 0xE0));
        using var f = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 21, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
        ds.DrawText(text, new Rect(x, y, w, h), RsSub, f);
    }

    // ---- ModalSheet Chooser 全部 5 页签几何与绘制 ----

    private static Rect CgpChooserCellRect(int tab, int index, GpCell c)
    {
        switch (tab)
        {
            case 0: // 手柄（左簇 / 右簇 / 底部功能键，中间留给 Claw 8 EX 陶瓷线稿）
            {
                if (c.Row > 620)
                {
                    int bIdx = Math.Clamp((int)MathF.Round((c.Col - 652f) / 52f), 0, 3);
                    float bx = 960f + (bIdx - 1.5f) * 160f;
                    return new Rect(bx - 70f, 948f - 36f, 140f, 72f);
                }
                bool left = c.Col < 600f;
                if (c.Row < 260)
                {
                    float sx = left
                        ? (c.Col < 350 ? 276f : 418f)
                        : (c.Col < 1110 ? 1502f : 1644f);
                    return new Rect(sx - 62f, 286f - 35f, 124f, 70f);
                }
                float baseX = left ? 346f : 1574f;
                float origX = left ? 330f : 1130f;
                float cx = baseX + (c.Col - origX) * 2.05f;
                float cy = c.Row < 460
                    ? 494f + (c.Row - 370f) * 1.65f
                    : 786f + (c.Row - 552f) * 1.65f;
                return new Rect(cx - 47f, cy - 36f, 94f, 72f);
            }
            case 1: // 鼠标（中间鼠标线稿 + 左右引线 3D 键帽）
            {
                if (c.Row > 560)
                {
                    float bx = c.Col < 730 ? 810f : 1110f;
                    return new Rect(bx - 115f, 924f - 38f, 230f, 76f);
                }
                float mx = c.Col < 730 ? 500f : 1420f;
                float my = 380f + (c.Row - 318f) * 1.85f;
                return new Rect(mx - 115f, my - 38f, 230f, 76f);
            }
            case 2: // 键盘（标准 87 键 15u 全尺寸 3D 键帽托盘）
            {
                const float u = 103.6f, gap = 10f, x0 = 193f, y0 = 250f, rowStep = 98f, fnGap = 18f;
                return new Rect(x0 + c.Col * u, y0 + c.Row * rowStep + (c.Row > 0 ? fnGap : 0), c.Units * u - gap, 84f);
            }
            case 3: // 数字键盘（顶部媒体键 + 左侧编辑与方向键 + 右侧 17 键小键盘）
            {
                const float uW = 136f, uH = 112f, gap = 12f, x0 = 422f, y0 = 280f;
                return new Rect(x0 + c.Col * uW, y0 + c.Row * uH, c.Units * uW - gap, c.Tall * uH - gap);
            }
            default: // 系统（3 列分组立体卡片）
            {
                const float colW = 512f, gapX = 38f, x0 = 156f, y0 = 240f, rowH = 108f;
                return new Rect(x0 + c.Col * (colW + gapX), y0 + c.Row * (rowH + CpSep), colW, rowH);
            }
        }
    }

    private void CgpPaintModalChooser(CgpView v, GpPickerState p)
    {
        var (curCmds, curCodes) = CgpCurrentBinding(p);
        var cells = GpChooserCells(p.Tab);
        p.Cell = Math.Clamp(p.Cell, 0, Math.Max(0, cells.Length - 1));
        string chordStr = string.Join("+", p.Chord.Select(c => c.Cmd));
        string key = $"{p.Source}|{p.Desktop}|{p.SteamMapping}|{p.Tab}|{p.Cell}|{p.InTabs}|{_gpArtLoaded}|{chordStr}|{string.Join(",", curCmds)}|{string.Join(",", curCodes)}|{CsRaster}";
        if (v.ModalBodyKey == key) return;
        v.ModalBodyKey = key;

        using var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.ModalBodyS, v.ModalBody, RsX, 200, RsW, RsY + RsH - 200));
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(-RsX, -200) * Matrix3x2.CreateScale(CsRaster);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;

        if (p.Tab == 4)
        {
            // 系统动作：3 列立体 CpCard
            for (int col = 0; col < 3; col++)
            {
                int count = cells.Count(c => (int)c.Col == col);
                if (count == 0) continue;
                var firstRect = CgpChooserCellRect(4, 0, cells.First(c => (int)c.Col == col));
                var seps = new List<float>();
                for (int r = 1; r < count; r++) seps.Add((float)firstRect.Y + r * (108f + CpSep) - CpSep);
                float totalH = count * 108f + (count - 1) * CpSep;
                CpCard(ds, (float)firstRect.X, (float)firstRect.Y, (float)firstRect.Width, totalH, seps, 30);
            }
            for (int i = 0; i < cells.Length; i++)
            {
                var c = cells[i];
                var r = CgpChooserCellRect(4, i, c);
                bool isCur = curCmds.Contains(c.Cmd) || c.Code > 0 && curCodes.Contains(c.Code);
                bool unsupported = p.Desktop && c.Code == 0;
                using var dim = unsupported ? ds.CreateLayer(0.4f) : null;
                using var tf = new CanvasTextFormat { FontFamily = RmFont(c.Label), FontSize = 32, FontWeight = RsWt(c.Label, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
                ds.DrawText(c.Label, new Rect(r.X + 36, r.Y, r.Width - 96, r.Height), RmInk, tf);
                if (isCur) ds.FillCircle((float)(r.X + r.Width - 44), (float)(r.Y + r.Height / 2), 8, CpAccentInk);
            }
            return;
        }

        // 凹槽浅灰托盘底（手柄 / 鼠标 / 键盘 / 数字键盘）
        float trayH = p.Tab == 2 ? 684f : 846f;
        ds.FillRoundedRectangle(152, 214, 1616, trayH, 36, 36, Color.FromArgb(255, 0xF0, 0xF1, 0xF6));
        ds.DrawRoundedRectangle(152.75f, 214.75f, 1614.5f, trayH - 1.5f, 36, 36, Color.FromArgb(255, 0xE2, 0xE3, 0xEA), 1.5f);

        if (p.Tab == 0)
        {
            var focusCell = !p.InTabs && p.Cell >= 0 && p.Cell < cells.Length ? cells[p.Cell] : default;
            string focusBtn = focusCell.Cmd is { Length: > 4 } s && s.StartsWith("pad:") ? s[4..] : "";
            string mapBtn = focusBtn switch { "Guide" => "", _ => focusBtn };
            CgpDrawClawWireframe(ds, 620, 420, 680, 320, mapBtn, backFace: false, rumbleView: false, 0, 0, pulse: false, default, stickArrow: focusBtn);
        }
        else if (p.Tab == 1)
        {
            string focusCmd = !p.InTabs && p.Cell >= 0 && p.Cell < cells.Length ? cells[p.Cell].Cmd : "";
            CgpDrawMouseDiagram(ds, 960, 566, focusCmd);
        }

        float capFont = p.Tab == 0 ? 23f : p.Tab == 1 ? 26f : 27f;
        for (int i = 0; i < cells.Length; i++)
        {
            var c = cells[i];
            var r = CgpChooserCellRect(p.Tab, i, c);
            bool inChord = p.Chord.Contains(c);
            bool isCur = !inChord && (c.Cmd.Length > 0 && curCmds.Contains(c.Cmd) || c.Code > 0 && curCodes.Contains(c.Code));
            bool dimmed = (c.Cmd.Length == 0 && c.Code == 0) || (GpIsSpecial(p.Source) && c.Cmd.StartsWith("pad:")) || (p.Desktop && c.Code == 0);
            string label = p.Tab == 0 ? CgpPadCellLabel(c) : c.Label;
            CgpDrawKeycap3D(ds, (float)r.X, (float)r.Y, (float)r.Width, (float)r.Height, 18f, label, capFont, inChord, isCur, dimmed);
        }

        if (p.Tab == 2)
        {
            // 底部紫色组合键状态胶囊
            var focusCell = !p.InTabs && p.Cell >= 0 && p.Cell < cells.Length ? cells[p.Cell] : cells[0];
            var previewParts = p.Chord.Select(x => x.Label).ToList();
            if (!GpIsModifier(focusCell) || previewParts.Count == 0) previewParts.Add(focusCell.Label);
            string preview = string.Join(" + ", previewParts);
            string hint = p.Chord.Count > 0
                ? $"（再选普通键完成组合，或按 Y 直接绑定 {string.Join(" + ", p.Chord.Select(x => x.Label))}）"
                : $"（点修饰键叠加组合，点普通键写入 {GpSourceName(p.Source)}）";

            const string prefix = "当前组合预览：";
            float w1 = CpTextW(ds, prefix, 26), w2 = CpTextW(ds, preview, 28), w3 = CpTextW(ds, hint, 24);
            float pillW = Math.Min(1560f, w1 + w2 + w3 + 96f), pillX = 960f - pillW / 2f, pillY = 932f, pillH = 74f;
            ds.FillRoundedRectangle(pillX, pillY, pillW, pillH, 37, 37, Color.FromArgb(255, 0xEC, 0xE6, 0xFF));
            ds.DrawRoundedRectangle(pillX + 0.75f, pillY + 0.75f, pillW - 1.5f, pillH - 1.5f, 37, 37, Color.FromArgb(255, 0xD1, 0xC4, 0xFF), 1.5f);

            float tx = pillX + 38f;
            using (var f1 = new CanvasTextFormat { FontFamily = RmFont(prefix), FontSize = 26, FontWeight = RsWt(prefix, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(prefix, new Rect(tx, pillY, w1 + 10, pillH), RsSub, f1);
            tx += w1 + 8f;
            using (var f2 = new CanvasTextFormat { FontFamily = RmFont(preview), FontSize = 28, FontWeight = RsWt(preview, 600), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(preview, new Rect(tx, pillY, w2 + 16, pillH), CpAccentInk, f2);
            tx += w2 + 14f;
            using (var f3 = new CanvasTextFormat { FontFamily = RmFont(hint), FontSize = 24, FontWeight = RsWt(hint, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
                ds.DrawText(hint, new Rect(tx, pillY, Math.Max(60, pillX + pillW - 30 - tx), pillH), RsSub, f3);
        }
    }

    private static string CgpPadCellLabel(GpCell c) => c.Cmd switch
    {
        "pad:LStickUp" => "LS ↑", "pad:LStickDown" => "LS ↓", "pad:LStickLeft" => "LS ←", "pad:LStickRight" => "LS →", "pad:LS" => "LS 按下",
        "pad:RStickUp" => "RS ↑", "pad:RStickDown" => "RS ↓", "pad:RStickLeft" => "RS ←", "pad:RStickRight" => "RS →", "pad:RS" => "RS 按下",
        "pad:Up" => "十字 ↑", "pad:Down" => "十字 ↓", "pad:Left" => "十字 ←", "pad:Right" => "十字 →",
        _ => c.Label,
    };

    private static void CgpDrawMouseDiagram(CanvasDrawingSession ds, float cx, float cy, string focusCmd)
    {
        float w = 236f, h = 368f, left = cx - w / 2f, top = cy - h / 2f, split = top + 148f;
        var stroke = Color.FromArgb(255, 0x5F, 0x61, 0x68);
        using var padGrad = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(left, top), EndPoint = new Vector2(left + w, top + h) };

        // 鼠标主体陶瓷壳
        using var body = CanvasGeometry.CreateRoundedRectangle(ds.Device, left, top, w, h, 112, 124);
        ds.FillGeometry(body, Colors.White);

        using (ds.CreateLayer(1, body))
        {
            if (focusCmd == "mouse:Left") ds.FillRectangle(left, top, w / 2f, split - top, padGrad);
            else if (focusCmd == "mouse:Right") ds.FillRectangle(cx, top, w / 2f, split - top, padGrad);
        }
        ds.DrawGeometry(body, stroke, 3.5f);
        ds.DrawLine(cx, top + 2, cx, split, stroke, 2.8f);
        ds.DrawLine(left + 4, split, left + w - 4, split, stroke, 2.8f);

        // 滚轮与侧键
        bool wheelHi = focusCmd is "mouse:Middle" or "wheel:Up" or "wheel:Down" or "wheel:Left" or "wheel:Right";
        if (wheelHi) ds.FillRoundedRectangle(cx - 12, top + 38, 24, 68, 12, 12, padGrad);
        else ds.FillRoundedRectangle(cx - 12, top + 38, 24, 68, 12, 12, Color.FromArgb(255, 0xE4, 0xE5, 0xEC));
        ds.DrawRoundedRectangle(cx - 12, top + 38, 24, 68, 12, 12, wheelHi ? CpAccentInk : stroke, 2.8f);

        bool fwdHi = focusCmd == "mouse:Forward", backHi = focusCmd == "mouse:Back";
        ds.FillRoundedRectangle(left - 8, cy - 28, 14, 48, 6, 6, fwdHi ? RwC2 : Color.FromArgb(255, 0xE4, 0xE5, 0xEC));
        ds.DrawRoundedRectangle(left - 8, cy - 28, 14, 48, 6, 6, fwdHi ? CpAccentInk : stroke, 2.4f);
        ds.FillRoundedRectangle(left - 8, cy + 32, 14, 48, 6, 6, backHi ? RwC2 : Color.FromArgb(255, 0xE4, 0xE5, 0xEC));
        ds.DrawRoundedRectangle(left - 8, cy + 32, 14, 48, 6, 6, backHi ? CpAccentInk : stroke, 2.4f);

        var anchors = new Dictionary<string, Vector2>
        {
            ["mouse:Left"] = new(cx - 56, top + 74),
            ["mouse:Right"] = new(cx + 56, top + 74),
            ["mouse:Middle"] = new(cx, top + 72),
            ["wheel:Up"] = new(cx + 12, top + 50),
            ["wheel:Down"] = new(cx + 12, top + 94),
            ["mouse:Forward"] = new(left - 4, cy - 4),
            ["mouse:Back"] = new(left - 4, cy + 56),
            ["wheel:Left"] = new(cx - 12, top + 72),
            ["wheel:Right"] = new(cx + 12, top + 72),
        };

        var leadDim = Color.FromArgb(140, 0xB8, 0xBA, 0xC4);
        for (int i = 0; i < GpMouseCells.Length; i++)
        {
            var cell = GpMouseCells[i];
            if (!anchors.TryGetValue(cell.Cmd, out var a)) continue;
            var r = CgpChooserCellRect(1, i, cell);
            Vector2 from = cell.Row > 560
                ? new Vector2((float)(r.X + r.Width / 2), (float)r.Y)
                : new Vector2(cell.Col < 730 ? (float)(r.X + r.Width) : (float)r.X, (float)(r.Y + r.Height / 2));
            bool on = cell.Cmd == focusCmd;
            ds.DrawLine(from, a, on ? CpAccentInk : leadDim, on ? 3.2f : 1.8f, RwRoundStroke);
            ds.FillCircle(a, on ? 6.5f : 4f, on ? CpAccentInk : stroke);
        }
    }

    private void CgpFocusModalChooser(CgpView v, GpPickerState p, bool snap)
    {
        if (_cgpTouchMode)
        {
            if (v.ModalCur.Shown) CurShow(v.ModalCur, false, 80);
            return;
        }
        bool first = !v.ModalCur.Shown || snap;
        if (p.InTabs)
        {
            var tr = CgpChooserTabRect(p.Tab);
            CurToCocoon(v.ModalCur, (float)tr.X, (float)tr.Y, (float)tr.Width, (float)tr.Height, 31f, move: !first);
        }
        else
        {
            var cells = GpChooserCells(p.Tab);
            var r = CgpChooserCellRect(p.Tab, p.Cell, cells[p.Cell]);
            float rad = p.Tab == 4 ? 30f : 18f;
            CurToCocoon(v.ModalCur, (float)r.X, (float)r.Y, (float)r.Width, (float)r.Height, rad, move: !first);
        }
        if (!v.ModalCur.Shown) CurShow(v.ModalCur, true, 140, snap ? 80 : 20);
    }

    // ---- ModalSheet 列表型子页面（Key 多触发页 / Settings 触发设置页） ----

    private List<CgpRow> CgpConvertPerfRows(List<PerfRow> perfRows)
    {
        var list = new List<CgpRow>();
        bool nextBreak = true;
        for (int i = 0; i < perfRows.Count; i++)
        {
            var pr = perfRows[i];
            if (pr.Section) { nextBreak = true; continue; }
            if (pr.Skip) continue;

            var cr = new CgpRow
            {
                Break = nextBreak,
                Id = $"m_{i}", // i 存原始 PerfRow 下标
                Title = pr.Title,
                Sub = CpSub(pr),
                Value = pr.Value,
                Disabled = pr.Disabled,
                Changed = pr.Changed,
                Danger = pr.Title.Contains("删除"),
                AccentValue = pr.Title == "测试指令",
                Press = pr.Press,
            };
            nextBreak = false;

            if (CpIsToggle(pr))
            {
                cr.Kind = CgpRowKind.Toggle;
                cr.Choice = pr.Choice;
                cr.Sub = null;
                cr.Press = () =>
                {
                    int next = pr.Choice == 1 ? 0 : 1;
                    Cue(next == 1 ? Sfx.ToggleOn : Sfx.ToggleOff);
                    if (pr.Select != null) pr.Select(next);
                    else pr.Press?.Invoke();
                    CgpPaint();
                };
            }
            else if (CpIsPick(pr))
            {
                cr.Kind = CgpRowKind.Dropdown;
                cr.Options = CpOpts(pr);
                cr.Choice = pr.Choice;
                cr.Select = idx => { CpChoose(pr, idx); CgpPaint(); };
            }
            else if (CpIsSlider(pr))
            {
                cr.Kind = CgpRowKind.Slider;
                cr.Number = pr.Number;
                cr.Min = pr.Minimum;
                cr.Max = pr.Maximum;
                cr.Step = Math.Max(1, pr.Increment);
                cr.SetNumber = n => { pr.SetNumber?.Invoke(n); CgpPaint(); };
            }
            else if (CpIsStepper(pr))
            {
                cr.Kind = CgpRowKind.Stepper;
                cr.StepBy = d => { pr.Step?.Invoke(d); CgpPaint(); };
            }
            else
            {
                cr.Kind = CgpRowKind.Action;
                cr.Nav = pr.Title is "指令" or "设置" || pr.Sub;
                if (cr.Value == "›") cr.Value = "";
            }
            list.Add(cr);
        }
        return list;
    }

    private List<CgpRow> _cgpModalConverted = new();
    private List<int> _cgpModalPerfMap = new();

    private void CgpPaintModalList(CgpView v, List<PerfRow> perfRows)
    {
        _cgpModalPerfMap.Clear();
        for (int i = 0; i < perfRows.Count; i++)
            if (!perfRows[i].Section && !perfRows[i].Skip) _cgpModalPerfMap.Add(i);

        _cgpModalConverted = CgpConvertPerfRows(perfRows);
        int convDrop = _cgpModalDrop >= 0 ? _cgpModalPerfMap.IndexOf(_cgpModalDrop) : -1;

        var sb = new System.Text.StringBuilder();
        sb.Append(_gpPicker?.Source).Append('|').Append(_gpPicker?.Level).Append('|').Append(_gpPicker?.Act).Append('|').Append(convDrop).Append('|').Append(CsRaster).Append('|');
        foreach (var r in _cgpModalConverted)
            sb.Append(r.Title).Append('\u0001').Append(r.Value).Append('\u0001').Append(r.Choice).Append('\u0001').Append(r.Number).Append(r.Disabled ? 'd' : ' ').Append('\u0002');
        string listKey = sb.ToString();

        if (listKey != v.ModalListKey)
        {
            v.ModalLay = CgpLayoutRows(_cgpModalConverted, convDrop, CgpModalListW, out var groups);
            v.ModalListKey = listKey;
            float H = v.ModalLay.Count == 0 ? 200 : v.ModalLay[^1].Y + v.ModalLay[^1].H;
            v.ModalListH = H;
            float pad = 16, R0 = CsRaster, lw = CgpModalListW + pad * 2, lh = H + pad * 2 + 40;
            int pw = (int)Math.Ceiling(lw * R0), ph = (int)Math.Ceiling(lh * R0);
            if (v.ModalListS == null || (int)v.ModalListS.Size.Width != pw || (int)v.ModalListS.Size.Height != ph)
            {
                v.ModalListS?.Dispose();
                v.ModalListS = G.Surface(pw, ph);
                ((CompositionSurfaceBrush)v.ModalList.Brush).Surface = v.ModalListS;
            }
            v.ModalList.Size = new Vector2(lw, lh);
            using var ds = CanvasComposition.CreateDrawingSession(v.ModalListS);
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad, pad) * Matrix3x2.CreateScale(CsRaster);
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            foreach (var (y0, y1, seps) in groups) CpCard(ds, 0, y0, CgpModalListW, y1 - y0, seps, 30);
            foreach (var (i, y, _) in v.ModalLay) CgpPaintRow(ds, _cgpModalConverted[i], i, y, CgpModalListW, convDrop);
        }
        else v.ModalLay = CgpLayoutRows(_cgpModalConverted, convDrop, CgpModalListW, out _);
    }

    private void CgpFocusModalList(CgpView v, List<PerfRow> perfRows, int perfFocus, bool snap)
    {
        int convIdx = Math.Max(0, _cgpModalPerfMap.IndexOf(_cgpModalDrop >= 0 ? _cgpModalDrop : perfFocus));
        var hit = v.ModalLay.FirstOrDefault(e => e.I == convIdx);
        float view = CgpModalListBottom - CgpModalListTop, max = Math.Max(0, v.ModalListH - view + 30), target = Math.Clamp(v.ModalScroll, 0, max);

        if (hit.H > 0 && !(v.ModalManual && _cgpTouchMode))
        {
            float top = hit.Y - 40, bottom = hit.Y + hit.H + 40;
            if (_cgpModalDrop >= 0 && convIdx < _cgpModalConverted.Count)
            {
                var (_, oy, _, oh) = CgpOptRect(CgpModalListW, CgpRowBaseH(_cgpModalConverted[convIdx]), hit.Y, _cgpModalDropSel);
                top = Math.Min(top, oy - 40);
                bottom = oy + oh + 60;
            }
            if (bottom > target + view) target = bottom - view;
            if (top < target) target = top;
            target = Math.Clamp(target, 0, max);
        }

        if (!v.ModalOffsetInit || snap)
        {
            v.ModalOffsetInit = true;
            v.ModalScroll = target;
            RwJump(v.ModalList, new Vector3(CgpModalListX - 16, CgpModalListTop - 16 - target, 0));
        }
        else if (MathF.Abs(target - v.ModalScroll) > 0.5f)
        {
            v.ModalScroll = target;
            var to = new Vector3(CgpModalListX - 16, CgpModalListTop - 16 - target, 0);
            if (Reduced) RwJump(v.ModalList, to); else v.ModalList.StartAnimation("Offset", RwSpring(to));
        }

        if (_cgpTouchMode || hit.H <= 0 || convIdx >= _cgpModalConverted.Count)
        {
            if (v.ModalCur.Shown) CurShow(v.ModalCur, false, 80);
            return;
        }

        bool first = !v.ModalCur.Shown || snap;
        if (_cgpModalDrop >= 0)
        {
            var (ox, oy, ow, oh) = CgpOptRect(CgpModalListW, CgpRowBaseH(_cgpModalConverted[convIdx]), hit.Y, _cgpModalDropSel);
            CurToCocoon(v.ModalCur, CgpModalListX + ox, CgpModalListTop + oy - target, ow, oh, 24, move: !first);
        }
        else
        {
            float baseH = CgpRowBaseH(_cgpModalConverted[convIdx]);
            CurToCocoon(v.ModalCur, CgpModalListX, CgpModalListTop + hit.Y - target, CgpModalListW, baseH, 30, move: !first);
        }
        if (!v.ModalCur.Shown) CurShow(v.ModalCur, true, 140, snap ? 80 : 20);
    }

    // ---------------- 手柄 / 键盘导航 ----------------

    private void CgpSetTouchMode(bool on)
    {
        if (_cgpTouchMode == on) return;
        _cgpTouchMode = on;
        if (_cgp is not { } v) return;
        if (on)
        {
            if (v.Cur.Shown) CurShow(v.Cur, false, 80);
            if (v.ModalCur.Shown) CurShow(v.ModalCur, false, 80);
        }
        else
        {
            v.Cur.Shown = false;
            v.ModalCur.Shown = false;
        }
    }

    private bool CgpMove(Direction dir)
    {
        if (!CgpActive) return false;
        CgpSetTouchMode(false);
        int d = dir is Direction.Up or Direction.Left ? -1 : 1;
        bool horiz = dir is Direction.Left or Direction.Right;

        if (_gpPicker is { } p)
        {
            if (p.Level == GpSubLevel.Chooser)
            {
                if (p.InTabs)
                {
                    if (horiz) { CgpSwitchTab(d); return true; }
                    if (dir == Direction.Down) { p.InTabs = false; CuePan(0.5f); CgpPaint(); return true; }
                    Cue(Sfx.Bump);
                    return true;
                }
                var cells = GpChooserCells(p.Tab);
                var c0 = CgpChooserCellRect(p.Tab, p.Cell, cells[p.Cell]);
                var from = new Vector2((float)(c0.X + c0.Width / 2), (float)(c0.Y + c0.Height / 2));
                int best = -1;
                double bestScore = double.MaxValue;
                for (int pass = 0; pass < 2 && best < 0; pass++)
                {
                    for (int i = 0; i < cells.Length; i++)
                    {
                        if (i == p.Cell) continue;
                        var r = CgpChooserCellRect(p.Tab, i, cells[i]);
                        var vec = new Vector2((float)(r.X + r.Width / 2), (float)(r.Y + r.Height / 2)) - from;
                        float along = dir switch { Direction.Right => vec.X, Direction.Left => -vec.X, Direction.Down => vec.Y, _ => -vec.Y };
                        float across = horiz ? MathF.Abs(vec.Y) : MathF.Abs(vec.X);
                        if (along <= 6f || (pass == 0 && across > along * 1.45f)) continue;
                        double score = along + across * 2.1;
                        if (score < bestScore) { bestScore = score; best = i; }
                    }
                }
                if (best >= 0) { p.Cell = best; CuePan(0.5f); CgpPaint(); }
                else if (dir == Direction.Up) { p.InTabs = true; CuePan(0.5f); CgpPaint(); }
                else Cue(Sfx.Bump);
                return true;
            }

            // Modal 列表（Key / Settings）
            var mrows = p.Level == GpSubLevel.Key ? GpKeyRows(p) : GpSettingsRows(p);
            if (_cgpModalDrop >= 0 && _cgpModalDrop < mrows.Count)
            {
                var opts = CpOpts(mrows[_cgpModalDrop])!;
                if (horiz) { Cue(Sfx.Bump); return true; }
                int to = _cgpModalDropSel + d;
                if (to < 0 || to >= opts.Length) { Cue(Sfx.Bump); return true; }
                _cgpModalDropSel = to;
                CuePan(0.5f);
                CgpPaint();
                return true;
            }

            int mf = p.Level == GpSubLevel.Key ? p.KeyFocus : p.SettingsFocus;
            if (!horiz)
            {
                for (int i = mf + d; i >= 0 && i < mrows.Count; i += d)
                {
                    if (!CpFocusable(mrows[i])) continue;
                    if (p.Level == GpSubLevel.Key) p.KeyFocus = i; else p.SettingsFocus = i;
                    if (_cgp != null) _cgp.ModalManual = false;
                    CuePan(0.5f);
                    CgpPaint();
                    return true;
                }
                Cue(Sfx.Bump);
                return true;
            }
            if (mf >= 0 && mf < mrows.Count && !mrows[mf].Disabled)
            {
                var mr = mrows[mf];
                if (CpIsSlider(mr)) { CpStep(mr, d); CgpPaint(); return true; }
                if (CpIsStepper(mr)) { Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown); mr.Step?.Invoke(d); CgpPaint(); return true; }
            }
            Cue(Sfx.Bump);
            return true;
        }

        // 主页面下拉展开中
        if (_cgpDrop >= 0 && _cgpDrop < _cgpRows.Count)
        {
            var opts = _cgpRows[_cgpDrop].Options!;
            if (horiz) { Cue(Sfx.Bump); return true; }
            int to = _cgpDropSel + d;
            if (to < 0 || to >= opts.Length) { Cue(Sfx.Bump); return true; }
            _cgpDropSel = to;
            CuePan(0.5f);
            CgpPaint();
            return true;
        }

        // 主页面左列分类
        if (_cgpZone == 0)
        {
            if (dir == Direction.Right)
            {
                if (_cgpRows.Count == 0) { Cue(Sfx.Bump); return true; }
                _cgpZone = 1;
                _cgpFocus = 0;
                if (_cgp != null) _cgp.Manual = false;
                Cue(Sfx.Nav);
                CgpPaint();
                return true;
            }
            if (dir == Direction.Left) { Cue(Sfx.Bump); return true; }
            int to = _cgpCat + d;
            if (to < 0 || to >= CgpCats.Length) { Cue(Sfx.Bump); return true; }
            _cgpCat = to;
            _cgpFocus = 0;
            _cgpDrop = -1;
            if (_cgp != null) { _cgp.Scroll = 0; _cgp.Manual = false; _cgp.OffsetInit = false; }
            CuePan(0.5f);
            CgpPaint();
            return true;
        }

        // 主页面右列
        if (!horiz)
        {
            int to = _cgpFocus + d;
            if (to < 0 || to >= _cgpRows.Count) { Cue(Sfx.Bump); return true; }
            _cgpFocus = to;
            if (_cgp != null) _cgp.Manual = false;
            CuePan(0.5f);
            CgpPaint();
            return true;
        }

        var row = _cgpRows[_cgpFocus];
        if (!row.Disabled && row.Kind == CgpRowKind.Slider)
        {
            CgpStepSlider(row, d);
            return true;
        }
        if (dir == Direction.Left)
        {
            _cgpZone = 0;
            _cgpDrop = -1;
            Cue(Sfx.Back);
            CgpPaint();
            return true;
        }
        Cue(Sfx.Bump);
        return true;
    }

    private void CgpStepSlider(CgpRow row, int d)
    {
        int next = Math.Clamp(row.Number + d * Math.Max(1, row.Step), row.Min, row.Max);
        if (next == row.Number) { Cue(Sfx.Bump); return; }
        float f = (next - row.Min) / (float)Math.Max(1, row.Max - row.Min);
        Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown, MathF.Pow(2, f - 0.5f));
        row.SetNumber?.Invoke(next);
    }

    private bool CgpActivate()
    {
        if (!CgpActive) return false;
        CgpSetTouchMode(false);

        if (_gpPicker is { } p)
        {
            if (p.Level == GpSubLevel.Chooser)
            {
                if (p.InTabs) { p.InTabs = false; Cue(Sfx.Nav); CgpPaint(); return true; }
                var cell = GpChooserCells(p.Tab)[p.Cell];
                CgpActivateChooserCell(p, cell);
                return true;
            }

            var mrows = p.Level == GpSubLevel.Key ? GpKeyRows(p) : GpSettingsRows(p);
            if (_cgpModalDrop >= 0 && _cgpModalDrop < mrows.Count)
            {
                var dr = mrows[_cgpModalDrop];
                int sel = _cgpModalDropSel;
                _cgpModalDrop = -1;
                Cue(Sfx.Positive);
                CpChoose(dr, sel);
                CgpPaint();
                return true;
            }

            int mf = p.Level == GpSubLevel.Key ? p.KeyFocus : p.SettingsFocus;
            if (mf < 0 || mf >= mrows.Count) return true;
            var mr = mrows[mf];
            if (mr.Disabled) { if (mr.Press != null) mr.Press(); else Cue(Sfx.Bump); return true; }
            if (CpIsPick(mr))
            {
                _cgpModalDrop = mf;
                _cgpModalDropSel = Math.Clamp(mr.Choice, 0, CpOpts(mr)!.Length - 1);
                Cue(Sfx.MenuIn);
                CgpPaint();
                return true;
            }
            if (CpIsToggle(mr))
            {
                int next = mr.Choice == 1 ? 0 : 1;
                Cue(next == 1 ? Sfx.ToggleOn : Sfx.ToggleOff);
                if (mr.Select != null) mr.Select(next); else mr.Press?.Invoke();
                CgpPaint();
                return true;
            }
            if (CpIsSlider(mr) || CpIsStepper(mr)) { Cue(Sfx.Bump); return true; }
            RowPress(mr);
            CgpPaint();
            return true;
        }

        // 主页面下拉已展开
        if (_cgpDrop >= 0 && _cgpDrop < _cgpRows.Count)
        {
            var dr = _cgpRows[_cgpDrop];
            int sel = _cgpDropSel;
            _cgpDrop = -1;
            Cue(Sfx.Positive);
            dr.Select?.Invoke(sel);
            CgpPaint();
            return true;
        }

        if (_cgpZone == 0)
        {
            if (_cgpRows.Count == 0) { Cue(Sfx.Bump); return true; }
            _cgpZone = 1;
            _cgpFocus = 0;
            if (_cgp != null) _cgp.Manual = false;
            Cue(Sfx.Nav);
            CgpPaint();
            return true;
        }

        if (_cgpFocus < 0 || _cgpFocus >= _cgpRows.Count) return true;
        var row = _cgpRows[_cgpFocus];
        if (row.Disabled) { Toast(row.Why ?? "当前暂不可用"); return true; }
        switch (row.Kind)
        {
            case CgpRowKind.Dropdown:
                _cgpDrop = _cgpFocus;
                _cgpDropSel = Math.Clamp(row.Choice, 0, row.Options!.Length - 1);
                Cue(Sfx.MenuIn);
                CgpPaint();
                break;
            case CgpRowKind.Toggle:
                Cue(row.Choice == 0 ? Sfx.ToggleOn : Sfx.ToggleOff);
                row.Press?.Invoke();
                break;
            case CgpRowKind.Slider:
                Cue(Sfx.Bump);
                break;
            default:
                row.Press?.Invoke();
                break;
        }
        return true;
    }

    private void CgpActivateChooserCell(GpPickerState p, GpCell cell)
    {
        if (GpIsSpecial(p.Source) && cell.Cmd.StartsWith("pad:")) { Toast("专用功能键使用系统动作或键盘鼠标指令"); return; }
        if (cell.Cmd.Length == 0 && cell.Code == 0) { Toast($"「{cell.Label}」暂未开放"); return; }
        if (p.Desktop && cell.Code == 0) { Toast($"桌面模式下手柄发不出「{cell.Label}」"); return; }
        if (p.Tab == 2 && GpIsModifier(cell))
        {
            if (!p.Chord.Remove(cell) && p.Chord.Count < 3) p.Chord.Add(cell);
            Cue(Sfx.Nav);
            CgpPaint();
            return;
        }

        bool wasDirect = p.DirectChooser;
        string srcName = GpSourceName(p.Source);
        string boundLabel = p.Chord.Count > 0 ? string.Join(" + ", p.Chord.Select(x => x.Label).Append(cell.Label)) : GpCellName(cell);
        GpChooseCommand(p, cell);
        Cue(Sfx.Positive);
        if (wasDirect)
        {
            _gpPicker = null;
            CgpPaint();
            Toast($"{srcName} 已设为 {boundLabel}");
        }
        else CgpPaint();
    }

    private bool CgpBack()
    {
        if (!CgpActive) return false;
        if (_gpPicker is { } p)
        {
            if (_cgpModalDrop >= 0) { _cgpModalDrop = -1; Cue(Sfx.Back); CgpPaint(); return true; }
            if (p.Level == GpSubLevel.Chooser && p.DirectChooser || p.Level == GpSubLevel.Key)
            {
                CgpCloseModal();
                return true;
            }
            p.Level = GpSubLevel.Key;
            p.Chord.Clear();
            p.InTabs = false;
            if (_cgp != null) { _cgp.ModalScroll = 0; _cgp.ModalManual = false; _cgp.ModalOffsetInit = false; }
            Cue(Sfx.Back);
            CgpPaint();
            return true;
        }
        if (_cgpDrop >= 0) { _cgpDrop = -1; Cue(Sfx.Back); CgpPaint(); return true; }
        if (_cgpZone == 1) { _cgpZone = 0; Cue(Sfx.Back); CgpPaint(); return true; }
        CloseGamepad();
        return true;
    }

    private bool CgpPadButton(bool isY)
    {
        if (!CgpActive) return false;

        if (_gpPicker is { } p)
        {
            if (!isY) return true;
            if (p.Level == GpSubLevel.Chooser)
            {
                // 键盘页已选修饰键时，按 Y 直接绑定单独修饰键（如单键 Ctrl / Shift / Alt）
                if (p.Tab == 2 && p.Chord.Count > 0)
                {
                    var last = p.Chord[^1];
                    p.Chord.RemoveAt(p.Chord.Count - 1);
                    bool wasDirect = p.DirectChooser;
                    string srcName = GpSourceName(p.Source);
                    string boundLabel = p.Chord.Count > 0 ? string.Join(" + ", p.Chord.Select(x => x.Label).Append(last.Label)) : last.Label;
                    GpChooseCommand(p, last);
                    Cue(Sfx.Positive);
                    if (wasDirect) { _gpPicker = null; CgpPaint(); Toast($"{srcName} 已设为 {boundLabel}"); }
                    else CgpPaint();
                    return true;
                }
                // 普通单键直接进选择器时，按右上 Y 进入完整多触发与高级设置页
                if (p.DirectChooser && !p.Desktop && !GpIsSpecial(p.Source))
                {
                    p.DirectChooser = false;
                    p.Level = GpSubLevel.Key;
                    p.KeyFocus = 1;
                    p.Chord.Clear();
                    p.InTabs = false;
                    if (_cgp != null) { _cgp.ModalScroll = 0; _cgp.ModalManual = false; _cgp.ModalOffsetInit = false; }
                    Cue(Sfx.MenuIn);
                    CgpPaint();
                    return true;
                }
                CgpBack();
            }
            return true;
        }

        // 主页面：按键映射行按 Y 直接打开该键的多触发配置页；震动页 X 测试震动、Y 恢复默认
        if (_cgpZone == 1 && isY && _cgpFocus >= 0 && _cgpFocus < _cgpRows.Count && _cgpRows[_cgpFocus].Secondary is { } sec)
        {
            sec();
            return true;
        }
        if (_cgpCat == 3)
        {
            if (isY) { _gpRumbleL = _gpRumbleR = 100; CgpPaint(); _ = GpWrite("restore_rumble"); }
            else _ = GpTestRumble();
        }
        return true;
    }

    private bool CgpSwitchTab(int delta)
    {
        if (!CgpActive) return false;
        if (_gpPicker is { Level: GpSubLevel.Chooser } p)
        {
            p.Tab = (p.Tab + delta + GpChooserTabs.Length) % GpChooserTabs.Length;
            p.Cell = 0;
            p.Chord.Clear();
            CgpPaint();
            return true;
        }
        return true;
    }

    // ---------------- Toast 提示胶囊 ----------------

    private bool CgpToast(string text)
    {
        if (!CgpActive || _cgp is not { } v) return false;
        float R = CsRaster, h = 84, pad = 30;
        float tw;
        using (var f0 = new CanvasTextFormat { FontFamily = RmFont(text), FontSize = 30, FontWeight = RsWt(text, 500), WordWrapping = CanvasWordWrapping.NoWrap })
        using (var l0 = new CanvasTextLayout(G.Device, text, f0, 1400, 100)) tw = (float)l0.LayoutBounds.Width;
        float w = Math.Min(1500, tw + 130), x = CsW / 2 - w / 2, y = CpBottom - h - 30;
        string kind = NoticeKind(text);
        using (var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.ToastS, v.Toast, x - pad, y - pad, w + pad * 2, h + pad * 2)))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad - x, pad - y) * Matrix3x2.CreateScale(R);
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            CpCard(ds, x, y, w, h, null, h / 2);
            var c1 = kind == "error" ? Color.FromArgb(255, 0xFF, 0x7A, 0x84) : kind == "warning" ? Color.FromArgb(255, 0xFF, 0xC1, 0x6B) : RwC1;
            var c2 = kind == "error" ? RmRed : kind == "warning" ? Color.FromArgb(255, 0xF0, 0x8A, 0x24) : RwC2;
            using (var br = new CanvasLinearGradientBrush(ds.Device, c1, c2) { StartPoint = new Vector2(x + 26, y + 22), EndPoint = new Vector2(x + 66, y + 62) })
                ds.FillCircle(x + 46, y + h / 2, 18, br);
            ds.FillCircle(x + 46, y + h / 2, 6, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
            using var f = new CanvasTextFormat { FontFamily = RmFont(text), FontSize = 30, FontWeight = RsWt(text, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
            ds.DrawText(text, new Rect(x + 84, y, w - 110, h), RmInk, f);
        }
        int gen = ++v.ToastGen;
        var home = v.Toast.Offset;
        if (Reduced) v.Toast.Opacity = 1;
        else
        {
            v.Toast.Offset = home + new Vector3(0, 24, 0);
            v.Toast.StartAnimation("Offset", CsV3(home, 220, new Bezier(0.2f, 0.8f, 0.2f, 1f)));
            v.Toast.StartAnimation("Opacity", CsS(1, 160, Bezier.EaseOut));
        }
        After(2400, () => { if (v.ToastGen == gen) v.Toast.StartAnimation("Opacity", CsS(0, 220, Bezier.EaseOut)); });
        return true;
    }

    // ---------------- 鼠标 / 触摸 / 滚轮 ----------------

    private sealed class CgpTouch
    {
        public uint Id;
        public bool Modal;
        public float X0, Y0, Scroll0, LastY, Vel;
        public long LastT;
        public bool Drag, Slider;
        public int Row = -1;
    }
    private CgpTouch? _cgpT;

    private void CgpHover(double sx, double sy)
    {
        if (!CgpActive || CgpModalOpen || _cgpT != null || _cgp is not { } v) return;
        var (x, y) = CpLocal(sx, sy);
        if (x < CpListX || x > CpListX + CpListW || y < CpTop || y > CpBottom) return;
        float ly = y - CpTop + v.Scroll;
        var hit = v.Lay.FirstOrDefault(l => ly >= l.Y && ly < l.Y + l.H);
        if (hit.H <= 0 || hit.I < 0 || hit.I >= _cgpRows.Count) return;
        string ks = _cgpRows[hit.I].KeySource;
        if (ks.Length > 0 && ks != _cgpActiveKey)
        {
            _cgpActiveKey = ks;
            CgpPaintPanelInner(v);
        }
    }

    private void CgpWheel(int delta)
    {
        if (!CgpActive || _cgp is not { } v) return;
        float step = (float)(delta / 120.0 * 112.0);
        if (_gpPicker is { } p)
        {
            if (p.Level == GpSubLevel.Chooser) return;
            float max = Math.Max(0, v.ModalListH - (CgpModalListBottom - CgpModalListTop) + 30);
            float to = Math.Clamp(v.ModalScroll - step, 0, max);
            v.ModalScroll = to;
            v.ModalManual = true;
            var vec = new Vector3(CgpModalListX - 16, CgpModalListTop - 16 - to, 0);
            if (Reduced) RwJump(v.ModalList, vec); else v.ModalList.StartAnimation("Offset", RwSpring(vec));
            return;
        }
        float maxMain = Math.Max(0, v.ListH - (CpBottom - CpTop) + 30);
        float toMain = Math.Clamp(v.Scroll - step, 0, maxMain);
        v.Scroll = toMain;
        v.Manual = true;
        var vecMain = new Vector3(CpListX - 16, CpTop - 16 - toMain, 0);
        if (Reduced) RwJump(v.List, vecMain); else v.List.StartAnimation("Offset", RwSpring(vecMain));
    }

    private bool CgpPointerDown(PointerRoutedEventArgs e, Microsoft.UI.Input.PointerPoint pt)
    {
        if (!CgpActive || _cgp is not { } v) return false;
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch && pt.Position.X <= EdgeBackPx) return false;
        var (x, y) = CpLocal(pt.Position.X, pt.Position.Y);
        CgpSetTouchMode(e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch);

        var t = new CgpTouch { Id = pt.PointerId, Modal = CgpModalOpen, X0 = x, Y0 = y, LastY = y, LastT = NowMs };
        if (t.Modal && _gpPicker is { Level: not GpSubLevel.Chooser })
        {
            v.ModalList.StopAnimation("Offset");
            v.ModalList.Offset = new Vector3(CgpModalListX - 16, CgpModalListTop - 16 - v.ModalScroll, 0);
            t.Scroll0 = v.ModalScroll;
            if (x >= CgpModalListX && x <= CgpModalListX + CgpModalListW && y >= CgpModalListTop && y <= CgpModalListBottom)
            {
                float ly = y - CgpModalListTop + v.ModalScroll;
                var hit = v.ModalLay.FirstOrDefault(l => ly >= l.Y && ly < l.Y + l.H);
                if (hit.H > 0 && hit.I < _cgpModalConverted.Count)
                {
                    t.Row = hit.I;
                    var cr = _cgpModalConverted[hit.I];
                    if (cr.Kind == CgpRowKind.Slider && !cr.Disabled)
                    {
                        var (_, sx0, sx1, _) = CpSliderX(44f, CgpModalListW - 34f);
                        float lx = x - CgpModalListX;
                        if (ly > hit.Y + hit.H - 90 && lx >= sx0 - 30 && lx <= sx1 + 30) { t.Slider = true; CgpDragSlider(cr, lx, sx0, sx1); }
                    }
                    if (!t.Slider) CgpPressRowFx(v.ModalPress, CgpModalListW, hit.Y, CgpRowBaseH(cr), true);
                }
            }
        }
        else if (!t.Modal)
        {
            v.List.StopAnimation("Offset");
            v.List.Offset = new Vector3(CpListX - 16, CpTop - 16 - v.Scroll, 0);
            t.Scroll0 = v.Scroll;
            if (x >= CpListX && x <= CpListX + CpListW && y >= CpTop && y <= CpBottom)
            {
                float ly = y - CpTop + v.Scroll;
                var hit = v.Lay.FirstOrDefault(l => ly >= l.Y && ly < l.Y + l.H);
                if (hit.H > 0 && hit.I < _cgpRows.Count)
                {
                    t.Row = hit.I;
                    var cr = _cgpRows[hit.I];
                    if (cr.Kind == CgpRowKind.Slider && !cr.Disabled)
                    {
                        var (_, sx0, sx1, _) = CpSliderX(44f, CpListW - 34f);
                        float lx = x - CpListX;
                        if (ly > hit.Y + hit.H - 90 && lx >= sx0 - 30 && lx <= sx1 + 30) { t.Slider = true; CgpDragSlider(cr, lx, sx0, sx1); }
                    }
                    if (!t.Slider) CgpPressRowFx(v.Press, CpListW, hit.Y, CgpRowBaseH(cr), true);
                }
            }
        }

        _cgpT = t;
        _rootEl.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private void CgpDragSlider(CgpRow r, float lx, float x0, float x1)
    {
        float f = Math.Clamp((lx - x0) / Math.Max(1f, x1 - x0), 0, 1);
        int inc = Math.Max(1, r.Step);
        int n = Math.Clamp(r.Min + (int)MathF.Round(f * (r.Max - r.Min) / inc) * inc, r.Min, r.Max);
        if (n == r.Number) return;
        Cue(n > r.Number ? Sfx.SliderUp : Sfx.SliderDown, MathF.Pow(2, f - 0.5f));
        r.SetNumber?.Invoke(n);
    }

    private void CgpPressRowFx(SpriteVisual press, float w, float rowY, float rowH, bool on)
    {
        if (on)
        {
            press.Offset = new Vector3(16, 16 + rowY, 0);
            press.Size = new Vector2(w, rowH);
            press.StopAnimation("Opacity");
            press.Opacity = 1;
        }
        else press.StartAnimation("Opacity", CsS(0, 160, Bezier.EaseOut));
    }

    private bool CgpPointerMove(PointerRoutedEventArgs e)
    {
        if (_cgpT is not { } t || e.Pointer.PointerId != t.Id) return false;
        e.Handled = true;
        if (_cgp is not { } v) return true;
        var pos = e.GetCurrentPoint(_rootEl).Position;
        var (x, y) = CpLocal(pos.X, pos.Y);

        if (t.Slider)
        {
            if (t.Modal && t.Row >= 0 && t.Row < _cgpModalConverted.Count)
            {
                var (_, sx0, sx1, _) = CpSliderX(44f, CgpModalListW - 34f);
                CgpDragSlider(_cgpModalConverted[t.Row], x - CgpModalListX, sx0, sx1);
            }
            else if (!t.Modal && t.Row >= 0 && t.Row < _cgpRows.Count)
            {
                var (_, sx0, sx1, _) = CpSliderX(44f, CpListW - 34f);
                CgpDragSlider(_cgpRows[t.Row], x - CpListX, sx0, sx1);
            }
            return true;
        }

        if (t.Modal && _gpPicker is { Level: GpSubLevel.Chooser }) return true;
        if (!t.Modal && t.X0 < CpListX) return true;
        if (!t.Drag && MathF.Abs(y - t.Y0) < 18f) return true;

        if (!t.Drag)
        {
            t.Drag = true;
            CgpPressRowFx(t.Modal ? v.ModalPress : v.Press, 0, 0, 0, false);
        }

        long now = NowMs;
        float dt = Math.Max(1, now - t.LastT) / 1000f;
        t.Vel = t.Vel * 0.4f + (y - t.LastY) / dt * 0.6f;
        t.LastY = y;
        t.LastT = now;

        if (t.Modal)
        {
            float max = Math.Max(0, v.ModalListH - (CgpModalListBottom - CgpModalListTop) + 30);
            float s = t.Scroll0 - (y - t.Y0);
            if (s < 0) s /= 3f; else if (s > max) s = max + (s - max) / 3f;
            v.ModalScroll = s;
            v.ModalManual = true;
            v.ModalList.Offset = new Vector3(CgpModalListX - 16, CgpModalListTop - 16 - s, 0);
        }
        else
        {
            float max = Math.Max(0, v.ListH - (CpBottom - CpTop) + 30);
            float s = t.Scroll0 - (y - t.Y0);
            if (s < 0) s /= 3f; else if (s > max) s = max + (s - max) / 3f;
            v.Scroll = s;
            v.Manual = true;
            v.List.Offset = new Vector3(CpListX - 16, CpTop - 16 - s, 0);
        }
        return true;
    }

    private bool CgpPointerUp(PointerRoutedEventArgs e, bool canceled = false)
    {
        if (_cgpT is not { } t || e.Pointer.PointerId != t.Id) return false;
        _cgpT = null;
        _rootEl.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (_cgp is not { } v) return true;

        CgpPressRowFx(v.Press, 0, 0, 0, false);
        CgpPressRowFx(v.ModalPress, 0, 0, 0, false);
        if (t.Slider || canceled) return true;

        if (t.Drag)
        {
            if (t.Modal)
            {
                float max = Math.Max(0, v.ModalListH - (CgpModalListBottom - CgpModalListTop) + 30);
                float target = Math.Clamp(v.ModalScroll - t.Vel * 0.32f, 0, max);
                v.ModalScroll = target;
                CgpFlingVisual(v.ModalList, new Vector3(CgpModalListX - 16, CgpModalListTop - 16 - target, 0), t.Vel);
            }
            else
            {
                float max = Math.Max(0, v.ListH - (CpBottom - CpTop) + 30);
                float target = Math.Clamp(v.Scroll - t.Vel * 0.32f, 0, max);
                v.Scroll = target;
                CgpFlingVisual(v.List, new Vector3(CpListX - 16, CpTop - 16 - target, 0), t.Vel);
            }
            return true;
        }

        float x = t.X0, y = t.Y0;
        MarkInput();
        if (t.Modal && _gpPicker is { } p) CgpTapModal(v, p, x, y, t.Row);
        else CgpTapMain(v, x, y, t.Row);
        return true;
    }

    private void CgpFlingVisual(Visual vis, Vector3 to, float vel)
    {
        if (Reduced) { RwJump(vis, to); return; }
        var a = G.C.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(1, to, G.Ease(new Bezier(0.15f, 0.7f, 0.25f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(Math.Clamp(MathF.Abs(vel) * 0.5f, 220, 650));
        vis.StartAnimation("Offset", a);
    }

    private void CgpTapModal(CgpView v, GpPickerState p, float x, float y, int rowIdx)
    {
        // 点击面板外暗色遮罩或左上「B ←」胶囊
        if (x < RsX || x > RsX + RsW || y < RsY || y > RsY + RsH || (x >= 120 && x <= 280 && y >= 94 && y <= 186))
        {
            CgpBack();
            return;
        }
        // 右上紫色「Y」胶囊
        if (p.Level == GpSubLevel.Chooser && x >= 1644 && x <= 1796 && y >= 94 && y <= 186)
        {
            CgpPadButton(isY: true);
            return;
        }

        if (p.Level == GpSubLevel.Chooser)
        {
            // 分段页签栏
            if (x >= CgpTabBarX && x <= CgpTabBarX + CgpTabBarW && y >= CgpTabBarY && y <= CgpTabBarY + CgpTabBarH)
            {
                if (x < CgpTabBarX + 82) { Cue(Sfx.Tab); CgpSwitchTab(-1); return; }
                if (x > CgpTabBarX + CgpTabBarW - 82) { Cue(Sfx.Tab); CgpSwitchTab(1); return; }
                for (int i = 0; i < GpChooserTabs.Length; i++)
                {
                    if (CgpChooserTabRect(i).Contains(new Windows.Foundation.Point(x, y)))
                    {
                        if (p.Tab != i) { p.Tab = i; p.Cell = 0; p.Chord.Clear(); Cue(Sfx.Tab); CgpPaint(); }
                        return;
                    }
                }
            }

            // 键帽网格 / 系统动作卡片
            var cells = GpChooserCells(p.Tab);
            for (int i = 0; i < cells.Length; i++)
            {
                if (CgpChooserCellRect(p.Tab, i, cells[i]).Contains(new Windows.Foundation.Point(x, y)))
                {
                    p.InTabs = false;
                    p.Cell = i;
                    CgpActivateChooserCell(p, cells[i]);
                    return;
                }
            }
            return;
        }

        // Modal 列表（Key / Settings）
        var mrows = p.Level == GpSubLevel.Key ? GpKeyRows(p) : GpSettingsRows(p);
        if (_cgpModalDrop >= 0 && _cgpModalDrop < mrows.Count)
        {
            int convOpen = _cgpModalPerfMap.IndexOf(_cgpModalDrop);
            var dh = v.ModalLay.FirstOrDefault(l => l.I == convOpen);
            float ly0 = y - CgpModalListTop + v.ModalScroll;
            var (ox, oy, ow, _) = CgpOptRect(CgpModalListW, CgpRowBaseH(_cgpModalConverted[convOpen]), dh.Y, 0);
            int k = (int)MathF.Floor((ly0 - oy) / CpOptH);
            var opts = CpOpts(mrows[_cgpModalDrop])!;
            if (dh.H > 0 && x - CgpModalListX >= ox && x - CgpModalListX <= ox + ow && k >= 0 && k < opts.Length)
            {
                var dr = mrows[_cgpModalDrop];
                _cgpModalDrop = -1;
                Cue(Sfx.Positive);
                CpChoose(dr, k);
                CgpPaint();
            }
            else { _cgpModalDrop = -1; Cue(Sfx.Back); CgpPaint(); }
            return;
        }

        if (rowIdx < 0 || rowIdx >= _cgpModalConverted.Count || rowIdx >= _cgpModalPerfMap.Count) return;
        int perfIdx = _cgpModalPerfMap[rowIdx];
        if (p.Level == GpSubLevel.Key) p.KeyFocus = perfIdx; else p.SettingsFocus = perfIdx;
        var cr = _cgpModalConverted[rowIdx];
        float lx = x - CgpModalListX;

        if (cr.Disabled) { if (cr.Press != null) cr.Press(); else Cue(Sfx.Bump); CgpPaint(); return; }
        if (cr.Kind == CgpRowKind.Slider)
        {
            var (smx, _, _, spx) = CpSliderX(44f, CgpModalListW - 34f);
            if (MathF.Abs(lx - smx) < 42f) CgpStepSlider(cr, -1);
            else if (MathF.Abs(lx - spx) < 42f) CgpStepSlider(cr, 1);
            CgpPaint();
            return;
        }
        if (cr.Kind == CgpRowKind.Stepper)
        {
            float R = CgpModalListW - 34f;
            Cue(Sfx.Nav);
            if (lx > R - 96f) cr.StepBy?.Invoke(1);
            else if (lx > R - 420f) cr.StepBy?.Invoke(-1);
            return;
        }
        v.ModalManual = true;
        CgpActivate();
    }

    private void CgpTapMain(CgpView v, float x, float y, int rowIdx)
    {
        if (x < 200 && y < 130) { CgpBack(); return; } // 左上「B ←」

        if (_cgpDrop >= 0 && _cgpDrop < _cgpRows.Count)
        {
            var dh = v.Lay.FirstOrDefault(l => l.I == _cgpDrop);
            float ly0 = y - CpTop + v.Scroll;
            var (ox, oy, ow, _) = CgpOptRect(CpListW, CgpRowBaseH(_cgpRows[_cgpDrop]), dh.Y, 0);
            int k = (int)MathF.Floor((ly0 - oy) / CpOptH);
            var opts = _cgpRows[_cgpDrop].Options!;
            if (dh.H > 0 && x - CpListX >= ox && x - CpListX <= ox + ow && k >= 0 && k < opts.Length)
            {
                var dr = _cgpRows[_cgpDrop];
                _cgpDrop = -1;
                Cue(Sfx.Positive);
                dr.Select?.Invoke(k);
                CgpPaint();
            }
            else { _cgpDrop = -1; Cue(Sfx.Back); CgpPaint(); }
            return;
        }

        // 左列分类点击
        if (x >= CpLeftX && x <= CpLeftX + CpLeftW && y >= CpTop && y < CpTop + CgpCats.Length * CgpSlot)
        {
            int c = (int)((y - CpTop) / CgpSlot);
            if (c >= 0 && c < CgpCats.Length)
            {
                _cgpCat = c;
                _cgpZone = 0;
                _cgpFocus = 0;
                _cgpDrop = -1;
                v.Scroll = 0;
                v.Manual = false;
                v.OffsetInit = false;
                Cue(Sfx.Nav);
                CgpPaint();
            }
            return;
        }

        if (rowIdx < 0 || rowIdx >= _cgpRows.Count) return;
        _cgpFocus = rowIdx;
        _cgpZone = 1;
        var row = _cgpRows[rowIdx];
        if (row.KeySource.Length > 0) _cgpActiveKey = row.KeySource;
        if (row.Disabled) { Toast(row.Why ?? "当前暂不可用"); CgpPaint(); return; }

        float lx = x - CpListX;
        if (row.Kind == CgpRowKind.Slider)
        {
            var (smx, _, _, spx) = CpSliderX(44f, CpListW - 34f);
            if (MathF.Abs(lx - smx) < 42f) CgpStepSlider(row, -1);
            else if (MathF.Abs(lx - spx) < 42f) CgpStepSlider(row, 1);
            CgpPaint();
            return;
        }

        v.Manual = true;
        if (row.Kind == CgpRowKind.Dropdown)
        {
            _cgpDrop = rowIdx;
            _cgpDropSel = Math.Clamp(row.Choice, 0, row.Options!.Length - 1);
            Cue(Sfx.MenuIn);
            CgpPaint();
        }
        else if (row.Kind == CgpRowKind.Toggle)
        {
            Cue(row.Choice == 0 ? Sfx.ToggleOn : Sfx.ToggleOff);
            row.Press?.Invoke();
        }
        else
        {
            Cue(Sfx.Nav);
            row.Press?.Invoke();
        }
    }
}
