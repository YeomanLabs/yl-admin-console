namespace YeomanLabs.AdminConsole.Core.Search;

/// <summary>Palette scope; the <c>u:</c> / <c>d:</c> / <c>g:</c> / <c>&gt;</c> prefixes narrow it (UX §3).</summary>
public enum SearchScope
{
    All,
    Users,
    Devices,
    Groups,
    Actions,
}
