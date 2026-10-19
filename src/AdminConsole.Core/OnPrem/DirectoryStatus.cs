namespace AdminConsole.Core.OnPrem;

/// <summary>
/// Whether the console can talk to Active Directory at all (M12.1), and what it is talking to.
/// </summary>
/// <remarks>
/// Every on-prem feature is greyed with <see cref="Reason"/> when this is not connected. Off the corporate network is
/// the normal case for a laptop, not an error, and it should read as one.
/// </remarks>
public sealed record DirectoryStatus
{
    public required bool IsConnected { get; init; }

    /// <summary>DNS name of the domain, e.g. <c>corp.example.com</c>.</summary>
    public string? DomainName { get; init; }

    /// <summary>The naming context, e.g. <c>DC=corp,DC=example,DC=com</c>.</summary>
    public string? RootDn { get; init; }

    /// <summary>The domain controller actually in use. Shown because "which DC" is the first question when a read is stale.</summary>
    public string? DomainController { get; init; }

    /// <summary>Who the console is bound as.</summary>
    public string? BoundAs { get; init; }

    /// <summary>Why it is not connected, in words, or null when it is.</summary>
    public string? Reason { get; init; }

    public static DirectoryStatus NotConfigured =>
        new() { IsConnected = false, Reason = "No domain configured. On-prem features are off." };

    public static DirectoryStatus Unreachable(string detail) =>
        new() { IsConnected = false, Reason = $"No domain controller reachable. {detail}" };

    /// <summary>The connection signs in as an account whose password has not been given this session.</summary>
    public bool NeedsSignIn { get; init; }

    public static DirectoryStatus NotSignedIn(string domain, string account) =>
        new() { IsConnected = false, NeedsSignIn = true, DomainName = domain, Reason = $"Not signed in to {domain}. Sign in as {account} to use on-prem features." };

    public static DirectoryStatus SignInRefused(string domain, string account) =>
        new() { IsConnected = false, NeedsSignIn = true, DomainName = domain, Reason = $"{domain} did not accept the password for {account}. Sign in again." };
}
