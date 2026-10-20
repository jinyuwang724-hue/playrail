namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    public void ActivateShortcut(string route)
    {
        Log.Info($"shortcut activation: {route}");
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
            if (!_ccOpen) OpenControlCenter();
        }
        else if (route == "library" && _ccOpen) CloseControlCenter();
    }
}
