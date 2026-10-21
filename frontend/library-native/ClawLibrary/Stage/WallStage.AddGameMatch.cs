// 添加游戏 › 匹配游戏（子页面）：选完 exe 先进这页，之后「匹配」那一行 / Y 再进来。
// 不分左右栏，一整列：标题下面一行是搜索，照 folia 书库首页的搜索框（和游戏库首页搜索面板同一套：带框输入框，
// 高 44、圆角 12、底 5 %、描边 24 %，打字时描边换强调色，左放大镜、右 ×），右边跟主按钮「搜索」（高 44、圆角 12、15 px 粗体、底 16 %）。
// 和 folia 一样打字不搜，回车 / 「搜索」才搜；进页时先用 exe 名搜一次。
// 下面整宽（40–1240）是匹配出来的游戏（设置行：行号、左名称、右年份，正在用的那款前面一个点），搜索中列表上方一道细线上有光来回走。
// A 用焦点那款并回到封面页，B 回封面页。
using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Windows.Foundation;
using Rect = Windows.Foundation.Rect;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private static readonly FontSpec AgIcons = new("Segoe Fluent Icons", 400);
    private static readonly Rect AgField = new(40, AgCatTop, 640, 44);
    private static readonly Rect AgFieldClear = new(AgField.Right - 6 - 32, AgField.Y + 6, 32, 32);
    private static readonly Rect AgFieldInput = new(AgField.X + 36, AgField.Y + 1, AgFieldClear.X - 4 - (AgField.X + 36), 42);
    /// <summary>结果列表的顶；搜索中那道光走的线在它上面。</summary>
    private const float AgMatchTop = AgCatTop + 44 + 28;
    private static readonly Rect AgSearchLine = new(40, AgMatchTop - 14, AgRowsW, 2);
    private Rect _agSubmit;
    private SpriteVisual? _agSearchBar;
    /// <summary>输入框里按的是回车（回车才搜；点别处失去焦点只记下字）。</summary>
    private bool _agEditEnter;

    // ---------------- 输入 ----------------

    /// <summary>搜索那一行（zone 0）：0 输入框、1「搜索」，左右走；列表（zone 1）：第几款，上下走，顶上再往上回输入框。</summary>
    private void AgMoveMatch(AgDraft d, int dx, int dy)
    {
        int n = d.Matches.Count;
        if (_agZone == 0)
        {
            if (dx != 0 && _agIndex != Math.Clamp(_agIndex + dx, 0, 1)) _agIndex += dx;
            else if (dy > 0 && n > 0) { _agZone = 1; _agIndex = Math.Clamp(d.Match, 0, n - 1); }
            else return;
        }
        else
        {
            if (dy > 0 && _agIndex < n - 1) _agIndex++;
            else if (dy < 0 && _agIndex > 0) _agIndex--;
            else if (dy < 0) { _agZone = 0; _agIndex = 0; }
            else return;
        }
        NavHaptic("move");
        AgPaint();
    }

    private void AgActivateMatch(AgDraft d)
    {
        if (_agZone == 1) { if (_agIndex < d.Matches.Count && !d.Searching) AgChooseMatch(d, _agIndex); return; }
        if (_agIndex == 0) AgBeginEdit("query");
        else AgMatchSubmit(d);
    }

    private void AgMatchSubmit(AgDraft d)
    {
        if (d.Query.Trim().Length == 0) { _agZone = 0; _agIndex = 0; AgBeginEdit("query"); return; }
        d.Query = d.Query.Trim();
        _ = AgSearch(d);
    }

    private void AgMatchClear(AgDraft d)
    {
        d.Query = "";
        _agZone = 0; _agIndex = 0;
        AgBeginEdit("query");
    }

    // ---------------- 绘制 ----------------

    private void AgPaintMatch(CanvasDrawingSession ds, AgDraft d)
    {
        AgTitle(ds, "匹配游戏");

        // 搜索一行：输入框 + 「搜索」
        bool typing = _agEditing == "query";
        var field = AgField;
        float fcy = (float)(field.Y + field.Height / 2);
        ds.FillRoundedRectangle(field, 12, 12, Gfx.WithAlpha(CcText, 0.05));
        ds.DrawRoundedRectangle(new Rect(field.X + 0.5, field.Y + 0.5, field.Width - 1, field.Height - 1), 11.5f, 11.5f,
            typing ? PerfAccent : Gfx.WithAlpha(CcText, 0.24), 1);
        PText(ds, "", AgIcons, 16, Gfx.WithAlpha(CcText, 0.7), (float)field.X + 12, fcy, 24);
        if (!typing)
        {
            bool empty = d.Query.Length == 0;
            PText(ds, empty ? "游戏名" : d.Query, TextKit.Ui(400), 16, Gfx.WithAlpha(CcText, empty ? 0.38 : 0.92),
                (float)AgFieldInput.X, fcy, (float)AgFieldInput.Width);
        }
        if (d.Query.Length > 0 || typing)
        {
            PText(ds, "", AgIcons, 14, Gfx.WithAlpha(CcText, 0.8), (float)AgFieldClear.X, (float)(AgFieldClear.Y + 16), 32, CanvasHorizontalAlignment.Center);
            _agHits.Add((AgFieldClear, 0, 0, () => AgMatchClear(d)));
        }
        _agHits.Add((field, 0, 0, () => AgBeginEdit("query")));

        var bf = TextKit.Ui(700);
        string label = d.Searching ? "搜索中" : "搜索";
        float lw = PWidth(ds, label, bf, 15);
        _agSubmit = new Rect(field.Right + 12, field.Y, 16 + 18 + 8 + lw + 16, 44);
        bool sf = _agZone == 0 && _agIndex == 1;
        ds.FillRoundedRectangle(_agSubmit, 12, 12, Gfx.WithAlpha(CcText, sf ? 0.22 : 0.16));
        if (sf) ds.DrawRoundedRectangle(_agSubmit, 12, 12, Gfx.WithAlpha(CcText, 0.26), 1);
        float scy = (float)(_agSubmit.Y + 22);
        PText(ds, "", AgIcons, 18, CcText, (float)_agSubmit.X + 16, scy, 24);
        PText(ds, label, bf, 15, CcText, (float)_agSubmit.X + 16 + 18 + 8, scy, lw + 4);
        _agHits.Add((_agSubmit, 0, 1, () => AgMatchSubmit(d)));

        // 手柄焦点：圆角白框（打字时输入框自己换了强调色描边，不再叠一圈）
        Rect? ring = _agZone != 0 ? null : _agIndex == 0 ? (typing ? null : field) : _agSubmit;
        if (ring is { } f)
            ds.DrawRoundedRectangle(new Rect(f.X - 2, f.Y - 2, f.Width + 4, f.Height + 4), 14, 14, CcText, 2);

        // 下面整宽：匹配出来的游戏，左名称、右年份
        int n = d.Matches.Count;
        if (d.Searching) ds.FillRectangle(AgSearchLine, Gfx.WithAlpha(CcText, 0.08));
        var rows = d.Matches.Select((m, i) => new PerfRow
        {
            Title = Str(m["name"]) ?? "",
            Value = Int(m["year"]) is > 0 and var y ? y.ToString() : "",
            Changed = i == d.Match,
        }).ToList();
        var old = ds.Transform;
        // 和分类页一样的整宽设置行：行号 / 焦点三角在左头，名称缩进 36
        ds.Transform = Matrix3x2.CreateTranslation(40, AgMatchTop) * old;
        PaintRowList(ds, rows, _agZone == 1 ? _agIndex : -1, null, true, AgRowsW);
        ds.Transform = old;
        for (int i = 0; i < n; i++)
        {
            int at = i;
            _agHits.Add((new Rect(40, AgMatchTop + i * PerfRowH, AgRowsW, PerfRowH), 1, i, () => { if (!d.Searching) AgChooseMatch(d, at); }));
        }
        if (n == 0 && !d.Searching && d.Status.Length > 0)
            PText(ds, d.Status, TextKit.Ui(400), 17, Gfx.WithAlpha(CcText, 0.45), 40 + 36, AgMatchTop + PerfRowH / 2, AgRowsW);
    }

    /// <summary>搜索中：右栏顶上的细线上一道白光来回走。</summary>
    private void AgMatchSearching()
    {
        if (_ag == null) return;
        bool on = _agOpen && _agPage == AgPage.Match && _agDraft is { Searching: true };
        if (!on) { if (_agSearchBar != null) { _agSearchBar.StopAnimation("Offset.X"); _agSearchBar.IsVisible = false; } return; }
        if (_agSearchBar == null)
        {
            _agSearchBar = G.Sprite(G.Solid(Microsoft.UI.Colors.White), relative: false);
            _ag.Children.InsertAtTop(_agSearchBar);
        }
        if (_agSearchBar.IsVisible) return;
        float k = _ccK, x0 = (float)AgSearchLine.X * k, x1 = (float)(AgSearchLine.Right - 120) * k;
        _agSearchBar.Size = new Vector2(120 * k, 2 * k);
        _agSearchBar.Offset = new Vector3(x0, (float)AgSearchLine.Y * k, 0);
        _agSearchBar.IsVisible = true;
        if (Reduced) return;
        var a = G.C.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, x0);
        a.InsertKeyFrame(0.5f, x1, G.C.CreateCubicBezierEasingFunction(new Vector2(0.65f, 0), new Vector2(0.35f, 1)));
        a.InsertKeyFrame(1, x0, G.C.CreateCubicBezierEasingFunction(new Vector2(0.65f, 0), new Vector2(0.35f, 1)));
        a.Duration = TimeSpan.FromMilliseconds(1400);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        _agSearchBar.StartAnimation("Offset.X", a);
    }
}
