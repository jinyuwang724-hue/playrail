// 控制中心 → 手柄：线稿上的动效，全用 Composition 动画在合成线程上跑（满帧，不靠整页重画）。
// 震动：线稿层带衰减地抖一下（幅度跟强度走），对应握把亮一下，再有一圈握把形状的描边往外扩散淡出（左右各按自己的强度）。
using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private ContainerVisual _gpArtLayer = null!;
    private readonly Dictionary<string, SpriteVisual> _gpGlow = new(), _gpEcho = new();

    /// <summary>握把发光 / 描边扩散的两层；线稿和这些都在 _gpArtLayer 里，抖的时候一起抖。</summary>
    private void EnsureGpFx()
    {
        foreach (var side in new[] { "GripL", "GripR" })
        {
            foreach (var dict in new[] { _gpGlow, _gpEcho })
            {
                var s = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
                s.Opacity = 0;
                dict[side] = s;
                _gpArtLayer.Children.InsertAtTop(s);
            }
        }
    }

    /// <summary>按当前缩放把握把发光 / 描边画进各自的面，摆好位置。线稿和区域都载入后调。</summary>
    private void PaintGpFx(float k, float raster)
    {
        var accent = PerfAccent;
        foreach (var side in new[] { "GripL", "GripR" })
        {
            if (!_gpRegions.TryGetValue(side, out var region)) continue;
            var b = region.ComputeBounds();
            float pad = 6, w = (float)b.Width + pad * 2, h = (float)b.Height + pad * 2;
            var shift = Matrix3x2.CreateTranslation(-(float)b.X + pad, -(float)b.Y + pad);
            foreach (var (dict, fill) in new[] { (_gpGlow, true), (_gpEcho, false) })
            {
                var sprite = dict[side];
                CcSet(sprite, CcDraw(w, h, raster, ds =>
                {
                    using var g = region.Transform(shift);
                    if (fill) ds.FillGeometry(g, Gfx.WithAlpha(accent, 0.75f));
                    else ds.DrawGeometry(g, accent, 2.2f);
                }));
                sprite.Size = new Vector2(w * k, h * k);
                sprite.Offset = new Vector3(((float)b.X - pad) * k, ((float)b.Y - pad) * k, 0);
                sprite.CenterPoint = new Vector3(w * k / 2, h * k / 2, 0);
            }
        }
    }

    /// <summary>震一下的画面：左右强度 0–1。抖动 420 ms 衰减到 0；握把亮起再暗下；握把形状的描边往外扩 1.0→1.3 淡出。</summary>
    private void GpPlayRumble(float left, float right)
    {
        if (_gp == null || Reduced) return;
        float amp = 1.2f + 2.8f * Math.Max(left, right);
        var shake = G.C.CreateVector3KeyFrameAnimation();
        var linear = G.C.CreateLinearEasingFunction();
        const int steps = 14;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps, decay = (1 - t) * (1 - t);
            float x = i == steps ? 0 : (i % 2 == 0 ? 1 : -1) * amp * decay * _ccK;
            float y = i == steps ? 0 : (i % 4 < 2 ? 0.35f : -0.35f) * amp * decay * _ccK;
            shake.InsertKeyFrame(t, new Vector3(x, y, 0), linear);
        }
        shake.Duration = TimeSpan.FromMilliseconds(420);
        _gpArtLayer.StartAnimation("Offset", shake);
        foreach (var (side, s) in new[] { ("GripL", left), ("GripR", right) })
        {
            if (s <= 0 || !_gpGlow.ContainsKey(side)) continue;
            var glow = G.C.CreateScalarKeyFrameAnimation();
            glow.InsertKeyFrame(0, 0);
            glow.InsertKeyFrame(0.18f, 0.25f + 0.6f * s);
            glow.InsertKeyFrame(1, 0);
            glow.Duration = TimeSpan.FromMilliseconds(650);
            _gpGlow[side].StartAnimation("Opacity", glow);
            var fade = G.C.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0.9f * s + 0.1f);
            fade.InsertKeyFrame(1, 0);
            fade.Duration = TimeSpan.FromMilliseconds(720);
            var grow = G.C.CreateVector3KeyFrameAnimation();
            grow.InsertKeyFrame(0, Vector3.One);
            grow.InsertKeyFrame(1, new Vector3(1.18f + 0.14f * s, 1.18f + 0.14f * s, 1), G.C.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1), new Vector2(0.36f, 1)));
            grow.Duration = TimeSpan.FromMilliseconds(720);
            _gpEcho[side].StartAnimation("Opacity", fade);
            _gpEcho[side].StartAnimation("Scale", grow);
        }
    }
}
