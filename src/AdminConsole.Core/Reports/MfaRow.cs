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

    public string MethodsText => Methods.Count == 0 ? "None" : string.Join(", ", Methods.Select(MfaReportService.MethodName).Distinct());
}
