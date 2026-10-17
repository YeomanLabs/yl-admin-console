namespace AdminConsole.Core.Users.BulkEdit;

public enum BulkStatus
{
    Changed,
    NothingToDo,
    Failed,

    /// <summary>One half landed and the other did not: Entra changed, AD refused, or the reverse.</summary>
    PartlyDone,
}
