namespace YeomanLabs.AdminConsole.Core.Offboarding;

/// <summary>
/// What one step will actually do, worked out before anything runs (UX §8 step 1). <see cref="Summary"/> must name the
/// real objects — "Remove 3 licenses: Microsoft 365 E3, Visio Plan 2, Power BI Pro" — never a generic sentence.
/// </summary>
public sealed record StepPreview
{
    public required string StepId { get; init; }

    /// <summary>One concrete line describing the change, or why there is nothing to do.</summary>
    public required string Summary { get; init; }

    /// <summary>False greys the row and stops the runner calling <c>ExecuteAsync</c> at all.</summary>
    public bool CanRun { get; init; } = true;

    /// <summary>UX §6 caption: why this step cannot run, or a warning worth reading before it does.</summary>
    public string? Reason { get; init; }

    /// <summary>Per-object detail lines: the groups being skipped, the devices being retired.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>True when the target is already in the wanted state, so running the step is a no-op (idempotence).</summary>
    public bool IsNoOp { get; init; }

    /// <summary>The preview itself failed to read Graph; the step is shown but cannot be trusted to run.</summary>
    public bool Failed { get; init; }

    public static StepPreview Blocked(string stepId, string summary, string reason) =>
        new() { StepId = stepId, Summary = summary, CanRun = false, Reason = reason };

    public static StepPreview NoOp(string stepId, string summary) =>
        new() { StepId = stepId, Summary = summary, CanRun = false, IsNoOp = true };

    public static StepPreview Error(string stepId, string summary, string reason) =>
        new() { StepId = stepId, Summary = summary, CanRun = false, Reason = reason, Failed = true };
}
