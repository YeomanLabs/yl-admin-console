using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AdminConsole.App.Converters;

/// <summary>
/// Turns a tree depth into a left margin, so the OU tree can be drawn as a flat list (M12.3).
/// </summary>
/// <remarks>
/// A flat indented list beats a real <c>TreeView</c> here: every OU is already loaded, the list has to stay
/// keyboard-navigable in one sequence, and a TreeView's expanders would hide containers an admin is looking for.
/// </remarks>
public sealed class DepthToMarginConverter : IValueConverter
{
    /// <summary>Pixels per level. One step, not two, because AD hierarchies get deep and the panel is narrow.</summary>
    public double Step { get; set; } = 14;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is int depth ? depth * Step : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
