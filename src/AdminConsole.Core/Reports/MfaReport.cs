namespace AdminConsole.Core.Reports;

/// <summary>M19.1: MFA registration across the tenant, dated.</summary>
public sealed record MfaReport
{
    public required DateTimeOffset TakenAt { get; init; }

    /// <summary>When Entra last refreshed the registration details it reports (its own lag, separate from ours).</summary>
    public DateTimeOffset? SourceUpdatedAt { get; init; }

    public IReadOnlyList<MfaRow> Rows { get; init; } = [];

    public int Count(MfaStatus status) => Rows.Count(r => r.Status == status);

    /// <summary>The figure the portal's "MFA capable" tile shows: registered with a method that can do MFA.</summary>
    public int MfaCapable { get; init; }

    public int AdminsNotReady => Rows.Count(r => r.IsAdmin && r.Status is MfaStatus.Missing or MfaStatus.PhoneOnly);

    /// <summary>Accounts blocked from signing in, which Entra leaves out of its registration details altogether.</summary>
    public int BlockedLeftOut { get; init; }
}
