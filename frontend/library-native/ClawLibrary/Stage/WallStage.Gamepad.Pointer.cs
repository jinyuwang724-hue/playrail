using ClawLibrary.Engine;
using ClawLibrary.Data;
using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private readonly List<SettingsPointerTarget> _gpTargets = new();
    private int _gpInputRevision;
    private SettingsWriteQueue? _gpRangeWrites;

    private bool GpRefreshObsolete(int revision) => revision != _gpInputRevision || _settingsGesture.Active
        || _gpRangeWrites?.IsBusy == true || _gpRumbleEdits > 0 || _gpLayoutEdits > 0;

    private void GpRangeChanged(string key, string method, string side, int first, int second)
    {
        ++_gpInputRevision;
        _gpRangeWrites ??= new SettingsWriteQueue(ex => Toast($"没有写进手柄：{ex.Message}"), () => { _ = GpRefresh(); });
        _ = _gpRangeWrites.Enqueue(key, 600, async () =>
        {
            _agent ??= new AgentClient();
            await _agent.Call(method, JsonValue.Create(side), JsonValue.Create(first), JsonValue.Create(second));
        });
    }

    private SettingsPointerTarget? GpTargetAt(double x, double y)
    {
        for (int i = _gpTargets.Count - 1; i >= 0; i--)
            if (_gpTargets[i].Contains(x, y)) return _gpTargets[i];
        return null;
    }

    private void GpSelectCategory(int category)
    {
        SettingsResetPointer();
        _gpCat = Math.Clamp(category, 0, GpCatRows.Length - 1);
        _gpPicker = null; _gpEditing = false; _gpInSide = true; _gpFocus = 0;
        BuildGpItems(); GpPlaceArt(); GpShowFaces(GpPageFaces()); PaintGpBody();
    }

    private void GpPointerBrowse(int delta)
    {
        if (_gpPicker?.Level == GpSubLevel.Chooser) GpSwitchTab(delta);
        else if (_gpPicker == null) GpSelectCategory(_gpCat + delta);
    }

    private void GpPointerSwipe(double dx, double dy)
    {
        if (_gpPicker?.Level == GpSubLevel.Chooser && Math.Abs(dx) >= 60 && Math.Abs(dx) > Math.Abs(dy))
            GpSwitchTab(dx < 0 ? 1 : -1);
        else if (_gpPicker == null && Math.Abs(dy) >= 50 && Math.Abs(dy) > Math.Abs(dx))
            GpPointerBrowse(dy < 0 ? 1 : -1);
    }

    private void GpApplyPointerTarget(SettingsPointerTarget hit, double x)
    {
        switch (hit.Kind)
        {
            case SettingsTargetKind.Category: GpSelectCategory(hit.Row); return;
            case SettingsTargetKind.Action:
                if (hit.Row == 0) GpBack();
                else if (hit.Row == 1) GpPadButton(false);
                else if (hit.Row == 2) GpPadButton(true);
                return;
            case SettingsTargetKind.Tab:
                if (_gpPicker is { Level: GpSubLevel.Group } gp) GpGroupSwitch(gp, hit.Row - gp.Group);
                else if (_gpPicker is { } tab) { GpSwitchTab(hit.Row - tab.Tab); tab.InTabs = false; PaintGpBody(); }
                return;
            case SettingsTargetKind.Cell:
                if (_gpPicker is { } picker) { picker.Cell = hit.Row; picker.InTabs = false; GpPickerActivate(); }
                return;
            case SettingsTargetKind.Slider: GpSliderAt(hit, x); return;
        }
        if (hit.Row < 0 || hit.Row >= _gpItems.Count) return;
        _gpInSide = false; _gpEditing = false; _gpFocus = hit.Row;
        var item = _gpItems[hit.Row];
        if (item.Disabled) Toast(item.Why ?? "现在改不了");
        else
        {
            ++_gpInputRevision;
            if (item.Press != null) item.Press();
            else if (item.Choices is { Length: > 0 } choices) item.SetChoice?.Invoke((item.Choice + 1) % choices.Length);
        }
        BuildGpItems(); GpShowFaces(GpPageFaces()); PaintGpBody();
    }

    private void GpSliderAt(SettingsPointerTarget hit, double x)
    {
        if (hit.Row < 0 || hit.Row >= _gpItems.Count) return;
        var item = _gpItems[hit.Row];
        if (item.Disabled || item.Number is not int current || item.SetNumber == null) return;
        _gpInSide = false; _gpEditing = false; _gpFocus = hit.Row;
        int value = SettingsPointerGesture.SliderValue(x, hit.X, hit.Width, item.Min, item.Max, item.StepBy);
        if (value == current) return;
        ++_gpInputRevision;
        item.SetNumber(value);
        BuildGpItems(); GpShowFaces(GpPageFaces()); PaintGpBody();
    }

    private void GpAddItemTargets(CanvasDrawingSession ds, GpItem item, int index)
    {
        if (item.Map)
        {
            _gpTargets.Add(new(index, SettingsTargetKind.Item, item.Col == -1 ? item.X - item.MapW : item.X, item.Y - item.MapH / 2 - _gpMapScroll, item.MapW, item.MapH));
            return;
        }
        float x = item.Side < 0 ? item.X - 140 : item.Side > 0 ? item.X - 10 : item.X - 190;
        _gpTargets.Add(new(index, SettingsTargetKind.Item, x, item.Y - 18 - (GpIsMapPage ? _gpMapScroll : 0), item.Side == 0 ? 380 : 150, 88));
        if (item.Disabled || item.Number is not int value || item.SetNumber == null) return;
        const float widthSlider = 110;
        float leftSlider = item.Side < 0 ? item.X - widthSlider : item.X;
        float cy = item.Y + 56;
        float fraction = Math.Clamp((value - item.Min) / (float)Math.Max(1, item.Max - item.Min), 0, 1);
        ds.FillRoundedRectangle(leftSlider, cy - 2, widthSlider, 4, 2, 2, Gfx.WithAlpha(CcText, .18f));
        if (fraction > 0) ds.FillRoundedRectangle(leftSlider, cy - 2, widthSlider * fraction, 4, 2, 2, PerfAccent);
        ds.FillCircle(leftSlider + widthSlider * fraction, cy, 5, CcText);
        _gpTargets.Add(new(index, SettingsTargetKind.Slider, leftSlider, cy - 22, widthSlider, 44));
    }
}
