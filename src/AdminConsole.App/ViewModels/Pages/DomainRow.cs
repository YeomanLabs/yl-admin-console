using AdminConsole.Core.OnPrem;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// One row on the Domain page (M12.3). Users, groups and computers share a row shape so switching between them does
/// not move everything on screen — the eye stays where it was.
/// </summary>
public sealed record DomainRow
{
    public required Guid ObjectGuid { get; init; }
    public required OnPremKind Kind { get; init; }
    public required string Primary { get; init; }
    public required string Secondary { get; init; }

    /// <summary>Where it lives, as a readable path rather than a distinguished name.</summary>
    public required string Location { get; init; }

    public string? Detail { get; init; }
    public bool IsDisabled { get; init; }

    /// <summary>A short flag shown on the right: the group's scope, or the reason a row stands out.</summary>
    public string? Badge { get; init; }

    public static DomainRow From(OnPremUser u) => new()
    {
        ObjectGuid = u.ObjectGuid,
        Kind = OnPremKind.User,
        Primary = u.Primary,
        Secondary = u.UserPrincipalName ?? u.SamAccountName,
        Location = DistinguishedNames.Friendly(u.DistinguishedName),
        Detail = string.Join(" · ", new[] { u.JobTitle, u.Department }.Where(s => !string.IsNullOrWhiteSpace(s))),
        IsDisabled = !u.Enabled,
        Badge = u.IsLockedOut ? "Locked out" : null,
    };

    public static DomainRow From(OnPremGroup g) => new()
    {
        ObjectGuid = g.ObjectGuid,
        Kind = OnPremKind.Group,
        Primary = g.Primary,
        Secondary = g.SamAccountName,
        Location = DistinguishedNames.Friendly(g.DistinguishedName),
        Detail = g.Description,
        // Scope and category have no cloud equivalent, so they are the thing worth showing without opening it.
        Badge = g.ScopeText,
    };

    public static DomainRow From(OnPremComputer c) => new()
    {
        ObjectGuid = c.ObjectGuid,
        Kind = OnPremKind.Computer,
        Primary = c.Name,
        Secondary = c.DnsHostName ?? c.SamAccountName,
        Location = DistinguishedNames.Friendly(c.DistinguishedName),
        Detail = string.Join(" ", new[] { c.OperatingSystem, c.OperatingSystemVersion }.Where(s => !string.IsNullOrWhiteSpace(s))),
        IsDisabled = !c.Enabled,
    };

    // Identity, not value equality: WPF keys a list's automation peers by item equality, so a refilled list whose
    // rows equal the ones before reused their detached peers, and a screen reader lost the rows (0.3.3).
    public bool Equals(DomainRow? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
