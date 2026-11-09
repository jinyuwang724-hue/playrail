from pathlib import Path
from importlib.util import spec_from_file_location,module_from_spec
s=spec_from_file_location('edit',Path(__file__).with_name('Edit-SpecialKeys.py'));m=module_from_spec(s);s.loader.exec_module(m)
edit=m.edit;LIB=m.LIB
p=LIB/'Data/LibraryWindow.cs'
edit(p,'        if (message == 0x007e', '        if (message == 0x0083 && !Program.Args.Contains("--windowed")) return 0; // WM_NCCALCSIZE: the entire monitor is client area.\n        if (message == 0x007e')
p=LIB/'Stage/WallStage.ShortcutSurfaces.cs'
edit(p,'    internal bool SwitcherOpen','    private bool? _specialStickNavigates;\n    internal bool SwitcherOpen')
edit(p,'        _rootEl.Overlay.Opacity = open ? 0 : 1;','''        if (_pad != null)
        {
            if (open) { _specialStickNavigates ??= _pad.StickNavigates; _pad.StickNavigates = true; }
            else if (_specialStickNavigates is bool previous) { _pad.StickNavigates = previous; _specialStickNavigates = null; }
        }
        _rootEl.Overlay.Opacity = open ? 0 : 1;''')
p=LIB/'Stage/WallStage.cs'
edit(p,'        if (_ccOpen) { PanVelocity = Vector2.Zero;', '        if (_ccOpen || _wsOpen || _qsOpen) { PanVelocity = Vector2.Zero;')
edit(p,'        if (_ccOpen || MorphBusy) return;','        if (_ccOpen || _wsOpen || _qsOpen || MorphBusy) return;')
edit(p,'            if (_qsOpen) { if (intent == TouchResult.Horizontal', '            if (_wsOpen) { }\n            else if (_qsOpen) { if (intent == TouchResult.Horizontal')
edit(p,'        if (_qsOpen)\n        {\n            if (result == TouchResult.Tap', '''        if (_wsOpen)
        {
            if (result == TouchResult.Tap) WsClick(pt.Position.X, pt.Position.Y);
            else if (result == TouchResult.Horizontal) WsMove(pt.Position.X < _dragFrom.x ? Direction.Right : Direction.Left);
            return;
        }
        if (_qsOpen)
        {
            if (result == TouchResult.Tap''')
p=LIB/'Package.appxmanifest';edit(p,'Version="0.1.197.0"','Version="0.1.198.0"')
