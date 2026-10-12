using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ClawScaling;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WindowClass { public uint Style; public WndProc Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; }
    [StructLayout(LayoutKind.Sequential)] struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    public delegate nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam);
    delegate bool EnumProc(nint hwnd, nint data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out Rect rect, int size);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClass(ref WindowClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern bool PeekMessage(out Message message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] static extern uint MsgWaitForMultipleObjectsEx(uint count, nint[] handles, uint timeout, uint mask, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll")] public static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool AdjustWindowRectEx(ref Rect rect, uint style, bool menu, uint exStyle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint device);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICaptureInterop { nint CreateForWindow(nint hwnd, in Guid iid); nint CreateForMonitor(nint monitor, in Guid iid); }
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDxgiAccess { nint GetInterface(in Guid iid); }
    public static GraphicsCaptureItem CaptureItem(nint hwnd)
    {
        nint pointer = GraphicsCaptureItem.As<ICaptureInterop>().CreateForWindow(hwnd, new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"));
        try { return GraphicsCaptureItem.FromAbi(pointer); } finally { Marshal.Release(pointer); }
    }
    public static IDirect3DDevice CaptureDevice(Vortice.DXGI.IDXGIDevice device)
    {
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(device.NativePointer, out nint pointer));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(pointer); } finally { Marshal.Release(pointer); }
    }
    public static Vortice.Direct3D11.ID3D11Texture2D Texture(IDirect3DSurface surface) =>
        new(surface.As<IDxgiAccess>().GetInterface(new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C")));

    public static Rect Bounds(nint hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, 9, out Rect rect, Marshal.SizeOf<Rect>()) != 0) GetWindowRect(hwnd, out rect);
        return rect;
    }
    public static Rect Monitor(nint hwnd)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) throw new InvalidOperationException("无法读取目标屏幕");
        return info.Monitor;
    }
    public record Target(long Hwnd, uint Pid, string Process, string Title, int Width, int Height, bool Foreground);
    static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) {
        "steam", "steamwebhelper", "GameOverlayUI", "explorer", "ApplicationFrameHost", "msedge", "chrome", "Codex", "ClawScaling",
        "GamingMode", "Playhub", "XboxGamingBar", "MotionAssistant", "Clash Verge", "clash-verge", "StartMenuExperienceHost", "SearchHost",
        "ChatGPT", "ClickToDo", "TextInputHost", "SystemSettings", "ShellExperienceHost", "LockApp", "SnippingTool", "SecurityHealthSystray"
    };
    public static Target[] Targets()
    {
        var results = new List<Target>();
        nint foreground = GetForegroundWindow();
        EnumWindows((hwnd, _) => {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || !GetClientRect(hwnd, out Rect client) || client.Width < 200 || client.Height < 120) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            try {
                using var process = Process.GetProcessById((int)pid);
                if (Excluded.Contains(process.ProcessName) || pid == Environment.ProcessId) return true;
                var title = new StringBuilder(1024); GetWindowText(hwnd, title, title.Capacity);
                if (title.Length == 0) return true;
                results.Add(new(hwnd.ToInt64(), pid, process.ProcessName, title.ToString(), client.Width, client.Height, hwnd == foreground));
            } catch (ArgumentException) { } catch (System.ComponentModel.Win32Exception) { }
            return true;
        }, 0);
        return results.OrderByDescending(x => x.Foreground).ThenByDescending(x => x.Width * (long)x.Height).Take(40).ToArray();
    }
    static readonly Dictionary<nint, WndProc> WindowHandlers = [];
    static readonly WndProc GenericProc = DispatchWindow;
    static bool registered;
    static nint DispatchWindow(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        nint result = WindowHandlers.TryGetValue(hwnd, out var handler) ? handler(hwnd, message, wParam, lParam) : DefWindowProc(hwnd, message, wParam, lParam);
        if (message == 0x82) WindowHandlers.Remove(hwnd);
        return result;
    }
    static nint CreateOwnWindow(WndProc proc, string title, uint style, uint exStyle, int width, int height)
    {
        var wc = new WindowClass { Proc = GenericProc, Instance = GetModuleHandle(null), ClassName = "ClawToolboxScalingOutput" };
        if (!registered) { if (RegisterClass(ref wc) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); registered = true; }
        nint result = CreateWindowEx(exStyle, wc.ClassName, title, style, 100, 100, width, height, 0, 0, wc.Instance, 0);
        if (result == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        WindowHandlers[result] = proc;
        return result;
    }
    public static nint OutputWindow(WndProc proc)
    {
        nint result = CreateOwnWindow(proc, "Claw 画面缩放", 0x80000000, 0x08000080, 32, 32);
        SetWindowDisplayAffinity(result, 0x11);
        return result;
    }
    public static nint TestWindow(WndProc proc, int width, int height)
    {
        var rect = new Rect { Right = width, Bottom = height }; AdjustWindowRectEx(ref rect, 0x00cf0000, false, 0x08000080);
        return CreateOwnWindow(proc, "Claw Scaling Capture Test", 0x10cf0000, 0x08000080, rect.Width, rect.Height);
    }
    public static void Pump()
    {
        while (PeekMessage(out Message message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
    }
    public static void Wait(AutoResetEvent command, AutoResetEvent? frame, uint milliseconds)
    {
        nint[] handles = frame == null ? [command.SafeWaitHandle.DangerousGetHandle()] : [command.SafeWaitHandle.DangerousGetHandle(), frame.SafeWaitHandle.DangerousGetHandle()];
        MsgWaitForMultipleObjectsEx((uint)handles.Length, handles, milliseconds, 0x04ff, 0x0004);
    }
}
