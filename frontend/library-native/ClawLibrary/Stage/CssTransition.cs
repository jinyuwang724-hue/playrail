// 一个标量属性上的 CSS transition（合成线程跑）：照浏览器的规则，新目标从此刻的值接着走（焦点来回切不跳），
// 正好掉头回到上一次的起点时按 CSS 的 reversing shortening 缩短时长（走了三成就只用三成的时间回去）。
// 此刻的值在 UI 线程按同一条曲线算，不去读合成层。
using ClawLibrary.Engine;
using Microsoft.UI.Composition;

namespace ClawLibrary.Stage;

public sealed class CssTransition
{
    private readonly Gfx _g;
    private readonly CompositionObject _target;
    private readonly string _prop;
    private readonly Action<float> _set;
    private float _from, _to, _revStart;
    private long _start;
    private double _ms, _factor = 1;
    private Bezier _ease;

    public CssTransition(Gfx g, CompositionObject target, string prop, Action<float> setStatic, float value)
    {
        _g = g; _target = target; _prop = prop; _set = setStatic;
        _from = _to = _revStart = value;
        setStatic(value);
    }

    public float Target => _to;

    private double Progress(long now) => _ms <= 0 ? 1 : Math.Clamp((now - _start) / _ms, 0, 1);

    /// <summary>此刻的值（动画途中按同一条曲线算）。</summary>
    public float Value(long now)
    {
        double t = Progress(now);
        return t >= 1 ? _to : _from + (_to - _from) * (float)_ease.Ease(t);
    }

    public void To(float to, double ms, Bezier ease, long now, bool animate = true)
    {
        bool running = Progress(now) < 1;
        if (to == _to && (running || !animate)) { if (!animate) Snap(to); return; }
        float cur = Value(now);
        if (!animate || ms <= 0 || Math.Abs(cur - to) < 1e-4) { Snap(to); return; }
        double factor = 1;
        float revStart = cur;
        if (running && Math.Abs(to - _revStart) < 1e-4)
        {
            // CSS：新终点等于上一次的（反向调整后的）起点 → 时长 × |输出进度 × 旧系数 + 1 − 旧系数|
            double outProgress = _ease.Ease(Progress(now));
            factor = Math.Clamp(Math.Abs(outProgress * _factor + 1 - _factor), 0, 1);
            revStart = _to;
        }
        _from = cur; _to = to; _start = now; _ms = ms * factor; _factor = factor; _revStart = revStart; _ease = ease;
        _target.StartAnimation(_prop, _g.Scalar(cur, to, _ms, ease));
    }

    private void Snap(float v)
    {
        _target.StopAnimation(_prop);
        _set(v);
        _from = _to = _revStart = v;
        _ms = 0; _factor = 1;
    }
}
