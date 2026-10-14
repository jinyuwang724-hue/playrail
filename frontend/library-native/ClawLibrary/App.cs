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

public sealed class App : Application
{
    public static string CacheDir { get; private set; } = "";
    public static bool Packaged { get; private set; }
    private Window? _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Packaged = DetectPackaged();
            CacheDir = Packaged
                ? Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawLibrary");
            Log.Init(Path.Combine(CacheDir, "logs"));
            Log.Info($"start pid={Environment.ProcessId} packaged={Packaged} args=[{string.Join(' ', Program.Args)}] since-process-start={SinceProcessStartMs():F0}ms");
            _window = new MainWindow();
            _window.Activate();
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

public sealed class MainWindow : Window
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
public sealed class RootElement : ContentControl
{
    public RootElement()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
    }

    public Grid Surface => (Grid)Content;
}
