using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private readonly SettingsPointerGesture _dsGesture = new();
    private readonly List<SettingsPointerTarget> _dsTargets = new();
    private SettingsPointerTarget? _dsPressedTarget;
    private bool _dsManualScroll, _dsPressedBack, _dsCanDragScroll;
    private float _dsScrollAtPress;

    private bool DsBackHit(double x, double y) => x / _ccK is >= 16 and <= 68 && y / _ccK is >= 16 and <= 76;
    private bool DsListHit(double x, double y) => x / _ccK >= PerfRightX && x / _ccK < PerfRightX + PerfRightW
        && y / _ccK >= PerfTop && y / _ccK < PerfTop + PerfViewH;

    private SettingsPointerTarget? DsTargetAt(double x, double y)
    {
        if (!DsListHit(x, y)) return null;
        double lx = x / _ccK - PerfRightX, ly = y / _ccK - PerfTop + _dsScroll;
        for (int i = _dsTargets.Count - 1; i >= 0; i--)
            if (_dsTargets[i].Contains(lx, ly)) return _dsTargets[i];
        return null;
    }

    private bool DsPointerBegin(uint id, double x, double y)
    {
        var hit = DsTargetAt(x, y);
        if (!_dsGesture.Begin(id, x, y, hit?.Kind == SettingsTargetKind.Slider)) return false;
        _dsPressedTarget = hit;
        _dsPressedBack = DsBackHit(x, y);
        _dsCanDragScroll = DsListHit(x, y);
        _dsScrollAtPress = _dsScroll;
        return true;
    }

    private void DsPointerMove(uint id, double x, double y)
    {
        var mode = _dsGesture.Move(id, x, y);
        if (mode == SettingsPointerMode.Scroll && _dsCanDragScroll)
            DsScrollTo(_dsScrollAtPress - (y - _dsGesture.StartY) / _ccK);
        else if (mode == SettingsPointerMode.Slider && _dsPressedTarget is { } hit)
            DsSliderAt(hit, x);
    }

    private void DsPointerEnd(uint id, double x, double y)
    {
        if (!_dsGesture.Owns(id)) return;
        DsPointerMove(id, x, y);
        var mode = _dsGesture.End(id);
        if (mode == SettingsPointerMode.Pending)
        {
            if (_dsPressedBack && DsBackHit(x, y)) DsBack();
            else if (_dsPressedTarget is { } down && DsTargetAt(x, y) is { } up
                && down.Row == up.Row && down.Kind == up.Kind && down.Choice == up.Choice)
                DsApplyTarget(up, x);
        }
        _dsPressedTarget = null;
        if (_dsOpen && _dsWrites?.IsBusy != true) _ = DsRefresh();
    }

    private void DsPointerCancel(uint id)
    {
        if (!_dsGesture.Owns(id)) return;
        _dsGesture.Cancel(id);
        _dsPressedTarget = null;
        if (_dsOpen && _dsWrites?.IsBusy != true) _ = DsRefresh();
    }

    private void DsClick(double x, double y)
    {
        MarkInput();
        if (DsBackHit(x, y)) { DsBack(); return; }
        if (DsTargetAt(x, y) is { } hit) DsApplyTarget(hit, x);
    }

    private void DsApplyTarget(SettingsPointerTarget hit, double x)
    {
        if (hit.Row < 0 || hit.Row >= _dsRows.Count) return;
        _dsFocus = hit.Row;
        _dsManualScroll = true;
        var row = _dsRows[hit.Row];
        if (!row.Disabled)
            switch (hit.Kind)
            {
                case SettingsTargetKind.Choice:
                    if (hit.Choice != row.Choice) row.Select?.Invoke(hit.Choice);
                    break;
                case SettingsTargetKind.Decrease: row.Step?.Invoke(-1); break;
                case SettingsTargetKind.Increase: row.Step?.Invoke(1); break;
                case SettingsTargetKind.Slider: DsSliderAt(hit, x); break;
            }
        PaintDs();
    }

    private void DsSliderAt(SettingsPointerTarget hit, double x)
    {
        if (hit.Row < 0 || hit.Row >= _dsRows.Count) return;
        var row = _dsRows[hit.Row];
        if (row.Disabled || row.SetNumber == null) return;
        _dsFocus = hit.Row;
        _dsManualScroll = true;
        int value = SettingsPointerGesture.SliderValue(x / _ccK - PerfRightX, hit.X, hit.Width, row.Minimum, row.Maximum, row.Increment);
        if (value != row.Number) row.SetNumber(value);
    }

    private void DsScrollTo(double position)
    {
        _dsManualScroll = true;
        _dsScroll = SettingsPointerGesture.Scroll(position, RowsHeight(_dsRows), PerfViewH);
        PaintDs();
    }

    private void DsWheel(int delta) { MarkInput(); DsScrollTo(_dsScroll - delta / 120.0 * 84); }

    private void DsResumeKeyboard()
    {
        if (!_dsManualScroll) return;
        float y = 0;
        var visible = new List<int>();
        bool focusVisible = false;
        for (int i = 0; i < _dsRows.Count; i++)
        {
            var row = _dsRows[i];
            float height = row.Section ? PerfSecH : PerfRowH;
            if (!row.Section && !row.Skip && y >= _dsScroll && y + height <= _dsScroll + PerfViewH)
            {
                visible.Add(i);
                if (i == _dsFocus) focusVisible = true;
            }
            y += height;
        }
        if (!focusVisible && visible.Count > 0) _dsFocus = visible[0];
        _dsManualScroll = false;
    }
}
