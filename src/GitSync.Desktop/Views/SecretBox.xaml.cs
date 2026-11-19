using System.Windows;
using System.Windows.Controls;

namespace GitSync.Desktop.Views;

/// <summary>
/// A key/token field that hides its value behind dots, with an eye button to reveal it.
/// The masked box and the revealed text box stay in sync and both drive <see cref="Secret"/>.
/// The value starts hidden and the reveal only affects this one PC's screen.
/// </summary>
public partial class SecretBox : UserControl
{
    private bool _syncing;

    public SecretBox() => InitializeComponent();

    public static readonly DependencyProperty SecretProperty = DependencyProperty.Register(
        nameof(Secret), typeof(string), typeof(SecretBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSecretChanged));

    public string Secret
    {
        get => (string)GetValue(SecretProperty);
        set => SetValue(SecretProperty, value);
    }

    private static void OnSecretChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (SecretBox)d;
        if (self._syncing) return;
        var value = e.NewValue as string ?? "";
        self._syncing = true;
        if (self.Masked.Password != value) self.Masked.Password = value;
        if (self.Plain.Text != value) self.Plain.Text = value;
        self._syncing = false;
    }

    private void OnMaskedChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Plain.Text = Masked.Password;
        Secret = Masked.Password;
        _syncing = false;
    }

    private void OnPlainChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Masked.Password = Plain.Text;
        Secret = Plain.Text;
        _syncing = false;
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        var reveal = Plain.Visibility != Visibility.Visible;
        Plain.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        Masked.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        Eye.Text = reveal ? "" : "";   // eye-off / eye
        if (reveal) { Plain.Focus(); Plain.CaretIndex = Plain.Text.Length; }
        else Masked.Focus();
    }
}
