using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;

namespace AdminConsole.Core.Tests.Offboarding;

/// <summary>The offboard dialog fills in step by step: each preview is reported the moment it is ready.</summary>
public sealed class PreviewProgressTests
{
    private static OffboardingContext Ctx() => new()
    {
        User = new DirectoryUser { Id = "u-1", AccountEnabled = true, OnPremisesSyncEnabled = false, Licensed = true, DisplayName = "Ada", UserPrincipalName = "ada@contoso.com", Raw = "{}" },
        Tenant = new TenantContext(),
        CorrelationId = "run-1",
    };

    [Fact]
    public async Task EachPreview_IsReportedAsItFinishes_NotWhenTheSlowestDoes()
    {
        var slow = new TaskCompletionSource();
        var steps = new IOffboardingStep[] { new Step("fast-1", Task.CompletedTask), new Step("slow", slow.Task), new Step("fast-2", Task.CompletedTask) };
        var runner = new OffboardingRunner(steps, NullLogger<OffboardingRunner>.Instance);
        var reported = new List<string>();
        var progress = new SyncProgress(p => { lock (reported) reported.Add(p.StepId); });

        var all = runner.PreviewAsync(Ctx(), steps.Select(s => s.Id).ToList(), CancellationToken.None, progress);

        lock (reported) reported.Should().BeEquivalentTo(["fast-1", "fast-2"], "the quick ones are in before the slow one finishes");
        all.IsCompleted.Should().BeFalse();

        slow.SetResult();
        var previews = await all;

        reported.Should().HaveCount(3);
        previews.Select(p => p.StepId).Should().Equal("fast-1", "slow", "fast-2");
    }

    /// <summary>Reports on the calling thread, unlike Progress&lt;T&gt;, so the test can look straight away.</summary>
    private sealed class SyncProgress(Action<StepPreview> report) : IProgress<StepPreview>
    {
        public void Report(StepPreview value) => report(value);
    }

    private sealed class Step(string id, Task waitFor) : IOffboardingStep
    {
        public string Id { get; } = id;
        public string Title => Id;
        public StepSurface Surface => StepSurface.Graph;
        public Capability? RequiredCapability => null;
        public bool DefaultEnabled => true;
        public bool HaltsOnFailure => false;

        public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
        {
            await waitFor;
            return new StepPreview { StepId = Id, Summary = Id };
        }

        public Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct) => Task.FromResult(StepResult.Success(Id, Id));
    }
}
