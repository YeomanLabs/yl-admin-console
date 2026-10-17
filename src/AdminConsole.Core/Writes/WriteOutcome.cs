using AdminConsole.Core.Exchange;
using AdminConsole.Core.Graph;
using AdminConsole.Core.SharePoint;

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

    /// <summary>The Exchange error behind a failed mailbox write (doc 13); null for Graph writes.</summary>
    public ExchangeException? ExchangeError { get; init; }

    /// <summary>The SharePoint error behind a failed OneDrive write (M16); null otherwise.</summary>
    public SharePointException? SharePointError { get; init; }

    /// <summary>Something Exchange said about a write that succeeded — shown, never dropped (doc 13 §1).</summary>
    public string? Warning { get; init; }

    /// <summary>Row id in <c>actions.db</c>; 0 when nothing was logged.</summary>
    public long ActionId { get; init; }

    /// <summary>Reverses this write. Null when the action is not reversible or it failed.</summary>
    public Func<CancellationToken, Task<WriteOutcome>>? Undo { get; init; }

    /// <summary>True when the target was already in the requested state, so nothing was sent.</summary>
    public bool WasNoOp { get; init; }

    public bool CanUndo => Success && Undo is not null;

    public string? Details => Error is not null ? GraphErrorText.Details(Error)
        : ExchangeError is not null ? ExchangeErrorText.Details(ExchangeError)
        : SharePointError is not null ? SharePointException.Details(SharePointError)
        : null;

    public static WriteOutcome Failed(string summary, GraphException ex, long actionId, string? roleHint = null) => new()
    {
        Success = false,
        Summary = summary,
        FriendlyError = GraphErrorText.Friendly(ex, roleHint),
        Error = ex,
        ActionId = actionId,
    };

    public static WriteOutcome FailedExchange(string summary, ExchangeException ex, long actionId) => new()
    {
        Success = false,
        Summary = summary,
        FriendlyError = ExchangeErrorText.Friendly(ex),
        ExchangeError = ex,
        ActionId = actionId,
    };

    public static WriteOutcome FailedSharePoint(string summary, SharePointException ex, long actionId) => new()
    {
        Success = false,
        Summary = summary,
        FriendlyError = SharePointException.Friendly(ex),
        SharePointError = ex,
        ActionId = actionId,
    };

    public static WriteOutcome NoOp(string summary) => new()
    {
        Success = true,
        Summary = summary,
        WasNoOp = true,
    };
}
