namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    public void ActivateShortcut(string route)
    {
        Log.Info($"shortcut activation: {route}");
        if (_qsOpen) return;   // 快速设置键开的是快捷设置面板，不进控制中心
        if (route is "control-center" or "library")
        {
            if (MorphBusy) RushMorph();
            if (_backgroundSettingsOpen) CloseBackgroundSettings();
        }
        if (route == "control-center")
        {
            ClosePerf(instant: true);
            CloseGamepad(instant: true);
            CloseDisplay(instant: true);
            CloseSystem(instant: true);
            CloseSettingsPage(instant: true);
            if (!_ccOpen) OpenControlCenter();
        }
        else if (route == "library" && _ccOpen) CloseControlCenter();
    }
}
