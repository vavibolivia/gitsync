using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GitSync.Desktop.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object? p, CultureInfo c)
    {
        var b = value is true;
        if (p as string == "Invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var visible = value is not null && !(value is string s && s.Length == 0);
        if (p as string == "Invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        string.Equals(value?.ToString(), p?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EqualsToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        string.Equals(value?.ToString(), p?.ToString(), StringComparison.Ordinal);
    public object ConvertBack(object value, Type t, object? p, CultureInfo c) =>
        value is true ? p! : Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var n = value is int i ? i : 0;
        var visible = n > 0;
        if (p as string == "Invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Maps RepoStatus / BranchStatus / log level names to theme brushes. ConverterParameter "Soft" returns a translucent tint.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> SoftCache = new();

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = value?.ToString() switch
        {
            "Synced" or "Success" => "SuccessBrush",
            "OutOfSync" or "Differs" or "Warning" => "WarningBrush",
            "GitHubAhead" => "GitHubAccentBrush",
            "GitLabAhead" => "GitLabBrush",
            "Diverged" or "Error" => "DangerBrush",
            "GitHubOnly" or "GitLabOnly" or "Info" => "InfoBrush",
            "Command" => "TextMuteBrush",
            _ => "TextDimBrush",
        };
        var brush = (Brush)Application.Current.FindResource(key);
        if (p as string != "Soft") return brush;
        if (SoftCache.TryGetValue(key, out var soft)) return soft;
        var col = ((SolidColorBrush)brush).Color;
        soft = new SolidColorBrush(Color.FromArgb(34, col.R, col.G, col.B));
        soft.Freeze();
        return SoftCache[key] = soft;
    }
    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class StatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value?.ToString() switch
    {
        "Unknown" => "확인 전",
        "Synced" => "동기화됨",
        "OutOfSync" => "차이 있음",
        "GitHubOnly" => "GitHub에만",
        "GitLabOnly" => "GitLab에만",
        "Empty" => "빈 저장소",
        "Error" => "오류",
        "Differs" => "다름 · 분석 필요",
        "GitHubAhead" => "GitHub 앞섬",
        "GitLabAhead" => "GitLab 앞섬",
        "Diverged" => "분기됨",
        _ => value?.ToString() ?? "",
    };
    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class LogIconConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value?.ToString() switch
    {
        "Success" => "",
        "Warning" => "",
        "Error" => "",
        "Command" => "",
        _ => "",
    };
    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
