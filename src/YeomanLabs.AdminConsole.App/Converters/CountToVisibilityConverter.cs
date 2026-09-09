using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace YeomanLabs.AdminConsole.App.Converters;

/// <summary>
/// Count → Visibility. Default: visible when the count is zero (empty-state captions). Set <see cref="WhenNonZero"/>
/// for the opposite (sections that only appear once they have rows).
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool WhenNonZero { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch { int i => i, long l => (int)l, null => 0, _ => System.Convert.ToInt32(value, culture) };
        var show = WhenNonZero ? count > 0 : count == 0;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
