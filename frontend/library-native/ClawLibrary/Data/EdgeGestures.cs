// 游戏模式下关掉屏幕边缘的触摸手势（从底边上滑 = 开始菜单 / 任务栏，侧边滑 = 通知中心 / 小组件）。
// 用窗口属性 System.EdgeGesture.DisableTouchWhenFullscreen：窗口全屏且在前台时系统不响应边缘手势，只影响打了标记的窗口，
// 桌面模式下撤掉标记。按虚表调 IPropertyStore（AOT 下不能用 ComImport）。
using System.Runtime.InteropServices;

namespace ClawLibrary.Data;

internal static class EdgeGestures
{
    private static readonly Guid IidPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    private static readonly Guid EdgeGestureFmtid = new("32ce38b2-2c9a-41b1-9bc5-b3784394aa44");

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Fmtid; public uint Pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant { [FieldOffset(0)] public ushort Vt; [FieldOffset(8)] public short BoolVal; }

    /// <summary>disable = true：这个窗口全屏时不响应边缘手势；false：恢复系统默认。</summary>
    public static unsafe bool Set(nint hwnd, bool disable)
    {
        if (hwnd == 0) return false;
        int hr = SHGetPropertyStoreForWindow(hwnd, IidPropertyStore, out nint store);
        if (hr != 0 || store == 0) { Log.Warn($"edge gestures: property store 0x{hr:x8}"); return false; }
        try
        {
            var vtbl = *(nint**)store;
            var key = new PropertyKey { Fmtid = EdgeGestureFmtid, Pid = 2 };
            // 恢复默认 = 清掉这个属性（VT_EMPTY）
            var value = disable ? new PropVariant { Vt = 11 /* VT_BOOL */, BoolVal = -1 } : new PropVariant { Vt = 0 };
            hr = ((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)vtbl[6])(store, &key, &value);   // SetValue
            if (hr == 0) hr = ((delegate* unmanaged[Stdcall]<nint, int>)vtbl[7])(store);   // Commit
            if (hr != 0) Log.Warn($"edge gestures: set 0x{hr:x8}");
            return hr == 0;
        }
        finally { ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)store)[2])(store); }
    }

    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(nint hwnd, in Guid iid, out nint store);
}
