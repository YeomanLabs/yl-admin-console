using System.Text.Json;

namespace YeomanLabs.AdminConsole.Core.Models;

/// <summary>One <c>directoryAudit</c> row for the Activity tab (doc 04 §Audit / activity). Needs AuditLog.Read.All.</summary>
public sealed record AuditEvent
{
    public required string Id { get; init; }
    public required string Activity { get; init; }
    public DateTimeOffset? ActivityDateTime { get; init; }

    /// <summary>success | failure | timeout | unknownFutureValue</summary>
    public string? Result { get; init; }

    public string? ResultReason { get; init; }
    public string? Category { get; init; }

    /// <summary>Who did it: the initiating user's UPN/display name, or the app's display name.</summary>
    public string? InitiatedBy { get; init; }

    public bool IsFailure => string.Equals(Result, "failure", StringComparison.OrdinalIgnoreCase);

    public static AuditEvent FromJson(JsonElement e)
    {
        string? initiatedBy = null;
        if (e.GetObject("initiatedBy") is { } by)
        {
            if (by.GetObject("user") is { } user) initiatedBy = user.GetString("userPrincipalName") ?? user.GetString("displayName");
            initiatedBy ??= by.GetObject("app")?.GetString("displayName");
        }

        return new AuditEvent
        {
            Id = e.GetString("id") ?? throw new JsonException("audit event without id"),
            Activity = e.GetString("activityDisplayName") ?? "(unknown activity)",
            ActivityDateTime = e.GetDate("activityDateTime"),
            Result = e.GetString("result"),
            ResultReason = e.GetString("resultReason"),
            Category = e.GetString("category"),
            InitiatedBy = initiatedBy,
        };
    }
}
