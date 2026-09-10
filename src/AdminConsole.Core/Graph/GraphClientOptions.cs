using AdminConsole.Core.Auth;

namespace AdminConsole.Core.Graph;

/// <summary>Retry, throttling and concurrency knobs for <see cref="GraphClient"/>. Defaults follow docs/03-ARCHITECTURE.md.</summary>
public sealed class GraphClientOptions
{
    /// <summary>Total attempts per request including the first. Doc 03: max 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>First backoff delay when no Retry-After is present; doubles per attempt (1-2-4-8 s).</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on a single computed backoff (Retry-After from Graph is always honored as-is).</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Concurrent in-flight requests per client. Doc 03: 4.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>
    /// Scopes passed to MSAL when fetching a token. A Graph token carries every consented scope in <c>scp</c>
    /// regardless of the subset requested, so the always-consented <c>User.Read</c> is sufficient.
    /// </summary>
    public IReadOnlyList<string> TokenScopes { get; set; } = [Scopes.UserRead];

    /// <summary>Delay implementation; tests substitute a recorder so retries are instant and observable.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public string UserAgent { get; set; } = "AdminConsole/0.1";
}
