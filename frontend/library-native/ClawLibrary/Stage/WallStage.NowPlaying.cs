// 正在游戏：从游戏库启动时开始一次会话，在不在跑看「说了算」的地方（Steam 游戏看 Steam 注册表，本地游戏看 ClawGameSession 写的注册表，
// Xbox 游戏看交接时的进程）。游戏中回到游戏库先看到「正在游戏」面板（继续游戏 / 退出游戏 / 游戏库）。
// 游戏结束：记本次时长、排到最近游玩第一位；桌面模式下把游戏库带回前台，封面飞回卡片。
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClawLibrary.Data;
using static ClawLibrary.Engine.Geometry;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    /// <summary>
    /// 这款游戏在不在跑，看谁说了算：Steam 游戏看 Steam 自己的 Apps\&lt;appid&gt;\Running；
    /// 本地游戏由 ClawGameSession.exe 启动，看它写的 ClawToolbox\Games\Running；Xbox 游戏只能看交接时的那个进程。
    /// </summary>
    private enum SessionSource { Steam, Wrapper, Window }

    private sealed class GameSession
    {
        public required LibraryItem Item;
        public SessionSource Source;
        public int Pid;
        public DateTime ProcessStart;
        public uint WindowPid;
        public long WindowStart;
        public nint Hwnd;
        public DateTime Started;
        /// <summary>已启动、还没看到「在跑」：Steam 下载更新、启动器转圈都算在这里，2 分钟还没跑起来就放弃。</summary>
        public bool Pending = true;
        public bool Ending;
    }

    private GameSession? _game;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _gameTimer;
    /// <summary>用户在正在游戏面板上选了「游戏库」：这次回来不再弹面板，直到游戏库再次失去焦点。</summary>
    private bool _nowPlayingDismissed;
    private const string GamesKey = @"Software\ClawToolbox\Games";

    private GameSession? RunningGame(string? id) => _game is { Pending: false } g && g.Item.Id == id ? g : null;
    internal bool IsRunningGame(string id) => RunningGame(id) != null;

    private void RefreshRunningGameActions(string id)
    {
        if (_cs?.Page is { Tab: 0 } page && page.Sel < page.Items.Count && page.Items[page.Sel] is { } selected && selected.Id == id)
        {
            page.Act = 0;
            CsPaintActs(page, selected, CsActsWidth(selected));
        }
        if (_expandedKey is { } key && _tiles.TryGetValue(key, out var tile) && tile.Shown == id) tile.Redraw();
        if (_qsOpen) QsRebuild();
    }

    private static string PlayedText(GameSession? g)
    {
        if (g == null) return "";
        var t = DateTime.Now - g.Started;
        return t.TotalMinutes < 1 ? "刚刚开始" : t.TotalHours < 1 ? $"已玩 {(int)t.TotalMinutes} 分钟" : $"已玩 {(int)t.TotalHours} 小时 {t.Minutes} 分钟";
    }

    /// <summary>按 A 启动的那一刻开始一次会话（先是待定）；Steam 游戏把开始时间记进注册表，游戏库重启也能接上。</summary>
    private void BeginLaunchSession(LibraryItem item, SessionSource source)
    {
        _game = new GameSession { Item = item, Source = source, Started = DateTime.Now };
        _nowPlayingDismissed = false;
        if (source == SessionSource.Steam)
            try { using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(GamesKey + @"\Steam"); k.SetValue(item.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()); }
            catch (Exception ex) { Log.Warn($"steam session key: {ex.Message}"); }
        Log.Info($"game session {item.Id} launched via {source}");
        _gameTimer ??= NewGameTimer();
        _gameTimer.Start();
    }

    /// <summary>启动画面交接给游戏窗口：记下窗口（继续游戏用）；Xbox 游戏从这一刻起跟这个进程。</summary>
    private void BeginGameSession(LibraryItem item, nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (_game is { } g && g.Item.Id == item.Id)
        {
            g.Hwnd = hwnd;
            g.WindowPid = pid;
            g.WindowStart = ReadGameProcessStart(pid);
            if (g.Source != SessionSource.Window) return;
        }
        else if (item.XboxAumid == null) return;   // 只有 Xbox 游戏靠窗口
        else _game = new GameSession { Item = item, Source = SessionSource.Window, Hwnd = hwnd, Started = DateTime.Now };
        var s = _game!;
        s.Pid = (int)pid; s.Pending = false;
        s.WindowPid = pid; s.WindowStart = ReadGameProcessStart(pid);
        try { using var p = Process.GetProcessById((int)pid); s.ProcessStart = p.StartTime; Log.Info($"game session {item.Id} window {p.ProcessName} pid={pid}"); }
        catch (Exception ex) { Log.Warn($"game session start: {ex.Message}"); }
        EnhancedSleepClient.Publish(s.Pid);
        _gameTimer ??= NewGameTimer();
        _gameTimer.Start();
        RefreshRunningGameActions(item.Id);
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer NewGameTimer()
    {
        var t = _dq.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(2);
        t.Tick += (_, _) => PollGameSession();
        return t;
    }

    private static bool Alive(int pid, DateTime start)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited && (start == default || p.StartTime == start); }
        catch (ArgumentException) { return false; }
        catch { return true; }   // 看不到（权限）就当还在
    }

    /// <summary>Steam 自己的「在跑」标记：Apps\&lt;appid&gt;\Running = 1，或 RunningAppID 就是这款。</summary>
    private static bool SteamRunning(string appid)
    {
        try
        {
            using var steam = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("RunningAppID") is int running && running != 0 && running.ToString() == appid) return true;
            using var app = steam?.OpenSubKey(@"Apps\" + appid);
            return app?.GetValue("Running") is int r && r != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// ClawGameSession 写的「在跑」：pid|开始时间|启动器 pid。启动器还在就算在跑；启动器没了（游戏库升级时随包一起被结束）
    /// 但游戏主进程还在，也算在跑，只是跟不到它的子进程了。两个都没了才清掉这一条。
    /// </summary>
    private static (int Pid, long Start)? WrapperRunning(string id)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(GamesKey + @"\Running");
            if (k?.GetValue(id) is not string v || v.Split('|') is not { Length: >= 3 } p) return null;
            int pid = int.Parse(p[0]); long start = long.Parse(p[1]);
            bool wrapper = int.TryParse(p[2], out int wp) && Alive(wp, default);
            if (!wrapper && !Alive(pid, default)) { k.Close(); ClearWrapperEntry(id); return null; }
            return (pid, start);
        }
        catch { return null; }
    }

    private static void ClearWrapperEntry(string id)
    {
        try { using var w = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(GamesKey + @"\Running", true); w?.DeleteValue(id, false); } catch { }
    }

    private bool SessionRunning(GameSession g) => g.Source switch
    {
        SessionSource.Steam => SteamRunning(g.Item.Id),
        SessionSource.Wrapper => WrapperRunning(g.Item.Id) is { } w && (g.Pid = w.Pid) > 0,
        _ => Alive(g.Pid, g.ProcessStart),
    };

    private void PollGameSession()
    {
        var g = _game;
        if (g == null) { _gameTimer?.Stop(); return; }
        if (SessionRunning(g))
        {
            if (g.Pid > 0) EnhancedSleepClient.Publish(g.Pid);
            if (g.Pending)
            {
                g.Pending = false;
                RefreshRunningGameActions(g.Item.Id);
                Log.Info($"game session {g.Item.Id} running");
            }
            if (_lm is { Mode: LmMode.Playing } lm && !lm.Leaving) PaintLaunchCopy(lm);   // 时长每 2 秒刷新
            if (_npBar != null) PaintNowPlayingBar();
            return;
        }
        if (g.Pending)
        {
            if (DateTime.Now - g.Started < TimeSpan.FromMinutes(2)) return;
            Log.Info($"game session {g.Item.Id} never started; dropped");
            _game = null; _gameTimer?.Stop(); ClearSteamEntry(g);
            return;
        }
        EndGameSession(g);
    }

    private static void ClearSteamEntry(GameSession g)
    {
        if (g.Source != SessionSource.Steam) return;
        try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(GamesKey + @"\Steam", true); k?.DeleteValue(g.Item.Id, false); } catch { }
    }

    /// <summary>游戏库启动时：上次从游戏库开的游戏还在跑（游戏库被重启 / 更新过），接上计时。</summary>
    private void RestoreGameSession()
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(GamesKey + @"\Running"))
                foreach (var id in k?.GetValueNames() ?? Array.Empty<string>())
                    if (WrapperRunning(id) is { } w && Lookup(id) is { } item)
                    {
                        _game = new GameSession { Item = item, Source = SessionSource.Wrapper, Pid = w.Pid, Pending = false, Started = DateTimeOffset.FromUnixTimeSeconds(w.Start).LocalDateTime };
                        break;
                    }
            if (_game == null)
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(GamesKey + @"\Steam"))
                    foreach (var id in k?.GetValueNames() ?? Array.Empty<string>())
                    {
                        if (!SteamRunning(id) || Lookup(id) is not { } item) { using var w = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(GamesKey + @"\Steam", true); w?.DeleteValue(id, false); continue; }
                        long.TryParse(k!.GetValue(id) as string, out long start);
                        _game = new GameSession { Item = item, Source = SessionSource.Steam, Pending = false, Started = DateTimeOffset.FromUnixTimeSeconds(start).LocalDateTime };
                        break;
                    }
        }
        catch (Exception ex) { Log.Warn($"restore game session: {ex.Message}"); }
        if (_game is { } g)
        {
            Log.Info($"game session {g.Item.Id} restored ({g.Source}, since {g.Started:HH:mm})");
            _windowFocusWatch ??= WatchLaunchFocus();
            _gameTimer ??= NewGameTimer();
            _gameTimer.Start();
        }
    }

    private void EndGameSession(GameSession g)
    {
        _game = null;
        EnhancedSleepClient.Publish(0);
        HideNowPlayingBar();
        _gameTimer?.Stop();
        var played = DateTime.Now - g.Started;
        Log.Info($"game session end {g.Item.Id} after {played.TotalMinutes:F1} min");
        RecordPlaytime(g.Item, g.Started, played);
        ClearSteamEntry(g);
        // 排到最近游玩第一位
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        g.Item.LastPlayed = now;
        if (_lib.ById.TryGetValue(g.Item.Id, out var same)) same.LastPlayed = now;
        try { ApplyLibrarySnapshot(_lib.Refilter()); } catch (Exception ex) { Log.Error("reorder after game", ex); }
        RefreshRunningGameActions(g.Item.Id);
        if (g.Ending && !NowPlayingShown)
        {
            DropParkedMotion();
            Toast("已结束游戏");
            return;
        }
        // 桌面模式下游戏库不会自己出来：带回前台（全屏体验里系统本来就会切回主页）
        if (LibraryWindow.Foreground != _windowHandle && _window is MainWindow main) main.ActivateRoute("restore-library");
        string note = played.TotalMinutes < 1 ? "已退出" : $"已退出 · 本次玩了 {(played.TotalHours >= 1 ? $"{(int)played.TotalHours} 小时 " : "")}{played.Minutes} 分钟";
        if (_lm is { } lm && !lm.Leaving && lm.Item.Id == g.Item.Id) { lm.Mode = LmMode.Ended; lm.Note = note; PaintLaunchCopy(lm); }
        else if (UnparkNowPlaying(g) && _lm is { } kept) { kept.Mode = LmMode.Ended; kept.Note = note; PaintLaunchCopy(kept); }
        else
        {
            StartLaunchMotion(g.Item, TileShowing(g.Item.Id), LmMode.Ended);
            if (_lm != null) _lm.Note = note;
        }
        var ended = _lm;
        After(1800, () => { if (_lm == ended && ended != null) { ended.From = FlyBackRect(ended.Item.Id) ?? ended.From; EndLaunchMotion("back"); } });
    }

    private Tile? TileShowing(string id) => _tiles.Values.FirstOrDefault(t => t.Shown == id && t.Target == id);

    private Engine.Rect? FlyBackRect(string id)
    {
        if (TileShowing(id) is not { } tile) return null;
        var cell = ScreensaverScreenRect(tile);
        double fh = Math.Min(cell.Height, cell.Width * 1.5), fw = fh * 2 / 3;
        return new Engine.Rect(cell.X + (cell.Width - fw) / 2, cell.Y + (cell.Height - fh) / 2, fw, fh);
    }

    private static void RecordPlaytime(LibraryItem item, DateTime started, TimeSpan played)
    {
        try
        {
            Directory.CreateDirectory(App.CacheDir);
            File.AppendAllText(Path.Combine(App.CacheDir, "playtime.tsv"),
                $"{item.Id}\t{new DateTimeOffset(started).ToUnixTimeSeconds()}\t{(int)played.TotalSeconds}\t{item.Title}\n");
        }
        catch (Exception ex) { Log.Warn($"playtime: {ex.Message}"); }
    }

    /// <summary>游戏库回到前台、有游戏在跑：先给「正在游戏」面板（用户刚选过「游戏库」就不再弹）。</summary>
    private void MaybeShowNowPlaying()
    {
        if (_game is not { } g || _nowPlayingDismissed || _ccOpen || _ob != null) return;
        if (g.Pending) return;
        if (!SessionRunning(g)) { PollGameSession(); return; }
        if (_lm != null && !_lm.Leaving) return;
        if (_prefs.NowPlaying == NowPlayingStyle.Bar) { ShowNowPlayingBar(g); return; }
        if (UnparkNowPlaying(g)) { Log.Info($"now playing panel {g.Item.Id} (kept)"); return; }
        StartLaunchMotion(g.Item, TileShowing(g.Item.Id), LmMode.Playing);
        Log.Info($"now playing panel {g.Item.Id}");
    }

    private void NowPlayingPress(int button)
    {
        var g = _game;
        if (g == null) { EndLaunchMotion("back"); return; }
        switch (button)
        {
            case 0: ResumeGame(g); break;
            case 1: _ = ForceQuitAsync(g); break;
            case 3: OpenSwitcherFromAnywhere(); break;
            default: BrowseFromNowPlaying(); break;
        }
    }

    private void BrowseFromNowPlaying()
    {
        _nowPlayingDismissed = true;
        if (_lm is { } lm && _game is { } g) lm.From = FlyBackRect(g.Item.Id) ?? lm.From;
        EndLaunchMotion("browse");
    }

    private void ResumeGame(GameSession g)
    {
        if (g.Ending) return;
        nint hwnd = GameWindow(g);
        if (hwnd != 0) (_window as MainWindow)?.HandOffForeground();
        bool ok = hwnd != 0 && (LibraryWindow.BringForward(hwnd) || LibraryWindow.BringForward(hwnd, unlock: true));
        Log.Info($"resume game {g.Item.Id} hwnd={hwnd} foreground={ok}");
        if (ok) { g.Hwnd = hwnd; EndLaunchMotion("handoff"); HideNowPlayingBar(); }
        else
        {
            if (_lm != null) { _lm.Note = "没能切回游戏，请用窗口切换器"; PaintLaunchCopy(_lm); }
            else Toast("没能切回游戏，请用窗口切换器");
            Cue(Sfx.Error);
        }
    }

    private string? SteamGameFolder(GameSession g) => _steamRoot != null && g.Source == SessionSource.Steam
        ? Steam.InstallDirs(_steamRoot).GetValueOrDefault(g.Item.Id) : null;

    private static bool InGameFolder(uint pid, string? folder) =>
        folder != null && OpenWindows.ExePath(pid) is { } exe &&
        exe.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private nint GameWindow(GameSession g)
    {
        if (g.WindowStart != 0 && IsWindowVisible(g.Hwnd) && GetWindowThreadProcessId(g.Hwnd, out uint pid) != 0 &&
            pid == g.WindowPid && ReadGameProcessStart(pid) == g.WindowStart) return g.Hwnd;
        if (g.Pid > 0 && SessionRunning(g))
        {
            nint tracked = MainWindowOf(g.Pid);
            if (tracked != 0) return tracked;
        }
        string? folder = SteamGameFolder(g);
        return folder == null ? 0 : OpenWindows.List().FirstOrDefault(w => InGameFolder(w.Pid, folder))?.Hwnd ?? 0;
    }

    private bool IsGameWindow(GameSession g, nint window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0) return false;
        if (g.WindowStart != 0 && g.WindowPid == pid && ReadGameProcessStart(pid) == g.WindowStart) return true;
        return g.Source == SessionSource.Wrapper ? WrapperRunning(g.Item.Id)?.Pid == pid :
            InGameFolder(pid, SteamGameFolder(g));
    }

    private async Task EndForegroundGameAsync(nint window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        long start = ReadGameProcessStart(pid);
        if (start == 0 || !LibraryWindow.OwnsForeground(window)) { Toast("没能确认要结束的游戏"); return; }
        try
        {
            _agent ??= new AgentClient();
            await _agent.Call("close_game", System.Text.Json.Nodes.JsonValue.Create((int)pid));
            if (ReadGameProcessStart(pid) == start && Alive((int)pid, DateTime.FromFileTime(start)))
                throw new InvalidOperationException("游戏进程没有退出");
            Toast("已结束游戏");
        }
        catch (Exception ex) { Log.Warn($"end foreground game: {ex.Message}"); Toast("没能结束游戏"); }
    }

    private static long ReadGameProcessStart(uint pid)
    {
        nint process = OpenProcessQs(0x1000, false, pid);
        if (process == 0) return 0;
        try { return GetProcessTimes(process, out long start, out _, out _, out _) ? start : 0; }
        finally { CloseHandleQs(process); }
    }

    private async Task ForceQuitAsync(GameSession g)
    {
        if (_game != g || g.Ending) return;
        if (!SessionRunning(g)) { PollGameSession(); return; }
        g.Ending = true;
        Log.Info($"force quit {g.Item.Id} pid={g.Pid}");
        if (NowPlayingShown) { _lm!.Note = "正在结束…"; PaintLaunchCopy(_lm); }
        try
        {
            if (g.Source == SessionSource.Wrapper && TryEndWrappedGame(g.Item.Id))
            {
                for (int i = 0; i < 45 && _game == g && SessionRunning(g); i++) await Task.Delay(100);
                if (_game != g) return;
                if (SessionRunning(g)) throw new InvalidOperationException("游戏进程没有退出");
            }
            else
            {
                nint window = GameWindow(g);
                GetWindowThreadProcessId(window, out uint pid);
                long start = ReadGameProcessStart(pid);
                bool tracked = pid == g.WindowPid && start == g.WindowStart;
                bool verified = g.Source == SessionSource.Steam ? InGameFolder(pid, SteamGameFolder(g)) : tracked;
                if (!verified || start == 0 || pid == Environment.ProcessId)
                    throw new InvalidOperationException("无法确认游戏进程");
                try { await Task.Run(() => TerminateGameProcess(pid, start)); }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    // The existing elevated command only accepts the confirmed foreground target.
                    if (ReadGameProcessStart(pid) != start || !LibraryWindow.BringForward(window, unlock: true)) throw;
                    _agent ??= new AgentClient();
                    await _agent.Call("close_game", System.Text.Json.Nodes.JsonValue.Create((int)pid));
                    if (ReadGameProcessStart(pid) == start && Alive((int)pid, DateTime.FromFileTime(start)))
                        throw new InvalidOperationException("游戏进程没有退出");
                }
            }
            if (_game == g) EndGameSession(g);
        }
        catch (Exception ex)
        {
            Log.Warn($"force quit {g.Item.Id}: {ex.Message}");
            if (_game == g && NowPlayingShown) { _lm!.Note = "没能结束游戏"; PaintLaunchCopy(_lm); }
            Cue(Sfx.Error);
            Toast("没能结束游戏");
        }
        finally { g.Ending = false; }
    }

    private static bool TryEndWrappedGame(string id)
    {
        if (!EventWaitHandle.TryOpenExisting(@"Local\ClawGameSession.Quit." + id, out var quit)) return false;
        using (quit) return quit.Set();
    }

    private static void TerminateGameProcess(uint pid, long expectedStart)
    {
        nint process = OpenProcessQs(0x00101001, false, pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION | TERMINATE
        if (process == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!GetProcessTimes(process, out long start, out _, out _, out _) || start != expectedStart)
                throw new InvalidOperationException("游戏进程已改变");
            if (!TerminateProcess(process, 1))
            {
                int error = Marshal.GetLastWin32Error();
                if (WaitForGameExit(process, 0) != 0) throw new System.ComponentModel.Win32Exception(error);
            }
            if (WaitForGameExit(process, 4500) != 0)
                throw new InvalidOperationException("游戏进程没有退出");
        }
        finally { CloseHandleQs(process); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject")] private static extern uint WaitForGameExit(nint process, uint milliseconds);

    private static nint MainWindowOf(int pid)
    {
        nint found = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint p);
            if (p != pid) return true;
            found = h; return false;
        }, 0);
        return found;
    }

    private delegate bool EnumProc(nint hwnd, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
}

