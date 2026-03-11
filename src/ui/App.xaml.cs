using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace KernelFlirt.UI;

public partial class App : Application
{
    private static readonly string CrashLog = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogCrash("AppDomain.UnhandledException", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("DispatcherUnhandledException", e.Exception);
        e.Handled = true;
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n";
            File.AppendAllText(CrashLog, text);
        }
        catch { }
    }
}
