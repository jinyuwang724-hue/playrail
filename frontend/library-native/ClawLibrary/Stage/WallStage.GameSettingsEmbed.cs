// 游戏设置里那两块不是设置行的内容，都直接画在游戏设置页上（同一张页、同一个标题和左栏，不开别的页）：
//   手柄：设置行（全局 / 游戏配置档、预设）下面是手柄页按键映射那张图（C8EX 线稿 + 两侧和底部每个键映射到什么，
//         WallStage.GamepadMap.cs 的布局，缩小放进右栏）；焦点在图上走，A 打开手柄页同一套按键选择（WallStage.GamepadPicker.cs），
//         改的是这款游戏自己的那份布局（软件输入层，按 exe 名），不碰固件。
//   封面：只显示现在用着的五种图（封面页的「封面概览」，WallStage.AddGamePage.cs），A 从本地换一张，马上存。
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
    /// <summary>游戏设置 › 手柄（游戏配置档）：设置行下面画按键映射图。</summary>
    private bool GsPadMapMode => _stOpen && _gsItem != null && _stCat == 2 && _gpPicker == null && !_gpOpen && _gsPadFor != null && _gpLayout != null && _stBrowse == null;

    /// <summary>手柄页的按键选择按它自己的位置画（行从 GpPickX 起、选择指令以 GpCx 居中），挪到设置页右栏（分组页没有左边的分组列，一张列表）。</summary>
    /// <summary>
    /// 封面概览（封面页右栏 428 起、812 宽的排法）挪进游戏设置的右栏并放大 1.15 倍：
    /// 第一行小字（封面页里 y 160）和左栏的「分类」在同一条线上（124），左边对齐右栏。
    /// </summary>
    private const float GsArtScale = 1.15f;
    private Matrix3x2 GsArtMatrix => Matrix3x2.CreateTranslation(-AgRightX, -160) * Matrix3x2.CreateScale(GsArtScale) * Matrix3x2.CreateTranslation(StRightX, 124);

    private float GsPadDx => _gpPicker?.Level == GpSubLevel.Chooser ? StRightX + StRightW / 2 - GpCx : StRightX - GpPickX;

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
        _gsMapFocus = -1;
        if (_gpOpen) return;
        _gpPicker = null;
        _gpGame = null;
        _gpLayout = null;
    }

    private void GsPadOpen(string source)
    {
        SettingsResetPointer();
        _gpPickScroll = 0;
        _gpPicker = GpGroupState(source, false);
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

        // 按键：下面画映射图（GsPaintPadMap），这里只在还没读到时占一行
        if (_gpLayout == null) { _stRows.Add(new PerfRow { Section = true, Title = "按键" }); _stRows.Add(StInfo("按键", "正在读取…")); }
        else if (!_gpArtLoaded) _ = GsPadArt();
    }

    private async Task GsPadArt()
    {
        await GpLoadArt();
        if (_stOpen && _gsItem != null && _stCat == 2) PaintSt();
    }

    // ---------------- 手柄：按键映射图 ----------------

    /// <summary>图上焦点：-1 = 焦点在上面的设置行。</summary>
    private int _gsMapFocus = -1;
    private readonly List<GpItem> _gsMapItems = new();
    private float GsMapTop => PerfTop + RowsHeight(_stRows) + 8;

    private readonly List<(string Title, float X, float Y, float W)> _gsMapHeads = new();

    /// <summary>
    /// 映射图接在设置行下面，焦点进图后设置行（全局 / 游戏配置档、预设）照常显示；
    /// 万一焦点格子在屏幕外，整张图往上滚（_gsMapScroll，现在的排法放得下，一般是 0）。
    /// </summary>
    private float _gsMapScroll;

    /// <summary>右栏（428…1240）里：线稿 0.58（441 宽）居中，两侧各一列贴着它，下面四栏每栏 203。</summary>
    private GpMapGeo GsMapGeo
    {
        get
        {
            float t = GsMapTop - _gsMapScroll, cx = StRightX + StRightW / 2, half = GpW * 0.66f / 2;
            return new(0.66f, cx, t + 116, cx - half - 14, cx + half + 14, t + 30, 44, 180, t + 262, t + 296, 34, StRightX, StRightW / 4f);
        }
    }

    /// <summary>手柄页映射页的那些格子，去掉快速设置键、Claw 键、摇杆互换这种不归游戏布局管的。</summary>
    private void GsBuildMapItems()
    {
        var keep = _gpItems.ToList();
        _gpItems.Clear();
        BuildGpMapItems(false, GsMapGeo, s => !GpIsSpecial(s), _gsMapHeads);
        // 焦点格子在屏幕外：往上滚到刚好露出（滚动量按没滚时的位置算），再排一次
        if (_gsMapFocus >= 0 && _gsMapFocus < _gpItems.Count)
        {
            var f = _gpItems[_gsMapFocus];
            float want = Math.Max(0, f.Y + _gsMapScroll + f.MapH / 2 + 6 - 780);
            if (Math.Abs(want - _gsMapScroll) > 0.5f)
            {
                _gsMapScroll = want;
                _gpItems.Clear();
                BuildGpMapItems(false, GsMapGeo, s => !GpIsSpecial(s), _gsMapHeads);
            }
        }
        else _gsMapScroll = 0;
        _gsMapItems.Clear();
        _gsMapItems.AddRange(_gpItems);
        _gpItems.Clear();
        _gpItems.AddRange(keep);
    }

    private void GsPaintPadMap(CanvasDrawingSession ds)
    {
        GsBuildMapItems();
        var accent = PerfAccent;
        var g = GsMapGeo;
        var old = ds.Transform;
        PaintGpMapHeads(ds, _gsMapHeads);
        // 线稿：和映射页一样按整层画（缩放、挪到 ArtCx / ArtCy），这样线稿上的区域染色也对得上
        var art = Matrix3x2.CreateScale(g.ArtScale) * Matrix3x2.CreateTranslation(g.ArtCx - GpCx * g.ArtScale, g.ArtCy - GpCy * g.ArtScale);
        var focus = _gsMapFocus >= 0 && _gsMapFocus < _gsMapItems.Count ? _gsMapItems[_gsMapFocus] : null;
        if (_gpBitmaps.TryGetValue("front", out var bmp))
        {
            var mid = ds.Transform;
            ds.Transform = art * mid;
            ds.DrawImage(bmp, new Rect(GpX, GpY, GpW, GpH), bmp.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic);
            if (focus != null && GpKeys.TryGetValue(focus.Key, out var fk) && fk.Face == "front")
            {
                _gpFacesShown.Add("front");
                GpFill(ds, focus.Key, 0.42f, accent);
            }
            ds.Transform = mid;
        }
        foreach (var it in _gsMapItems) PaintGpMapRow(ds, it, it == focus);
        ds.Transform = old;
    }

    private bool GsMapMove(Direction dir)
    {
        if (!GsPadMapMode || _stZone != 1) return false;
        if (_gsMapFocus < 0)
        {
            // 设置行最后一行再往下：进图
            if (dir != Direction.Down) return false;
            int last = _stRows.FindLastIndex(r => !r.Section && !r.Skip);
            if (_stFocus != last) return false;
            GsBuildMapItems();
            if (_gsMapItems.Count == 0) return false;
            _gsMapFocus = Math.Max(0, _gsMapItems.FindIndex(it => it.Key == "LB"));
            NavHaptic("move");
            PaintSt();
            return true;
        }
        int dx = dir == Direction.Left ? -1 : dir == Direction.Right ? 1 : 0, dy = dir == Direction.Up ? -1 : dir == Direction.Down ? 1 : 0;
        var rects = _gsMapItems.Select(it => new Rect(it.Cx - 1, it.Cy - 1, 2, 2)).ToList();
        int next = AgSpatial(rects, _gsMapFocus, dx, dy);
        if (next >= 0) _gsMapFocus = next;
        else if (dy < 0) { _gsMapFocus = -1; _stFocus = _stRows.FindLastIndex(r => !r.Section && !r.Skip); }   // 图最上面再往上：回设置行
        else if (dx < 0) { _gsMapFocus = -1; _stZone = 0; }
        NavHaptic("move");
        PaintSt();
        return true;
    }

    private bool GsMapActivate()
    {
        if (!GsPadMapMode || _stZone != 1 || _gsMapFocus < 0 || _gsMapFocus >= _gsMapItems.Count) return false;
        GsPadOpen(_gsMapItems[_gsMapFocus].Key);
        return true;
    }

    private bool GsMapBack()
    {
        if (_gsMapFocus < 0) return false;
        _gsMapFocus = -1;
        _stZone = 0;
        PaintSt();
        return true;
    }

    /// <summary>点图上的一行：焦点过去并打开按键选择。</summary>
    private bool GsMapClick(double sx, double sy)
    {
        if (!GsPadMapMode) return false;
        float x = (float)sx / _ccK, y = (float)sy / _ccK;
        GsBuildMapItems();
        int best = _gsMapItems.FindIndex(it => new Rect(it.Col == -1 ? it.X - it.MapW : it.X, it.Y - it.MapH / 2, it.MapW, it.MapH).Contains(new Point(x, y)));
        if (best < 0) return false;
        _stZone = 1;
        _gsMapFocus = best;
        GsPadOpen(_gsMapItems[best].Key);
        return true;
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
            // 只有现在用着的那几张（封面概览），没有候选网格的页签
            _agTab = AgTabOverview;
            AgPaintOverview(ds, d);
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
        bool over = GsOverlay, map = GsPadMapMode;
        _stRightView.IsVisible = !over;
        _stRightView.Opacity = 1;
        if (!over && !map) { if (_stOver != null) _stOver.IsVisible = false; return; }
        if (_stOver == null)
        {
            _stOver = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            _st!.Children.InsertAtTop(_stOver);
        }
        _stOver.IsVisible = true;
        if (map && !over)
            CcSet(_stOver, CcDraw(1280, 800, raster, GsPaintPadMap));
        else if (GsPadPicking)
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
                ds.Transform = GsArtMatrix * ds.Transform;
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
        if (GsMapMove(dir)) return true;
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
        if (GsMapActivate()) return true;
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
        if (GsMapBack()) return true;
        if (!GsArtMode || _stZone != 1) return false;
        if (_agOffKind != null) { AgCloseOfficial(); return true; }
        _stZone = 0;
        PaintSt();
        return true;
    }

    private bool GsOverlayTab(int delta)
    {
        if (GsPadPicking) { GpSwitchTab(delta); return true; }
        return GsArtMode;   // 封面没有页签
    }

    /// <summary>点按：按键选择的页签 / 键格 / 行，封面的页签 / 图。</summary>
    private bool GsOverlayClick(double sx, double sy)
    {
        if (GsMapClick(sx, sy)) return true;
        if (!GsOverlay) return false;
        float x = (float)sx / _ccK, y = (float)sy / _ccK;
        if (x < StRightX - 16) return false;   // 左栏照常
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
                if (p.Level == GpSubLevel.Key) p.KeyFocus = t.Row; else if (p.Level == GpSubLevel.Group) p.GroupFocus = t.Row; else p.SettingsFocus = t.Row;
                GpPickerActivate();
                PaintSt();
                return true;
            }
            return true;
        }
        var d = _agDraft!;
        Matrix3x2.Invert(GsArtMatrix, out var inv);
        var pv = Vector2.Transform(new Vector2(x, y), inv);
        var pt = new Point(pv.X, pv.Y);
        if (_agOffKind != null)
        {
            if (_agOffUse.Contains(pt)) AgUseOfficial(d, _agOffKind);
            else if (!_agOffPanel.Contains(pt)) AgCloseOfficial();
            return true;
        }
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
