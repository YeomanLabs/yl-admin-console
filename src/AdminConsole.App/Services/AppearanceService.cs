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
    public const string RowHeightKey = "YlRowHeight";
    public const string RowPaddingKey = "YlRowPadding";

    public const string TextPrimaryKey = "YlTextPrimary";
    public const string TextSecondaryKey = "YlTextSecondary";

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

    /// <summary>Called once at startup so the resources exist before any template asks for them.</summary>
    public static void ApplyAll(UserSettings settings)
    {
        Apply(settings.Theme);
        Apply(settings.Density);
    }
}
