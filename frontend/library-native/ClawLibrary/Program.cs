// 入口：不用 XAML 生成的 Main，自己起 WinUI 应用。
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClawLibrary;

public static class Program
{
    public static string[] Args = Array.Empty<string>();

    [STAThread]
    private static int Main(string[] args)
    {
        Args = args;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("unobserved task", e.Exception); e.SetObserved(); };
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // 单实例：已经开着就把这次启动转给它（它会把窗口提到前台），自己退出
        try
        {
            var main = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("main");
            if (!main.IsCurrent)
            {
                var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
                Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait(3000);
                return 0;
            }
        }
        catch { }
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
