using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using AdminConsole.Core;

namespace AdminConsole.App.Services;

/// <summary>
/// Theme and density (UX §12 Appearance). Density is a single row-height resource that list templates bind to, which
/// is enough for the one thing an admin wants from it: more rows on screen.
/// </summary>
public static class AppearanceService
{
    public const string RowHeightKey = "AcRowHeight";
    public const string RowPaddingKey = "AcRowPadding";

    public const string TextPrimaryKey = "AcTextPrimary";
    public const string TextSecondaryKey = "AcTextSecondary";

    // Measured against the page each theme actually paints, because the library's own text brushes did not hold up:
    // in 3.0.5 TextFillColorSecondaryBrush renders around #868686 and primary came out #999999 inside a button, so
    // every caption in the app sat near the 4.5:1 floor and looked washed out. These clear it comfortably.
    private static readonly Color DarkPrimary = Color.FromRgb(0xFF, 0xFF, 0xFF);    // 16.5:1 on #1B1B1B
    private static readonly Color DarkSecondary = Color.FromRgb(0xC8, 0xC8, 0xC8);  //  9.9:1
    private static readonly Color LightPrimary = Color.FromRgb(0x1A, 0x1A, 0x1A);   // 16.9:1 on white
    private static readonly Color LightSecondary = Color.FromRgb(0x4A, 0x4A, 0x4A); //  8.6:1

    public static void Apply(AppTheme theme)
    {
        // UX §1 keeps Mica off; the backdrop stays None whichever theme is chosen.
        var applied = theme switch
        {
            AppTheme.Light => ApplicationTheme.Light,
            AppTheme.Dark => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
        };

        ApplicationThemeManager.Apply(applied, updateAccent: false);

        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var light = applied == ApplicationTheme.Light;
        resources[TextPrimaryKey] = new SolidColorBrush(light ? LightPrimary : DarkPrimary);
        resources[TextSecondaryKey] = new SolidColorBrush(light ? LightSecondary : DarkSecondary);
    }

    public static void Apply(AppDensity density)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        resources[RowHeightKey] = density == AppDensity.Compact ? 26d : 32d;
        resources[RowPaddingKey] = density == AppDensity.Compact ? new Thickness(8, 2, 8, 2) : new Thickness(8, 6, 8, 6);
    }

    public const string ChromeKey = "AcChrome";
    public const string RailKey = "AcRailBackground";
    public const string OnChromeKey = "AcOnChrome";
    public const string AccentKey = "AcAccent";
    public const string AccentDimKey = "AcAccentDim";

    /// <summary>
    /// Paints this installation's branding (M11.3). The chrome and accent come from <c>branding.json</c>; everything
    /// derived from them is computed here rather than configured, so a client cannot pick a combination that cannot
    /// be read.
    /// </summary>
    public static void Apply(Branding branding)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var b = branding.Sanitised();
        var chrome = Parse(b.ChromeColor);
        var accent = Parse(b.AccentColor);

        // The rail sits just above the chrome so the two read as one surface with an edge between them.
        var rail = Shift(chrome, IsDark(chrome) ? 0.06 : -0.06);

        // Text on the chrome is chosen from the chrome, not configured. This is the M8.4 bug made impossible: a light
        // foreground was hardcoded onto a navy panel, so selecting the light theme painted dark text on dark blue.
        var onChrome = IsDark(chrome) ? Color.FromRgb(0xF2, 0xF5, 0xFA) : Color.FromRgb(0x10, 0x14, 0x1A);

        resources[ChromeKey] = new SolidColorBrush(chrome);
        resources[RailKey] = new SolidColorBrush(rail);
        resources[OnChromeKey] = new SolidColorBrush(onChrome);
        resources[AccentKey] = new SolidColorBrush(accent);
        resources[AccentDimKey] = new SolidColorBrush(Color.FromArgb(0xB3, accent.R, accent.G, accent.B));

        ApplicationAccentColorManager.Apply(accent, ApplicationThemeManager.GetAppTheme(), false);
    }

    private static Color Parse(string hex)
    {
        var value = (Color?)ColorConverter.ConvertFromString(hex);
        return value ?? Color.FromRgb(0x1F, 0x27, 0x33);
    }

    /// <summary>WCAG relative luminance, the same measure the contrast pass used.</summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static bool IsDark(Color c) => Luminance(c) < 0.18;

    /// <summary>Lightens or darkens towards the nearer end, for a surface that sits just off another.</summary>
    private static Color Shift(Color c, double amount)
    {
        static byte Move(byte v, double amount) =>
            (byte)Math.Clamp(v + (amount > 0 ? (255 - v) * amount : v * amount), 0, 255);

        return Color.FromRgb(Move(c.R, amount), Move(c.G, amount), Move(c.B, amount));
    }

    /// <summary>Called once at startup so the resources exist before any template asks for them.</summary>
    public static void ApplyAll(UserSettings settings, Branding branding)
    {
        Apply(settings.Theme);
        Apply(settings.Density);
        Apply(branding);
    }
}
