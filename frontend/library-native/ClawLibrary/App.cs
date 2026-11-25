// 应用与窗口：全屏无边框，一个可获得焦点的根元素收键盘 / 鼠标，墙面挂在它的合成视觉层上。
using System.Diagnostics;
using ClawLibrary.Data;
using ClawLibrary.Stage;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClawLibrary;

public sealed partial class App : Application, Microsoft.UI.Xaml.Markup.IXamlMetadataProvider
{
    // 纯代码的 WinUI 应用没有生成的类型信息：把 XAML 元数据转给 WinUI 控件库自己的提供者（加载控件样式要用）
    private readonly Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider _controlsMeta = new();
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(Type type) => _controlsMeta.GetXamlType(type);
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(string fullName) => _controlsMeta.GetXamlType(fullName);
    public Microsoft.UI.Xaml.Markup.XmlnsDefinition[] GetXmlnsDefinitions() => _controlsMeta.GetXmlnsDefinitions();

    public static string CacheDir { get; private set; } = "";
    public static bool Packaged { get; private set; }
    private Window? _window;

    private static void LoadControlResources()
    {
        // WinUI 控件（搜索框）的默认样式；只能在应用启动时挂上
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { Current.Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources()); }
        catch (Exception ex) { StartupNote = $"XamlControlsResources: 0x{ex.HResult:X8} {ex}"; }
        StartupNote ??= $"controls resources {sw.ElapsedMilliseconds}ms";
    }

    public static string? StartupNote;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        LoadControlResources();
        try
        {
            Packaged = DetectPackaged();
            CacheDir = Packaged
                ? Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawLibrary");
            if (!Packaged && Program.Args.FirstOrDefault(a => a.StartsWith("--cache-dir=")) is { } cacheArg)
                CacheDir = Path.GetFullPath(cacheArg["--cache-dir=".Length..]);
            Log.Init(Path.Combine(CacheDir, "logs"));
            // 打包版不能带命令行参数：测试脚本把一次性的参数写在缓存目录的 args.txt 里（读完即删）
            var argsFile = Path.Combine(CacheDir, "args.txt");
            if (File.Exists(argsFile))
            {
                var extra = File.ReadAllText(argsFile).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                Program.Args = Program.Args.Concat(extra).ToArray();
                File.Delete(argsFile);
            }
            if (StartupNote != null) Log.Info(StartupNote);
            Log.Info($"start pid={Environment.ProcessId} packaged={Packaged} args=[{string.Join(' ', Program.Args)}] since-process-start={SinceProcessStartMs():F0}ms");
            Stage.I18n.Init(Stage.UiPrefs.Load().Lang);
            _ = Task.Run(Stage.TextKit.Warm);
            var initial = ActivationRoute(Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs().Data);
            initial.Route = initial.Route.Replace(ViaShell, "");
            _window = new MainWindow(initial.Route, initial.ReturnWindow);
            _window.Activate();
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += (_, activation) =>
            {
                // Copy the WinRT activation payload on the callback thread; only a managed route crosses into the UI queue.
                (string Route, nint ReturnWindow) route;
                try { route = ActivationRoute(activation.Data); }
                catch (Exception ex) { Log.Error("read activation", ex); route = ("activate", 0); }
                if (!dq.TryEnqueue(() =>
                {
                    try
                    {
                        Log.Info($"re-activated: {route}");
                        (_window as MainWindow)?.ActivateRoute(route.Route, route.ReturnWindow);
                    }
                    catch (Exception ex) { Log.Error("route activation", ex); }
                })) Log.Warn("activation queue unavailable");
            };
        }
        catch (Exception ex)
        {
            Log.Error("launch", ex);
            throw;
        }
    }

    public static double SinceProcessStartMs()
    {
        try { return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds; }
        catch { return -1; }
    }

    /// <summary>这次激活是游戏库自己请外壳转的（见 MainWindow.ActivateRoute），不要再转一次。</summary>
    public const string ViaShell = "|shell";

    private static (string Route, nint ReturnWindow) ActivationRoute(object data)
    {
        if (data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol && protocol.Uri.Scheme == "claw-library")
            return ((ToolboxActivation.IsRoute(protocol.Uri.Host) ? protocol.Uri.Host : "activate") + (protocol.Uri.Query.Contains("via=shell") ? ViaShell : ""),
                ToolboxActivation.ReturnWindow(protocol.Uri.Query));
        return ("activate", 0);
    }

    private static bool DetectPackaged()
    {
        try { return Windows.ApplicationModel.Package.Current?.Id != null; }
        catch { return false; }
    }
}

public sealed partial class MainWindow : Window
{
    private readonly RootElement _root;
    private WallStage? _stage;
    private string _pendingRoute;
    private LibraryTray? _tray;
    private bool _exiting;
    private int _routeRevision;