public sealed partial class WallStage
{
    /// <summary>录屏用：拿聚焦的游戏假装正在玩了 23 分钟（进程借资源管理器），弹出正在游戏面板。</summary>
    internal bool DemoNowPlaying(bool bar = false)
    {
        if (FocusKey is not { } key || SlotByKey(key) is not { } slot || ItemFor(slot) is not { } id || Lookup(id) is not { } item) return false;
        var shell = Process.GetProcessesByName("explorer").FirstOrDefault();
        if (shell == null) return false;
        _game = new GameSession { Item = item, Source = SessionSource.Window, Pending = false, Pid = shell.Id, ProcessStart = shell.StartTime, Started = DateTime.Now.AddMinutes(-23) };
        _nowPlayingDismissed = false;
        if (bar) ShowNowPlayingBar(_game!); else MaybeShowNowPlaying();
        return true;
    }

    /// <summary>录屏用：游戏结束的那一段（不记时长、不改排序、不拉前台）。</summary>
    internal void DemoGameEnded()
    {
        if (_game is not { } g) return;
        _game = null;
        string note = "已退出 · 本次玩了 23 分钟";
        if (_lm is { } lm && !lm.Leaving) { lm.Mode = LmMode.Ended; lm.Note = note; PaintLaunchCopy(lm); }
        var ended = _lm;
        After(1800, () => { if (_lm == ended && ended != null) { ended.From = FlyBackRect(ended.Item.Id) ?? ended.From; EndLaunchMotion("back"); } });
    }
}

