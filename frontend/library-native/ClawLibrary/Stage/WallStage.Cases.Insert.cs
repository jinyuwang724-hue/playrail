// 卡盒版的启动动画：插卡（规格 6）。卡盒飞到屏幕中间、转过来、盖子沿书脊打开，卡带抬起、沿弧线飞向屏幕底边的卡槽插进去，
// 卡槽发光、扫过一道亮光、整屏轻震；盖子合上、卡盒飞回原位。1580 ms 插进去的那一刻交给现有的启动流程（启动画面、等游戏到前台）。
// 启动失败、按 B 取消或超时：整段倒放（退卡）。时间线逐帧在 UI 线程上算（2.2 s），倒放就是时间往回走，中途随时可以反向。
//
// 时间线、各个面和音效移植自 "Finally 3D Game Cases"（https://github.com/otisabiburada/finally-3d-game-cases-plugin，
// src/engine.ts 的 runInsert；掌机变体：卡槽在屏幕底边）。
//
// BSD 3-Clause License
//
// Copyright (c) 2026, SANSO
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
//
// 1. Redistributions of source code must retain the above copyright notice, this
//    list of conditions and the following disclaimer.
//
// 2. Redistributions in binary form must reproduce the above copyright notice,
//    this list of conditions and the following disclaimer in the documentation
//    and/or other materials provided with the distribution.
//
// 3. Neither the name of the copyright holder nor the names of its
//    contributors may be used to endorse or promote products derived from
//    this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
// FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
// DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
// SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
using System.Buffers.Binary;
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const double InsertMs = 2200, InsertClickMs = 1580;

    /// <summary>一条关键帧轨道（Web Animations 的写法）：每个关键帧的缓动管到下一帧；global 是整条的缓动。</summary>
    private sealed class Kf
    {
        private readonly (double At, float[] V, Bezier? E)[] _k;
        private readonly Bezier? _global;

        public Kf(Bezier? global, params (double Ms, float[] V, Bezier? E)[] k)
        {
            _global = global;
            _k = k.Select(x => (x.Ms / InsertMs, x.V, x.E)).ToArray();
        }

        public float[] At(double ms)
        {
            double p = Math.Clamp(ms / InsertMs, 0, 1);
            if (_global is { } g) p = g.Ease(p);
            int i = 0;
            while (i < _k.Length - 2 && p > _k[i + 1].At) i++;
            var (a, va, e) = _k[i];
            var (b, vb, _) = _k[i + 1];
            double t = b - a <= 1e-9 ? 1 : Math.Clamp((p - a) / (b - a), 0, 1);
            if (e is { } ee) t = ee.Ease(t);
            var r = new float[va.Length];
            for (int j = 0; j < r.Length; j++) r[j] = (float)(va[j] + (vb[j] - va[j]) * t);
            return r;
        }
    }

    private sealed class CsInsertRun
    {
        public required LibraryItem Item;
        public required Visual Source;
        public required ContainerVisual Root, Shake, Wrap, St, Cover, Holder, Slot;
        public required SpriteVisual Shade, Glow, Floor, Glint, Seat, Bezel, SlotGlow, Streak, HolderShadow;
        public required Kf KShade, KSeat, KGlow, KWrap, KFloor, KGlint, KSt, KCover, KHolder, KShake, KBezel, KStreak, KShadow, KSlot;
        public required float Cw, Ch, D, DH;
        public required Vector2 HolderAt;
        public LoadedImageSurface? Poster, Label;
        public double T;
        public int Dir;
        public long Last;
        public bool Launched, Done, Ejecting;
        public required (float X, float Y, float W, float H) From;
    }

    private CsInsertRun? _csInsert;
    private bool _csInsertLoop;
    private Windows.Foundation.Rect? _lmFromRect;
    private CompositionSurfaceBrush? _csCartLight, _csCartLightTop, _csSlotFace, _csBezelFace;

    private static Bezier Bz(float a, float b, float c, float d) => new(a, b, c, d);

    /// <summary>A：插卡启动。正在玩游戏、没装、降低动态效果时直接走原来的启动。</summary>
    private void CsLaunch(LibraryItem g, Visual source, (float X, float Y, float W, float H) rect)
    {
        if (_csInsert != null) return;
        Cue(Sfx.Activate);
        if (!_csDemo && (_game != null || !g.Installed || Reduced))
        {
            _lmFromRect = CsScreenRect(rect);
            LaunchOrInstall(g);
            _lmFromRect = null;
            return;
        }
        var run = CsBuildInsert(g, source, rect);
        _csInsert = run;
        source.IsVisible = false;
        _cs!.Header.StartAnimation("Opacity", CsS(0, 300, CsQuart));
        run.Dir = 1;
        run.Last = NowMs;
        CsInsertApply(run);
        CsInsertSound(false, 0);
        CsInsertLoop(true);
        Log.Info($"cases insert {g.Id} {g.Title}");
    }

    private void CsInsertLoop(bool on)
    {
        if (on == _csInsertLoop) return;
        _csInsertLoop = on;
        if (on) CompositionTarget.Rendering += CsInsertFrame;
        else CompositionTarget.Rendering -= CsInsertFrame;
    }

    private void CsInsertFrame(object? sender, object e)
    {
        var run = _csInsert;
        if (run == null || run.Dir == 0) { CsInsertLoop(false); return; }
        long now = NowMs;
        double dt = Math.Min(50, now - run.Last);
        run.Last = now;
        run.T = Math.Clamp(run.T + run.Dir * dt, 0, InsertMs);
        CsInsertApply(run);
        if (run.Dir > 0 && !run.Launched && run.T >= InsertClickMs) CsInsertHandOff(run);
        if (run.Dir > 0 && run.T >= InsertMs)
        {
            run.Dir = 0;
            run.Done = true;
            // 启动画面已经盖住了：藏起来，等它收起（交给游戏 / 取消）再决定拆掉还是退卡
            run.Root.IsVisible = false;
            CsInsertLoop(false);
        }
        else if (run.Dir < 0 && run.T <= 0) CsInsertFinish(run);
    }

    /// <summary>卡带插到底：照原来的方式启动（启动画面从卡盒原位升起）。没起来（出错）就退卡。</summary>
    private void CsInsertHandOff(CsInsertRun run)
    {
        run.Launched = true;
        _lmFromRect = CsScreenRect(run.From);
        if (_csDemo) { Cue(Sfx.Launch); StartLaunchMotion(run.Item, null); _lmFromRect = null; return; }
        try { LaunchOrInstall(run.Item); }
        catch (Exception ex) { Log.Error("cases launch", ex); }
        _lmFromRect = null;
        if (_lm == null)
        {
            if (run.Item.XboxAumid != null) After(1500, () => { if (_csInsert == run && _lm == null) CsEject(run, 0); });
            else CsEject(run, 0);
        }
    }

    /// <summary>启动画面收起时（WallStage.LaunchMotion.cs 的 EndLaunchMotion）：交给游戏就拆掉，取消 / 超时就退卡。</summary>
    private void CsLaunchEnded(string reason)
    {
        var run = _csInsert;
        if (run == null) return;
        if (reason is "back" or "timeout") { CsEject(run, run.Done ? 300 : 0); return; }
        if (run.Done) CsInsertFinish(run);
        else After(Math.Max(0, InsertMs - run.T) + 20, () => { if (_csInsert == run && run.Done) CsInsertFinish(run); });
    }

    /// <summary>B（插卡途中）：从此刻倒放。</summary>
    private void CsInsertBack()
    {
        var run = _csInsert;
        if (run == null || run.Ejecting) { Cue(Sfx.None); return; }
        Cue(Sfx.None);
        if (run.Launched) return;   // 已经交给启动画面：B 由它处理（EndLaunchMotion → 退卡）
        CsEject(run, 0);
    }

    private void CsEject(CsInsertRun run, double delay)
    {
        if (run.Ejecting) return;
        run.Ejecting = true;
        run.Launched = true;
        void Go()
        {
            if (_csInsert != run) return;
            run.Root.IsVisible = true;
            run.Dir = -1;
            run.Last = NowMs;
            CsInsertSound(true, run.T);
            CsInsertLoop(true);
        }
        if (run.Done) { run.Root.IsVisible = true; CsInsertApply(run); }
        if (delay > 0) After(delay, Go); else Go();
        Log.Info($"cases eject {run.Item.Id} from {run.T:F0}ms");
    }

    private void CsInsertFinish(CsInsertRun run)
    {
        if (_csInsert != run) return;
        _csInsert = null;
        CsInsertLoop(false);
        run.Source.IsVisible = true;
        _top.Children.Remove(run.Root);
        Images.Release(run.Poster);
        Images.Release(run.Label);
        run.Root.Dispose();
        _cs?.Header.StartAnimation("Opacity", CsS(1, 300, CsQuart));
    }

    // ---------------- 各个面 ----------------

    private CsInsertRun CsBuildInsert(LibraryItem g, Visual source, (float X, float Y, float W, float H) rect)
    {
        CsEnsureShared();
        const float W = CsW, H = CsH;
        var (cA, _) = CsLookOf(g);
        var cB = Gfx.Mix(cA, 0.45, Colors.Black);
        float ch = MathF.Round(Math.Min(H * 0.56f, W * 0.42f * 1.5f)), cw = MathF.Round(ch / 1.5f);
        float cx = MathF.Round(W * 0.56f - cw / 2), cy = MathF.Round(H * 0.42f - ch / 2), d = Math.Max(10, MathF.Round(cw * 0.06f));
        float s0 = rect.W / cw, x0 = rect.X + rect.W / 2 - (cx + cw / 2), y0 = rect.Y + rect.H / 2 - (cy + ch / 2);
        const float rad = 7;

        var root = G.Container(relative: false);
        root.Size = new Vector2(W, H);
        root.Scale = new Vector3(_cs!.K, _cs.K, 1);
        root.Offset = _cs.Root.Offset;
        var shake = G.Container(relative: false);
        shake.Size = new Vector2(W, H);
        shake.Clip = G.C.CreateInsetClip();
        root.Children.InsertAtTop(shake);

        // 底：radial-gradient(120% 95% at 55% 40%, 主色压暗 82%, #050506)
        var shadeBrush = G.C.CreateRadialGradientBrush();
        shadeBrush.MappingMode = CompositionMappingMode.Absolute;
        shadeBrush.EllipseCenter = new Vector2(W * 0.55f, H * 0.40f);
        shadeBrush.EllipseRadius = new Vector2(W * 1.2f * 0.75f, H * 0.95f * 0.85f);
        shadeBrush.ColorStops.Add(G.C.CreateColorGradientStop(0, Gfx.Mix(cA, 0.18, Colors.Black)));
        shadeBrush.ColorStops.Add(G.C.CreateColorGradientStop(1, Gfx.Rgba(5, 5, 6)));
        var shade = G.Sprite(shadeBrush, relative: false);
        shade.Size = new Vector2(W, H);
        shake.Children.InsertAtTop(shade);
        var glowBrush = G.C.CreateRadialGradientBrush();
        glowBrush.ColorStops.Add(G.C.CreateColorGradientStop(0, cA));
        glowBrush.ColorStops.Add(G.C.CreateColorGradientStop(1, Gfx.WithAlpha(cA, 0)));
        var glow = G.Sprite(glowBrush, relative: false);
        glow.Offset = new Vector3(MathF.Round(cx - cw * 0.9f), MathF.Round(cy - ch * 0.35f), 0);
        glow.Size = new Vector2(MathF.Round(cw * 2.8f), MathF.Round(ch * 1.7f));
        shake.Children.InsertAtTop(glow);

        float slotY = H - 7;
        float D = MathF.Round(cw * 0.5f), DH = MathF.Round(D * 1.16f), dcx = cx + cw / 2, dcy = cy + ch * 0.36f;
        var bezel = G.Sprite(CsBezelFace(D), relative: false);
        bezel.Offset = new Vector3(MathF.Round(W / 2 - D * 0.8f), slotY - 12, 0);
        bezel.Size = new Vector2(MathF.Round(D * 1.6f), 24);
        shake.Children.InsertAtTop(bezel);

        // 卡盒：wrap 管飞行，persp 给透视（1400，原点 50% 35%），st 整体转向，盖子沿书脊开合
        var wrap = G.Container(relative: false);
        wrap.Size = new Vector2(cw, ch);
        wrap.CenterPoint = new Vector3(cw / 2, ch / 2, 0);
        shake.Children.InsertAtTop(wrap);
        var floorBrush = G.C.CreateRadialGradientBrush();
        floorBrush.ColorStops.Add(G.C.CreateColorGradientStop(0, Gfx.Rgba(0, 0, 0, 0.7)));
        floorBrush.ColorStops.Add(G.C.CreateColorGradientStop(1, Gfx.Rgba(0, 0, 0, 0)));
        var floor = G.Sprite(floorBrush, relative: false);
        floor.Size = new Vector2(cw * 1.16f, ch * 0.12f);
        floor.Offset = new Vector3(-cw * 0.08f, ch * (1 + 0.09f - 0.12f), 0);
        floor.CenterPoint = new Vector3(floor.Size.X / 2, floor.Size.Y / 2, 0);
        wrap.Children.InsertAtTop(floor);
        var persp = G.Container(relative: false);
        persp.Offset = new Vector3(cw / 2, ch * 0.35f, 0);
        persp.TransformMatrix = Tile.Perspective(1400);
        wrap.Children.InsertAtTop(persp);
        var st = G.Container(relative: false);
        st.Size = new Vector2(cw, ch);
        st.Offset = new Vector3(-cw / 2, -ch * 0.35f, 0);
        persp.Children.InsertAtTop(st);

        SpriteVisual Face(float x, float y, float w, float h, CompositionBrush brush, float r, Matrix4x4 m)
        {
            var s = G.Sprite(brush, relative: false);
            s.Offset = new Vector3(x, y, 0);
            s.Size = new Vector2(w, h);
            if (r > 0) s.Clip = G.C.CreateGeometricClip(RoundRect(w, h, r));
            s.TransformMatrix = m;
            st.Children.InsertAtTop(s);
            return s;
        }
        var bBrush = G.Solid(cB);
        var back = Face(0, 0, cw, ch, bBrush, rad, Matrix4x4.CreateTranslation(0, 0, -d));
        var backSheen = Face(0, 0, cw, ch, G.LinearGradient(135, cw, ch, (0, Gfx.Rgba(255, 255, 255, 0.10)), (1, Gfx.Rgba(0, 0, 0, 0.25))), rad, Matrix4x4.CreateTranslation(0, 0, -d));
        _ = back; _ = backSheen;
        Face(cw * 0.04f, ch * 0.03f, cw * 0.92f, ch * 0.94f, G.LinearGradient(180, cw, ch, (0, Gfx.Rgba(0x1c, 0x1e, 0x24)), (1, Gfx.Rgba(0x0f, 0x11, 0x15))), rad - 2, Matrix4x4.CreateTranslation(0, 0, -(d - 2)));
        Face(cw * 0.22f, ch * 0.16f, cw * 0.56f, ch * 0.40f, G.Solid(Gfx.Rgba(0, 0, 0, 0.55)), 6, Matrix4x4.CreateTranslation(0, 0, -(d - 3)));
        // 托槽里的卡带：880 ms 时换成飞出去的那一张
        var seatHost = G.Container(relative: false);
        seatHost.Offset = new Vector3(MathF.Round(cw / 2 - D / 2), MathF.Round(ch * 0.36f - DH / 2), 0);
        seatHost.Size = new Vector2(D, DH);
        seatHost.TransformMatrix = Matrix4x4.CreateTranslation(0, 0, -(d - 3));
        st.Children.InsertAtTop(seatHost);
        var labelPath = g.Art is { } kit ? kit.Has('x') ? kit.File('x') : kit.Has('s') ? kit.File('s') : kit.Has('p') ? kit.File('p') : null : null;
        var labelSurf = labelPath != null ? Images.AcquireReuse(labelPath, (int)(D * CsRaster * 1.1f), (int)(D * CsRaster * 1.1f)) : null;
        var seat = CsLightCart(seatHost, D, DH, cA, labelSurf);
        // 四个侧面
        static Matrix4x4 About(Vector3 o, Matrix4x4 m) => Matrix4x4.CreateTranslation(-o) * m * Matrix4x4.CreateTranslation(o);
        float R(float deg) => deg * MathF.PI / 180;
        Face(0, 0, d, ch, G.LinearGradient(90, d, ch, (0, cA), (1, cB)), 0, About(new Vector3(0, ch / 2, 0), Matrix4x4.CreateRotationY(R(90))));
        Face(cw - d, 0, d, ch, G.LinearGradient(270, d, ch, (0, cB), (1, cA)), 0, About(new Vector3(d, ch / 2, 0), Matrix4x4.CreateRotationY(R(-90))));
        Face(0, 0, cw, d, G.LinearGradient(0, cw, d, (0, cB), (1, cA)), 0, About(new Vector3(cw / 2, 0, 0), Matrix4x4.CreateRotationX(R(-90))));
        Face(0, ch - d, cw, d, G.LinearGradient(180, cw, d, (0, cB), (1, Colors.Black)), 0, About(new Vector3(cw / 2, d, 0), Matrix4x4.CreateRotationX(R(90))));
        // 盖子：正面封面 + 卡盒质感 + 扫光；背面压暗的封面
        var cover = G.Container(relative: false);
        cover.Size = new Vector2(cw, ch);
        st.Children.InsertAtTop(cover);
        var posterPath = g.Art is { } k2 ? k2.Has('q') ? k2.File('q') : k2.Has('p') ? k2.File('p') : k2.Has('v') ? k2.File('v') : null : null;
        var posterSurf = posterPath != null ? Images.AcquireReuse(posterPath, (int)(cw * CsRaster), (int)(ch * CsRaster)) : null;
        SpriteVisual CoverFace(CompositionBrush brush, Matrix4x4? m = null)
        {
            var s = G.Sprite(brush, relative: false);
            s.Size = new Vector2(cw, ch);
            s.Clip = G.C.CreateGeometricClip(RoundRect(cw, ch, rad));
            s.BackfaceVisibility = CompositionBackfaceVisibility.Hidden;
            if (m is { } mm) s.TransformMatrix = mm;
            cover.Children.InsertAtTop(s);
            return s;
        }
        var flip = About(new Vector3(cw / 2, ch / 2, 0), Matrix4x4.CreateRotationY(MathF.PI));
        var pb = G.C.CreateSurfaceBrush(posterSurf); pb.Stretch = CompositionStretch.UniformToFill;
        CoverFace(G.Solid(cB), flip);
        var pbBack = G.C.CreateSurfaceBrush(posterSurf); pbBack.Stretch = CompositionStretch.UniformToFill;
        CoverFace(pbBack, flip);
        CoverFace(G.Solid(Gfx.Rgba(12, 14, 19, 0.88)), flip);
        CoverFace(G.Solid(cB));
        CoverFace(pb);
        CoverFace(_csOverlay!);
        var band = G.Sprite(G.Solid(cA), relative: false);
        band.Size = new Vector2(cw, ch * 0.046f);
        band.BackfaceVisibility = CompositionBackfaceVisibility.Hidden;
        band.Clip = G.C.CreateGeometricClip(RoundRect(cw, ch * 0.046f + rad, rad));
        cover.Children.InsertAtTop(band);
        var glintHost = G.Container(relative: false);
        glintHost.Size = new Vector2(cw, ch);
        glintHost.Clip = G.C.CreateGeometricClip(RoundRect(cw, ch, rad));
        glintHost.BackfaceVisibility = CompositionBackfaceVisibility.Hidden;
        cover.Children.InsertAtTop(glintHost);
        var glint = G.Sprite(G.LinearGradient(100, cw * 0.45, ch * 1.4, (0, Gfx.Rgba(255, 255, 255, 0)), (0.5, Gfx.Rgba(255, 255, 255, 0.38)), (1, Gfx.Rgba(255, 255, 255, 0))), relative: false);
        glint.Size = new Vector2(cw * 0.45f, ch * 1.4f);
        glint.Offset = new Vector3(-cw * 0.6f, -ch * 0.2f, 0);
        glint.RotationAngleInDegrees = 14;
        glintHost.Children.InsertAtTop(glint);

        // 飞出去的卡带：在卡槽这一层（屏幕底边以下裁掉）
        var layer = G.Container(relative: false);
        layer.Size = new Vector2(W, H);
        layer.Clip = G.C.CreateInsetClip(0, 0, 0, H - slotY);
        shake.Children.InsertAtTop(layer);
        var holder = G.Container(relative: false);
        var holderAt = new Vector2(MathF.Round(dcx - D / 2), MathF.Round(dcy - DH / 2));
        holder.Offset = new Vector3(holderAt, 0);
        holder.Size = new Vector2(D, DH);
        holder.Opacity = 0;
        layer.Children.InsertAtTop(holder);
        var hShadow = G.Sprite(_csShadowBrush, relative: false);
        hShadow.Size = new Vector2(D * 0.88f + CsSoftPad * 2 - 40, DH * 0.88f + CsSoftPad * 2 - 40);
        hShadow.Offset = new Vector3(D * 0.06f - CsSoftPad + 20, DH * 0.09f - CsSoftPad + 20, 0);
        holder.Children.InsertAtTop(hShadow);
        var cartHost = G.Container(relative: false);
        cartHost.Size = new Vector2(D, DH);
        holder.Children.InsertAtTop(cartHost);
        CsLightCart(cartHost, D, DH, cA, labelSurf);
        Images.AddRef(labelSurf);

        var slot = G.Container(relative: false);
        slot.Offset = new Vector3(MathF.Round(W / 2 - D * 0.62f), slotY - 3, 0);
        slot.Size = new Vector2(MathF.Round(D * 1.24f), 7);
        slot.CenterPoint = new Vector3(slot.Size.X / 2, 3.5f, 0);
        var slotGlowBrush = G.C.CreateMaskBrush();
        slotGlowBrush.Source = G.Solid(Colors.White);
        slotGlowBrush.Mask = _csSoft;
        var slotGlow = G.Sprite(slotGlowBrush);
        slotGlow.Size = new Vector2(CsSoftPad * 2 - 40, CsSoftPad * 2 - 40);
        slotGlow.Offset = new Vector3(-CsSoftPad + 20, -CsSoftPad + 20, 0);
        slot.Children.InsertAtTop(slotGlow);
        var slotFace = G.Sprite(CsSlotFace(slot.Size.X));
        slot.Children.InsertAtTop(slotFace);
        var streak = G.Sprite(G.LinearGradient(90, slot.Size.X * 0.3, 2, (0, Gfx.Rgba(255, 255, 255, 0)), (0.5, Gfx.Rgba(255, 255, 255, 0.95)), (1, Gfx.Rgba(255, 255, 255, 0))), relative: false);
        streak.Size = new Vector2(slot.Size.X * 0.3f, 2);
        streak.Offset = new Vector3(0, 2, 0);
        slot.Children.InsertAtTop(streak);
        shake.Children.InsertAtTop(slot);
        _top.Children.InsertAtTop(root);

        // ---- 时间线（runInsert 的关键帧，毫秒）----
        float[] V(params float[] v) => v;
        var backV = V(x0, y0, s0);
        float tx = W / 2 - dcx, ty = slotY + DH * 0.62f - dcy, preX = W / 2 - dcx, preY = slotY - DH * 0.75f - dcy, midX = preX * 0.5f - D * 0.18f, midY = preY * 0.42f;
        var e1 = Bz(.2f, .8f, .3f, 1);
        return new CsInsertRun
        {
            Item = g, Source = source, Root = root, Shake = shake, Wrap = wrap, St = st, Cover = cover, Holder = holder, Slot = slot,
            Shade = shade, Glow = glow, Floor = floor, Glint = glint, Seat = seat, Bezel = bezel, SlotGlow = slotGlow, Streak = streak, HolderShadow = hShadow,
            Cw = cw, Ch = ch, D = D, DH = DH, HolderAt = holderAt, Poster = posterSurf, Label = labelSurf, From = rect,
            KShade = new(Bezier.EaseInOut, (0, V(0), null), (200, V(1), null), (1880, V(1), null), (2200, V(1), null)),
            KSeat = new(null, (0, V(1), null), (880, V(1), null), (881, V(0), null), (2200, V(0), null)),
            KGlow = new(Bezier.EaseInOut, (0, V(0), null), (500, V(.55f), null), (1500, V(.4f), null), (1900, V(0), null), (2200, V(0), null)),
            KWrap = new(null,
                (0, V(x0, y0, s0, 1), e1), (360, V(0, -6, 1.035f, 1), Bz(.4f, 0, .3f, 1)), (520, V(0, 0, 1, 1), null),
                (1640, V(0, 0, 1, 1), Bz(.6f, 0, .4f, 1)), (1900, V(backV[0], backV[1], backV[2], 1), Bezier.EaseIn),
                (1990, V(backV[0], backV[1], backV[2], 0), null), (2200, V(backV[0], backV[1], backV[2], 0), null)),
            KFloor = new(Bezier.EaseOut, (0, V(0, .6f), null), (420, V(1, 1), null), (1640, V(1, 1), null), (1880, V(0, .7f), null), (2200, V(0, 1), null)),
            KGlint = new(Bz(.4f, 0, .2f, 1), (0, V(0), null), (80, V(0), null), (560, V(MathF.Round(cw * 2.6f)), null), (2200, V(MathF.Round(cw * 2.6f)), null)),
            KSt = new(null, (0, V(0, 0), e1), (480, V(8, -22), Bezier.EaseInOut), (1500, V(6, -14), Bezier.EaseInOut), (1830, V(5, -10), Bezier.EaseOut),
                (1870, V(3, -12), Bezier.EaseInOut), (2200, V(0, 0), null)),
            KCover = new(null, (0, V(0), null), (380, V(0), Bz(.5f, 0, .3f, 1)), (860, V(-168), Bezier.EaseInOut), (960, V(-157), Bezier.EaseInOut),
                (1040, V(-160), null), (1520, V(-160), Bz(.55f, 0, .85f, .35f)), (1830, V(0), null), (2200, V(0), null)),
            KHolder = new(null,
                (0, V(0, 0, 0, 6, -14, 0, .97f), null), (879, V(0, 0, 0, 6, -14, 0, .97f), null), (880, V(1, 0, 0, 6, -14, 0, .97f), e1),
                (1010, V(1, 0, -ch * .06f, -4, 3, 0, 1.07f), Bezier.EaseInOut), (1080, V(1, 0, -ch * .055f, 2, -2, 0, 1.06f), Bz(.45f, 0, .55f, 1)),
                (1250, V(1, midX, midY, 0, 0, -6, .95f), Bz(.45f, 0, .55f, 1)), (1430, V(1, preX, preY, 0, 0, 0, .84f), Bz(.6f, 0, 1, .5f)),
                (1580, V(1, tx, ty, 0, 0, 0, .82f), null), (2200, V(0, tx, ty, 0, 0, 0, .82f), null)),
            KShake = new(Bezier.EaseOut, (0, V(0), null), (1575, V(0), null), (1605, V(2.5f), null), (1650, V(-1), null), (1700, V(0), null), (2200, V(0), null)),
            KBezel = new(e1, (0, V(0, 10), null), (900, V(0, 10), null), (1120, V(1, 0), null), (1760, V(1, 0), null), (2000, V(0, 8), null), (2200, V(0, 8), null)),
            KStreak = new(null, (0, V(0, 0), null), (1560, V(0, 0), null), (1650, V(1, D * .6f), null), (1780, V(0, D * 1.24f), null), (2200, V(0, D * 1.24f), null)),
            KShadow = new(null, (0, V(.9f, 0, 0), null), (880, V(.9f, 0, 0), null), (1060, V(.45f, D * .06f, D * .1f), null), (1400, V(0, D * .06f, D * .1f), null), (2200, V(0, D * .06f, D * .1f), null)),
            KSlot = new(Bezier.EaseOut, (0, V(0, .9f, 0), null), (950, V(0, .9f, 0), null), (1150, V(1, 1, 0), null), (1480, V(1, 1, 0), null),
                (1600, V(1, 1.05f, 1), null), (1800, V(1, 1, .55f), null), (2200, V(0, 1, 0), null)),
        };
    }

    /// <summary>浅灰外壳的卡带：标签是这款游戏的方图，下面一条主色、金手指（runInsert 的 makeItem）。返回整张卡带。</summary>
    private SpriteVisual CsLightCart(ContainerVisual host, float D, float DH, Color a, LoadedImageSurface? label)
    {
        if (_csCartLight == null)
        {
            _csCartLight = G.C.CreateSurfaceBrush(CcDraw(D, DH, 1.5f, ds =>
            {
                using var shape = LightCartShape(ds, D, DH);
                using (var body = CssGradient(ds, 180, 0, 0, D, DH, (0, Gfx.Rgba(0xe9, 0xe9, 0xec)), (1, Gfx.Rgba(0xc9, 0xc9, 0xcf))))
                    ds.FillGeometry(shape, body);
                using (ds.CreateLayer(1, shape))
                {
                    ds.DrawGeometry(shape, Gfx.Rgba(255, 255, 255, 0.5), 4);
                    float px0 = D * 0.14f, px1 = D * 0.86f, py = DH * 0.84f, ph = DH * 0.10f;
                    for (float x = px0; x + 3 <= px1; x += 7)
                    {
                        ds.FillRectangle(x, py, 3, ph, Gfx.Rgba(0xe0, 0xbf, 0x55));
                        ds.FillRectangle(x + 3, py, 1, ph, Gfx.Rgba(0x8a, 0x6d, 0x1c));
                    }
                    ds.FillRoundedRectangle(D * 0.07f - 2, DH * 0.06f - 2, D * 0.86f + 4, DH * 0.62f + 4, 5, 5, Gfx.Rgba(0, 0, 0, 0.15));
                }
            }));
            _csCartLightTop = G.C.CreateSurfaceBrush(CcDraw(D, DH, 1.5f, ds =>
            {
                using var shape = LightCartShape(ds, D, DH);
                using (ds.CreateLayer(1, shape))
                using (var gloss = CssGradient(ds, 120, 0, 0, D, DH, (0, Gfx.Rgba(255, 255, 255, 0.4)), (0.4f, Gfx.Rgba(255, 255, 255, 0))))
                    ds.FillRectangle(0, 0, D, DH, gloss);
            }));
        }
        var whole = G.Sprite(null, relative: false);
        whole.Size = new Vector2(D, DH);
        host.Children.InsertAtTop(whole);
        var shell = G.Sprite(_csCartLight);
        whole.Children.InsertAtTop(shell);
        var lb = G.C.CreateSurfaceBrush(label);
        lb.Stretch = CompositionStretch.UniformToFill;
        var lab = G.Sprite(lb, relative: false);
        lab.Offset = new Vector3(D * 0.07f, DH * 0.06f, 0);
        lab.Size = new Vector2(D * 0.86f, DH * 0.62f);
        lab.Clip = G.C.CreateGeometricClip(RoundRect(lab.Size.X, lab.Size.Y, 4));
        whole.Children.InsertAtTop(lab);
        var stripe = G.Sprite(G.Solid(a), relative: false);
        stripe.Offset = new Vector3(D * 0.07f, DH * 0.73f, 0);
        stripe.Size = new Vector2(D * 0.63f, DH * 0.04f);
        stripe.Clip = G.C.CreateGeometricClip(RoundRect(stripe.Size.X, stripe.Size.Y, 2));
        whole.Children.InsertAtTop(stripe);
        whole.Children.InsertAtTop(G.Sprite(_csCartLightTop));
        return whole;
    }

    private static CanvasGeometry LightCartShape(ICanvasResourceCreator rc, float w, float h)
    {
        using var round = CanvasGeometry.CreateRoundedRectangle(rc, 0, 0, w, h, 7, 7);
        using var cut = CanvasGeometry.CreatePolygon(rc, new[] { new Vector2(0, 0), new Vector2(w * 0.8f, 0), new Vector2(w, h * 0.12f), new Vector2(w, h), new Vector2(0, h) });
        return round.CombineWith(cut, Matrix3x2.Identity, CanvasGeometryCombine.Intersect);
    }

    private CompositionSurfaceBrush CsSlotFace(float w)
    {
        return _csSlotFace ??= G.C.CreateSurfaceBrush(CcDraw(w, 7, 2, ds =>
        {
            using (var b = CssGradient(ds, 180, 0, 0, w, 7, (0, Colors.Black), (1, Gfx.Rgba(0x1b, 0x1e, 0x24))))
                ds.FillRoundedRectangle(0, 0, w, 7, 3.5f, 3.5f, b);
            ds.DrawRoundedRectangle(0.5f, 0.5f, w - 1, 6, 3, 3, Gfx.Rgba(255, 255, 255, 0.16), 1);
            ds.DrawLine(3, 0.5f, w - 3, 0.5f, Gfx.Rgba(255, 255, 255, 0.25), 1);
        }));
    }

    private CompositionSurfaceBrush CsBezelFace(float D)
    {
        float w = MathF.Round(D * 1.6f);
        return _csBezelFace ??= G.C.CreateSurfaceBrush(CcDraw(w, 24, 2, ds =>
        {
            using (var b = CssGradient(ds, 180, 0, 0, w, 24, (0, Gfx.Rgba(0x2a, 0x2e, 0x36)), (1, Gfx.Rgba(0x12, 0x14, 0x18))))
                ds.FillRoundedRectangle(0, 0, w, 24, 12, 12, b);
            ds.DrawLine(10, 0.5f, w - 10, 0.5f, Gfx.Rgba(255, 255, 255, 0.12), 1);
        }));
    }

    // ---------------- 每一帧 ----------------

    private void CsInsertApply(CsInsertRun r)
    {
        double t = r.T;
        float R(float deg) => deg * MathF.PI / 180;
        static Matrix4x4 About(Vector3 o, Matrix4x4 m) => Matrix4x4.CreateTranslation(-o) * m * Matrix4x4.CreateTranslation(o);
        r.Shade.Opacity = r.KShade.At(t)[0];
        r.Seat.Opacity = r.KSeat.At(t)[0];
        r.Glow.Opacity = r.KGlow.At(t)[0];
        var w = r.KWrap.At(t);
        r.Wrap.Offset = new Vector3(MathF.Round(CsW * 0.56f - r.Cw / 2) + w[0], MathF.Round(CsH * 0.42f - r.Ch / 2) + w[1], 0);
        r.Wrap.Scale = new Vector3(w[2], w[2], 1);
        r.Wrap.Opacity = w[3];
        var f = r.KFloor.At(t);
        r.Floor.Opacity = f[0];
        r.Floor.Scale = new Vector3(f[1], f[1], 1);
        r.Glint.Offset = new Vector3(-r.Cw * 0.6f + r.KGlint.At(t)[0], -r.Ch * 0.2f, 0);
        var s = r.KSt.At(t);
        r.St.TransformMatrix = About(new Vector3(r.Cw / 2, r.Ch / 2, 0), Matrix4x4.CreateRotationY(R(s[1])) * Matrix4x4.CreateRotationX(R(s[0])));
        r.Cover.TransformMatrix = About(new Vector3(0, r.Ch / 2, 0), Matrix4x4.CreateRotationY(R(r.KCover.At(t)[0])));
        // perspective(1000px) translate(x,y) rotateX rotateY rotateZ scale(s)，原点在卡带中心
        var h = r.KHolder.At(t);
        var m = Matrix4x4.CreateScale(h[6], h[6], 1) * Matrix4x4.CreateRotationZ(R(h[5])) * Matrix4x4.CreateRotationY(R(h[4])) * Matrix4x4.CreateRotationX(R(h[3]))
            * Matrix4x4.CreateTranslation(h[1], h[2], 0) * Tile.Perspective(1000);
        r.Holder.TransformMatrix = About(new Vector3(r.D / 2, r.DH / 2, 0), m);
        r.Holder.Opacity = h[0];
        r.Shake.Offset = new Vector3(0, r.KShake.At(t)[0], 0);
        var b = r.KBezel.At(t);
        r.Bezel.Opacity = b[0];
        r.Bezel.Offset = new Vector3(r.Bezel.Offset.X, CsH - 7 - 12 + b[1], 0);
        var k = r.KStreak.At(t);
        r.Streak.Opacity = k[0];
        r.Streak.Offset = new Vector3(k[1], 2, 0);
        var sh = r.KShadow.At(t);
        r.HolderShadow.Opacity = sh[0];
        r.HolderShadow.Offset = new Vector3(r.D * 0.06f - CsSoftPad + 20 + sh[1], r.DH * 0.09f - CsSoftPad + 20 + sh[2], 0);
        var sl = r.KSlot.At(t);
        r.Slot.Opacity = sl[0];
        r.Slot.Scale = new Vector3(sl[1], sl[1], 1);
        r.SlotGlow.Opacity = sl[2];
    }

    // ---------------- 音效 ----------------
    //
    // 原版用 Web Audio 在固定时间点合成：带通噪声的「咔哒」、正弦下滑的「闷响」、扫频噪声的「滑动」。这里照同样的参数合成成
    // 48 kHz 单声道 WAV，交给同一个声音引擎放。设置 › 游戏库 › 插卡音效 可以关掉。

    private readonly record struct Hit(int Kind, double Ms, double Freq, double Len, double Gain, double Q = 2, double Freq2 = 0);

    private static readonly Hit[] InsertHits =
    {
        new(0, 380, 3200, .03, .55), new(1, 400, 140, .08, .25), new(0, 880, 2400, .04, .35), new(0, 900, 4200, .025, .4),
        new(2, 1320, 900, .3, .35, 1.2, 2600), new(0, 1575, 1800, .05, .6, 3), new(1, 1580, 110, .12, .5), new(0, 1850, 2800, .035, .5), new(1, 1855, 160, .07, .25),
    };

    private static readonly Hit[] EjectHits =
    {
        new(0, 330, 3200, .03, .5), new(1, 350, 140, .08, .22), new(0, 600, 2200, .04, .45, 3), new(1, 610, 110, .1, .4),
        new(2, 620, 2600, .3, .35, 1.2, 900), new(0, 1300, 4200, .025, .4), new(0, 1820, 2800, .035, .55), new(1, 1840, 160, .08, .3),
    };

    private int _csSoundSlot;

    /// <summary>eject：从插卡时间线的 fromT 倒放回 0，只放倒放会经过的那些声音。</summary>
    private void CsInsertSound(bool eject, double fromT)
    {
        if (!_prefs.InsertSound) return;
        var hits = eject ? EjectHits.Where(h => h.Ms >= InsertMs - fromT).Select(h => h with { Ms = h.Ms - (InsertMs - fromT) }).ToArray() : InsertHits;
        if (hits.Length == 0) return;
        try
        {
            string dir = Path.Combine(App.CacheDir, "sfx");
            Directory.CreateDirectory(dir);
            int id = 28 + (_csSoundSlot++ & 3);
            string path = Path.Combine(dir, $"insert-{id}.wav");
            File.WriteAllBytes(path, SynthHits(hits));
            if (AudioEngine.LoadSfx(id, path)) AudioEngine.PlaySfx(id, 1, 0);
        }
        catch (Exception ex) { Log.Warn($"insert sound: {ex.Message}"); }
    }

    private static byte[] SynthHits(Hit[] hits)
    {
        const int Rate = 48000;
        double end = hits.Max(h => h.Ms / 1000 + h.Len) + 0.05;
        var buf = new double[(int)(end * Rate)];
        var rng = new Random(7);
        foreach (var h in hits)
        {
            int start = (int)(h.Ms / 1000 * Rate), n = (int)(h.Len * Rate) + Rate / 50;
            // 双二阶带通（RBJ，峰值 0 dB）
            double b0 = 0, b1 = 0, b2 = 0, a1 = 0, a2 = 0, x1 = 0, x2 = 0, y1 = 0, y2 = 0, phase = 0;
            for (int i = 0; i < n && start + i < buf.Length; i++)
            {
                double t = i / (double)Rate;
                double env;
                if (h.Kind == 2)
                {
                    double up = h.Len * 0.3;
                    env = t < up ? h.Gain * t / up : h.Gain * Math.Pow(0.0008 / h.Gain, (t - up) / (h.Len - up));
                }
                else env = t < 0.002 ? h.Gain * t / 0.002 : h.Gain * Math.Pow(0.0008 / h.Gain, (t - 0.002) / (h.Len - 0.002));
                if (t > h.Len) env = 0;
                double v;
                if (h.Kind == 1)
                {
                    double f = t < h.Len * 0.6 ? h.Freq * 1.6 * Math.Pow(1 / 1.6, t / (h.Len * 0.6)) : h.Freq;
                    phase += 2 * Math.PI * f / Rate;
                    v = Math.Sin(phase);
                }
                else
                {
                    double f = h.Kind == 2 ? h.Freq * Math.Pow(h.Freq2 / h.Freq, Math.Min(1, t / h.Len)) : h.Freq;
                    if (i % 16 == 0 || i == 0)
                    {
                        double w0 = 2 * Math.PI * f / Rate, alpha = Math.Sin(w0) / (2 * h.Q), a0 = 1 + alpha;
                        b0 = alpha / a0; b1 = 0; b2 = -alpha / a0; a1 = -2 * Math.Cos(w0) / a0; a2 = (1 - alpha) / a0;
                    }
                    double x = rng.NextDouble() * 2 - 1;
                    v = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                    x2 = x1; x1 = x; y2 = y1; y1 = v;
                }
                buf[start + i] += v * env * 0.85;
            }
        }
        var wav = new byte[44 + buf.Length * 2];
        void Tag(int at, string s) { for (int i = 0; i < 4; i++) wav[at + i] = (byte)s[i]; }
        Tag(0, "RIFF"); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8); Tag(8, "WAVE");
        Tag(12, "fmt "); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), Rate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), Rate * 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        Tag(36, "data"); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), buf.Length * 2);
        for (int i = 0; i < buf.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), (short)Math.Round(Math.Tanh(buf[i]) * 32000));
        return wav;
    }
}
