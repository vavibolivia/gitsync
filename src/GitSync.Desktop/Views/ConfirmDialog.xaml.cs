using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GitSync.Desktop.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText, bool danger, IEnumerable<string>? details)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        var list = details?.ToList() ?? [];
        DetailsList.ItemsSource = list;
        DetailsBox.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (danger)
        {
            ConfirmButton.Style = (Style)FindResource("DangerButton");
            IconGlyph.Text = "";
            IconGlyph.Foreground = (Brush)FindResource("DangerBrush");
            IconBadge.Background = new SolidColorBrush(Color.FromArgb(38, 0xF2, 0x60, 0x6E));
        }
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

public static class Dialogs
{
    public static bool Confirm(string title, string message, string confirmText = "확인", bool danger = false,
        IEnumerable<string>? details = null)
    {
        var owner = Application.Current.MainWindow as MainWindow;
        var dlg = new ConfirmDialog(title, message, confirmText, danger, details) { Owner = owner };
        owner?.SetDimmed(true);
        try { return dlg.ShowDialog() == true; }
        finally { owner?.SetDimmed(false); }
    }
}