public sealed partial class WallStage
{
    private SpriteVisualHolder? _npBar;
    private sealed class SpriteVisualHolder { public required Microsoft.UI.Composition.SpriteVisual Sprite; public Microsoft.Graphics.Canvas.CanvasBitmap? Thumb; }

    /// <summary>第二种：直接回墙面，顶上居中一条「正在游戏」（封面小图 + 名字 + 时长 + B 回到游戏）。</summary>
    private void ShowNowPlayingBar(GameSession g)
    {
        if (_npBar == null)
        {
            var s = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            _top.Children.InsertAtTop(s);
            _npBar = new SpriteVisualHolder { Sprite = s };
            if (g.Item.Art is { } art && (art.Has('q') || art.Has('p')))
            {
                var holder = _npBar;
                string path = art.Has('q') ? art.File('q') : art.File('p');
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var bmp = await Microsoft.Graphics.Canvas.CanvasBitmap.LoadAsync(G.Device, path, 96);
                        _dq.TryEnqueue(() => { if (_npBar == holder) { holder.Thumb = bmp; PaintNowPlayingBar(); } else bmp.Dispose(); });
                    }
                    catch (Exception ex) { Log.Warn($"now playing thumb: {ex.Message}"); }
                });
            }
            _npBar.Sprite.StartAnimation("Opacity", G.Scalar(0, 1, Reduced ? 1 : 280, Engine.Bezier.EaseOut));
        }
        PaintNowPlayingBar();
        Log.Info($"now playing bar {g.Item.Id}");
    }

    private void PaintNowPlayingBar()
    {
        if (_npBar is not { } bar || _game is not { } g) return;
        const float h = 72;
        string title = g.Item.Title, sub = $"正在游戏 · {PlayedText(g)}", hint = "B 回到游戏";
        float w;
        using (var probe = new Microsoft.Graphics.Canvas.CanvasRenderTarget(G.Device, 1, 1, 96))
        using (var ds0 = probe.CreateDrawingSession())
        {
            using var t1 = TextKit.Layout(ds0, title, TextKit.Ui(700), 20, 600, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left);
            using var t2 = TextKit.Layout(ds0, sub, TextKit.Ui(500), 15, 600, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left);
            using var t3 = TextKit.Layout(ds0, hint, TextKit.Ui(600), 15, 300, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left);
            w = 12 + 40 + 16 + (float)Math.Max(t1.LayoutBounds.Width, t2.LayoutBounds.Width) + 28 + (float)t3.LayoutBounds.Width + 28 + 24;
        }
        bar.Sprite.Size = new System.Numerics.Vector2(w, h);
        bar.Sprite.Offset = new System.Numerics.Vector3((float)(View.Width - w) / 2, 18, 0);
        CcSet(bar.Sprite, CcDraw(w, h, Chrome.Dpi, ds =>
        {
            ds.FillRoundedRectangle(0, 0, w, h, h / 2, h / 2, Gfx.Rgba(14, 14, 18, 0.88));
            ds.DrawRoundedRectangle(0.5f, 0.5f, w - 1, h - 1, h / 2, h / 2, Gfx.Rgba(255, 255, 255, 0.12), 1);
            if (bar.Thumb is { } th)
            {
                using var clip = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreateRoundedRectangle(ds, 16, 8, 37, 56, 6, 6);
                using (ds.CreateLayer(1, clip)) ds.DrawImage(th, new Windows.Foundation.Rect(16, 8, 37, 56), th.Bounds);
            }
            float x = 16 + 37 + 16;
            using var t1 = TextKit.Layout(ds, title, TextKit.Ui(700), 20, 600, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left);
            using var t2 = TextKit.Layout(ds, sub, TextKit.Ui(500), 15, 600, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left);
            ds.DrawTextLayout(t1, x, 11, TextKit.Ink());
            ds.DrawTextLayout(t2, x, 40, TextKit.Ink(0.66));
            using var t3 = TextKit.Layout(ds, hint, TextKit.Ui(600), 15, 300, Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left);
            float pw = (float)t3.LayoutBounds.Width + 28, px = w - 14 - pw;
            ds.FillRoundedRectangle(px, 18, pw, 36, 18, 18, Gfx.Rgba(255, 255, 255));
            ds.DrawTextLayout(t3, px + 14, 36 - (float)t3.LayoutBounds.Height / 2, Gfx.Rgba(12, 12, 14));
        }));
    }

    private void HideNowPlayingBar()
    {
        if (_npBar is not { } bar) return;
        _npBar = null;
        bar.Sprite.StartAnimation("Opacity", FadeOut(Reduced ? 1 : 200));
        After(240, () => { _top.Children.Remove(bar.Sprite); bar.Sprite.Dispose(); bar.Thumb?.Dispose(); });
    }

    /// <summary>
    /// 启动拦截：同一款正在玩 → 切回去，不再启动一次；另一款 → 先给正在玩的那款的面板，提示先退出。
    /// </summary>
    private bool InterceptLaunchWhilePlaying(LibraryItem item)
    {
        if (_game is not { } g) return false;
        if (!g.Pending && !SessionRunning(g)) { PollGameSession(); return _game != null && InterceptLaunchWhilePlaying(item); }
        if (item.Id == g.Item.Id) { ResumeGame(g); return true; }
        StartLaunchMotion(g.Item, TileShowing(g.Item.Id), LmMode.Playing);
        if (_lm != null) { _lm.Note = $"先退出「{g.Item.Title}」再启动「{item.Title}」"; _lm.Btn = 1; PaintLaunchCopy(_lm); }
        return true;
    }
}

