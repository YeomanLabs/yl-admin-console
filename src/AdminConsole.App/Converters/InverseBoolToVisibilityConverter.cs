using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AdminConsole.App.Converters;

/// <summary>
/// true → Collapsed, false → Visible. For the control that should be there when a state is <em>not</em> reached, such
/// as a Cancel button that disappears once a run has finished.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
