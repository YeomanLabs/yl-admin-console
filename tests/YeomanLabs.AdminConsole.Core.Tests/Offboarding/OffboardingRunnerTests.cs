using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Offboarding;
using YeomanLabs.AdminConsole.Core.Writes;

namespace YeomanLabs.AdminConsole.Core.Tests.Offboarding;

/// <summary>M6.1: the runner's own behaviour, exercised with fake steps so nothing here depends on Graph.</summary>
public sealed class OffboardingRunnerTests
{
    private static OffboardingContext Context() => new()
    {
        User = new DirectoryUser
        {
            Id = "u-1",
            DisplayName = "Ada Lovelace",
            UserPrincipalName = "ada@contoso.com",
            AccountEnabled = true,
            OnPremisesSyncEnabled = false,
            Licensed = true,
            Raw = "{}",
        },
        Tenant = new TenantContext(),
        CorrelationId = "run-1",
    };

    private static OffboardingRunner Runner(params IOffboardingStep[] steps) =>
        new(steps, NullLogger<OffboardingRunner>.Instance);

    private static async Task<(OffboardingRun Run, List<StepProgress> Progress)> RunAsync(OffboardingRunner runner, OffboardingContext ctx, CancellationToken ct = default)
    {
        var seen = new List<StepProgress>();
        var previews = await runner.PreviewAsync(ctx, runner.Steps.Select(s => s.Id).ToList(), CancellationToken.None);
        var run = await runner.ExecuteAsync(ctx, previews, new Progress<StepProgress>(seen.Add), ct);
        return (run, seen);
    }

    [Fact]
    public async Task Preview_RunsEveryChosenStep_InCatalogOrder()
    {
        var runner = Runner(new FakeStep("one"), new FakeStep("two"), new FakeStep("three"));

        var previews = await runner.PreviewAsync(Context(), ["three", "one", "two"], CancellationToken.None);

        previews.Select(p => p.StepId).Should().Equal(["one", "two", "three"], "the catalog order wins over the caller's order");
    }

    [Fact]
    public async Task Preview_OnlyCoversTheStepsAskedFor()
    {
        var skipped = new FakeStep("two");
        var runner = Runner(new FakeStep("one"), skipped);

        var previews = await runner.PreviewAsync(Context(), ["one"], CancellationToken.None);

        previews.Should().ContainSingle().Which.StepId.Should().Be("one");
        skipped.PreviewCount.Should().Be(0);
    }

    [Fact]
    public async Task Preview_AStepThatThrows_StillGetsARow()
    {
        // A missing row would read as "this step will not run" when in truth nobody knows.
        var runner = Runner(new FakeStep("one") { PreviewThrows = new InvalidOperationException("boom") });

        var previews = await runner.PreviewAsync(Context(), ["one"], CancellationToken.None);

        var preview = previews.Should().ContainSingle().Subject;
        preview.Failed.Should().BeTrue();
        preview.CanRun.Should().BeFalse();
        preview.Reason.Should().Contain("boom");
    }

    [Fact]
    public async Task Execute_RunsInOrder_AndReportsProgressAroundEachStep()
    {
        var order = new List<string>();
        var runner = Runner(
            new FakeStep("one") { OnExecute = order.Add },
            new FakeStep("two") { OnExecute = order.Add },
            new FakeStep("three") { OnExecute = order.Add });

        var (run, progress) = await RunAsync(runner, Context());

        order.Should().Equal("one", "two", "three");
        run.Completed.Should().Be(3);
        run.Summary.Should().StartWith("3 completed");
        progress.Where(p => p.Outcome == StepOutcome.Running).Select(p => p.StepId).Should().Equal("one", "two", "three");
        progress.Last().Total.Should().Be(3);
        progress.Last().Index.Should().Be(3);
    }

    [Fact]
    public async Task Execute_NeverRunsAStepWhosePreviewSaidItCannot()
    {
        var blocked = new FakeStep("two") { Preview = StepPreview.Blocked("two", "Clear manager", "Mastered in on-prem AD (Phase 2)") };
        var runner = Runner(new FakeStep("one"), blocked, new FakeStep("three"));

        var (run, _) = await RunAsync(runner, Context());

        blocked.ExecuteCount.Should().Be(0, "the preview is the gate; a step never decides for itself");
        run.Results.Single(r => r.StepId == "two").Outcome.Should().Be(StepOutcome.Skipped);
        run.Results.Single(r => r.StepId == "two").Message.Should().Be("Mastered in on-prem AD (Phase 2)");
        run.Completed.Should().Be(2);
        run.Skipped.Should().Be(1);
    }

    [Fact]
    public async Task Execute_ANoOpPreview_SkipsWithItsOwnWording()
    {
        var runner = Runner(new FakeStep("one") { Preview = StepPreview.NoOp("one", "Already disabled — nothing to do") });

        var (run, _) = await RunAsync(runner, Context());

        run.Results.Single().Outcome.Should().Be(StepOutcome.Skipped);
        run.Results.Single().Message.Should().Be("Already disabled — nothing to do");
    }

    [Fact]
    public async Task Execute_AFailureCarriesOn_UnlessTheStepHaltsTheRun()
    {
        var later = new FakeStep("three");
        var runner = Runner(
            new FakeStep("one") { Result = StepResult.Failed("one", "Graph said no") },
            new FakeStep("two"),
            later);

        var (run, _) = await RunAsync(runner, Context());

        run.Halted.Should().BeFalse();
        later.ExecuteCount.Should().Be(1, "one broken step must not leave the account half-offboarded");
        run.Failed.Should().Be(1);
        run.Completed.Should().Be(2);
    }

