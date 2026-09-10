using System.Diagnostics;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding;

/// <summary>
/// Runs an offboarding (doc 06, UX §8): previews every step in parallel, then executes the enabled ones strictly in
/// catalog order. A failure only stops the run when the step says <see cref="IOffboardingStep.HaltsOnFailure"/>;
/// otherwise the run carries on so one broken step cannot leave the account half-offboarded.
/// </summary>
public sealed class OffboardingRunner
{
    private readonly IReadOnlyList<IOffboardingStep> _steps;
    private readonly ILogger<OffboardingRunner> _logger;

    public OffboardingRunner(IEnumerable<IOffboardingStep> steps, ILogger<OffboardingRunner> logger)
    {
        _steps = steps.ToList();
        _logger = logger;
    }

    /// <summary>Every registered step, in the order doc 06 lists them.</summary>
    public IReadOnlyList<IOffboardingStep> Steps => _steps;

    public IOffboardingStep? Find(string stepId) => _steps.FirstOrDefault(s => s.Id == stepId);

    /// <summary>
    /// Previews the chosen steps at once (doc 06: previews are parallel). A step that throws still gets a row, because
    /// a missing row in the dialog would be read as "this step will not run" when nobody actually knows.
    /// </summary>
    public async Task<IReadOnlyList<StepPreview>> PreviewAsync(OffboardingContext ctx, IReadOnlyCollection<string> enabledStepIds, CancellationToken ct)
    {
        var chosen = _steps.Where(s => enabledStepIds.Contains(s.Id)).ToList();
        var previews = await Task.WhenAll(chosen.Select(async step =>
        {
            try
            {
                return await step.PreviewAsync(ctx, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GraphException ex)
            {
                _logger.LogWarning("Preview of {StepId} failed: {Code}", step.Id, ex.ErrorCode);
                return StepPreview.Error(step.Id, step.Title, GraphErrorText.Friendly(ex));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Preview of {StepId} threw", step.Id);
                return StepPreview.Error(step.Id, step.Title, $"The preview failed: {ex.Message}");
            }
        })).ConfigureAwait(false);

        // Back into catalog order: Task.WhenAll preserves the input order, and the input was already ordered.
        return previews;
    }

    /// <summary>
    /// Executes the steps whose preview said they can run, in order. <paramref name="previews"/> comes from
    /// <see cref="PreviewAsync"/>; a step with no preview is not executed, so nothing runs that the admin never saw.
    /// </summary>
    public async Task<OffboardingRun> ExecuteAsync(
        OffboardingContext ctx,
        IReadOnlyList<StepPreview> previews,
        IProgress<StepProgress>? progress,
        CancellationToken ct)
    {
        var byId = previews.ToDictionary(p => p.StepId, StringComparer.Ordinal);
        var queue = _steps.Where(s => byId.ContainsKey(s.Id)).ToList();
        var results = new List<StepResult>(queue.Count);
        var total = queue.Count;
        var runClock = Stopwatch.StartNew();
        var halted = false;
        var cancelled = false;

        for (var i = 0; i < queue.Count; i++)
        {
            var step = queue[i];
            var preview = byId[step.Id];

            // Cancel aborts before the next step, never in the middle of one (UX §8 step 2).
            if (ct.IsCancellationRequested) cancelled = true;

            if (halted || cancelled)
            {
                var why = cancelled ? "Not run — the run was cancelled" : "Not run — an earlier step failed";
                results.Add(new StepResult { StepId = step.Id, Outcome = StepOutcome.NotRun, Message = why });
                Report(progress, step.Id, StepOutcome.NotRun, why, TimeSpan.Zero, i + 1, total);
                continue;
            }

            if (!preview.CanRun)
            {
                var why = preview.IsNoOp ? preview.Summary : preview.Reason ?? "Skipped";
                results.Add(StepResult.Skipped(step.Id, why));
                Report(progress, step.Id, StepOutcome.Skipped, why, TimeSpan.Zero, i + 1, total);
                continue;
            }

            Report(progress, step.Id, StepOutcome.Running, null, TimeSpan.Zero, i + 1, total);
            var stepClock = Stopwatch.StartNew();
            StepResult result;
            try
            {
                result = await step.ExecuteAsync(ctx, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                result = new StepResult { StepId = step.Id, Outcome = StepOutcome.NotRun, Message = "Cancelled" };
            }
            catch (GraphException ex)
            {
                _logger.LogWarning("Step {StepId} failed: {Code}", step.Id, ex.ErrorCode);
                result = StepResult.Failed(step.Id, GraphErrorText.Friendly(ex), ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Step {StepId} threw", step.Id);
                result = StepResult.Failed(step.Id, ex.Message);
            }

            result = result with { Duration = stepClock.Elapsed };
            results.Add(result);
            Report(progress, step.Id, result.Outcome, result.Message, result.Duration, i + 1, total);

            if (result.Outcome == StepOutcome.Failed && step.HaltsOnFailure)
            {
                _logger.LogWarning("Offboarding halted: {StepId} failed and halts the run", step.Id);
                halted = true;
            }
        }

        _logger.LogInformation("Offboarding of {UserId} finished in {Ms} ms (correlation {Correlation})", ctx.UserId, runClock.ElapsedMilliseconds, ctx.CorrelationId);

        return new OffboardingRun
        {
            Results = results,
            Elapsed = runClock.Elapsed,
            Halted = halted,
            Cancelled = cancelled,
        };
    }

    /// <summary>
    /// Reverses what can be reversed, most recent first, so a later step's change is undone before the earlier one it
    /// depended on. Undo is best-effort: one failure does not stop the rest.
    /// </summary>
    public async Task<UndoRun> UndoAsync(OffboardingRun run, IProgress<StepProgress>? progress, CancellationToken ct)
    {
        var undoable = run.Undoable.Reverse().ToList();
        var results = new List<(string, WriteOutcome)>(undoable.Count);

        for (var i = 0; i < undoable.Count; i++)
        {
            var step = undoable[i];
            Report(progress, step.StepId, StepOutcome.Running, null, TimeSpan.Zero, i + 1, undoable.Count);
            try
            {
                var outcome = await step.Undo!(ct).ConfigureAwait(false);
                results.Add((step.StepId, outcome));
                Report(progress, step.StepId, outcome.Success ? StepOutcome.Success : StepOutcome.Failed, outcome.Success ? outcome.Summary : outcome.FriendlyError, TimeSpan.Zero, i + 1, undoable.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Undo of {StepId} failed", step.StepId);
                var failed = new WriteOutcome { Success = false, Summary = $"Could not undo {step.StepId}", FriendlyError = ex.Message };
                results.Add((step.StepId, failed));
                Report(progress, step.StepId, StepOutcome.Failed, ex.Message, TimeSpan.Zero, i + 1, undoable.Count);
            }
        }

        return new UndoRun { Results = results };
    }

    private static void Report(IProgress<StepProgress>? progress, string stepId, StepOutcome outcome, string? message, TimeSpan duration, int index, int total) =>
        progress?.Report(new StepProgress { StepId = stepId, Outcome = outcome, Message = message, Duration = duration, Index = index, Total = total });
}
