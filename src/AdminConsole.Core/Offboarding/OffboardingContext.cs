using AdminConsole.Core.Auth;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding.Snapshot;

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
    /// <remarks>Previews run in parallel, so every read and write of it goes through the lock.</remarks>
    public Dictionary<string, object> Scratch { get; } = [];

    /// <summary>The automatic reply the auto-reply step sets; null uses the template in doc 06 step 12.</summary>
    public string? AutoReplyMessage { get; init; }

    /// <summary>Where the OneDrive export step writes its ZIP (M16.4); null leaves that step blocked with the reason.</summary>
    public string? ExportFolder { get; init; }

    /// <summary>The snapshot a restore puts back (M15.4); null for an offboarding.</summary>
    public OffboardingSnapshot? RestoreFrom { get; init; }

    /// <summary>Where a restore moves the AD account; null means back where the snapshot found it.</summary>
    public string? RestoreOuDn { get; init; }

    public string UserId => User.Id;

    public string UserLabel => User.DisplayName ?? User.UserPrincipalName ?? User.Id;

    public T? Get<T>(string key) where T : class
    {
        lock (Scratch) return Scratch.TryGetValue(key, out var v) ? v as T : null;
    }

    public void Set(string key, object value)
    {
        lock (Scratch) Scratch[key] = value;
    }

    /// <summary>
    /// The value under <paramref name="key"/>, made once. Eleven Exchange steps preview at the same moment and all need
    /// the same mailbox: the first starts the read and the rest await that one task instead of sending ten more.
    /// </summary>
    public T GetOrAdd<T>(string key, Func<T> create) where T : class
    {
        lock (Scratch)
        {
            if (Scratch.TryGetValue(key, out var v) && v is T existing) return existing;
            var made = create();
            Scratch[key] = made;
            return made;
        }
    }
}
