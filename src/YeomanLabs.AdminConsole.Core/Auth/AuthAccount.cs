namespace YeomanLabs.AdminConsole.Core.Auth;

/// <summary>The signed-in account, decoupled from MSAL's <c>IAccount</c> so App never references MSAL.</summary>
public sealed record AuthAccount
{
    public required string Username { get; init; }
    public required string TenantId { get; init; }
    public required string HomeAccountId { get; init; }
}
