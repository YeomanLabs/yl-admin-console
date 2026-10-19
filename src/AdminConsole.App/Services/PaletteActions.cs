using AdminConsole.Core.Search;

namespace AdminConsole.App.Services;

/// <summary>The palette's "Actions" group (UX §3): command names and aliases. Ids are dispatched by the shell.</summary>
public static class PaletteActions
{
    public const string SyncNow = "sync-now";
    public const string NewUser = "new-user";
    public const string Offboard = "offboard";
    public const string Settings = "settings";

    public static IReadOnlyList<SearchAction> All { get; } =
    [
        new SearchAction { Id = SyncNow, Title = "Sync now", Aliases = ["refresh", "delta"], Description = "Force a delta sync (Ctrl+Shift+S)" },
        new SearchAction { Id = NewUser, Title = "Onboard someone…", Aliases = ["new user", "create user", "onboard", "starter", "copy user", "new user from template"], Description = "From a template or a copy of someone (Ctrl+N)" },
        new SearchAction { Id = Offboard, Title = "Offboard user…", Aliases = ["decom", "leaver", "terminate"], Description = "Run the offboarding checklist (Ctrl+Shift+O)" },
        new SearchAction { Id = Settings, Title = "Settings", Aliases = ["preferences", "options", "permissions"], Description = "Ctrl+," },
    ];
}
