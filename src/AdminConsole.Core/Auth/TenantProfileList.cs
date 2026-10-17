namespace AdminConsole.Core.Auth;

/// <summary>What <c>tenant.json</c> holds since M20.1: the profiles, and which one the console last opened.</summary>
public sealed record TenantProfileList
{
    public IReadOnlyList<TenantProfile> Profiles { get; init; } = [];

    public string? LastUsedId { get; init; }

    /// <summary>The profile to open at start: the last used, else the first.</summary>
    public TenantProfile? Default => Profiles.FirstOrDefault(p => p.Id == LastUsedId) ?? Profiles.FirstOrDefault();

    /// <summary>Adds or replaces by id.</summary>
    public TenantProfileList With(TenantProfile profile) => this with
    {
        Profiles = Profiles.Any(p => p.Id == profile.Id)
            ? [.. Profiles.Select(p => p.Id == profile.Id ? profile : p)]
            : [.. Profiles, profile],
    };

    public TenantProfileList Without(string id) => this with
    {
        Profiles = [.. Profiles.Where(p => p.Id != id)],
        LastUsedId = LastUsedId == id ? null : LastUsedId,
    };
}
