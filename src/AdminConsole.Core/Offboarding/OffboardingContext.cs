using AdminConsole.Core.Auth;
using AdminConsole.Core.Models;

namespace AdminConsole.Core.Offboarding;

/// <summary>
/// Everything a step needs about one run (doc 06). The user is read fresh from Graph, not from the cache, because a
/// stale <c>accountEnabled</c> or licence list would make the preview lie about what is going to happen.
/// </summary>
public sealed class OffboardingContext
{
    public required DirectoryUser User { get; init; }

    public required TenantContext Tenant { get; init; }

    /// <summary>Who inherits ownership, delegated access and forwarded mail. Null means "no delegate chosen".</summary>
    public PersonRef? Delegate { get; init; }

    /// <summary>Groups every action log row this run produces, so the Activity page can show the run as one thing.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Values a step wants the attribute step to stamp; empty means use the defaults from doc 06 step 17.</summary>
    public IReadOnlyDictionary<string, string?> AttributeStamps { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Shared between steps within one run (doc 06): a step's preview leaves what it found here so its execute does not
    /// have to read Graph twice, and later steps can see what earlier ones did.
    /// </summary>
    public Dictionary<string, object> Scratch { get; } = [];

    public string UserId => User.Id;

    public string UserLabel => User.DisplayName ?? User.UserPrincipalName ?? User.Id;

    public T? Get<T>(string key) where T : class => Scratch.TryGetValue(key, out var v) ? v as T : null;

    public void Set(string key, object value) => Scratch[key] = value;
}
