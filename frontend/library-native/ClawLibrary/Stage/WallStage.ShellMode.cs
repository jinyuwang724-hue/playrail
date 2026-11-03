// 游戏模式 / 桌面模式：游戏库只管界面，真正切换的是 Payload\shell\ClawShell.exe（未打包的小启动器，源码在 native/ClawShell）。
// 游戏模式 = 登录后 ClawShell 当 Windows 外壳，不启动 explorer；桌面模式 = 普通 explorer。切换不重启，只启停 explorer。
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClawLibrary.Data;

namespace ClawLibrary.Stage;

internal static class ShellMode
{
    public static string Exe => Path.Combine(AppContext.BaseDirectory, "Payload", "shell", "ClawShell.exe");
    public static bool Available => File.Exists(Exe);
    public static bool GameMode => Available && !ExplorerRunning() && ReadState()?["mode"]?.GetValue<string>() == "game";

    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawToolbox", "shell", "state.json");

    /// <summary>上次读到的状态（ClawShell 写的 state.json）。</summary>
    public static JsonObject? State { get; private set; }

    /// <summary>下次登录是游戏模式（Winlogon Shell 指向 ClawShell）。</summary>
    public static bool DefaultGame => State?["default"]?.GetValue<string>() == "game";
    public static bool ForceFlag => State?["forceFlag"]?.GetValue<bool>() == true;

    /// <summary>现在是桌面模式：explorer 是桌面外壳。</summary>
    public static bool ExplorerRunning()
    {
        nint shell = GetShellWindow();
        if (shell == 0) return false;
        GetWindowThreadProcessId(shell, out uint pid);
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static JsonObject? ReadState()
    {
        try { State = JsonNode.Parse(File.ReadAllText(StatePath)) as JsonObject; }
        catch { }
        return State;
    }

    /// <summary>
    /// 跑 ClawShell 的一个命令，返回退出码（0 成功 / 1 失败 / 2 拒绝，-1 没跑起来）。
    /// 带「子进程脱离应用包」的策略启动：ClawShell 在包里会再启动自己一次，那一份在包外，注册表和 explorer 都是真实的。
    /// </summary>
    public static async Task<int> Run(params string[] args)
    {
        if (!Available) return -1;
        int code = await Task.Run(() =>
        {
            // --task：ClawShell 经计划任务再跑一份完全在包外的（从应用包里起的进程写 HKCU 会进包的虚拟注册表）
            string cmd = "\"" + Exe + "\" " + string.Join(" ", args) + " --task";
            nint list = 0, policy = 0;
            try
            {
                nint size = 0;
                InitializeProcThreadAttributeList(0, 1, 0, ref size);
                list = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) return -1;
                policy = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(policy, 0x1);   // PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE
                if (!UpdateProcThreadAttribute(list, 0, 0x00020012, policy, 4, 0, 0)) return -1;
                var si = new STARTUPINFOEX { StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() }, lpAttributeList = list };
                if (!CreateProcess(null, cmd, 0, 0, false, 0x00080000 | 0x08000000, 0, null, ref si, out var pi)) return -1;
                CloseHandle(pi.hThread);
                WaitForSingleObject(pi.hProcess, 130_000);
                GetExitCodeProcess(pi.hProcess, out uint exit);
                CloseHandle(pi.hProcess);
                return (int)exit;
            }
            catch (Exception ex) { Log.Warn($"clawshell {cmd}: {ex.Message}"); return -1; }
            finally
            {
                if (list != 0) { DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(list); }
                if (policy != 0) Marshal.FreeHGlobal(policy);
            }
        });
        ReadState();
        Log.Info($"clawshell {string.Join(" ", args)} -> {code} {State?["message"]}");
        return code;
    }

    /// <summary>拒绝 / 失败时给用户看的一句话。</summary>
    public static string Why()
    {
        string m = State?["message"]?.GetValue<string>() ?? "";
        if (m.Contains("force-desktop.flag")) return "已强制桌面模式";
        if (m.Contains("library")) return "游戏库没有启动";
        if (m.Contains("steam")) return "Steam 没有启动";
        if (m.Contains("explorer-respawn")) return "桌面没有关掉";
        if (m.Contains("explorer")) return "桌面没有启动";
        if (m.Contains("not-installed")) return "外壳组件没有装好";
        return "没有切换";
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
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(nint h, out uint code);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}

public sealed partial class WallStage
{
    private bool _shellBusy;

    /// <summary>控制中心电源菜单里的模式按钮：桌面模式时是「返回游戏模式」（手柄图标），游戏模式时是「切换到桌面」（显示器图标）。</summary>
    private void CcUpdateModeButton()
    {
        var b = _ccPower.FirstOrDefault(p => p.Id == "mode");
        if (b == null) return;
        bool desktop = ShellMode.ExplorerRunning();
        string label = desktop ? "返回游戏模式" : "切换到桌面";
        string glyph = desktop ? "" : "";
        if (b.Label == label && b.Glyph == glyph) return;
        b.Label = label; b.Glyph = glyph;
        CcChromeDesc["mode"] = desktop ? "关闭桌面，只留游戏库" : "打开 Windows 桌面，游戏库留在后台";
        if (b.Size.X > 0) CcPaintButton(b);
    }

    private async Task CcSwitchMode()
    {
        if (_shellBusy) return;
        _shellBusy = true;
        try
        {
            bool toGame = ShellMode.ExplorerRunning();
            Toast(toGame ? "正在返回游戏模式…" : "正在切换到桌面…");
            if (!toGame) (_window as MainWindow)?.BeginDesktopHandoff();
            int code = await ShellMode.Run(toGame ? "to-game" : "to-desktop");
            if (code != 0) Toast(ShellMode.Why());

            CcUpdateModeButton();
        }
        finally { (_window as MainWindow)?.FinishDesktopHandoff(); _shellBusy = false; }
    }

    /// <summary>设置 › 通用 › 开机：游戏模式 / 桌面模式。桌面模式下面才是原来的「全屏体验」。</summary>
    private void BuildStBootMode()
    {
        if (!ShellMode.Available) return;
        if (ShellMode.State == null && !_shellBusy) { _shellBusy = true; _ = ShellMode.Run("status").ContinueWith(_ => _dq.TryEnqueue(() => { _shellBusy = false; if (_stOpen) Repaint(); })); }
        bool game = ShellMode.DefaultGame;
        _stRows.Add(StPick("开机进入", new[] { "游戏模式", "桌面模式" }, game ? 0 : 1, i =>
        {
            if ((i == 0) == ShellMode.DefaultGame || _shellBusy) return;
            _shellBusy = true;
            _ = SetBootMode(i == 0);
        }, null, ShellMode.ForceFlag ? "已强制桌面" : ""));
    }

    private async Task SetBootMode(bool game)
    {
        try
        {
            int code = await ShellMode.Run("set-default", game ? "game" : "desktop");
            Toast(code == 0 ? (game ? "开机进入游戏模式" : "开机进入桌面模式") : ShellMode.Why());
        }
        finally { _shellBusy = false; if (_stOpen) Repaint(); }
    }
}