public sealed partial class WallStage
{
    /// <summary>
    /// 经 Payload\session\ClawGameSession.exe 启动本地游戏。它带着「子进程脱离应用包」的策略启动，
    /// 所以游戏本身跑在应用包外面（存档、注册表都写到真实位置，和双击 exe 一样）。
    /// </summary>
    private bool StartViaGameSession(LibraryItem item, string exe, string dir, string args)
    {
        string session = Path.Combine(AppContext.BaseDirectory, "Payload", "session", "ClawGameSession.exe");
        if (!File.Exists(session) || !File.Exists(exe)) { Log.Warn($"game session launcher missing or exe gone: {exe}"); return false; }
        static string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
        string cmd = $"{Q(session)} --id {Q(item.Id)} --exe {Q(exe)} --dir {Q(string.IsNullOrEmpty(dir) ? Path.GetDirectoryName(exe) ?? "" : dir)} --args {Q(args)}";
        nint list = 0;
        try
        {
            nint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            list = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new System.ComponentModel.Win32Exception();
            nint policy = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(policy, 0x1);   // PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE
            if (!UpdateProcThreadAttribute(list, 0, 0x00020012 /* PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY */, policy, 4, 0, 0))
                throw new System.ComponentModel.Win32Exception();
            var si = new STARTUPINFOEX { StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() }, lpAttributeList = list };
            if (!CreateProcess(null, cmd, 0, 0, false, 0x00080000 /* EXTENDED_STARTUPINFO_PRESENT */ | 0x08000000 /* CREATE_NO_WINDOW */, 0, null, ref si, out var pi))
                throw new System.ComponentModel.Win32Exception();
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            Marshal.FreeHGlobal(policy);
            BeginLaunchSession(item, SessionSource.Wrapper);
            Log.Info($"launch via game session {item.Id} {exe} {args}");
            return true;
        }
        catch (Exception ex) { Log.Warn($"game session launch {item.Id}: {ex.Message}"); return false; }
        finally { if (list != 0) { DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(list); } }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public nint lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint prev, nint retSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcess(string? app, string cmd, nint pa, nint ta, bool inherit, uint flags, nint env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
}

public sealed partial class WallStage
{
    /// <summary>测试用（--state=gstest）：经 ClawGameSession 启动 cmd（写一个文件到 LocalAppData、再跑 6 秒 ping），看会话从待定到在跑再到结束。</summary>
    internal void DemoGameSessionProbe()
    {
        var item = new LibraryItem { Id = "gstest", Title = "会话测试" };
        string probe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gs_probe.txt");
        bool ok = StartViaGameSession(item, @"C:\Windows\System32\cmd.exe", "", $"/c echo probe> \"{probe}\" & start /b ping -n 7 127.0.0.1");
        Log.Info($"gstest started={ok}");
    }
}

public sealed partial class WallStage
{
    /// <summary>正在游戏画面开着（游戏中唤出的那一屏）。</summary>
    internal bool NowPlayingShown => _lm is { Leaving: false, Mode: LmMode.Playing };

