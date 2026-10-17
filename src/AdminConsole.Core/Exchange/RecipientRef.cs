namespace AdminConsole.Core.Exchange;

/// <summary>
/// A person or group an Exchange property points at. <c>GrantSendOnBehalfTo</c>, <c>ForwardingAddress</c> and the
/// delivery restriction lists hold Exchange <em>names</em> (<c>AndrewYeoman</c>), which mean nothing to an admin and are not
/// stable, so they are resolved to this before they are shown (doc 13 §3).
/// </summary>
public sealed record RecipientRef
{
    /// <summary>What Exchange stored — kept, because it is what a remove must match when nothing else resolves.</summary>
    public required string ExchangeName { get; init; }

    public string? DisplayName { get; init; }
    public string? PrimarySmtp { get; init; }

    /// <summary>Entra object id, when the recipient has one; the identity every write uses.</summary>
    public string? ObjectId { get; init; }

    public string? RecipientType { get; init; }

    /// <summary>The identity to hand back to Exchange: the object id when known, else the name it gave us.</summary>
    public string Identity => ObjectId ?? ExchangeName;

    public string Display => DisplayName is { Length: > 0 } d
        ? PrimarySmtp is { Length: > 0 } smtp ? $"{d} ({smtp})" : d
        : ExchangeName;

    public static RecipientRef Unresolved(string name) => new() { ExchangeName = name };
}
