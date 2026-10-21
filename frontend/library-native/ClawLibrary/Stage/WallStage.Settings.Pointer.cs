using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private readonly SettingsPointerGesture _settingsGesture = new();
    private readonly List<SettingsPointerTarget> _settingsTargets = new();
    private SettingsPointerTarget? _settingsPressedTarget;
    private bool _settingsManualScroll, _settingsCanDragScroll, _settingsWasList;
    private float _settingsScrollAtPress, _gpPickScroll;
    private List<PerfRow> _gpPickRows = new();

    private bool SettingsPageOpen => _ccOpen && (_dsOpen || _syOpen || _stOpen || _perfOpen || _gpOpen);
    private bool SettingsHasList => _dsOpen || _syOpen || _stOpen || _perfOpen || _gpPicker?.Level is GpSubLevel.Key or GpSubLevel.Settings;
    private List<PerfRow> SettingsRows => _dsOpen ? _dsRows : _syOpen ? _syRows : _stOpen ? _stRows : _perfOpen ? _perfRows : _gpPickRows;
    private float SettingsListX => _gpOpen ? GpPickX : PerfRightX;
    private float SettingsListY => _gpOpen ? 196 : PerfTop;
    private float SettingsListH => _gpOpen ? 520 : PerfViewH;
    private float SettingsScroll
    {
        get => _dsOpen ? _dsScroll : _syOpen ? _syScroll : _stOpen ? _stScroll : _perfOpen ? _perfScroll : _gpPickScroll;
        set { if (_dsOpen) _dsScroll = value; else if (_syOpen) _syScroll = value; else if (_stOpen) _stScroll = value; else if (_perfOpen) _perfScroll = value; else _gpPickScroll = value; }
    }
    private int SettingsFocus
    {
        get => _dsOpen ? _dsFocus : _syOpen ? _syFocus : _stOpen ? _stFocus : _perfOpen ? _perfFocus : _gpPicker?.Level == GpSubLevel.Key ? _gpPicker.KeyFocus : _gpPicker?.SettingsFocus ?? -1;
        set
        {
            if (_dsOpen) _dsFocus = value;
            else if (_syOpen) _syFocus = value;
            else if (_stOpen) { _stFocus = value; _stZone = 1; }
            else if (_perfOpen) _perfFocus = value;
            else if (_gpPicker is { } p) { if (p.Level == GpSubLevel.Key) p.KeyFocus = value; else p.SettingsFocus = value; }
        }
    }

    private void SettingsResetPointer()
    {
        _settingsGesture.Reset();
        _settingsPressedTarget = null;
        _settingsManualScroll = false;
    }

    private void SettingsPaint()
    {
        if (_dsOpen) PaintDs();
        else if (_syOpen) PaintSy();
        else if (_stOpen) PaintSt();
        else if (_perfOpen) PaintPerf();
        else if (_gpOpen) PaintGpBody();
    }

    private bool SettingsListHit(double x, double y) => SettingsHasList
        && x / _ccK >= SettingsListX && x / _ccK < SettingsListX + PerfRightW
        && y / _ccK >= SettingsListY && y / _ccK < SettingsListY + SettingsListH;

    private SettingsPointerTarget? SettingsTargetAt(double x, double y)
    {
        if (_gpOpen && GpTargetAt(x / _ccK, y / _ccK) is { Kind: SettingsTargetKind.Category or SettingsTargetKind.Action } chrome) return chrome;
        if (!SettingsHasList) return GpTargetAt(x / _ccK, y / _ccK);
        if (!SettingsListHit(x, y)) return null;
        double lx = x / _ccK - SettingsListX, ly = y / _ccK - SettingsListY + SettingsScroll;
        for (int i = _settingsTargets.Count - 1; i >= 0; i--)
            if (_settingsTargets[i].Contains(lx, ly)) return _settingsTargets[i];
        return null;
    }

    private bool SettingsPointerBegin(uint id, double x, double y)
    {
        var hit = SettingsTargetAt(x, y);
        if (!_settingsGesture.Begin(id, x, y, hit?.Kind == SettingsTargetKind.Slider)) return false;
        _settingsPressedTarget = hit;
        _settingsWasList = SettingsHasList;
        _settingsCanDragScroll = SettingsListHit(x, y);
        _settingsScrollAtPress = SettingsScroll;
        return true;
    }

    private void SettingsPointerMove(uint id, double x, double y)
    {
        var mode = _settingsGesture.Move(id, x, y);
        if (mode == SettingsPointerMode.Scroll && _settingsCanDragScroll)
            SettingsScrollTo(_settingsScrollAtPress - (y - _settingsGesture.StartY) / _ccK);
        else if (mode == SettingsPointerMode.Slider && _settingsPressedTarget is { } hit)
        {
            if (_settingsWasList) SettingsSliderAt(hit, x);
            else GpSliderAt(hit, x / _ccK);
        }
    }

    private void SettingsPointerEnd(uint id, double x, double y)
    {
        if (!_settingsGesture.Owns(id)) return;
        SettingsPointerMove(id, x, y);
        var mode = _settingsGesture.End(id);
        if (mode == SettingsPointerMode.Pending && _settingsPressedTarget is { } down && SettingsTargetAt(x, y) is { } up
            && down.Row == up.Row && down.Kind == up.Kind && down.Choice == up.Choice)
        {
            if (_settingsWasList && up.Kind != SettingsTargetKind.Category && up.Kind != SettingsTargetKind.Action) SettingsApplyTarget(up, x);
            else GpApplyPointerTarget(up, x / _ccK);
        }
        else if (mode == SettingsPointerMode.Pending && _settingsPressedTarget == null && _stOpen)
            StCategoryClick(x, y);   // 设置页左栏的分类：点一下切过去（和鼠标点同一个入口）
        else if (mode == SettingsPointerMode.Scroll && !_settingsWasList && _settingsPressedTarget?.Kind != SettingsTargetKind.Slider)
            GpPointerSwipe((x - _settingsGesture.StartX) / _ccK, (y - _settingsGesture.StartY) / _ccK);
        _settingsPressedTarget = null;
        if (_dsOpen && _dsWrites?.IsBusy != true) _ = DsRefresh();
    }

    private void SettingsPointerCancel(uint id)
    {
        if (!_settingsGesture.Owns(id)) return;
        _settingsGesture.Cancel(id);
        _settingsPressedTarget = null;
        if (_dsOpen && _dsWrites?.IsBusy != true) _ = DsRefresh();
    }

    private void SettingsClick(double x, double y)
    {
        MarkInput();
        if (SettingsTargetAt(x, y) is not { } hit) return;
        if (SettingsHasList && hit.Kind != SettingsTargetKind.Category && hit.Kind != SettingsTargetKind.Action) SettingsApplyTarget(hit, x);
        else GpApplyPointerTarget(hit, x / _ccK);
    }

    private void SettingsApplyTarget(SettingsPointerTarget hit, double x)
    {
        var rows = SettingsRows;
        if (hit.Row < 0 || hit.Row >= rows.Count) return;
        SettingsFocus = hit.Row;
        _settingsManualScroll = true;
        var row = rows[hit.Row];
        if (!row.Disabled && !row.Skip)
            switch (hit.Kind)
            {
                case SettingsTargetKind.Row: row.Press?.Invoke(); break;
                case SettingsTargetKind.Choice:
                    if (hit.Choice != row.Choice) row.Select?.Invoke(hit.Choice);
                    break;
                case SettingsTargetKind.Decrease: row.Step?.Invoke(-1); break;
                case SettingsTargetKind.Increase: row.Step?.Invoke(1); break;
                case SettingsTargetKind.Slider: SettingsSliderAt(hit, x); break;
            }
        SettingsPaint();
    }

    private void SettingsSliderAt(SettingsPointerTarget hit, double x)
    {
        var rows = SettingsRows;
        if (hit.Row < 0 || hit.Row >= rows.Count) return;
        var row = rows[hit.Row];
        if (row.Disabled || row.Skip || row.SetNumber == null) return;
        SettingsFocus = hit.Row;
        _settingsManualScroll = true;
        int value = SettingsPointerGesture.SliderValue(x / _ccK - SettingsListX, hit.X, hit.Width, row.Minimum, row.Maximum, row.Increment);
        if (value != row.Number) row.SetNumber(value);
    }

    private void SettingsScrollTo(double position)
    {
        _settingsManualScroll = true;
        SettingsScroll = SettingsPointerGesture.Scroll(position, RowsHeight(SettingsRows), SettingsListH);
        SettingsPaint();
    }

    private void SettingsWheel(int delta)
    {
        MarkInput();
        if (_settingsGesture.Active) return;
        if (SettingsHasList) SettingsScrollTo(SettingsScroll - delta / 120.0 * 84);
        else if (_gpOpen) GpPointerBrowse(delta < 0 ? 1 : -1);
    }

    private void SettingsResumeKeyboard()
    {
        _settingsGesture.Reset();
        if (!_settingsManualScroll || !SettingsHasList) return;
        float y = 0;
        int first = -1;
        bool focusVisible = false;
        var rows = SettingsRows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            float height = row.Section ? PerfSecH : PerfRowH;
            if (!row.Section && !row.Skip && y >= SettingsScroll && y + height <= SettingsScroll + SettingsListH)
            {
                if (first < 0) first = i;
                if (i == SettingsFocus) focusVisible = true;
            }
            y += height;
        }
        if (!focusVisible && first >= 0) SettingsFocus = first;
        _settingsManualScroll = false;
    }
}
