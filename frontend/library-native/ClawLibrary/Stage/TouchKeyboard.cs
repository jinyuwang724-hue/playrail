// 屏幕键盘：输入框拿到焦点时把触摸键盘叫出来，打完收回去。
// 先走系统的 InputPane（InputPaneInterop.TryShow）；掌机的手柄会被 Windows 当成接了硬件键盘，这时 TryShow 常常不出，
// 就退回老办法：TabTip 的 ITipInvocation.Toggle（COM，按虚表直接调，AOT 下不能用 ComImport）。
// 键盘到底出没出来看 InputPane.OccludedRect（挡住窗口的那块）。
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClawLibrary.Stage;

internal static class TouchKeyboard
{
    private static readonly Guid TipClsid = new("4ce576fa-83dc-4f88-951c-9d0782b4e376");
    private static readonly Guid TipIid = new("37c994e7-432b-4834-a2f7-dce1f13b834b");

    public static bool TryShow(nint hwnd)
    {
        try { return Windows.UI.ViewManagement.InputPaneInterop.GetForWindow(hwnd)?.TryShow() == true; }
        catch (Exception ex) { Log.Warn($"touch keyboard: {ex.Message}"); return false; }
    }

    public static void Hide(nint hwnd)
    {
        bool hidden = false;
        try { hidden = Windows.UI.ViewManagement.InputPaneInterop.GetForWindow(hwnd)?.TryHide() == true; }
        catch (Exception ex) { Log.Warn($"touch keyboard hide: {ex.Message}"); }
        if (!hidden && Visible(hwnd)) Toggle();
    }

    /// <summary>
    /// 键盘挡住了窗口多少：实测 InputPane.Visible 在这台机器上一直是 false，OccludedRect 才准（出来时高 400 左右）。
    /// TextInputHost 的窗口一直是隐身状态、标题还跟系统语言走，看窗口判断不了。
    /// </summary>
    public static bool Visible(nint hwnd)
    {
        try
        {
            var p = Windows.UI.ViewManagement.InputPaneInterop.GetForWindow(hwnd);
            return p != null && (p.Visible || p.OccludedRect.Height > 0);
        }
        catch { return false; }
    }

    /// <summary>TabTip 的开关：开着就关、关着就开，所以只在确认没出来时调。TabTip 没在跑先把它拉起来。</summary>
    public static unsafe bool Toggle()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int hr = CoCreateInstance(TipClsid, 0, 0x4 /* CLSCTX_LOCAL_SERVER */, TipIid, out nint tip);
            if (hr == 0 && tip != 0)
            {
                try
                {
                    var vtbl = *(nint**)tip;
                    hr = ((delegate* unmanaged[Stdcall]<nint, nint, int>)vtbl[3])(tip, GetDesktopWindow());
                    if (hr != 0) Log.Warn($"touch keyboard toggle: 0x{hr:x8}");
                    return hr == 0;
                }
                finally { ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)tip)[2])(tip); }
            }
            if (attempt == 0) StartTabTip();
            else Log.Warn($"touch keyboard: TabTip not available (0x{hr:x8})");
        }
        return false;
    }

    private static void StartTabTip()
    {
        try
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "microsoft shared", "ink", "TabTip.exe");
            if (!File.Exists(exe)) return;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
            Thread.Sleep(400);
        }
        catch (Exception ex) { Log.Warn($"start TabTip: {ex.Message}"); }
    }

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint obj);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
}