    [Fact]
    public async Task Execute_AHaltingStepThatFails_StopsEverythingAfterIt()
    {
        var later = new FakeStep("two");
        var runner = Runner(
            new FakeStep("one") { HaltsOnFailure = true, Result = StepResult.Failed("one", "Could not disable the account") },
            later,
            new FakeStep("three"));

        var (run, progress) = await RunAsync(runner, Context());

        run.Halted.Should().BeTrue();
        later.ExecuteCount.Should().Be(0);
        run.Results.Where(r => r.Outcome == StepOutcome.NotRun).Should().HaveCount(2);
        run.Results[1].Message.Should().Be("Not run — an earlier step failed");
        progress.Should().Contain(p => p.Outcome == StepOutcome.NotRun);
        run.Summary.Should().Be("1 failed, 2 not run · 0 s");
    }

    [Fact]
    public async Task Execute_AStepThatThrows_BecomesAFailedRow_NotACrash()
    {
        var runner = Runner(new FakeStep("one") { ExecuteThrows = new InvalidOperationException("boom") }, new FakeStep("two"));

        var (run, _) = await RunAsync(runner, Context());

        run.Results[0].Outcome.Should().Be(StepOutcome.Failed);
        run.Results[0].Message.Should().Be("boom");
        run.Completed.Should().Be(1, "the run carries on");
    }

    [Fact]
    public async Task Execute_CancelStopsBeforeTheNextStep_NeverMidStep()
    {
        using var cts = new CancellationTokenSource();
        var second = new FakeStep("two");
        var runner = Runner(new FakeStep("one") { OnExecute = _ => cts.Cancel() }, second, new FakeStep("three"));

        var (run, _) = await RunAsync(runner, Context(), cts.Token);

        run.Cancelled.Should().BeTrue();
        run.Results[0].Outcome.Should().Be(StepOutcome.Success, "the step that was already running finished");
        second.ExecuteCount.Should().Be(0);
        run.Results[1].Message.Should().Be("Not run — the run was cancelled");
    }

    [Fact]
    public async Task Undo_ReversesTheSuccessfulSteps_MostRecentFirst()
    {
        var undone = new List<string>();
        var runner = Runner(
            new FakeStep("one") { Undo = () => undone.Add("one") },
            new FakeStep("two") { Result = StepResult.Failed("two", "no") },
            new FakeStep("three") { Undo = () => undone.Add("three") });

        var (run, _) = await RunAsync(runner, Context());
        run.CanUndo.Should().BeTrue();
        run.Undoable.Select(r => r.StepId).Should().Equal("one", "three");

        var undo = await runner.UndoAsync(run, null, CancellationToken.None);

        undone.Should().Equal(["three", "one"], "a later change is reversed before the earlier one it depended on");
        undo.Reversed.Should().Be(2);
        undo.Failed.Should().Be(0);
    }

    [Fact]
    public async Task Undo_OneFailureDoesNotStopTheRest()
    {
        var undone = new List<string>();
        var runner = Runner(
            new FakeStep("one") { Undo = () => undone.Add("one") },
            new FakeStep("two") { Undo = () => throw new InvalidOperationException("nope") });

        var (run, _) = await RunAsync(runner, Context());
        var undo = await runner.UndoAsync(run, null, CancellationToken.None);

        undone.Should().Equal("one");
        undo.Reversed.Should().Be(1);
        undo.Failed.Should().Be(1);
    }

    [Fact]
    public async Task Run_WithNothingToDo_SaysSoRatherThanShowingZeroes()
    {
        var runner = Runner(new FakeStep("one") { Preview = StepPreview.NoOp("one", "Nothing to do") });

        var (run, _) = await RunAsync(runner, Context());

        run.CanUndo.Should().BeFalse();
        run.Summary.Should().Be("1 skipped · 0 s");
    }

    /// <summary>A step with no Graph behind it, so the runner's own logic is what is under test.</summary>
    private sealed class FakeStep : IOffboardingStep
    {
        public FakeStep(string id) => Id = id;

        public string Id { get; }
        public string Title => $"Step {Id}";
        public StepSurface Surface => StepSurface.Graph;
        public Capability? RequiredCapability => null;
        public bool DefaultEnabled => true;
        public bool HaltsOnFailure { get; init; }

        public StepPreview? Preview { get; init; }
        public StepResult? Result { get; init; }
        public Exception? PreviewThrows { get; init; }
        public Exception? ExecuteThrows { get; init; }
        public Action<string>? OnExecute { get; init; }
        public Action? Undo { get; init; }

        public int PreviewCount { get; private set; }
        public int ExecuteCount { get; private set; }

        public Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
        {
            PreviewCount++;
            if (PreviewThrows is not null) throw PreviewThrows;
            return Task.FromResult(Preview ?? new StepPreview { StepId = Id, Summary = Title });
        }

        public Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
        {
            ExecuteCount++;
            OnExecute?.Invoke(Id);
            if (ExecuteThrows is not null) throw ExecuteThrows;
            if (Result is not null) return Task.FromResult(Result);

            Func<CancellationToken, Task<WriteOutcome>>? undo = Undo is null
                ? null
                : _ =>
                {
                    Undo();
                    return Task.FromResult(new WriteOutcome { Success = true, Summary = $"Reversed {Id}" });
                };

            return Task.FromResult(StepResult.Success(Id, $"Did {Id}", undo));
        }
    }
}
