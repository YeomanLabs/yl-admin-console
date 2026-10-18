namespace AdminConsole.Core.Reports;

/// <summary>A licensed account (M19.2): every holder since 0.3.3, so a subscription can list who holds it.</summary>
public sealed record LicensedUserRow
{
    public required string UserId { get; init; }
    public required string DisplayName { get; init; }
    public required string Upn { get; init; }
    public bool Enabled { get; init; }
    public IReadOnlyList<string> Licences { get; init; } = [];

    /// <summary>The SKU ids behind <see cref="Licences"/>, in the same order: how a subscription finds its holders.</summary>
    public IReadOnlyList<string> SkuIds { get; init; } = [];

    /// <summary>"Direct", "Group" or "Direct and group", per licence, in the order of <see cref="Licences"/>.</summary>
    public IReadOnlyList<string> AssignedBy { get; init; } = [];

    public DateTimeOffset? LastSignIn { get; init; }
    public DateTimeOffset? Created { get; init; }
    public bool IsQuiet { get; init; }

    public bool IsFlagged => !Enabled || IsQuiet;

    public string Reason => !Enabled
        ? "Sign-in blocked but still licensed"
        : IsQuiet
            ? LastSignIn is { } at ? $"No sign-in since {at.ToLocalTime():d}" : "Never signed in"
            : LastSignIn is { } last ? $"Last signed in {last.ToLocalTime():d}" : "No sign-in recorded";

    /// <summary>How this person holds one licence: "Direct", "Group" or "Direct and group".</summary>
    public string HowHeld(string skuId)
    {
        var i = SkuIds.ToList().FindIndex(s => string.Equals(s, skuId, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i < AssignedBy.Count ? AssignedBy[i] : "Direct";
    }

    public override string ToString() => $"{DisplayName}, {Reason}";

    public string LicencesText => string.Join(", ", Licences.Select((l, i) => i < AssignedBy.Count && AssignedBy[i] != "Direct" ? $"{l} ({AssignedBy[i].ToLowerInvariant()})" : l));

    // Identity, not value equality: WPF keys a list's automation peers by item equality, so after a rebuild a row equal
    // to the one before took over that row's detached peer and dropped out of what a screen reader can reach (0.3.3).
    public bool Equals(LicensedUserRow? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
