using AdminConsole.Core.Auth;

namespace AdminConsole.Core.Exchange;

/// <summary>Retry, throttling and concurrency knobs for <see cref="ExchangeClient"/> (docs/13-EXCHANGE-ONLINE.md §1).</summary>
public sealed class ExchangeClientOptions
{
    /// <summary>Worldwide Exchange Online. Doc 13 lists the sovereign clouds; none is supported yet.</summary>
    public Uri BaseAddress { get; set; } = new("https://outlook.office365.com/");

    /// <summary>Total attempts per request including the first.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>First backoff when Exchange gives no wait of its own; doubles per attempt (1-2-4-8 s).</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Upper bound on a computed wait, including one derived from <c>Rate-Limit-Reset</c>. A <c>Retry-After</c> from the
    /// service is always honoured as given.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// In-flight requests per client. Below Graph's 4 because one cmdlet costs Exchange far more than one Graph read, and
    /// every response carries a concurrency counter (<c>X-SLT-ConcurrencyCount</c>) that says so.
    /// </summary>
    public int MaxConcurrency { get; set; } = 3;

    /// <summary>Rows per page, sent as <c>Prefer: odata.maxpagesize</c>. 1,000 is the service maximum.</summary>
    public int PageSize { get; set; } = 1000;

    /// <summary>Guards against a nextLink that never ends. 1,000 pages of 1,000 is far beyond any tenant this is for.</summary>
    public int MaxPages { get; set; } = 1000;

    public IReadOnlyList<string> TokenScopes { get; set; } = [Scopes.ExchangeManage];

    /// <summary>Delay implementation; tests substitute a recorder so retries are instant and observable.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Clock for turning <c>Rate-Limit-Reset</c> into a wait.</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    public string UserAgent { get; set; } = "AdminConsole/0.1";
}
