using ClawLibrary.Data;

static void Check(bool value, string label)
{
    if (!value) throw new Exception(label);
    Console.WriteLine("PASS " + label);
}

Check(ToolboxActivation.ShouldMinimize("minimize-library", false, false), "ordinary desktop permits Claw minimize");
Check(ToolboxActivation.ShouldMinimize("minimize-control-center", false, false), "control center uses the same toggle");
Check(!ToolboxActivation.ShouldMinimize("minimize-library", true, false), "Xbox full-screen mode never minimizes");
Check(!ToolboxActivation.ShouldMinimize("minimize-library", false, true), "Steam Big Picture never minimizes");
Check(!ToolboxActivation.ShouldMinimize("minimize-library", null, false), "unknown Xbox mode keeps open-only behavior");
Check(!ToolboxActivation.ShouldMinimize("minimize-library", false, null), "unknown Steam window state keeps open-only behavior");
Check(!ToolboxActivation.ShouldMinimize("library", false, false), "ordinary launch never toggles");
Check(ToolboxActivation.Target("restore-control-center") == "control-center", "cold start opens the configured destination");
Check(ToolboxActivation.Target("minimize-library") == "library", "a stale minimize request on cold start opens the app");
Check(ToolboxActivation.PreservePage("restore-library"), "restoring an existing window preserves its current page");
Check(!ToolboxActivation.PreservePage("library"), "ordinary library navigation still opens the library");
Check(!ToolboxActivation.IsRoute("minimize-any-app"), "unrecognized window actions are rejected");
