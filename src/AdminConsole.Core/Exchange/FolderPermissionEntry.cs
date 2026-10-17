using System.Text.Json;

namespace AdminConsole.Core.Exchange;

/// <summary>One row of <c>Get-MailboxFolderPermission</c>: who may do what in one folder.</summary>
public sealed record FolderPermissionEntry
{
    /// <summary><c>Default</c> (everyone in the organisation), <c>Anonymous</c> (everyone outside it), or a person's name.</summary>
    public required string Principal { get; init; }

    public IReadOnlyList<string> Rights { get; init; } = [];

    /// <summary>Calendar delegate flags (<c>Delegate</c>, <c>CanViewPrivateItems</c>), when set.</summary>
    public IReadOnlyList<string> SharingFlags { get; init; } = [];

    public bool IsDefault => string.Equals(Principal, "Default", StringComparison.OrdinalIgnoreCase);
    public bool IsAnonymous => string.Equals(Principal, "Anonymous", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Everyone in the organisation" for Default; the principal otherwise.</summary>
    public string Who => IsDefault ? "Everyone in the organisation (Default)" : IsAnonymous ? "Anyone outside it (Anonymous)" : Principal;

    public static FolderPermissionEntry FromJson(JsonElement row) => new()
    {
        Principal = ExchangeValues.NonEmpty(row, "User") ?? "(unknown)",
        Rights = ExchangeValues.Rights(row),
        SharingFlags = ExchangeValues.Rights(row, "SharingPermissionFlags"),
    };
}
