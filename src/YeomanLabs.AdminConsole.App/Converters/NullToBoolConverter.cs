using System.Globalization;
using System.Windows.Data;

namespace YeomanLabs.AdminConsole.App.Converters;

/// <summary>Null or empty string → false; anything else → true. For <c>IsOpen</c>-style bindings driven by an optional caption.</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && !(value is string s && s.Length == 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
