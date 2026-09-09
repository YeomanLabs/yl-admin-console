using System.Text.Json;

namespace YeomanLabs.AdminConsole.Core.Models;

/// <summary><c>user.signInActivity</c> (Entra ID P1 + AuditLog.Read.All). Null everywhere means "not available on this tenant's license".</summary>
public sealed record SignInActivity
{
    public DateTimeOffset? LastSignInDateTime { get; init; }
    public DateTimeOffset? LastNonInteractiveSignInDateTime { get; init; }
    public DateTimeOffset? LastSuccessfulSignInDateTime { get; init; }
    public string? LastSignInRequestId { get; init; }

    /// <summary>The most recent of interactive / non-interactive, for the "last seen" caption.</summary>
    public DateTimeOffset? LastSeen =>
        LastSignInDateTime is { } a && (LastNonInteractiveSignInDateTime is not { } b || a > b) ? a : LastNonInteractiveSignInDateTime;

    public static SignInActivity? FromJson(JsonElement user)
    {
        if (!user.TryGetProperty("signInActivity", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        return new SignInActivity
        {
            LastSignInDateTime = s.GetDate("lastSignInDateTime"),
            LastNonInteractiveSignInDateTime = s.GetDate("lastNonInteractiveSignInDateTime"),
            LastSuccessfulSignInDateTime = s.GetDate("lastSuccessfulSignInDateTime"),
            LastSignInRequestId = s.GetString("lastSignInRequestId"),
        };
    }
}
