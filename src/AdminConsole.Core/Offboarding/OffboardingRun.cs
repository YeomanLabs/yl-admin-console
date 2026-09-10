using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding;

/// <summary>The finished run (UX §8 step 3): "9 completed, 1 failed, 1 skipped · 41 s", plus what can still be undone.</summary>
public sealed record OffboardingRun
{
    public required IReadOnlyList<StepResult> Results { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>True when a halting step failed, so the steps after it were never attempted.</summary>
    public bool Halted { get; init; }

    public bool Cancelled { get; init; }

    public int Completed => Results.Count(r => r.Outcome == StepOutcome.Success);
    public int Failed => Results.Count(r => r.Outcome == StepOutcome.Failed);
    public int Skipped => Results.Count(r => r.Outcome == StepOutcome.Skipped);
    public int NotRun => Results.Count(r => r.Outcome == StepOutcome.NotRun);

    public IReadOnlyList<StepResult> Undoable => Results.Where(r => r.CanUndo).ToList();

    public bool CanUndo => Undoable.Count > 0;

    /// <summary>"9 completed, 1 failed, 1 skipped · 41 s". Zero counts are left out so the line stays readable.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>(4);
            if (Completed > 0) parts.Add($"{Completed} completed");
            if (Failed > 0) parts.Add($"{Failed} failed");
            if (Skipped > 0) parts.Add($"{Skipped} skipped");
            if (NotRun > 0) parts.Add($"{NotRun} not run");
            if (parts.Count == 0) parts.Add("nothing to do");
            return $"{string.Join(", ", parts)} · {Elapsed.TotalSeconds:F0} s";
        }
    }
}

/// <summary>Progress for the live log (UX §8 step 2): one of these before and after every step.</summary>
public sealed record StepProgress
{
    public required string StepId { get; init; }
    public required StepOutcome Outcome { get; init; }
    public string? Message { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>1-based position in the enabled list, for "step 4 of 11".</summary>
    public int Index { get; init; }

    public int Total { get; init; }
}

/// <summary>The outcome of undoing a finished run.</summary>
public sealed record UndoRun
{
    public required IReadOnlyList<(string StepId, WriteOutcome Outcome)> Results { get; init; }

    public int Reversed => Results.Count(r => r.Outcome.Success);
    public int Failed => Results.Count(r => !r.Outcome.Success);
}
