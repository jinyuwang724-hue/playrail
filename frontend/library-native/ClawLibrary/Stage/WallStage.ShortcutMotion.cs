using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private LayerVisual? _shortcutLayer;
    private CompositionEffectFactory? _shortcutBlurFactory;
    private CompositionEffectBrush? _shortcutBlur;
    private bool _shortcutLayerAttached, _shortcutBlurUnavailable;

    // Inspired by Folia's softFocus.ts (aecd330b): 8px soft focus, 1.02x size,
    // 520ms resolve / 360ms departure. Only the compositor implements the effect.
    public void BeginShortcutSoftFocus(bool show, bool entrance, double ms, CompositionEasingFunction easing)
    {
        if (_shortcutBlurUnavailable || Reduced) return;
        try
        {
            _shortcutLayer ??= G.C.CreateLayerVisual();
            // The effect's intermediate surface needs explicit pixel bounds;
            // relative layout alone leaves LayerVisual's effect surface empty.
            _shortcutLayer.Size = new Vector2((float)View.Width, (float)View.Height);
            if (_shortcutBlur == null)
            {
                if (_shortcutBlurFactory == null)
                {
                    using var blur = new GaussianBlurEffect
                    {
                        Name = "SoftFocus", BlurAmount = 0, BorderMode = EffectBorderMode.Hard,
                        Optimization = EffectOptimization.Balanced,
                        Source = new CompositionEffectSourceParameter("Backdrop"),
                    };
                    _shortcutBlurFactory = G.C.CreateEffectFactory(blur, new[] { "SoftFocus.BlurAmount" });
                }
                _shortcutBlur = _shortcutBlurFactory.CreateBrush();
            }
            if (!_shortcutLayerAttached)
            {
                _shortcutLayerAttached = true;
                ElementCompositionPreview.SetElementChildVisual(_rootEl.Surface, null);
                _top.Parent?.Children.Remove(_top);
                _shortcutLayer.Children.InsertAtTop(_top);
                ElementCompositionPreview.SetElementChildVisual(_rootEl.Surface, _shortcutLayer);
                _shortcutLayer.Effect = _shortcutBlur;
            }
            if (show && entrance)
            {
                _shortcutBlur.Properties.StopAnimation("SoftFocus.BlurAmount");
                _shortcutBlur.Properties.InsertScalar("SoftFocus.BlurAmount", 8);
            }
            var animation = G.C.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(ms);
            animation.InsertExpressionKeyFrame(0, "this.StartingValue");
            animation.InsertKeyFrame(1, show ? 0 : 8, easing);
            _shortcutBlur.Properties.StartAnimation("SoftFocus.BlurAmount", animation);
        }
        catch (Exception ex)
        {
            _shortcutBlurUnavailable = true;
            EndShortcutSoftFocus();
            Log.Error("shortcut soft-focus unavailable; using opacity/scale", ex);
        }
    }

    public void EndShortcutSoftFocus()
    {
        if (_shortcutBlur != null)
        {
            _shortcutBlur.Properties.StopAnimation("SoftFocus.BlurAmount");
            _shortcutBlur.Properties.InsertScalar("SoftFocus.BlurAmount", 0);
        }
        if (_shortcutLayerAttached && _shortcutLayer != null)
        {
            _shortcutLayer.Effect = null;
            _top.Parent?.Children.Remove(_top);
            ElementCompositionPreview.SetElementChildVisual(_rootEl.Surface, _top);
            _shortcutLayerAttached = false;
        }
        // LayerVisual binds its implicit content into the brush. A brush that
        // has been detached cannot be rebound as an unbound effect next time.
        _shortcutBlur?.Dispose(); _shortcutBlur = null;
    }

    private void DisposeShortcutMotion()
    {
        EndShortcutSoftFocus();
        _shortcutBlur?.Dispose(); _shortcutBlurFactory?.Dispose(); _shortcutLayer?.Dispose();
    }
}