    /// <summary>Claw 键在正在游戏画面上：等于「继续游戏」。没有在跑的游戏返回 false，照常收起游戏库。</summary>
    internal bool NowPlayingResume()
    {
        if (!NowPlayingShown || _game is not { Pending: false } g) return false;
        ResumeGame(g);
        return LibraryWindow.Foreground != _windowHandle;
    }
}

public sealed partial class WallStage
{
    /// <summary>测试用（--state=npattach）：本地游戏里有正在跑的（按程序路径对），就当它是从游戏库打开的，接上会话。</summary>
    internal void DemoAttachRunningGame()
    {
        foreach (var item in _lib.Games)
        {
            string? exe = item.ExePath ?? (item.Platform != "steam" ? Steam.ShortcutLaunch(_steamUser, item.Id)?.Exe : null);
            if (exe == null) continue;
            var name = Path.GetFileNameWithoutExtension(exe);
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    _game = new GameSession { Item = item, Source = SessionSource.Window, Pending = false, Pid = p.Id, ProcessStart = p.StartTime, Hwnd = MainWindowOf(p.Id), Started = p.StartTime };
                    _windowFocusWatch ??= WatchLaunchFocus();
                    _gameTimer ??= NewGameTimer(); _gameTimer.Start();
                    Log.Info($"npattach {item.Id} {item.Title} pid={p.Id}");
                    return;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        Log.Info("npattach: nothing running");
    }
}
