using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ClawLibrary.Engine;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const string StartupReadyProperty = "ClawToolbox.Library.Ready.v1";
    public bool StartupShieldRequested { get; set; }
    private bool _startupDisposed, _startupSceneReady, _startupRevealing, _startupWasVisible;
    private EventHandler<object>? _startupPresented;
    private ContainerVisual? _startupShield;
    private LoadedImageSurface? _startupMarkSurface;
    private readonly StartupVisibilityGate _startupVisibility = new();

    private void PrepareStartupShield()
    {
        if (!StartupShieldRequested || _prefs.FseSteam) return;
        const string path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";
        if (Registry.GetValue(path, "GamingHomeApp", null) as string != "ClawToolbox.Library_ba3r68zr466za!App" ||
            Registry.GetValue(path, "StartupToGamingHome", 0) is not int enabled || enabled != 1) return;
        var layer = G.C.CreateContainerVisual(); layer.RelativeSizeAdjustment = Vector2.One;
        var black = G.C.CreateSpriteVisual(); black.RelativeSizeAdjustment = Vector2.One;
        black.Brush = G.C.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        layer.Children.InsertAtBottom(black);
        _startupMarkSurface = LoadedImageSurface.StartLoadFromUri(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "StartupPages.png")));
        var brush = G.C.CreateSurfaceBrush(_startupMarkSurface); brush.Stretch = CompositionStretch.Uniform;
        var mark = G.C.CreateSpriteVisual(); mark.RelativeSizeAdjustment = Vector2.One; mark.Brush = brush;
        mark.CenterPoint = new Vector3((float)View.Width / 2, (float)View.Height / 2, 0);
        layer.Children.InsertAtTop(mark);
        var pulse = G.C.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0, .72f); pulse.InsertKeyFrame(.5f, 1); pulse.InsertKeyFrame(1, .72f);
        pulse.Duration = TimeSpan.FromSeconds(2.2); pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        mark.StartAnimation("Opacity", pulse);
        var size = G.C.CreateVector3KeyFrameAnimation();
        size.InsertKeyFrame(0, new Vector3(.98f, .98f, 1)); size.InsertKeyFrame(.5f, new Vector3(1.005f, 1.005f, 1)); size.InsertKeyFrame(1, new Vector3(.98f, .98f, 1));
        size.Duration = TimeSpan.FromSeconds(2.2); size.IterationBehavior = AnimationIterationBehavior.Forever;
        mark.StartAnimation("Scale", size);
        _startupShield = layer; _top.Children.InsertAtTop(layer);
        Log.Info("startup shield prepared inside library");
        CheckStartupShield();
    }
    private void CheckStartupShield()
    {
        if (_startupDisposed || _startupShield == null || _startupRevealing) return;
        bool visible = IsStartupWindowVisible();
        if (visible && !_startupWasVisible) Log.Info("startup shield visible on active user desktop");
        _startupWasVisible = visible;
        var result = _startupVisibility.Observe(NowMs, visible, _startupSceneReady);
        if (result != StartupReveal.Hold) { RevealStartupShield(result.ToString()); return; }
        After(100, CheckStartupShield);
    }
    private bool IsStartupWindowVisible()
    {
        if (!IsWindowVisible(_windowHandle) || IsIconic(_windowHandle) || GetAncestor(GetForegroundWindow(), 2) != _windowHandle) return false;
        if (DwmGetWindowAttribute(_windowHandle, 14, out int cloaked, sizeof(int)) != 0 || cloaked != 0) return false;
        nint input = OpenInputDesktop(0, false, 1);
        if (input == 0) return false;
        try
        {
            string? own = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
            return own != null && DesktopName(input) == own;
        }
        finally { CloseDesktop(input); }
    }
    private static string? DesktopName(nint desktop)
    {
        var name = new StringBuilder(128);
        return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) ? name.ToString() : null;
    }
    private void RevealStartupShield(string reason)
    {
        if (_startupShield == null || _startupRevealing) return;
        _startupRevealing = true;
        var fade = G.C.CreateScalarKeyFrameAnimation(); fade.InsertKeyFrame(0, 1); fade.InsertKeyFrame(1, 0); fade.Duration = TimeSpan.FromMilliseconds(260);
        _startupShield.StartAnimation("Opacity", fade);
        After(300, () =>
        {
            if (_startupShield != null) { _top.Children.Remove(_startupShield); _startupShield.Dispose(); _startupShield = null; }
            _startupMarkSurface?.Dispose(); _startupMarkSurface = null;
            MarkStartupReady();
            Log.Info($"startup shield released: {reason}");
        });
    }
    private void MarkStartupReady()
    {
        if (_startupDisposed) return;
        bool marked = SetProp(_windowHandle, StartupReadyProperty, 1);
        Log.Info($"startup surface ready: marked={marked} pending={Images.Pending} since-process={App.SinceProcessStartMs():F0}ms");
    }
    private void BeginStartupReadiness()
    {
        long began = NowMs;
        void Check()
        {
            if (_startupDisposed) return;
            long elapsed = NowMs - began;
            if (elapsed < 1500 || (Images.Pending > 0 && elapsed < 8000)) { After(100, Check); return; }
            int frames = 0;
            _startupPresented = (_, _) =>
            {
                if (++frames < 2) return;
                CompositionTarget.Rendering -= _startupPresented; _startupPresented = null;
                if (_startupDisposed) return;
                _startupSceneReady = true;
                if (_startupShield == null) MarkStartupReady();
            };
            CompositionTarget.Rendering += _startupPresented;
        }
        Check();
    }
    private void DisposeStartupReadiness()
    {
        _startupDisposed = true;
        if (_startupPresented != null) CompositionTarget.Rendering -= _startupPresented;
        _startupPresented = null;
        if (_startupShield != null) { _top.Children.Remove(_startupShield); _startupShield.Dispose(); _startupShield = null; }
        _startupMarkSurface?.Dispose(); _startupMarkSurface = null;
        RemoveProp(_windowHandle, StartupReadyProperty);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(nint hwnd, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint hwnd, string name);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder data, int length, out int needed);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
