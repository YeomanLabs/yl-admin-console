using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdminConsole.App.Converters;

/// <summary>
/// The frame of a multi-size <c>.ico</c> drawn for the size an image will occupy on screen (v0.3.1). WPF otherwise shows
/// whichever frame it decodes and scales it, so a 16-pixel title-bar icon came out as a blurred 256-pixel drawing. The
/// icon's small sizes are drawn differently from its large ones, and this is what makes them the ones used.
/// </summary>
/// <remarks>The value is the icon's pack path; the parameter is the size in device-independent pixels.</remarks>
public sealed class IconFrameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path) return null;
        var dips = parameter is string p && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 16;
        // The window's DPI once it exists; before that (the title bar binds while the window is built) the system DPI,
        // which is what a visual not yet on any monitor reports.
        var scale = VisualTreeHelper.GetDpi((Visual?)Application.Current?.MainWindow ?? new DrawingVisual()).DpiScaleX;
        var wanted = (int)Math.Round(dips * scale);

        try
        {
            var stream = Application.GetResourceStream(new Uri(path, UriKind.RelativeOrAbsolute))?.Stream;
            if (stream is null) return null;
            using (stream)
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                // The smallest frame at least as big as needed, so it is only ever scaled down; else the biggest there is.
                var frame = decoder.Frames.Where(f => f.PixelWidth >= wanted).OrderBy(f => f.PixelWidth).FirstOrDefault()
                            ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                frame.Freeze();
                return frame;
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or System.IO.FileFormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
