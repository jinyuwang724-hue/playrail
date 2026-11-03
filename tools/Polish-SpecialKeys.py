from pathlib import Path
from importlib.util import spec_from_file_location,module_from_spec
s=spec_from_file_location('edit',Path(__file__).with_name('Edit-SpecialKeys.py'));m=module_from_spec(s);s.loader.exec_module(m)
edit=m.edit;SRC=m.SRC;LIB=m.LIB
p=SRC/'plugin/claw_button_mappings.py'
edit(p,"        import psutil\n        return psutil.Process(pid.value).name().lower() != 'explorer.exe' ",'''        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        handle = kernel.OpenProcess(0x1000, False, pid.value)
        if not handle: return False
        try:
            name = ctypes.create_unicode_buffer(1024); size = wintypes.DWORD(1024)
            return bool(kernel.QueryFullProcessImageNameW(handle, 0, name, ctypes.byref(size))) and os.path.basename(name.value).lower() != 'explorer.exe'
        finally: kernel.CloseHandle(handle)''')
p=LIB/'Data/ToolboxActivation.cs'
edit(p,'or "restore-library" or "restore-control-center" or "minimize-library" or "minimize-control-center";','or "restore-library" or "restore-control-center" or "minimize-library" or "minimize-control-center" or "window-switcher";')
p=LIB/'MainWindow.Overlay.cs'
edit(p,'private bool _surfaceHideWindow, _surfaceHadOverlay;','private bool _surfaceHideWindow, _surfaceHadOverlay, _surfaceSession;')
edit(p,'if (special && _stage?.SpecialSurfaceOpen != true)','if (special && !_surfaceSession)')
edit(p,'                _surfaceHideWindow = !owned;','                _surfaceSession = true;\n                _surfaceHideWindow = !owned;')
edit(p,'        if (_surfaceHideWindow) HideShortcutWindow();\n        else if (!_surfaceHadOverlay) ReleaseOverlay();','        if (_surfaceHideWindow) CompleteShortcutHide();\n        else if (!_surfaceHadOverlay) ReleaseOverlay();\n        _surfaceSession = false;')
p=LIB/'MainWindow.Shortcut.cs'
edit(p,'        _shortcutMotion.Cancel();\n        bool returnFocus','        _surfaceSession = false;\n        _shortcutMotion.Cancel();\n        bool returnFocus')
p=LIB/'Stage/WallStage.cs'
for suffix in ('Move(dir)','Activate()','Back()','Click(sx, sy)'):
    old='        if (_ob != null) { Ob'+suffix+'; return; }\n        if (_qsOpen) { Qs'+suffix+'; return; }'
    new='        if (_qsOpen) { Qs'+suffix+'; return; }\n        if (_ob != null) { Ob'+suffix+'; return; }'
    edit(p,old,new)
p=LIB/'Stage/WallStage.QuickPanel.cs'
edit(p,'        _qsHideWindowAfter = false;\n        if (hide &&','        _qsHideWindowAfter = false;\n        if (!hide && !_wsOpen) SetSpecialInput(false);\n        if (hide &&')
p=LIB/'Stage/WallStage.ShortcutSurfaces.cs'
edit(p,'        NowPlayingYield();','        SetSpecialInput(true);\n        NowPlayingYield();')
p=LIB/'Package.appxmanifest';edit(p,'Version="0.1.196.0"','Version="0.1.197.0"')
p=SRC/'native/FirmwareShortcutFilter.cs'
edit(p,'        public long Suppressed {get;private set;}','        public bool RawReady {get{return keyboardSource!=null&&keyboardSource.Ready;}}\n        public int FirmwareWindows {get{return keyboardSource==null?0:keyboardSource.FirmwareWindows;}}\n        public long Suppressed {get;private set;}')
p=SRC/'native/ClawRouter.cs'
edit(p,'{"shortcutFilterReady",shortcutFilter != null},','{"shortcutFilterReady",shortcutFilter != null}, {"sessionGameMode",sessionGameMode}, {"rawKeyboardReady",shortcutFilter!=null&&shortcutFilter.RawReady}, {"firmwareWindows",shortcutFilter==null?0:shortcutFilter.FirmwareWindows},')
