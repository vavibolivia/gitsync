using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace GitSync.Desktop;

public partial class App : Application
{
    public static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitSync", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            WriteCrash(ex);
            MessageBox.Show(ex.Message, "GitSync 시작 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
        MessageBox.Show(e.Exception.Message, "GitSync 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteCrash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch { /* ignore */ }
    }
}
