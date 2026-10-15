// 墙上的一张卡（照 src/stage/tile.ts）。视觉树：
//   El（位置 / 尺寸，世界 px）
//    └ Persp（原点在卡中心，带 1400px 透视；焦点放大 pop 在这里）
//       └ Pivot（翻牌绕 Y 轴转、整墙波次的抬起 / 缩放 / 淡出）
//          └ Face（一份内容：底色、封面、渐变、染色、角标、标题；换内容时新旧两份叠着，在合成线程按时刻切换）
// 所有动画都交给合成线程；UI 线程只在换内容、换尺寸时建 / 画一次。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

/// <summary>卡面重建时接过去的那张图：种类、表面、此刻的取景。</summary>
public readonly record struct ImageCarry(char Kind, LoadedImageSurface? Surface, float Ratio);

public sealed class Tile : IDisposable
{
    private readonly WallStage _s;
    private Gfx G => _s.G;
    public readonly ContainerVisual El;
    private readonly ContainerVisual _persp;
    private readonly ContainerVisual _pivot;
    private readonly CompositionPropertySet _props;
    public Slot Slot;
    /// <summary>挂在哪半边墙（0 = 缝左，1 = 缝右）。</summary>
    public int Side;
    /// <summary>此刻摆放的目标矩形（让位动画中是终点）。</summary>
    public Engine.Rect Rect;
    /// <summary>此刻画着的内容（id），以及目标内容。</summary>
    public string? Shown, Target;
    public bool Expanded { get; private set; }
    /// <summary>卡面内容的根（控制中心形变用它的实时画面当 hero 的封面，保证起落帧与卡片完全一致）。</summary>
    internal Visual? FaceRoot => _face?.Root;
    /// <summary>卡面此刻用的那张图（控制中心的游戏库卡用同一张，同样从正中裁）。</summary>
    internal string? ArtPath => _face?.BaseArtPath;
    /// <summary>卡面此刻显示的那份解码好的图（形变飞的那张优先用它，保证已经画得出来）。</summary>
    internal LoadedImageSurface? ArtSurface => _face?.ArtSurface;
    public bool Focused { get; private set; }

    private Face? _face;
    private readonly List<(Face face, long until)> _leaving = new();
    private long _animUntil;

    // 让位动画（用来在途中打断时算出当前矩形）
    private Engine.Rect _animFrom, _animTo;
    private long _animStart;
    private double _animMs;
    private bool _rectAnimating;

    public Tile(WallStage stage, Slot slot)
    {
        _s = stage;
        Slot = slot;
        Rect = slot.Rect;
        El = G.C.CreateContainerVisual();
        _persp = G.Container();
        _persp.RelativeOffsetAdjustment = new Vector3(0.5f, 0.5f, 0);
        _persp.TransformMatrix = Perspective((float)Motion.Flip.Perspective);
        _pivot = G.Container();
        _pivot.RotationAxis = new Vector3(0, 1, 0);
        _persp.Children.InsertAtTop(_pivot);
        El.Children.InsertAtTop(_persp);
        _props = G.C.CreatePropertySet();
        _props.InsertScalar("pop", 0);
        Place(Rect);
    }

