namespace AdminConsole.Core.Reports;

/// <summary>One person in the MFA readiness report.</summary>
public sealed record MfaRow
{
    public required string UserId { get; init; }
    public required string DisplayName { get; init; }
    public required string Upn { get; init; }
    public bool IsAdmin { get; init; }
    public bool IsGuest { get; init; }
    public bool? Enabled { get; init; }
    public required MfaStatus Status { get; init; }

    /// <summary>What is missing, or why the person is exempt — one sentence.</summary>
    public required string Detail { get; init; }

    public IReadOnlyList<string> Methods { get; init; } = [];

    public bool PasswordlessCapable { get; init; }

    public string StatusText => Status switch
    {
        MfaStatus.Ready => "Ready",
        MfaStatus.PhoneOnly => "Phone only",
        MfaStatus.Missing => "No second factor",
        _ => "Exempt",
    };

    public override string ToString() => $"{DisplayName}, {StatusText}, {Detail}";

    public string MethodsText => Methods.Count == 0 ? "None" : string.Join(", ", Methods.Select(MfaReportService.MethodName).Distinct());

    // Identity, not value equality: WPF keys a list's automation peers by item equality, so after a rebuild a row equal
    // to the one before took over that row's detached peer and dropped out of what a screen reader can reach (0.3.3).
    public bool Equals(MfaRow? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
