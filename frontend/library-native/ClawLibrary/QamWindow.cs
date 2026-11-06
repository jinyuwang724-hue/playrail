// 快捷面板自己的窗口：启动时就建好、一直藏着。唤出时只把它显示出来（不激活、不进 Alt+Tab、置顶），
// 不碰游戏库主窗口 —— 游戏一直在前台、照常渲染，面板外面露出的就是游戏本身（窗口背景全透明）。
// 窗口只盖屏幕左边面板那一条；面板的进出场是合成层上的弹簧，不在 UI 线程逐帧算。
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace ClawLibrary;

internal sealed class QamWindow
{
    public readonly Window Win;
    public readonly nint Hwnd;
    private readonly Grid _root;
    private readonly SubclassProc _proc;
    public bool Shown { get; private set; }

    public QamWindow()
    {
        TransparentBackdrop.EnsureSystemQueue();
        Win = new Window { Title = "Claw 快捷面板" };
        _root = new Grid { Background = null, IsHitTestVisible = true };
        Win.Content = _root;
        Win.SystemBackdrop = new TransparentBackdrop();
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        Win.AppWindow.SetPresenter(presenter);
        Win.AppWindow.IsShownInSwitchers = false;
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Win);
        // 纯弹出窗口：去掉标题栏 / 可调边框（否则右边留一条 1 px 白边）
        long st = GetWindowLongPtr(Hwnd, -16).ToInt64();
        SetWindowLongPtr(Hwnd, -16, (nint)((st & ~0x00CF0000L) | 0x80000000L));
        long ex = GetWindowLongPtr(Hwnd, -20).ToInt64();
        SetWindowLongPtr(Hwnd, -20, (nint)((ex & ~0x300L) | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x8 /* TOPMOST */));
        // 窗口圆角 / 边框都不要
        int square = 1, noBorder = unchecked((int)0xfffffffe), off = 1;
        DwmSetWindowAttribute(Hwnd, 33, ref square, 4);
        DwmSetWindowAttribute(Hwnd, 34, ref noBorder, 4);
        DwmSetWindowAttribute(Hwnd, 3 /* TRANSITIONS_FORCEDISABLED */, ref off, 4);
        // 整个客户区都当「玻璃」交给 DWM 按 alpha 合成：没画到的地方透明（露出底下的游戏），不是黑底
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(Hwnd, ref margins);
        _proc = Handle;
        SetWindowSubclass(Hwnd, _proc, 83, 0);
    }

    private nint Handle(nint hwnd, uint msg, nuint wp, nint lp, nuint id, nuint data)
    {
        if (msg == 0x0083) return 0;   // WM_NCCALCSIZE：整个窗口都是客户区，没有边框
        if (msg == 0x21) return 3;      // WM_MOUSEACTIVATE → MA_NOACTIVATE：点面板不抢游戏的前台
        if (msg == 0x0245) return 3;    // WM_POINTERACTIVATE → PA_NOACTIVATE
        return DefSubclassProc(hwnd, msg, wp, lp);
    }

    public void SetContent(Visual v) => ElementCompositionPreview.SetElementChildVisual(_root, v);

    /// <summary>按物理像素摆到 (x, y, w, h)，置顶、不激活。</summary>
    public void Place(int x, int y, int w, int h) => SetWindowPos(Hwnd, -1, x, y, w, h, 0x0010 /* NOACTIVATE */ | 0x0200 /* NOOWNERZORDER */ | 0x0020 /* FRAMECHANGED */);

    public void Show()
    {
        ShowWindow(Hwnd, 4);   // SW_SHOWNOACTIVATE
        SetWindowPos(Hwnd, -1, 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x0200 | 0x0040);
        Shown = true;
    }

    public void Hide() { ShowWindow(Hwnd, 0); Shown = false; }

    /// <summary>截屏时把面板排除在外（截的是底下的游戏）。</summary>
    public void ExcludeFromCapture(bool on) => SetWindowDisplayAffinity(Hwnd, on ? 0x11u : 0u);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wp, nint lp, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins m);
}

/// <summary>全透明的窗口底：系统背景换成透明色刷（WinUIEx TransparentTintBackdrop 同样做法）。</summary>
internal sealed partial class TransparentBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? _compositor;

    protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _compositor ??= new Windows.UI.Composition.Compositor();
        connectedTarget.SystemBackdrop = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
    }

    /// <summary>系统合成器要求当前线程有 Windows.System 的 DispatcherQueue。</summary>
    public static void EnsureSystemQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;
        var opts = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2 /* CURRENT */, ApartmentType = 2 /* STA */ };
        CreateDispatcherQueueController(opts, out _);
    }

    [StructLayout(LayoutKind.Sequential)] private struct DispatcherQueueOptions { public int Size, ThreadType, ApartmentType; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);
}
