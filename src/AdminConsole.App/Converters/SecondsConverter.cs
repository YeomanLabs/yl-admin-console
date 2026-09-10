using System.Globalization;
using System.Windows.Data;
using AdminConsole.Core;

namespace AdminConsole.App.Converters;

/// <summary>Seconds → "2 minutes". For the interval dropdowns in Settings, which store a number but must read as time.</summary>
public sealed class SecondsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int seconds ? UserSettings.Describe(seconds) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true → false. For an IsEnabled that has to be the opposite of an is-busy flag.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