    public static Matrix4x4 Perspective(float d) => new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, -1 / d, 0, 0, 0, 1);

    public void Place(Engine.Rect r)
    {
        StopRectAnimation();
        Rect = r;
        El.Offset = new Vector3((float)r.X, (float)r.Y, 0);
        El.Size = new Vector2((float)r.Width, (float)r.Height);
    }

    private void StopRectAnimation()
    {
        if (!_rectAnimating) return;
        El.StopAnimation("Offset");
        El.StopAnimation("Size");
        _rectAnimating = false;
        _face?.EndScale();
    }

    /// <summary>此刻实际画在哪（让位动画途中按同一条曲线算）。</summary>
    public Engine.Rect CurrentRect(long nowMs)
    {
        if (!_rectAnimating) return Rect;
        double t = Math.Min(1, (nowMs - _animStart) / _animMs);
        double e = Motion.ReflowEase(t);
        return new Engine.Rect(
            _animFrom.X + (_animTo.X - _animFrom.X) * e, _animFrom.Y + (_animTo.Y - _animFrom.Y) * e,
            _animFrom.Width + (_animTo.Width - _animFrom.Width) * e, _animFrom.Height + (_animTo.Height - _animFrom.Height) * e);
    }

    /// <summary>让位：从 from 按弹簧曲线走到 to（合成线程）。标题按终点尺寸重画，途中随卡宽缩放。</summary>
    public void AnimateRect(Engine.Rect from, Engine.Rect to, double ms, long nowMs)
    {
        StopRectAnimation();
        _animFrom = from; _animTo = to; _animStart = nowMs; _animMs = ms; _rectAnimating = true;
        Rect = to;
        var off = G.SampledV3(new Vector3((float)from.X, (float)from.Y, 0), new Vector3((float)to.X, (float)to.Y, 0), ms, Motion.ReflowEase);
        var size = G.SampledV2(new Vector2((float)from.Width, (float)from.Height), new Vector2((float)to.Width, (float)to.Height), ms, Motion.ReflowEase);
        El.Offset = new Vector3((float)from.X, (float)from.Y, 0);
        El.Size = new Vector2((float)from.Width, (float)from.Height);
        El.StartAnimation("Offset", off);
        El.StartAnimation("Size", size);
        _face?.Relayout(to, scaleWith: El);
        _face?.TransitionArt(from, to, ms);
    }

    /// <summary>让位结束：定在终点。</summary>
    public void FinishRect()
    {
        if (!_rectAnimating) return;
        StopRectAnimation();
        Place(Rect);
    }

    // ---------------- 内容 ----------------

    /// <summary>立即画成目标内容（不动画）。</summary>
    public void Show(string? id)
    {
        Cancel();
        Target = id;
        Draw(id);
    }

    public void SetExpanded(bool on)
    {
        if (Expanded == on) return;
        Expanded = on;
        Draw(Shown);
        ApplyPop(true);
    }

    private void Draw(string? id)
    {
        Shown = id;
        var old = _face;
        // 同一款游戏重建卡面（展开 / 收起、换标题样式）：接着画正在显示的那张图
        var carry = old != null && id != null && old.ItemId == id ? old.CurrentImage : null;
        _face = new Face(_s, this, id, Rect, Expanded, carry);
        _face.ApplyFocus(Focused, Expanded, animate: false);
        _pivot.Children.InsertAtTop(_face.Root);
        if (old != null) { _pivot.Children.Remove(old.Root); old.Dispose(); }
        if (_rectAnimating)
        {
            // 正在让位：标题按终点排、随卡宽缩放；图从此刻的样子沿剩下的曲线过渡
            long now = _s.NowMs;
            var cur = CurrentRect(now);
            double remain = Math.Max(0, _animMs - (now - _animStart));
            _face.Relayout(_animTo, scaleWith: El);
            _face.TransitionArt(cur, _animTo, remain);
        }
    }

    /// <summary>尺寸定下来之后（让位动画结束）按新宽高比换图；同一张图只更新取景。</summary>
    public void FitArt() => _face?.FitArt(Rect);

    /// <summary>标题样式切换 / 栅格倍率变了：按当前尺寸重画文字。</summary>
    public void Redraw() => _face?.Relayout(Rect, null);

    public void SetFocused(bool on)
    {
        if (Focused == on) return;
        Focused = on;
        _face?.ApplyFocus(on, Expanded, animate: !_s.Reduced);
        ApplyPop(!_s.Reduced);
    }

    /// <summary>焦点放大：四边各外扩一个 gap（缩放量随卡尺寸，让位途中也对）。展开态不放大。</summary>
    private void ApplyPop(bool animate)
    {
        float target = Focused && !Expanded ? 1 : 0;
        var expr = G.C.CreateExpressionAnimation("Vector3(1 + p.pop * 16 / Max(1, el.Size.X), 1 + p.pop * 16 / Max(1, el.Size.Y), 1)");
        expr.SetReferenceParameter("p", _props);
        expr.SetReferenceParameter("el", El);
        _persp.StartAnimation("Scale", expr);
        if (animate) _props.StartAnimation("pop", G.Scalar(CurrentPop(), target, Motion.PopMs, Bezier.EaseOut));
        else { _props.StopAnimation("pop"); _props.InsertScalar("pop", target); }
        _popTarget = target;
    }

    private float _popTarget;
    private float CurrentPop() { _props.TryGetScalar("pop", out float v); return v; }

    // ---------------- 控制中心的「移形换影」（folia 集合层的出入场）：都动内层，外层矩形不动 ----------------

    /// <summary>hero：形变合成层飞行期间先藏起来。</summary>
    public void MorphHide()
    {
        Cancel();
        _pivot.Opacity = 0;
    }

    /// <summary>hero 揭晓：透明度 0.3 s ease-out，尺度 0.985 → 1（0.42 s，cubic-bezier(0.32,0.72,0,1)）。</summary>
    public void MorphReveal(double delayMs)
    {
        var op = G.Scalar(0, 1, 300, new Bezier(0f, 0f, 0.58f, 1f), delayMs);
        var sc = G.C.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0, new Vector3(0.985f, 0.985f, 1), G.Linear);
        sc.InsertKeyFrame(1, Vector3.One, G.Ease(new Bezier(0.32f, 0.72f, 0f, 1f)));
        sc.Duration = Gfx.Ms(420);
        sc.DelayTime = Gfx.Ms(delayMs);
        sc.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        _pivot.StartAnimation("Opacity", op);
        _pivot.StartAnimation("Scale", sc);
        _animUntil = _s.NowMs + (long)(delayMs + 420) + 50;
    }

    /// <summary>形变起飞：到 delay 那一刻才藏（合成线程上的关键帧，和飞行同一条时间轴）。</summary>
    public void MorphHideAt(double delayMs)
    {
        Cancel();
        var op = G.C.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(0, 1, G.Linear);
        op.InsertKeyFrame(0.999f, 1, G.Linear);
        op.InsertKeyFrame(1, 0, G.Linear);
        op.Duration = Gfx.Ms(Math.Max(1, delayMs));
        _pivot.StartAnimation("Opacity", op);
        _animUntil = _s.NowMs + (long)delayMs + 50;
    }

    /// <summary>形变落定：从 delay 起 dur 毫秒内淡入（不缩放，和合成层的最后一帧逐像素对齐）。</summary>
    public void MorphShowAt(double delayMs, double durMs)
    {
        var op = G.C.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(0, 0, G.Linear);
        op.InsertKeyFrame(1, 1, G.Linear);
        op.Duration = Gfx.Ms(durMs);
        op.DelayTime = Gfx.Ms(delayMs);
        op.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        _pivot.StartAnimation("Opacity", op);
        _animUntil = _s.NowMs + (long)(delayMs + durMs) + 50;
    }

    /// <summary>入场：从径向推开的位置（世界坐标）就位，透明度 0 → 1、尺度 0.94 → 1，0.5 s cubic-bezier(0.32,0.72,0,1)。</summary>
    public void MorphFlyIn(Vector2 from, double delayMs)
    {
        Cancel();
        var ease = new Bezier(0.32f, 0.72f, 0f, 1f);
        var off = G.C.CreateVector3KeyFrameAnimation();
        off.InsertKeyFrame(0, new Vector3(from, 0), G.Linear);
        off.InsertKeyFrame(1, Vector3.Zero, G.Ease(ease));
        var sc = G.C.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0, new Vector3(0.94f, 0.94f, 1), G.Linear);
        sc.InsertKeyFrame(1, Vector3.One, G.Ease(ease));
        var op = G.C.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(0, 0, G.Linear);
        op.InsertKeyFrame(1, 1, G.Ease(ease));
        foreach (var a in new KeyFrameAnimation[] { off, sc, op })
        {
            a.Duration = Gfx.Ms(500);
            a.DelayTime = Gfx.Ms(delayMs);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        _pivot.StartAnimation("Offset", off);
        _pivot.StartAnimation("Scale", sc);
        _pivot.StartAnimation("Opacity", op);
        _animUntil = _s.NowMs + (long)(delayMs + 500) + 50;
    }

    /// <summary>出场四散：沿径向挪开（世界坐标）、转一点、缩到 0.9，透明度 1 → 0.72 → 0。</summary>
    public void MorphScatter(Vector2 to, float rotateDeg, double durMs, double delayMs)
    {
        Cancel();
        var ease = G.Ease(new Bezier(0.22f, 1f, 0.36f, 1f));
        var off = G.C.CreateVector3KeyFrameAnimation();
        off.InsertKeyFrame(0, Vector3.Zero, G.Linear);
        off.InsertKeyFrame(1, new Vector3(to, 0), ease);
        var sc = G.C.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0, Vector3.One, G.Linear);
        sc.InsertKeyFrame(1, new Vector3(0.9f, 0.9f, 1), ease);
        var rot = G.C.CreateScalarKeyFrameAnimation();
        rot.InsertKeyFrame(0, 0, G.Linear);
        rot.InsertKeyFrame(1, rotateDeg, ease);
        var inOut = G.Ease(new Bezier(0.42f, 0f, 0.58f, 1f));
        var op = G.C.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(0, 1, G.Linear);
        op.InsertKeyFrame(0.5f, 0.72f, inOut);
        op.InsertKeyFrame(1, 0, inOut);
        foreach (var a in new KeyFrameAnimation[] { off, sc, rot, op })
        {
            a.Duration = Gfx.Ms(durMs);
            a.DelayTime = Gfx.Ms(delayMs);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        _pivot.StartAnimation("Offset", off);
        _pivot.StartAnimation("Scale", sc);
        _pivot.StartAnimation("Opacity", op);
        // 平面内的转角放在透视层上（_pivot 的转轴是翻牌用的 Y 轴）
        _persp.StartAnimation("RotationAngleInDegrees", rot);
        _animUntil = _s.NowMs + (long)(delayMs + durMs) + 50;
    }

    public void Cancel()
    {
        _persp.StopAnimation("RotationAngleInDegrees");
        _persp.RotationAngleInDegrees = 0;
        _pivot.StopAnimation("RotationAngleInDegrees");
        _pivot.StopAnimation("Offset");
        _pivot.StopAnimation("Scale");
        _pivot.StopAnimation("Opacity");
        _pivot.RotationAngleInDegrees = 0;
        _pivot.Offset = Vector3.Zero;
        _pivot.Scale = Vector3.One;
        _pivot.Opacity = 1;
        foreach (var (f, _) in _leaving) { _pivot.Children.Remove(f.Root); f.Dispose(); }
        _leaving.Clear();
        if (_face != null) { _face.Root.StopAnimation("Opacity"); _face.Root.Opacity = 1; }
        _animUntil = 0;
    }

    /// <summary>旧内容在合成线程上按时刻收起（动画结束后由舞台调 Sweep 拿掉）。</summary>
    private void SwapAt(double delay, double total, double at, string? to)
    {
        Target = to;
        Draw(to);   // 新内容立刻建好，先藏着；旧的那份（_pendingOld）按时刻收起
        double p = Math.Clamp(at / total, 0.0001, 0.9989);
        var hide = G.C.CreateScalarKeyFrameAnimation();
        hide.InsertKeyFrame((float)p, 1, G.Linear);
        hide.InsertKeyFrame((float)p + 0.001f, 0, G.Linear);
        hide.Duration = Gfx.Ms(total);
        hide.DelayTime = Gfx.Ms(delay);
        hide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var show = G.C.CreateScalarKeyFrameAnimation();
        show.InsertKeyFrame(0, 0, G.Linear);
        show.InsertKeyFrame((float)p, 0, G.Linear);
        show.InsertKeyFrame((float)p + 0.001f, 1, G.Linear);
        show.Duration = Gfx.Ms(total);
        show.DelayTime = Gfx.Ms(delay);
        show.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        _face!.Root.Opacity = 0;
        _face.Root.StartAnimation("Opacity", show);
        if (_pendingOld != null)
        {
            _pendingOld.Root.StartAnimation("Opacity", hide);
            _leaving.Add((_pendingOld, _s.NowMs + (long)(delay + total) + 50));
            _pendingOld = null;
        }
        _animUntil = Math.Max(_animUntil, _s.NowMs + (long)(delay + total) + 50);
    }

    private Face? _pendingOld;

    /// <summary>把当前内容留着当「离场」那份，Draw 不要释放它。</summary>
    private void DetachForSwap()
    {
        _pendingOld = _face;
        _face = null;
    }

    /// <summary>翻牌：转到 90° → 换内容 → 从另一侧转回来。</summary>
    public void Flip(string? to, double delay, int direction, bool reduced)
    {
        Cancel();
        DetachForSwap();
        if (reduced)
        {
            double half = Motion.ReducedFadeMs / 2;
            SwapAtFade(to, half);
            return;
        }
        double outMs = Motion.Flip.OutMs, inMs = Motion.Flip.InMs, total = outMs + inMs;
        float p = (float)(outMs / total);
        var rot = G.C.CreateScalarKeyFrameAnimation();
        rot.InsertKeyFrame(0, 0, G.Linear);
        rot.InsertKeyFrame(p, 90 * direction, G.Ease(Motion.Flip.OutEase));
        rot.InsertKeyFrame(p + 0.001f, -90 * direction, G.Linear);
        rot.InsertKeyFrame(1, 0, G.Ease(Motion.Flip.InEase));
        rot.Duration = Gfx.Ms(total);
        rot.DelayTime = Gfx.Ms(delay);
        rot.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        _pivot.StartAnimation("RotationAngleInDegrees", rot);
        SwapAt(delay, total, outMs, to);
    }

    /// <summary>降低动态效果：淡出 → 换内容 → 淡入。</summary>
    private void SwapAtFade(string? to, double half)
    {
        double total = half * 2;
        var fade = G.C.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 1, G.Linear);
        fade.InsertKeyFrame(0.5f, 0, G.Ease(Bezier.EaseIn));
        fade.InsertKeyFrame(1, 1, G.Ease(Bezier.EaseOut));
        fade.Duration = Gfx.Ms(total);
        _pivot.StartAnimation("Opacity", fade);
        SwapAt(0, total, half, to);
    }

    /// <summary>整墙波次：抬起淡出 → 换内容 → 等到自己的时刻落回。</summary>
    public void Wave(string? to, double outDelay, double inDelay, bool reduced)
    {
        if (reduced) { Flip(to, 0, 1, true); return; }
        Cancel();
        DetachForSwap();
        double outMs = Motion.Wave.OutMs, inMs = Motion.Wave.InMs;
        double inStart = Math.Max(outMs, inDelay - outDelay);
        double total = inStart + inMs;
        float pOut = (float)(outMs / total), pIn = (float)(inStart / total);
        var lifted = new Vector3(0, -(float)Motion.Wave.Lift, 0);
        float sc = (float)Motion.Wave.Scale;

        var off = G.C.CreateVector3KeyFrameAnimation();
        off.InsertKeyFrame(0, Vector3.Zero, G.Linear);
        off.InsertKeyFrame(pOut, lifted, G.Ease(Motion.Wave.OutEase));
        if (pIn > pOut) off.InsertKeyFrame(pIn, lifted, G.Linear);
        off.InsertKeyFrame(1, Vector3.Zero, G.Ease(Motion.Wave.InEase));
        var scale = G.C.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, Vector3.One, G.Linear);
        scale.InsertKeyFrame(pOut, new Vector3(sc, sc, 1), G.Ease(Motion.Wave.OutEase));
        if (pIn > pOut) scale.InsertKeyFrame(pIn, new Vector3(sc, sc, 1), G.Linear);
        scale.InsertKeyFrame(1, Vector3.One, G.Ease(Motion.Wave.InEase));
        var op = G.C.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(0, 1, G.Linear);
        op.InsertKeyFrame(pOut, 0, G.Ease(Motion.Wave.OutEase));
        if (pIn > pOut) op.InsertKeyFrame(pIn, 0, G.Linear);
        op.InsertKeyFrame(1, 1, G.Ease(Motion.Wave.InEase));
        foreach (var a in new KeyFrameAnimation[] { off, scale, op })
        {
            a.Duration = Gfx.Ms(total);
            a.DelayTime = Gfx.Ms(outDelay);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        _pivot.StartAnimation("Offset", off);
        _pivot.StartAnimation("Scale", scale);
        _pivot.StartAnimation("Opacity", op);
        SwapAt(outDelay, total, outMs, to);
    }

    /// <summary>新挂上的卡在整墙入场期间也要落回（保持抬起到自己的时刻）。</summary>
    public void Land(double delay)
    {
        Cancel();
        float sc = (float)Motion.Wave.Scale;
        var off = G.C.CreateVector3KeyFrameAnimation();
        off.InsertKeyFrame(0, new Vector3(0, -(float)Motion.Wave.Lift, 0), G.Linear);
        off.InsertKeyFrame(1, Vector3.Zero, G.Ease(Motion.Wave.InEase));
        var scale = G.C.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(sc, sc, 1), G.Linear);
        scale.InsertKeyFrame(1, Vector3.One, G.Ease(Motion.Wave.InEase));
        var op = G.C.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(0, 0, G.Linear);
        op.InsertKeyFrame(1, 1, G.Ease(Motion.Wave.InEase));
        foreach (var a in new KeyFrameAnimation[] { off, scale, op })
        {
            a.Duration = Gfx.Ms(Motion.Wave.InMs);
            a.DelayTime = Gfx.Ms(delay);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        _pivot.StartAnimation("Offset", off);
        _pivot.StartAnimation("Scale", scale);
        _pivot.StartAnimation("Opacity", op);
        _animUntil = _s.NowMs + (long)(delay + Motion.Wave.InMs) + 50;
    }

    /// <summary>动画都走完了：拿掉离场的旧内容。</summary>
    public void Sweep(long now)
    {
        for (int i = _leaving.Count - 1; i >= 0; i--)
            if (now >= _leaving[i].until)
            {
                _pivot.Children.Remove(_leaving[i].face.Root);
                _leaving[i].face.Dispose();
                _leaving.RemoveAt(i);
            }
    }

    public bool Busy(long now) => now < _animUntil || _leaving.Count > 0;

    public void Dispose()
    {
        Cancel();
        _face?.Dispose();
        _face = null;
        _pendingOld?.Dispose();
        El.Children.RemoveAll();
        El.Dispose();
        _props.Dispose();
    }

    // ================================================================
    // 一份卡面内容
    // ================================================================

    internal sealed class Face : IDisposable
    {
        private readonly WallStage _s;
        private Gfx G => _s.G;
        public readonly ContainerVisual Root;
        private readonly LibraryItem? _item;
        private readonly bool _expanded;
        private char? _artKind;
        private char? _baseKind;

        /// <summary>
        /// 在这个尺寸上用哪张图：永远是本来那张（竖封面 p、大背景 h、方裁 s / x），显示得比原图大时换成同一张图的高分辨率版
        /// （p → q 原尺寸竖封面，s → x 同一块方裁的大图）。所以小卡和放大后的卡是同一张封面、同样的构图。
        /// </summary>
        private char? KindFor(ArtKit art, Engine.Rect r)
        {
            if (_baseKind is not char b) return null;
            char hi = b == 'p' ? 'q' : b == 's' ? 'x' : b == 'h' ? 'g' : b;
            if (hi == b || !art.Has(hi)) return b;
            var (iw, ih) = NativeSize(art, b);
            double need = Math.Max(r.Width / iw, r.Height / ih) * G.RasterScale;
            return need > 1.05 ? hi : b;
        }
        private ImageLayer? _img, _incoming;
        private ContainerVisual? _imgHost;
        public string? ItemId => _item?.Id;
        /// <summary>这张卡用的那张图（按卡在墙上的本来尺寸挑的：竖封面 / 方裁 / 大背景）。</summary>
        public LoadedImageSurface? ArtSurface => _img?.Surface;
        public string? BaseArtPath => _item?.Art is { } a && _baseKind is char k ? a.File(k) : null;
        private SpriteVisual? _ring1, _ring4, _ring5, _shadowCaster, _stack1, _stack2, _tint, _hilite;
        private ContainerVisual? _ring;
        private DropShadow? _shadow;
        private SpriteVisual? _copy, _actions, _badge, _glyph;
        private TextArt? _copyArt, _actionsArt, _badgeArt, _glyphArt;
        private readonly ContainerVisual? _inner;
        private CompositionLinearGradientBrush? _tintBrush;

        public Face(WallStage s, Tile tile, string? id, Engine.Rect r, bool expanded, ImageCarry? carry = null)
        {
            _s = s;
            _expanded = expanded;
            Root = G.Container();
            Root.RelativeOffsetAdjustment = new Vector3(-0.5f, -0.5f, 0);
            _item = id != null ? s.Lookup(id) : null;
            if (_item == null)
            {
                // .face.is-wall：透明 + 1px 内描边（墨色 7%）
                var edge = Gfx.WithAlpha(TextKit.Ink(), 0.07);
                foreach (var (ox, oy, rw, rh, w, h) in new (float, float, float, float, float, float)[] { (0, 0, 1, 0, 0, 1), (0, 0, 0, 1, 1, 0), (0, 1, 1, 0, 0, 1), (1, 0, 0, 1, 1, 0) })
                {
                    var e = G.Sprite(G.Solid(edge), relative: false);
                    e.RelativeSizeAdjustment = new Vector2(rw, rh);
                    e.Size = new Vector2(w, h);
                    e.RelativeOffsetAdjustment = new Vector3(ox, oy, 0);
                    e.Offset = new Vector3(ox > 0 ? -1 : 0, oy > 0 ? -1 : 0, 0);
                    Root.Children.InsertAtTop(e);
                }
                return;
            }
            var item = _item;
            var art = item.Art;
            // 这张卡用哪张图由它在墙上的本来位置（模板 slot）决定，放大 / 让位时只换成同一张图的高分辨率版
            _baseKind = art != null ? PickArt(art, tile.Slot.Width, tile.Slot.Height, false) : null;
            _artKind = art != null ? KindFor(art, r) : null;
            var posterBg = Theme.PosterBg;

            // ---- 卡外的装饰：集合叠页边、投影、焦点环（卡面 overflow:hidden 不裁 box-shadow）----
            if (item.Kind == ItemKind.Collection)
            {
                _stack2 = G.Sprite(G.Solid(Gfx.Mix(posterBg, 0.9, TextKit.Ink())));
                _stack2.Offset = new Vector3(6, 6, 0);
                _stack1 = G.Sprite(G.Solid(Gfx.Mix(posterBg, 0.8, TextKit.Ink())));
                _stack1.Offset = new Vector3(3, 3, 0);
                Root.Children.InsertAtTop(_stack2);
                Root.Children.InsertAtTop(_stack1);
            }
            _shadowCaster = G.Sprite(G.Solid(Colors.Black));
            _shadowCaster.Opacity = 0;
            Root.Children.InsertAtTop(_shadowCaster);
            _ring = G.Container();
            _ring.Opacity = 0;
            SpriteVisual Ring(float inflate, Color c)
            {
                var sp = G.Sprite(G.Solid(c));
                sp.Size = new Vector2(inflate * 2, inflate * 2);
                sp.Offset = new Vector3(-inflate, -inflate, 0);
                _ring.Children.InsertAtTop(sp);
                return sp;
            }
            _ring5 = Ring(5, Gfx.Rgba(0, 0, 0, 0.38));
            _ring4 = Ring(4, TextKit.Ink());
            _ring1 = Ring(1, Gfx.Rgba(0, 0, 0, 0.62));
            Root.Children.InsertAtTop(_ring);

            // ---- 卡面本身（裁到卡的矩形）----
            _inner = G.Container();
            _inner.Clip = G.C.CreateInsetClip();
            Root.Children.InsertAtTop(_inner);
            var c = Gfx.Hex(art?.C, posterBg);
            _inner.Children.InsertAtTop(G.Sprite(G.Solid(_artKind != null ? Gfx.Mix(c, 0.3, Theme.Bg) : posterBg)));
            if (_artKind is char k)
            {
                _imgHost = G.Container();
                _inner.Children.InsertAtTop(_imgHost);
                if (carry is { } cy && art!.Has(cy.Kind))
                {
                    // 卡面重建（展开 / 收起、换标题样式）：先原样接着画正在显示的那张图、同样的取景，第一帧不变
                    _s.Images.AddRef(cy.Surface);
                    _img = MakeLayer(cy.Kind, cy.Surface, cy.Ratio);
                    _artKind = cy.Kind;
                }
                else _img = MakeLayer(k, AcquireFor(art!, k, r), (float)(ObjectX(art!, k, r.Width, r.Height) / 100));
            }
            else
            {
                // 原型封面：按色相生成（真机上只在缺图时出现）
                double hue = item.Hue;
                _inner.Children.InsertAtTop(G.Sprite(G.LinearGradient(160, r.Width, r.Height, (0, Gfx.Hsl(hue, 52, 50)), (1, Gfx.Hsl(hue + 40, 45, 18)))));
                var rad = G.C.CreateRadialGradientBrush();
                rad.MappingMode = CompositionMappingMode.Relative;
                rad.EllipseCenter = new Vector2(0.78f, 0.12f);
                rad.EllipseRadius = new Vector2(1.2f, 0.9f);
                var hc = Gfx.Hsl(hue + 30, 70, 72, 0.55);
                rad.ColorStops.Add(G.C.CreateColorGradientStop(0, hc));
                rad.ColorStops.Add(G.C.CreateColorGradientStop(0.6f, Gfx.WithAlpha(hc, 0)));
                _inner.Children.InsertAtTop(G.Sprite(rad));
                _glyphArt = s.Text.Glyph(item.Title, tile.Slot.Width, tile.Slot.Height, r.Width, r.Height, expanded);
                _glyph = TextSprite(_glyphArt, r, anchorBottom: false);
                _inner.Children.InsertAtTop(_glyph);
            }
            foreach (var sh in ShadeSprites(r)) _inner.Children.InsertAtTop(sh);
            // 焦点顶部 10% 高光
            _hilite = G.Sprite(G.LinearGradient(180, r.Width, r.Height, (0, Gfx.Rgba(255, 255, 255, 0.10)), (0.6, Gfx.Rgba(255, 255, 255, 0))));
            _hilite.Opacity = 0;
            _inner.Children.InsertAtTop(_hilite);
            // 主题染色压住背景卡，焦点 / 展开时褪掉
            _tintBrush = G.LinearGradient(145, r.Width, r.Height, (0, Theme.TintA), (1, Theme.TintB));
            _tint = G.Sprite(_tintBrush);
            _tint.Opacity = (float)Theme.TintOpacity;
            _inner.Children.InsertAtTop(_tint);
            _badgeArt = s.Text.Badge(item.Badge, item.Kind == ItemKind.Collection);
            _badge = TextSprite(_badgeArt, r, anchorBottom: false);
            _inner.Children.InsertAtTop(_badge);
            BuildCopy(r, null);
        }

        private IEnumerable<SpriteVisual> ShadeSprites(Engine.Rect r)
        {
            bool b = _s.Style == TitleStyle.B;
            Color K(double a) => Gfx.Rgba(0, 0, 0, a);
            if (_expanded && b)
            {
                yield return G.Sprite(G.LinearGradient(0, r.Width, r.Height, (0, K(.86)), (.14, K(.62)), (.30, K(.26)), (.46, K(0))));
                yield return G.Sprite(G.LinearGradient(90, r.Width, r.Height, (0, K(.88)), (.52, K(.50)), (1, K(.08))));
            }
            else if (_expanded)
                yield return G.Sprite(G.LinearGradient(0, r.Width, r.Height, (0, K(.88)), (.18, K(.66)), (.38, K(.24)), (.55, K(0))));
            else if (_artKind != null && b)
                yield return G.Sprite(G.LinearGradient(180, r.Width, r.Height, (.52, K(0)), (1, K(.78))));
            else if (_artKind != null)
                yield return G.Sprite(G.LinearGradient(180, r.Width, r.Height, (.45, K(0)), (.66, K(.38)), (1, K(.84))));
            else
                yield return G.Sprite(G.LinearGradient(180, r.Width, r.Height, (.34, K(.05)), (.56, K(.24)), (1, K(.92))));
        }

        /// <summary>文字图挂成精灵：anchorBottom 时按卡底边定位（D 居中 / B 靠左），让位途中可随卡宽缩放。</summary>
        private SpriteVisual TextSprite(TextArt art, Engine.Rect r, bool anchorBottom)
        {
            var sp = G.Sprite(G.C.CreateSurfaceBrush(art.Surface), relative: false);
            sp.Size = new Vector2((float)art.Rect.Width, (float)art.Rect.Height);
            PositionText(sp, art, r, anchorBottom);
            return sp;
        }

        private void PositionText(SpriteVisual sp, TextArt art, Engine.Rect r, bool anchorBottom)
        {
            sp.Size = new Vector2((float)art.Rect.Width, (float)art.Rect.Height);
            if (!anchorBottom)
            {
                sp.RelativeOffsetAdjustment = Vector3.Zero;
                sp.Offset = new Vector3((float)art.Rect.X, (float)art.Rect.Y, 0);
                return;
            }
            float ax = _s.Style == TitleStyle.B ? 0 : 0.5f;
            float anchorX = (float)(r.Width * ax);
            sp.RelativeOffsetAdjustment = new Vector3(ax, 1, 0);
            sp.Offset = new Vector3((float)art.Rect.X - anchorX, (float)(art.Rect.Y - r.Height), 0);
            sp.CenterPoint = new Vector3(anchorX - (float)art.Rect.X, (float)(r.Height - art.Rect.Y), 0);
        }

        private void BuildCopy(Engine.Rect r, Visual? scaleWith)
        {
            if (_item == null || _inner == null) return;
            var oldCopy = _copyArt; var oldActions = _actionsArt;
            _copyArt = _s.Text.Copy(_item, r.Width, r.Height, _expanded, _s.Style);
            if (_copy == null) { _copy = TextSprite(_copyArt, r, anchorBottom: true); _inner.Children.InsertAtTop(_copy); }
            else { ((CompositionSurfaceBrush)_copy.Brush).Surface = _copyArt.Surface; PositionText(_copy, _copyArt, r, true); }
            if (_expanded)
            {
                _actionsArt = _s.Text.Actions(r.Width, r.Height, _s.Style, _item.Kind == ItemKind.Game && !_item.Installed);
                if (_actions == null)
                {
                    _actions = TextSprite(_actionsArt, r, anchorBottom: true);
                    _inner.Children.InsertAtTop(_actions);
                    // .focus-actions：卡起步后 140ms 从下方 12px 淡入（240ms）
                    if (!_s.Reduced)
                    {
                        var o = _actions.Offset;
                        var op = G.Scalar(0, 1, Motion.FocusActionsMs, Bezier.EaseInOut, Motion.FocusActionsDelayMs);
                        var rise = G.C.CreateVector3KeyFrameAnimation();
                        rise.InsertKeyFrame(0, o + new Vector3(0, (float)Motion.FocusActionsRise, 0), G.Linear);
                        rise.InsertKeyFrame(1, o, G.Ease(Bezier.EaseInOut));
                        rise.Duration = Gfx.Ms(Motion.FocusActionsMs);
                        rise.DelayTime = Gfx.Ms(Motion.FocusActionsDelayMs);
                        rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                        _actions.StartAnimation("Opacity", op);
                        _actions.StartAnimation("Offset", rise);
                    }
                }
                else { ((CompositionSurfaceBrush)_actions.Brush).Surface = _actionsArt.Surface; _actions.StopAnimation("Offset"); PositionText(_actions, _actionsArt, r, true); }
            }
            _s.Text.Release(oldCopy);
            _s.Text.Release(oldActions);
            if (scaleWith != null)
            {
                // 让位途中：文字按终点尺寸画好，随卡宽等比缩放（网页里字号是 cqi，跟卡宽走）
                foreach (var sp in new[] { _copy, _actions })
                {
                    if (sp == null) continue;
                    var e = G.C.CreateExpressionAnimation($"Vector3(el.Size.X / {r.Width:F3}, el.Size.X / {r.Width:F3}, 1)");
                    e.SetReferenceParameter("el", scaleWith);
                    sp.StartAnimation("Scale", e);
                }
            }
        }

        public void EndScale()
        {
            foreach (var sp in new[] { _copy, _actions })
            {
                if (sp == null) continue;
                sp.StopAnimation("Scale");
                sp.Scale = Vector3.One;
            }
        }

        /// <summary>卡尺寸变了：按新尺寸重画标题（和网页 cqi 一样），并重新取景。</summary>
        public void Relayout(Engine.Rect r, Visual? scaleWith)
        {
            if (_item == null) return;
            BuildCopy(r, scaleWith);
            if (_tintBrush != null) Gfx.SetLinear(_tintBrush, 145, r.Width, r.Height);
            if (_badgeArt != null && _badge != null) { var nb = _s.Text.Badge(_item.Badge, _item.Kind == ItemKind.Collection); _s.Text.Release(_badgeArt); _badgeArt = nb; ((CompositionSurfaceBrush)_badge.Brush).Surface = nb.Surface; }
        }

        // ---------------- 封面图层：接着画、随卡取景、交叉淡入 ----------------

        private sealed class ImageLayer
        {
            public required SpriteVisual Sprite;
            public required CompositionSurfaceBrush Brush;
            public LoadedImageSurface? Surface;
            public char Kind;
            public float Ratio;
            // 取景动画（算此刻的取景用）
            public Engine.Rect From, To;
            public long Start;
            public double Ms;
        }

        public ImageCarry? CurrentImage => _img == null ? null : new ImageCarry(_img.Kind, _img.Surface, RatioNow(_img));

        /// <summary>这一层此刻的取景（取景动画途中按同一条曲线算）。</summary>
        private float RatioNow(ImageLayer l)
        {
            if (l.Ms <= 0 || _item?.Art is not { } art) return l.Ratio;
            double t = Math.Clamp((_s.NowMs - l.Start) / l.Ms, 0, 1);
            if (t >= 1) return l.Ratio;
            double e = Motion.ReflowEase(t);
            double w = l.From.Width + (l.To.Width - l.From.Width) * e, h = l.From.Height + (l.To.Height - l.From.Height) * e;
            return (float)(ObjectX(art, l.Kind, w, h) / 100);
        }

        private ImageLayer MakeLayer(char kind, LoadedImageSurface? surface, float ratio)
        {
            var brush = G.C.CreateSurfaceBrush(surface);
            brush.Stretch = CompositionStretch.UniformToFill;
            brush.VerticalAlignmentRatio = 0.5f;
            brush.HorizontalAlignmentRatio = ratio;
            var sp = G.Sprite(brush);
            _imgHost!.Children.InsertAtTop(sp);
            return new ImageLayer { Sprite = sp, Brush = brush, Surface = surface, Kind = kind, Ratio = ratio };
        }

        /// <summary>按显示尺寸解码：cover 时的缩放 × 栅格倍率，不超过原图；32px 一档好共用。</summary>
        private LoadedImageSurface? AcquireFor(ArtKit art, char kind, Engine.Rect r)
        {
            var (iw, ih) = NativeSize(art, kind);
            double cover = Math.Max(r.Width / iw, r.Height / ih) * G.RasterScale;
            double k2 = Math.Min(1, cover);
            return _s.Images.Acquire(art.File(kind), Bucket(iw * k2), Bucket(ih * k2));
        }

        /// <summary>取景（水平对齐比例）沿卡片的让位曲线走：每一刻都等于按那一刻的宽高比算的值。</summary>
        private void AnimateRatio(ImageLayer l, ArtKit art, Engine.Rect from, Engine.Rect to, double ms)
        {
            float end = (float)(ObjectX(art, l.Kind, to.Width, to.Height) / 100);
            float startRatio = RatioNow(l);
            if (ms < 16) { l.Brush.StopAnimation("HorizontalAlignmentRatio"); l.Brush.HorizontalAlignmentRatio = end; l.Ratio = end; l.Ms = 0; return; }
            var a = G.C.CreateScalarKeyFrameAnimation();
            const int n = 24;
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n, e = Motion.ReflowEase(t);
                double w = from.Width + (to.Width - from.Width) * e, h = from.Height + (to.Height - from.Height) * e;
                a.InsertKeyFrame((float)t, (float)(ObjectX(art, l.Kind, w, h) / 100), G.Linear);
            }
            a.Duration = Gfx.Ms(ms);
            _ = startRatio;   // 关键帧第 0 帧就是按 from 算的此刻取景
            l.Brush.StartAnimation("HorizontalAlignmentRatio", a);
            l.Ratio = end;
            l.From = from; l.To = to; l.Start = _s.NowMs; l.Ms = ms;
        }

        /// <summary>
        /// 卡片从 from 变到 to（ms 内，让位弹簧）：当前这张图原样留着、取景跟着走；要换成另一种图（竖封面 ⇄ 大背景裁切）时，
        /// 新图在旧图上面，解码好之后在剩下的时间里淡入（至少 180 ms）。第一帧永远和变之前一模一样。
        /// </summary>
        public void TransitionArt(Engine.Rect from, Engine.Rect to, double ms)
        {
            if (_item?.Art is not { } art || _img == null) return;
            Promote(force: true);
            var kind = KindFor(art, to) ?? _img.Kind;
            AnimateRatio(_img, art, from, to, ms);
            if (kind == _img.Kind) { UpgradeDecode(art, to); return; }
            var inc = MakeLayer(kind, AcquireFor(art, kind, to), (float)(ObjectX(art, kind, from.Width, from.Height) / 100));
            inc.Sprite.Opacity = 0;
            AnimateRatio(inc, art, from, to, ms);
            _incoming = inc;
            long start = _s.NowMs;
            void FadeIn()
            {
                if (_incoming != inc) return;
                double remain = Math.Max(180, ms - (_s.NowMs - start));
                inc.Sprite.StartAnimation("Opacity", G.Scalar(0, 1, remain, Bezier.EaseInOut));
                inc.Sprite.Opacity = 1;
                _s.After(remain + 30, () => { if (_incoming == inc) Promote(force: false); });
            }
            if (inc.Surface == null) FadeIn(); else _s.Images.WhenLoaded(inc.Surface, FadeIn);
        }

        /// <summary>淡入完成（或要开始下一次过渡）：新图层成为当前图层，旧的拿掉。</summary>
        private void Promote(bool force)
        {
            var inc = _incoming;
            if (inc == null) return;
            if (!force && !_s.Images.IsLoaded(inc.Surface) && inc.Surface != null) return;
            _incoming = null;
            inc.Sprite.StopAnimation("Opacity");
            inc.Sprite.Opacity = 1;
            if (_img != null)
            {
                _imgHost!.Children.Remove(_img.Sprite);
                _s.Images.Release(_img.Surface);
            }
            _img = inc;
            _artKind = inc.Kind;
        }

        /// <summary>同一张图：卡变大了需要更高的解码尺寸时，新尺寸解码好再换（内容相同，看不出切换）。</summary>
        private void UpgradeDecode(ArtKit art, Engine.Rect to)
        {
            var l = _img!;
            var next = AcquireFor(art, l.Kind, to);
            if (next == null || ReferenceEquals(next, l.Surface)) { _s.Images.Release(next); return; }
            _s.Images.WhenLoaded(next, () =>
            {
                if (_img != l || !_s.Images.IsLoaded(next)) { _s.Images.Release(next); return; }
                var old = l.Surface;
                l.Surface = next;
                l.Brush.Surface = next;
                _s.Images.Release(old);
            });
        }

        /// <summary>尺寸定下来（让位结束 / 直接摆放）：按新尺寸重画标题；图该换就交叉淡入，不再硬切。</summary>
        public void FitArt(Engine.Rect r)
        {
            EndScale();
            Relayout(r, null);
            if (_item?.Art is not { } art || _img == null) return;
            if (_incoming != null) { Promote(force: false); if (_incoming != null) return; }
            var kind = KindFor(art, r);
            if (kind is char k && k != _img.Kind) { TransitionArt(r, r, 0); return; }
            AnimateRatio(_img, art, r, r, 0);
            UpgradeDecode(art, r);
        }

        private static int Bucket(double v) => (int)Math.Ceiling(v / 32) * 32;

        private static (double w, double h) NativeSize(ArtKit art, char kind) => kind switch
        {
            'p' => (400, 600),
            'q' => (600, 900),
            'g' => (1080 * (art.Ha ?? 3.1), 1080),
            'v' => (440, 660),
            'n' => (320, 650),
            's' => (360, 360),
            'x' => (900, 900),
            _ => (1600, 1600 / Math.Max(0.1, art.Ha ?? 3.1)),
        };

        public void ApplyFocus(bool focused, bool expanded, bool animate)
        {
            if (_item == null) return;
            // 焦点环 + 投影：box-shadow 600ms ease；集合的叠页边同时褪掉
            float ring = focused ? 1 : 0;
            SetOpacity(_ring, ring, Motion.ShadowMs, Bezier.Ease_, animate);
            SetOpacity(_stack1, 1 - ring, Motion.ShadowMs, Bezier.Ease_, animate);
            SetOpacity(_stack2, 1 - ring, Motion.ShadowMs, Bezier.Ease_, animate);
            if (focused || expanded)
            {
                if (_shadow == null)
                {
                    _shadow = G.C.CreateDropShadow();
                    _shadow.SourcePolicy = CompositionDropShadowSourcePolicy.Default;
                    _shadowCaster!.Shadow = _shadow;
                }
                // --focus-lift: 0 12px 32px / 45%；展开：0 42px 110px / 74%
                _shadow.Offset = expanded ? new Vector3(0, 42, 0) : new Vector3(0, 12, 0);
                _shadow.BlurRadius = expanded ? 110 : 32;
                _shadow.Color = expanded ? Gfx.Rgba(0, 0, 0, 0.74) : Gfx.Rgba(0, 0, 0, 0.45);
                SetOpacity(_shadowCaster, 1, Motion.ShadowMs, Bezier.Ease_, animate);
            }
            else SetOpacity(_shadowCaster, 0, Motion.ShadowMs, Bezier.Ease_, animate);
            SetOpacity(_tint, focused || expanded ? 0 : (float)Theme.TintOpacity, Motion.TintMs, Bezier.Ease_, animate);
            SetOpacity(_hilite, focused ? 1 : 0, Motion.HighlightMs, Bezier.Ease_, animate);
        }

        private void SetOpacity(Visual? v, float to, double ms, Bezier ease, bool animate)
        {
            if (v == null) return;
            if (!animate) { v.StopAnimation("Opacity"); v.Opacity = to; return; }
            v.StartAnimation("Opacity", G.Scalar(v.Opacity, to, ms, ease));
            v.Opacity = to;
        }

        public void Dispose()
        {
            _s.Images.Release(_img?.Surface);
            _s.Images.Release(_incoming?.Surface);
            _s.Text.Release(_copyArt);
            _s.Text.Release(_actionsArt);
            _s.Text.Release(_badgeArt);
            _s.Text.Release(_glyphArt);
            Root.Children.RemoveAll();
            Root.Dispose();
        }
    }

    // ---------------- 选图（照 tile.ts pickArt / objectX） ----------------

    /// <summary>按卡片宽高比挑封面套件里的哪张图（规则见 game-library-ui/封面尺寸规范.md）：
    /// 宽高比 &lt; 0.8 的竖卡用 Steam 竖封面 p；其余都用大背景：方卡 s / x、横卡 h；缺哪张就退到有的那张。</summary>
    public static char? PickArt(ArtKit art, double w, double h, bool expanded)
    {
        double r = w / Math.Max(h, 1);
        string want = expanded ? "xhp"
            : r < 0.8 ? "pvnx"
            : r <= 1.12 ? (Math.Max(w, h) >= 500 ? "xshp" : "sxhp")
            : "hxp";
        foreach (char k in want) if (art.Has(k)) return k;
        return null;
    }

    /// <summary>横图在卡里取景：一律从正中裁（按焦点取景常常把主体裁掉，比如艾尔登法环）。</summary>
    public static double ObjectX(ArtKit art, char kind, double w, double h) => 50;
}
