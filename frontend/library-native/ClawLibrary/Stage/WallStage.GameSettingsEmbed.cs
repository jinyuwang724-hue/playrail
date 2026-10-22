// 游戏设置里那两块不是设置行的内容，都直接画在游戏设置页上（同一张页、同一个标题和左栏，不开别的页）：
//   手柄：每个键一行，A 打开手柄页同一套按键选择（触发方式 / 选择指令 / 触发设置，WallStage.GamepadPicker.cs），
//         画在右栏的位置上；改的是这款游戏自己的那份布局（软件输入层，按 exe 名），不碰固件。
//   封面：封面页右栏（四种图的候选网格 + 封面概览，WallStage.AddGamePage.cs）原样画在右栏，换一张就马上存。
// 右栏这时盖一层 _stOver（整页 1280×800 设计 px，只在右栏里画），设置行那层先藏起来。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition;
using Windows.Foundation;
using Rect = Windows.Foundation.Rect;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private SpriteVisual? _stOver;

    /// <summary>游戏设置 › 手柄里正在选某个键。</summary>
    private bool GsPadPicking => _stOpen && _gsItem != null && _gpPicker != null && !_gpOpen;
    /// <summary>游戏设置 › 封面：右栏是封面网格。</summary>
    private bool GsArtMode => _stOpen && _gsItem != null && _stCat == 3 && _agEmbed && _agDraft != null && !_agOpen;
    private bool GsOverlay => GsPadPicking || GsArtMode;

    /// <summary>手柄页的按键选择按它自己的位置画（行从 GpPickX 起、选择指令以 GpCx 居中），挪到设置页右栏。</summary>
    /// <summary>封面网格整体往下挪一点：页签和左栏的「分类」在同一条线上。</summary>
    private const float GsArtDy = 12;

    private float GsPadDx => _gpPicker?.Level == GpSubLevel.Chooser ? PerfRightX + PerfRightW / 2 - GpCx : PerfRightX - GpPickX;

    // ---------------- 手柄 ----------------

    private string? _gsPadFor;

    /// <summary>读这款游戏的布局（和手柄固件现在的映射，键名旁边显示用）。</summary>
    private void GsPadEnsure(string exe)
    {
        if (_gsPadFor == exe) return;
        _gsPadFor = exe;
        _gpGame = exe;
        _gpLayout = null;
        _ = GpRefresh();
    }

    private void GsPadReset()
    {
        _gsPadFor = null;
        if (_gpOpen) return;
        _gpPicker = null;
        _gpGame = null;
        _gpLayout = null;
    }

    private void GsPadOpen(string source)
    {
        SettingsResetPointer();
        _gpPickScroll = 0;
        _gpPicker = new GpPickerState { Source = source };
        _stZone = 1;
        PaintSt();
    }

    private void BuildGsPad(JsonObject? s, string wait, string noExe)
    {
        var pad = s?["pad"] as JsonObject;
        string? padExe = Str(pad?["exe"]);
        _stRows.Add(new PerfRow { Section = true, Title = "手柄档案" });
        if (s == null) { _stRows.Add(StInfo("按键映射", wait)); return; }
        if (padExe == null) { _stRows.Add(StInfo("按键映射", noExe)); return; }
        bool custom = Bool(pad?["custom"]);
        _stRows.Add(StPick("按键映射", new[] { "全局", "游戏配置档" }, custom ? 1 : 0, i =>
        {
            GsLocal("pad", "custom", JsonValue.Create(i == 1));
            _ = GsSet("padCustom", JsonValue.Create(i == 1), i == 1 ? "已从全局映射复制一份，进这款游戏时自动换上" : "这款游戏改回全局映射");
        }, 0));
        var presets = (pad?["presets"] as JsonArray)?.Select(Str).OfType<string>().ToList() ?? new();
        if (!custom)
        {
            if (_gsPadFor != null) GsPadReset();
            return;
        }
        GsPadEnsure(padExe);

        // 预设：存下来的配置档，选一个就整份换进这款游戏
        string cur = Str(pad?["preset"]) ?? "";
        var names = new List<string> { "无" };
        names.AddRange(presets);
        int at = Math.Max(0, names.IndexOf(cur));
        _stRows.Add(new PerfRow { Section = true, Title = "预设" });
        if (presets.Count > 0)
            _stRows.Add(StPick("预设", names.ToArray(), at, i =>
            {
                if (i == 0) return;
                string name = names[i];
                GsLocal("pad", "preset", JsonValue.Create(name));
                _ = GsSet("padPreset", JsonValue.Create(name), $"已换成预设「{name}」");
            }));
        else _stRows.Add(StInfo("预设", "还没有"));
        string saveAs = cur.Length > 0 ? cur : _gsItem?.Title ?? "";
        _stRows.Add(new PerfRow
        {
            Title = "保存为预设", Value = "›",
            Press = () => StEditText(saveAs, v =>
            {
                string name = v.Trim();
                if (name.Length == 0) return;
                GsLocal("pad", "preset", JsonValue.Create(name));
                _ = GsSet("padSavePreset", JsonValue.Create(name), $"已存成预设「{name}」");
            }),
        });
        if (at > 0)
            _stRows.Add(StAction("删除这个预设", "gspresetdel", "", () =>
            {
                if (!StConfirm("gspresetdel")) return;
                GsLocal("pad", "preset", JsonValue.Create(""));
                _ = GsSet("padDeletePreset", JsonValue.Create(cur), $"已删除预设「{cur}」");
            }));

        // 按键：这款游戏自己的布局里每个键做什么，A 进按键选择
        _stRows.Add(new PerfRow { Section = true, Title = "按键" });
        if (_gpLayout == null) { _stRows.Add(StInfo("按键", "正在读取…")); return; }
        foreach (var src in GpLayoutSources.Where(x => x != "Guide"))
        {
            string source = src;
            _stRows.Add(new PerfRow { Title = GpSourceName(src), Value = GpKeySummary(src) + "  ›", Press = () => GsPadOpen(source) });
        }
    }

    // ---------------- 封面 ----------------

    private void GsArtShow(AgDraft d)
    {
        _agDraft = d;
        _agPage = AgPage.Art;
        _agTab = AgTabOverview;
        _agZone = 1;
        _agIndex = 0;
        _agScroll = 0;
        _agManualScroll = false;
        _agOffKind = null;
        if (_stOpen && _stCat == 3) Repaint();
    }

    private void GsArtReset()
    {
        if (!_agEmbed) return;
        _agEmbed = false;
        _agOffKind = null;
        if (!_agOpen) _agDraft = null;
    }

    /// <summary>封面页的右栏：页签（＋「Steam 官方封面」）和网格 / 封面概览，位置和封面页一样。</summary>
    private void GsPaintArt(CanvasDrawingSession ds, AgDraft d)
    {
        int zone = _agZone;
        if (_stZone != 1) _agZone = 0;   // 焦点在左栏：右栏不画焦点框
        try
        {
            var counts = AgTabNames.Select((t, i) => (t, i < AgTabOverview
                ? d.Pending.Contains(AgTabKinds[i]) ? (string?)"…" : d.Cands[AgTabKinds[i]].Count(c => c.Url != null && !c.Url.StartsWith("file:")).ToString()
                : null)).ToList();
            AgTabsAt(ds, counts, _agTab, AgRightX - 12, AgBandY);
            if (_agTab < AgTabOverview && AgHasOfficial(d))
            {
                var f = TextKit.Ui(500);
                float tw = PWidth(ds, "Steam 官方封面", f, 14), bw = tw + 32;
                var b = new Rect(AgRightX + AgRightW - bw, AgBandY - 16, bw, 32);
                bool bf = _agZone == 1 && _agIndex == AgOffBtn;
                ds.FillRoundedRectangle(b, 10, 10, bf ? Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.96) : Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.12));
                PText(ds, "Steam 官方封面", f, 14, bf ? Gfx.Rgba(9, 9, 11) : CcText, (float)b.X, AgBandY, (float)bw, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center);
                _agHits.Add((b, 1, AgOffBtn, () => AgOpenOfficial(AgTabKinds[_agTab])));
            }
            using (ds.CreateLayer(1, new Rect(AgRightX - 8, AgContentTop - 8, AgRightW + 16, AgContentBottom - AgContentTop + 16)))
            {
                if (_agTab < AgTabOverview) AgPaintGrid(ds, d, AgTabKinds[_agTab]);
                else AgPaintOverview(ds, d);
            }
        }
        finally { _agZone = zone; }
    }

    /// <summary>测试用（Bench --state=gs0..gs4 --game=…）：直接打开这款游戏的设置，停在某个分类。</summary>
    internal void GameSettingsTestState(string id, int cat)
    {
        if (Lookup(id) is not { } item) { Log.Warn($"gs test: no game {id}"); return; }
        OpenGameSettings(item);
        _stCat = cat;
        Repaint();
    }

    // ---------------- 画 ----------------

    /// <summary>PaintSt 里：右栏换成按键选择 / 封面网格。</summary>
    private void PaintGsOverlay(float k, float raster)
    {
        bool over = GsOverlay;
        _stRightView.IsVisible = !over;
        if (!over) { if (_stOver != null) _stOver.IsVisible = false; return; }
        if (_stOver == null)
        {
            _stOver = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            _st!.Children.InsertAtTop(_stOver);
        }
        _stOver.IsVisible = true;
        if (GsPadPicking)
        {
            _gpTargets.Clear();
            float dx = GsPadDx;
            CcSet(_stOver, CcDraw(1280, 800, raster, ds =>
            {
                ds.Transform = Matrix3x2.CreateTranslation(dx, 0) * ds.Transform;
                PaintGpPicker(ds, PerfAccent);
            }));
        }
        else
        {
            _agHits.Clear();
            _agTabHits.Clear();
            var d = _agDraft!;
            CcSet(_stOver, CcDraw(1280, 800, raster, ds =>
            {
                ds.Transform = Matrix3x2.CreateTranslation(0, GsArtDy) * ds.Transform;
                GsPaintArt(ds, d);
                if (_agOffKind == null) return;
                ds.FillRectangle(0, 0, 1280, 800, Gfx.Rgba(0, 0, 0, 0.55));
                _agHits.Clear();
                _agTabHits.Clear();
                AgPaintOfficial(ds, d);
            }));
        }
        _stOver.Size = new Vector2(1280 * k, 800 * k);
        _stOver.Offset = Vector3.Zero;
    }

    // ---------------- 输入 ----------------

    /// <summary>手柄 / 封面嵌着时的方向键；false = 不归这里（照常走设置页）。</summary>
    private bool GsOverlayMove(Direction dir)
    {
        if (GsPadPicking) { GpPickerMove(dir); return true; }
        if (!GsArtMode || _stZone != 1) return false;
        var d = _agDraft!;
        if (_agOffKind != null) return true;
        int index = _agIndex;
        int dx = dir == Direction.Left ? -1 : dir == Direction.Right ? 1 : 0, dy = dir == Direction.Up ? -1 : dir == Direction.Down ? 1 : 0;
        _agManualScroll = false;
        AgMoveArt(d, dx, dy);
        if (_agZone == 0) { _agZone = 1; _agIndex = index; _stZone = 0; }   // 网格最左再往左 = 回左栏
        else if (_agZone == 2) { _agZone = 1; _agIndex = index; }          // 嵌着时没有「完成」
        NavHaptic("move");
        PaintSt();
        return true;
    }

    /// <summary>从左栏进右栏：封面落在正在用的那张。</summary>
    private bool GsOverlayEnter()
    {
        if (!GsArtMode) return false;
        var d = _agDraft!;
        int n = AgRightCount();
        _agZone = 1;
        _agIndex = n > 0 ? Math.Min(AgRightDefault(d), n - 1) : _agTab != AgTabOverview && AgHasOfficial(d) ? AgOffBtn : 0;
        _stZone = 1;
        PaintSt();
        return true;
    }

    private bool GsOverlayActivate()
    {
        if (GsPadPicking) { GpPickerActivate(); return true; }
        if (!GsArtMode) return false;
        if (_stZone != 1) return GsOverlayEnter();
        var d = _agDraft!;
        if (_agOffKind != null) { AgUseOfficial(d, _agOffKind); return true; }
        NavHaptic("select");
        AgRightPress(d, _agIndex);
        return true;
    }

    private bool GsOverlayBack()
    {
        if (GsPadPicking) { GpPickerBack(); return true; }
        if (!GsArtMode || _stZone != 1) return false;
        if (_agOffKind != null) { AgCloseOfficial(); return true; }
        _stZone = 0;
        PaintSt();
        return true;
    }

    private bool GsOverlayTab(int delta)
    {
        if (GsPadPicking) { GpSwitchTab(delta); return true; }
        if (!GsArtMode) return false;
        bool inGrid = _stZone == 1;
        AgSwitchTab(delta);
        if (_agZone == 0) { _agZone = 1; _agIndex = 0; if (!inGrid) _stZone = 0; }
        PaintSt();
        return true;
    }

    /// <summary>点按：按键选择的页签 / 键格 / 行，封面的页签 / 图。</summary>
    private bool GsOverlayClick(double sx, double sy)
    {
        if (!GsOverlay) return false;
        float x = (float)sx / _ccK, y = (float)sy / _ccK;
        if (x < PerfRightX - 16) return false;   // 左栏照常
        if (GsPadPicking)
        {
            var p = _gpPicker!;
            float px = x - GsPadDx;
            if (p.Level == GpSubLevel.Chooser)
            {
                for (int i = _gpTargets.Count - 1; i >= 0; i--)
                {
                    var t = _gpTargets[i];
                    if (!t.Contains(px, y)) continue;
                    if (t.Kind == SettingsTargetKind.Tab) { GpSwitchTab(t.Row - p.Tab); p.InTabs = false; PaintSt(); }
                    else if (t.Kind == SettingsTargetKind.Cell) { p.Cell = t.Row; p.InTabs = false; GpPickerActivate(); }
                    return true;
                }
                return true;
            }
            double lx = px - GpPickX, ly = y - 196 + _gpPickScroll;
            for (int i = _settingsTargets.Count - 1; i >= 0; i--)
            {
                var t = _settingsTargets[i];
                if (!t.Contains(lx, ly)) continue;
                if (p.Level == GpSubLevel.Key) p.KeyFocus = t.Row; else p.SettingsFocus = t.Row;
                GpPickerActivate();
                PaintSt();
                return true;
            }
            return true;
        }
        var d = _agDraft!;
        var pt = new Point(x, y - GsArtDy);
        if (_agOffKind != null)
        {
            if (_agOffUse.Contains(pt)) AgUseOfficial(d, _agOffKind);
            else if (!_agOffPanel.Contains(pt)) AgCloseOfficial();
            return true;
        }
        foreach (var (r, i) in _agTabHits)
            if (r.Contains(pt)) { _stZone = 1; GsOverlayTab(i - _agTab); return true; }
        foreach (var h in _agHits)
            if (h.R.Contains(pt))
            {
                _stZone = 1;
                _agZone = h.Zone;
                _agIndex = h.Index;
                h.Run();
                PaintSt();
                return true;
            }
        return true;
    }
}
