using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DupClean.UI.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object Convert(object value, Type t, object param, CultureInfo c)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object param, CultureInfo c)
        => value is Visibility.Visible;
}

[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToCollapsedConverter : IValueConverter
{
    public static readonly NullToCollapsedConverter Instance = new();

    public object Convert(object value, Type t, object param, CultureInfo c)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object param, CultureInfo c)
        => Binding.DoNothing;
}

[ValueConversion(typeof(long), typeof(string))]
public sealed class BytesConverter : IValueConverter
{
    public static readonly BytesConverter Instance = new();

    public object Convert(object value, Type t, object param, CultureInfo c)
    {
        if (value is not long bytes) return string.Empty;
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
            >= 1024L * 1024        => $"{bytes / (1024.0 * 1024):F1} MB",
            >= 1024L               => $"{bytes / 1024.0:F1} KB",
            _                      => $"{bytes} B"
        };
    }

    public object ConvertBack(object value, Type t, object param, CultureInfo c)
        => Binding.DoNothing;
}
