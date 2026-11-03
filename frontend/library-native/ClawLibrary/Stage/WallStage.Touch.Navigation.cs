using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private readonly TouchPointerGesture _pagePointer = new();
    private TouchSurface _settingsTouchSurface;
    private string? _settingsTouchKey;
    private string? _quickTouchId;
    private SpriteVisual? _touchNavigation;
    private int _touchNavigationMode = -1;
    private float _touchNavigationScale;
    private bool _touchFilterVisible;
    private int _touchShotStyle = -1;
    private bool _touchFilterOpen;

    private TouchSurface PointerSurface
    {
        get
        {
            if (_wsOpen) return new(6, _wsFocus, _wsCards.Count);
            if (_ob != null) return new(1, (int)_obStep, _obLang);
            if (_qsOpen) return new(2, QsRowsStyle ? 1 : 0, _qsVersion);
            if (_lm is { Leaving: false }) return new(3);
            if (_agOpen) return new(4, (int)_agPage * 100 + _agTab, _agOffKind == null ? 0 : 1);
            if (_shOpen) return new(5, _shTab * 4 + (_shViewer ? 1 : 0) + (_shFilterOpen ? 2 : 0), (int)_shStyle + _shVersion * 10, _shViewer ? ShViewerShot()?.Path : null);

            if (_gpOpen || GsPadPicking) return new(7, _gpCat, (int?)_gpPicker?.Level ?? -1,
                $"{_gsItem?.Id}:{_gpPicker?.Source}:{_gpPicker?.Tab}:{_gpPicker?.Act}:{_gpPicker?.Group}");
            if (_stOpen) return new(8, _stCat, _stVersion, _gsItem?.Id);
            if (_syOpen) return new(9, _syUpdatesOpen ? 1 : 0, _syVersion);
            if (_perfOpen) return new(10, _perfCurveEdit ? 1 : 0, _perfVersion);
            if (_dsOpen) return new(11);
            if (_ccOpen) return new(12, _ccFocus, (int)_ccZone);
            return new(13, _stack.Count, (_expandedKey != null ? 1 : 0) + (_searchOpen ? 2 : 0) + (_backgroundSettingsOpen ? 4 : 0));
        }
    }

    private static bool PointerEditor(object source)
    {
        for (var element = source as DependencyObject; element != null; element = VisualTreeHelper.GetParent(element))
            if (element is TextBox or PasswordBox) return true;
        return false;
    }

    private float TouchScale => Math.Min((float)View.Width / 1280, (float)View.Height / 800);
    // 0 = hidden, 2 = home control-centre menu. Child pages have no added back button.
    private int TouchNavigationMode => _stack.Count == 0 || _startupShield?.IsVisible == true ? 0
        : _wsOpen ? 0
        : _ob != null ? 0
        : _qsOpen || _lm is { Leaving: false } ? 0
        : _ccOpen || _searchOpen || _backgroundSettingsOpen || _expandedKey != null || _stack.Count > 1 ? 0 : 2;

    private bool TouchFilterHit(double x, double y) => _shOpen && !_shViewer
        && x >= 96 * TouchScale && x < 140 * TouchScale && y >= 24 * TouchScale && y < 72 * TouchScale;

    private bool TouchNavigationHit(double x, double y) => TouchFilterHit(x, y) || TouchShotStyleHit(x, y)
        || TouchNavigationMode == 2 && x >= 0 && x < 40 * TouchScale && y >= 24 * TouchScale && y < 72 * TouchScale;

    private bool TouchShotStyleHit(double x, double y) => _shOpen && !_shViewer && !_shFilterOpen
        && x >= 148 * TouchScale && x < 192 * TouchScale && y >= 24 * TouchScale && y < 72 * TouchScale;

    private bool TouchNavigationClick(double x, double y)
    {
        if (!TouchNavigationHit(x, y)) return false;
        if (TouchFilterHit(x, y)) { ShToggleFilter(); return true; }
        if (TouchShotStyleHit(x, y)) { ShX(); return true; }
        ToggleControlCenter();
        return true;
    }

    private void TouchNavigationFrame(object? sender, object e)
    {
        int mode = TouchNavigationMode;
        float k = TouchScale;
        bool filter = _shOpen && !_shViewer;
        if (mode == _touchNavigationMode && k == _touchNavigationScale && filter == _touchFilterVisible && _touchShotStyle == (int)_shStyle && _touchFilterOpen == _shFilterOpen) return;
        _touchFilterOpen = _shFilterOpen;
        _touchShotStyle = (int)_shStyle;
        _touchFilterVisible = filter;
        _touchNavigationMode = mode; _touchNavigationScale = k;
        if (_touchNavigation == null)
        {
            _touchNavigation = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            _top.Children.InsertAtTop(_touchNavigation);
        }
        _touchNavigation.IsVisible = mode == 2 || filter;
        if (mode == 0 && !filter) return;
        _top.Children.Remove(_touchNavigation); _top.Children.InsertAtTop(_touchNavigation);
        CcSet(_touchNavigation, CcDraw(filter ? 192 : 40, 48, k * Chrome.Dpi, ds =>
        {
            if (mode == 2) for (int row = 0; row < 3; row++) ds.DrawLine(12, 18 + row * 6, 28, 18 + row * 6, CcText, 1.8f);
            if (filter) LineIcons.Draw(ds, "", 118, 24, 20, CcText);
            if (filter && !_shFilterOpen) LineIcons.Draw(ds, _shStyle == ShStyle.Calendar ? "" : _shStyle == ShStyle.Projector ? "" : "", 170, 24, 20, CcText);
        }));
        _touchNavigation.Size = new Vector2((filter ? 192 : 40) * k, 48 * k);
        _touchNavigation.Offset = new Vector3(0, 24 * k, 0);
    }

    private bool UseSettingsPointer(double x, double y)
    {
        if (!SettingsPageOpen || _agOpen || _shOpen || _wsOpen || GsArtMode || TouchNavigationHit(x, y)) return false;
        if (!GsPadMapMode) return true;
        // Embedded controller diagram and the settings above it have separate hit maps.
        return x / _ccK < StRightX || SettingsTargetAt(x, y) != null;
    }

    private string? SettingsTouchIdentity(SettingsPointerTarget? target)
    {
        if (target is not { } hit) return null;
        if (SettingsHasList && hit.Kind is not (SettingsTargetKind.Category or SettingsTargetKind.Action))
            return hit.Row >= 0 && hit.Row < SettingsRows.Count ? SettingsRows[hit.Row].EditId ?? SettingsRows[hit.Row].Title : null;
        return $"{hit.Kind}:{hit.Row}:{hit.Choice}";
    }

    private double SettingsGpX(double screenX) => screenX / _ccK - (GsPadPicking ? GsPadDx : 0);
}
