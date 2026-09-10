using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AdminConsole.App.Converters;

/// <summary>Null or empty string → Collapsed; anything else → Visible.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    /// <param name="parameter">
    /// Pass <c>invert</c> to show when the value is absent instead of when it is present. A control and the reason it
    /// is unavailable are opposites of each other, and they read better as one converter than as two.
    /// </param>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value is null || (value is string s && s.Length == 0);
        var invert = parameter is string p && string.Equals(p, "invert", StringComparison.OrdinalIgnoreCase);
        return empty != invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
