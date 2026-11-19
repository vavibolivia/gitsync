using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GitSync.Desktop.ViewModels;

namespace GitSync.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        SourceInitialized += (_, _) => ApplyWindowEffects();
        StateChanged += (_, _) => OnStateChanged();
        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Contains("--demo")) _vm.LoadDemo();
            else await _vm.InitializeAsync();

            var page = ArgValue(args, "--page");
            if (page is not null) _vm.CurrentPage = page;
            if (ArgValue(args, "--snapshot") is { } file)
            {
                await Task.Delay(1500);
                SaveSnapshot(file);
                Application.Current.Shutdown();
            }
        };
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Renders only this window's own visual tree to a PNG (used for UI checks; never captures the screen).</summary>
    private void SaveSnapshot(string file)
    {
        if (Content is not FrameworkElement root) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        var bg = new System.Windows.Media.DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(file);
        enc.Save(fs);
    }

    public void SetDimmed(bool dimmed) => DimOverlay.Visibility = dimmed ? Visibility.Visible : Visibility.Collapsed;

    private void OnStateChanged()
    {
        // A maximized chrome-less window overhangs the screen by the resize border; compensate.
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // Dark window frame with square corners on Windows 11.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyWindowEffects()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 1, border = 0x00FFFFFF; // square corners (DWMWCP_DONOTROUND), white frame
        try
        {
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));  // DWMWA_WINDOW_CORNER_PREFERENCE
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
        }
        catch { /* older Windows */ }
    }
}
