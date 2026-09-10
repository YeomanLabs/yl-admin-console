using AdminConsole.Core.Graph;

namespace AdminConsole.Core.Writes;

/// <summary>
/// The result of one write (UX §10): what to say in the toast, whether Undo is offered, and the action-log row it
/// produced. Failures carry both the friendly text and the raw Graph error for the Details expander (UX §7).
/// </summary>
public sealed record WriteOutcome
{
    public required bool Success { get; init; }

    /// <summary>Toast text, e.g. "Set job title to Engineer on Ada Lovelace".</summary>
    public required string Summary { get; init; }

    /// <summary>Friendly failure text (UX §7); null on success.</summary>
    public string? FriendlyError { get; init; }

    public GraphException? Error { get; init; }

    /// <summary>Row id in <c>actions.db</c>; 0 when nothing was logged.</summary>
    public long ActionId { get; init; }

    /// <summary>Reverses this write. Null when the action is not reversible or it failed.</summary>
    public Func<CancellationToken, Task<WriteOutcome>>? Undo { get; init; }

    /// <summary>True when the target was already in the requested state, so nothing was sent.</summary>
    public bool WasNoOp { get; init; }

    public bool CanUndo => Success && Undo is not null;

    public string? Details => Error is null ? null : GraphErrorText.Details(Error);

    public static WriteOutcome Failed(string summary, GraphException ex, long actionId, string? roleHint = null) => new()
    {
        Success = false,
        Summary = summary,
        FriendlyError = GraphErrorText.Friendly(ex, roleHint),
        Error = ex,
        ActionId = actionId,
    };

    public static WriteOutcome NoOp(string summary) => new()
    {
        Success = true,
        Summary = summary,
        WasNoOp = true,
    };
}
