using AdminConsole.Core.Graph;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding;

/// <summary>How one step actually went (UX §8 steps 2 and 3).</summary>
public enum StepOutcome
{
    /// <summary>Queued but not reached, because a halting step failed or the run was cancelled.</summary>
    NotRun,

    Running,
    Success,
    Failed,

    /// <summary>Deliberately not done: the preview blocked it, or there was nothing to change.</summary>
    Skipped,
}

/// <summary>
/// The result of one step, and the undo that reverses it. Failures carry the Graph error so the summary's Details
/// expander can show it (UX §7).
/// </summary>
public sealed record StepResult
{
    public required string StepId { get; init; }
    public required StepOutcome Outcome { get; init; }

    /// <summary>One line for the live log: "Removed from 12 groups", "Already disabled — nothing to do".</summary>
    public required string Message { get; init; }

    public TimeSpan Duration { get; init; }

    public GraphException? Error { get; init; }

    public string? Details => Error is null ? null : GraphErrorText.Details(Error);

    /// <summary>Reverses this step. Null when the step is not reversible or did not change anything.</summary>
    public Func<CancellationToken, Task<WriteOutcome>>? Undo { get; init; }

    public bool CanUndo => Outcome == StepOutcome.Success && Undo is not null;

    public static StepResult Success(string stepId, string message, Func<CancellationToken, Task<WriteOutcome>>? undo = null) =>
        new() { StepId = stepId, Outcome = StepOutcome.Success, Message = message, Undo = undo };

    public static StepResult Skipped(string stepId, string message) =>
        new() { StepId = stepId, Outcome = StepOutcome.Skipped, Message = message };

    public static StepResult Failed(string stepId, string message, GraphException? error = null) =>
        new() { StepId = stepId, Outcome = StepOutcome.Failed, Message = message, Error = error };
}
