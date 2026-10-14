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
            _ = Task.Run(Stage.TextKit.Warm);
            _window = new MainWindow();
            _window.Activate();
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            try { Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += (_, _) => dq.TryEnqueue(() => { Log.Info("re-activated"); _window?.Activate(); }); } catch { }
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

    public MainWindow()
    {
        Title = "Claw 游戏库";
        _root = new RootElement { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 9, 9, 11)) };
        Content = _root;
        bool windowed = Program.Args.Contains("--windowed");
        try
        {
            var appWindow = AppWindow;
            appWindow.SetIcon("Assets\\Square44x44Logo.png");
            if (windowed)
            {
                appWindow.Resize(new Windows.Graphics.SizeInt32(1920, 1200));
            }
            else
            {
                appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            if (appWindow.TitleBar is { } tb) { tb.BackgroundColor = Colors.Black; tb.ButtonBackgroundColor = Colors.Black; }
        }
        catch (Exception ex) { Log.Warn($"window presenter: {ex.Message}"); }

        _root.Loaded += (_, _) =>
        {
            try
            {
                _stage = new WallStage(_root, this);
                _stage.Start();
            }
            catch (Exception ex) { Log.Error("stage start", ex); }
        };
        Closed += (_, _) => { _stage?.Dispose(); Log.Info("closed"); };
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
