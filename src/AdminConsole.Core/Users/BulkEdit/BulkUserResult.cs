namespace AdminConsole.Core.Users.BulkEdit;

/// <summary>What happened to one person, for the dialog's live list.</summary>
public sealed record BulkUserResult(string UserId, string Label, BulkStatus Status, string Detail)
{
    public bool Succeeded => Status is BulkStatus.Changed or BulkStatus.NothingToDo;
}
