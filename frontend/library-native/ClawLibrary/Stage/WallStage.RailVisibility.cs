using static ClawLibrary.Engine.ViewMath;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private bool LibraryRailVisible => _prefs.LibraryRail && !ScreensaverActive;
    private double LibrarySeamWidth => LibraryRailVisible ? (_searchOpen || !CurrentLayer.Home ? SeamFull : SeamHome) : 0;

    private void SetLibraryRail(bool visible)
    {
        if (!visible && _searchOpen) CloseSearch();
        SaveUi(_prefs with { LibraryRail = visible });
        ApplyLibraryRailVisibility(animate: true);
    }

    public void ToggleLibraryRail()
    {
        if (ConsumeScreensaverWake()) return;
        MarkInput();
        if (_ccOpen || MorphBusy || _stack.Count == 0) return;
        SetLibraryRail(!_prefs.LibraryRail);
        Cue(_prefs.LibraryRail ? Sfx.ToggleOn : Sfx.ToggleOff);
    }

    private void ApplyLibraryRailVisibility(bool animate)
    {
        if (_stack.Count == 0) return;
        bool visible = LibraryRailVisible;
        if (!visible)
        {
            _railFocused = false;
            _railFocus = -1;
            _railModel.SettingsOpen = false;
            HideSearchBox();
            if (FocusKey is { } key && _tiles.TryGetValue(key, out var tile)) tile.SetFocused(!ScreensaverActive);
        }
        _seamHost.IsVisible = visible;
        _edgeLeft.IsVisible = _edgeRight.IsVisible = visible;
        if (visible)
        {
            var target = ReopenSeamIfGone(LibrarySeamWidth);
            if (animate) TweenCamera(target);
        }
        if (animate) TweenSeam(LibrarySeamWidth);
        else
        {
            _seamTween = null;
            _openWidth = LibrarySeamWidth;
            _cam.StopAnimation("open");
            _cam.InsertScalar("open", (float)_openWidth);
            Recull(true);
        }
        UpdateSeamLights();
    }
}
