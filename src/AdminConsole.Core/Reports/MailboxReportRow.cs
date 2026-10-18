using AdminConsole.Core.Exchange;

namespace AdminConsole.Core.Reports;

/// <summary>One mailbox in the size and delegation report (M19.3).</summary>
public sealed record MailboxReportRow
{
    public required string UserId { get; init; }
    public required string Name { get; init; }
    public string? Address { get; init; }
    public required MailboxKind Kind { get; init; }
    public long? SizeBytes { get; init; }
    public long? Items { get; init; }
    public DateTimeOffset? LastUserAction { get; init; }
    public IReadOnlyList<string> FullAccess { get; init; } = [];
    public IReadOnlyList<string> SendAs { get; init; } = [];
    public IReadOnlyList<string> SendOnBehalf { get; init; } = [];

    /// <summary>Set when this mailbox's statistics or permissions could not be read; the rest of the row is what was.</summary>
    public string? Problem { get; init; }

    public override string ToString() => $"{Name}, {SizeText}, {DelegatesText}";

    public bool IsDelegated => FullAccess.Count + SendAs.Count + SendOnBehalf.Count > 0;

    public string SizeText => SizeBytes is { } b ? new ExchangeSize(b, false).ToDisplay() : "—";

    public string DelegatesText
    {
        get
        {
            var parts = new List<string>();
            if (FullAccess.Count > 0) parts.Add("Full Access: " + string.Join(", ", FullAccess));
            if (SendAs.Count > 0) parts.Add("Send As: " + string.Join(", ", SendAs));
            if (SendOnBehalf.Count > 0) parts.Add("Send on behalf: " + string.Join(", ", SendOnBehalf));
            return parts.Count == 0 ? "Nobody else" : string.Join(" · ", parts);
        }
    }

    // Identity, not value equality: WPF keys a list's automation peers by item equality, so after a rebuild a row equal
    // to the one before took over that row's detached peer and dropped out of what a screen reader can reach (0.3.3).
    public bool Equals(MailboxReportRow? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
