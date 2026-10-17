namespace AdminConsole.Core.Exchange;

/// <summary>One default folder and who may do what in it.</summary>
public sealed record FolderPermissions
{
    public required MailboxFolder Folder { get; init; }
    public IReadOnlyList<FolderPermissionEntry> Entries { get; init; } = [];
}
