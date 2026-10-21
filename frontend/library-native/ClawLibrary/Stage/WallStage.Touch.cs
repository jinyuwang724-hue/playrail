// 控制中心里的触控（设置类页面有自己的一套手势，见 WallStage.Settings.Pointer.cs）：
//   点 = 各页自己的点击（CcClick）；
//   拖 = 能滚的地方跟手滚（添加游戏的图片网格、截图页的日历）；
//   左右划 = 和手柄左右一样（控制中心卡片、截图页的放映 / 冲印 / 大图），添加游戏页是换页签；
//   从左边 64 DIP 以内往右划 = 返回（OnPointerPressed 里接；贴着边框划是 Windows 的小组件）。
// 鼠标滚轮和拖动同一套。
using System.Numerics;
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    /// <summary>划动至少这么远（DIP）才算，左右划还要比上下明显。</summary>
    private const double SwipeMin = 60;

    /// <summary>手指拖着走（屏幕 DIP 的增量）。</summary>
    private void CcDrag(double dx, double dy)
    {
        MarkInput();
        if (_agOpen) AgDrag((float)(dy / _ccK));
        else if (_shOpen) ShDrag((float)(dy / _ccK));
    }

    /// <summary>松手时按总位移判断是不是左右划。</summary>
    private void CcSwipe(double tx, double ty)
    {
        if (Math.Abs(tx) < SwipeMin || Math.Abs(tx) < Math.Abs(ty) * 1.2) return;
        if (_agOpen) { AgSwipe(tx < 0 ? 1 : -1); return; }
        if (SettingsPageOpen) return;
        if (_shOpen && ShDragScrolls) return;
        // 划得远多走几格（每 260 设计 px 一格，最多 4 格）
        int steps = Math.Clamp((int)Math.Round(Math.Abs(tx) / (_ccK * 260)), 1, 4);
        for (int i = 0; i < steps; i++) Move(tx < 0 ? Direction.Right : Direction.Left);
    }

    private void CcWheel(int delta, bool horizontal)
    {
        MarkInput();
        float d = delta / 120f * 84;
        if (_agOpen) { if (!horizontal) AgDrag(d); return; }
        if (_shOpen && ShDragScrolls) { if (!horizontal) ShDrag(d); return; }
        // 其余（卡片、放映、冲印）：滚一格走一格
        Move(delta > 0 ? Direction.Left : Direction.Right);
    }

    // ---------------- 添加游戏 ----------------

    /// <summary>手动滚过图片网格：先不让焦点把它拽回去（手柄一动就恢复跟焦点）。</summary>
    private bool _agManualScroll;

    private void AgDrag(float dy)
    {
        if (_agDraft == null || _agPage != AgPage.Art || _agTab >= AgTabOverview || _agOffKind != null) return;
        _agManualScroll = true;
        _agScroll -= dy;
        AgPaint();
    }

    private void AgSwipe(int delta)
    {
        if (_agPage == AgPage.Art && _agOffKind == null) AgSwitchTab(delta);
    }

    // ---------------- 截图页 ----------------

    /// <summary>日历排版是上下滚的一长列：拖动就滚它；放映、冲印、大图是左右走的，用划。</summary>
    private bool ShDragScrolls => _shStyle == ShStyle.Calendar && !_shViewer && !_shFilterOpen;

    private void ShDrag(float dy)
    {
        if (!ShDragScrolls || _shGrid == null) return;
        float k = _ccK, viewH = (float)View.Height / k - ShGridTop;
        _shScroll = Math.Clamp(_shScroll - dy, 0, Math.Max(0, _shContentH - viewH));
        _shGrid.StopAnimation("Offset.Y");
        _shGrid.Offset = new Vector3(0, -_shScroll * k, 0);
        ShCalImages();
    }
}