    public MainWindow(string route = "activate", nint returnWindow = 0)
    {
        _pendingRoute = route;
        Title = "Claw 游戏库";
        _root = new RootElement { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 9, 9, 11)) };
        Content = _root;
        bool windowed = Program.Args.Contains("--windowed");
        try
        {
            var appWindow = AppWindow;
            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ClawIcon.ico"));
            if (windowed)
            {
                appWindow.Resize(new Windows.Graphics.SizeInt32(1920, 1200));
            }
            else
            {
                var presenter = OverlappedPresenter.Create();
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                appWindow.SetPresenter(presenter);
            }
            if (appWindow.TitleBar is { } tb) { tb.BackgroundColor = Colors.Black; tb.ButtonBackgroundColor = Colors.Black; }
        }
        catch (Exception ex) { Log.Warn($"window presenter: {ex.Message}"); }

        InitializeShortcutWindow(returnWindow);
        _shortcutWindow?.EnsureFullscreenBounds();

        try
        {
            _tray = new LibraryTray(WinRT.Interop.WindowNative.GetWindowHandle(this),
                () => ActivateRoute("restore-library"), ExitLibrary);
            _tray.EnsureIcon();
        }
        catch (Exception ex) { Log.Warn($"tray init: {ex.Message}"); }
        AppWindow.Closing += (_, e) =>
        {
            if (!_exiting && UiPrefs.Load().BackgroundRun && _tray?.EnsureIcon() == true)
            {
                e.Cancel = true;
                RunInBackground();
            }
        };

        _root.Loaded += (_, _) =>
        {
            try
            {
                _stage = new WallStage(_root, this);
                _stage.StartupShieldRequested = _pendingRoute == "activate" && !Program.Args.Any(a => a.StartsWith("--state="));   // 录屏 / 测试状态：窗口不在前台，盖板会一直等
                _stage.Start();
                _shortcutWindow?.EnsureFullscreenBounds();
                if (_pendingRoute != "activate") ActivateRoute(_pendingRoute, returnWindow);
                else _stage.ActivateShortcut("library");
            }
            catch (Exception ex) { Log.Error("stage start", ex); }
        };
        Closed += (_, _) => { DisposeShortcutWindow(); _tray?.Dispose(); _stage?.Dispose(); Log.Info("closed"); };
    }

    public void ExitLibrary() { _exiting = true; Close(); }

    public void RunInBackground()
    {
        CancelShortcutTransition();
        _shortcutMotion.Cancel();
        _stage?.EndShortcutSoftFocus();
        CompleteShortcutHide();
    }

    public void ActivateRoute(string route, nint returnWindow = 0) => _ = ActivateOverlayRoute(route, returnWindow);

    private void ActivateRouteCore(string route, nint returnWindow = 0)
    {
        ++_routeRevision;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        bool viaShell = route.EndsWith(App.ViaShell, StringComparison.Ordinal);
        if (viaShell) route = route[..^App.ViaShell.Length];
        // Xbox 全屏体验里游戏在前台时，外壳把游戏库窗口隐藏了（DWM cloaked = shell）。我们自己抢前台，外壳有时不把它显示出来，
        // 所以请外壳来切：用自己的 claw-library:// 走一次系统激活，转回来的那次（via=shell）再照常显示
        if (!viaShell && !route.StartsWith("minimize-", StringComparison.Ordinal) && ToolboxWindow.XboxFseActive() == true && ToolboxWindow.ShellCloaked(hwnd))
        {
            Log.Info($"library cloaked by shell; asking the shell to switch ({route})");
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri($"claw-library://{route}?via=shell&returnWindow={returnWindow}"));
            return;
        }
        if (viaShell) Log.Info($"shell switch arrived ({route}) cloaked={ToolboxWindow.ShellCloaked(hwnd)}");

        if (AppWindow.IsVisible && !ToolboxWindow.IsIconic(hwnd) && route.StartsWith("minimize-", StringComparison.Ordinal) &&
            ToolboxActivation.ShouldMinimize(route, ToolboxWindow.XboxFseActive(), ToolboxWindow.SteamBigPictureOpen()))
        {
            // 游戏在跑、正在游戏画面开着：Claw 键 = 继续游戏。游戏库留在游戏后面不收进托盘，
            // 否则先淡出露出墙面、再等 Magpie / 小黄鸭重新缩放，中间一段黑屏
            if (_stage?.NowPlayingResume() == true) return;
            HideShortcutWindow();
            return;
        }
        _shortcutWindow?.RememberReturnWindow(returnWindow);
        _pendingRoute = ToolboxActivation.Target(route);
        // 游戏在跑：窗口出来之前先把「正在游戏」画面放上，进场动画带的就是它，不会先闪一下墙面
        if (_pendingRoute is "activate" or "library") _stage?.NowPlayingBeforeShow();
        else _stage?.NowPlayingYield();   // Claw 键唤控制中心：正在游戏画面让开，不挡控制中心也不抢它的按键
        ShowShortcutWindow(route);
    }

    private void RestoreShortcutContent(string route)
    {
        if (!AppWindow.IsVisible) AppWindow.Show(false);
        _stage?.ResumeFromTray();
        ToolboxWindow.Restore(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (!ToolboxActivation.PreservePage(route)) _stage?.ActivateShortcut(_pendingRoute);
        _root.Focus(FocusState.Programmatic);
    }
}

/// <summary>可获得焦点的根元素（Panel 不能拿焦点，用 ContentControl）。</summary>
public sealed partial class RootElement : ContentControl
{
    public RootElement()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        // 两层：Surface 上挂合成视觉层（墙面），Overlay 在它上面放少量 XAML 控件（搜索框，为了中文输入法）
        Surface = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        Overlay = new Canvas();
        var host = new Grid();
        host.Children.Add(Surface);
        host.Children.Add(Overlay);
        Content = host;
    }

    public Grid Surface { get; }
    public Canvas Overlay { get; }
}
