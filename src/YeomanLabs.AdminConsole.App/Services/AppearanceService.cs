using System.Windows;
using Wpf.Ui.Appearance;
using YeomanLabs.AdminConsole.Core;

namespace YeomanLabs.AdminConsole.App.Services;

/// <summary>
/// Theme and density (UX §12 Appearance). Density is a single row-height resource that list templates bind to, which
/// is enough for the one thing an admin wants from it: more rows on screen.
/// </summary>
public static class AppearanceService
{
    public const string RowHeightKey = "YlRowHeight";
    public const string RowPaddingKey = "YlRowPadding";

    public static void Apply(AppTheme theme)
    {
        // UX §1 keeps Mica off; the backdrop stays None whichever theme is chosen.
        ApplicationThemeManager.Apply(theme switch
        {
            AppTheme.Light => ApplicationTheme.Light,
            AppTheme.Dark => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
        }, updateAccent: false);
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
