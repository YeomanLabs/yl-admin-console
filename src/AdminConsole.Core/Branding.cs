using System.Text.Json.Serialization;

namespace AdminConsole.Core;

/// <summary>
/// What this installation calls itself and what it looks like (M11.3). Everything here is per-installation, so it
/// lives beside <c>settings.json</c> and <c>tenant.json</c> in <c>%LOCALAPPDATA%</c>.
/// </summary>
/// <remarks>
/// The product ships neutral. A consultancy handing this to a client sets the client's name and colour and the console
/// is theirs; nobody has to be shown somebody else's branding. The defaults below are deliberately no one's: a slate
/// chrome and a blue accent, both measured for contrast rather than chosen by eye.
/// </remarks>
public sealed record Branding
{
    /// <summary>Shown in the title bar, on the sign-in page and in Settings.</summary>
    public string ProductName { get; init; } = "Admin Console";

    /// <summary>One line under the product name on the sign-in page.</summary>
    public string Tagline { get; init; } = "Entra ID and Intune in one search-first console.";

    /// <summary>
    /// The window chrome: title bar, navigation rail, status bar and the quick-facts panels. Dark by design — the
    /// text drawn on it is chosen from its luminance, so a light chrome gets dark text rather than becoming unreadable.
    /// </summary>
    public string ChromeColor { get; init; } = "#1F2733";

    /// <summary>The accent: primary buttons, the active nav item, focus rings and the figures on Home.</summary>
    public string AccentColor { get; init; } = "#58A6FF";

    [JsonIgnore]
    public bool IsDefault => this == new Branding();

    /// <summary>
    /// A hex colour the app is willing to paint with. Anything else is ignored in favour of the default, because a
    /// typo in a hand-edited file should not produce an unreadable window.
    /// </summary>
    public static bool LooksLikeColor(string? value) =>
        value is not null
        && (value.Length == 7 || value.Length == 4)
        && value[0] == '#'
        && value[1..].All(Uri.IsHexDigit);

    /// <summary>Replaces anything unusable with the shipped default, so what comes out is always paintable.</summary>
    public Branding Sanitised() => new()
    {
        ProductName = string.IsNullOrWhiteSpace(ProductName) ? new Branding().ProductName : ProductName.Trim(),
        Tagline = Tagline?.Trim() ?? string.Empty,
        ChromeColor = LooksLikeColor(ChromeColor) ? ChromeColor! : new Branding().ChromeColor,
        AccentColor = LooksLikeColor(AccentColor) ? AccentColor! : new Branding().AccentColor,
    };
}
