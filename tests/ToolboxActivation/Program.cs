using ClawLibrary.Data;

static void Check(bool value, string label)
{
    if (!value) throw new Exception(label);
    Console.WriteLine("PASS " + label);
}

Check(ToolboxActivation.ShouldMinimize("minimize-library", false, false), "ordinary desktop permits Claw minimize");
Check(ToolboxActivation.ShouldMinimize("minimize-control-center", false, false), "control center uses the same toggle");
Check(ToolboxActivation.ShouldMinimize("minimize-library", true, false), "Xbox full-screen mode permits explicit shortcut minimize");
Check(ToolboxActivation.ShouldMinimize("minimize-library", false, true), "Steam permits explicit library shortcut minimize");
Check(ToolboxActivation.ShouldMinimize("minimize-library", null, false), "unavailable Xbox API does not block toggling");
Check(ToolboxActivation.ShouldMinimize("minimize-library", false, null), "unknown Steam state does not block toggling");
Check(!ToolboxActivation.ShouldMinimize("library", false, false), "ordinary launch never toggles");
Check(ToolboxActivation.Target("restore-control-center") == "control-center", "cold start opens the configured destination");
Check(ToolboxActivation.Target("minimize-library") == "library", "a stale minimize request on cold start opens the app");
Check(ToolboxActivation.PreservePage("restore-library"), "restoring an existing window preserves its current page");
Check(!ToolboxActivation.PreservePage("library"), "ordinary library navigation still opens the library");
Check(!ToolboxActivation.IsRoute("minimize-any-app"), "unrecognized window actions are rejected");
Check(ToolboxActivation.ReturnWindow("?returnWindow=123456") == 123456, "cold activation retains the original window");
Check(ToolboxActivation.ReturnWindow("?returnWindow=-1") == 0, "special HWND values rejected");
Check(ToolboxActivation.ReturnWindow("?returnWindow=invalid") == 0, "invalid target rejected");
