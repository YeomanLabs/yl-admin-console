using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace YeomanLabs.AdminConsole.App.Converters;

/// <summary>Null or empty string → Collapsed; anything else → Visible.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
