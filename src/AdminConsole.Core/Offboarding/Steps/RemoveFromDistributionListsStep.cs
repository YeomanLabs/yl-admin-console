using AdminConsole.Core.Auth;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Models;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Takes the leaver out of distribution lists and mail-enabled security groups (M15.3) — the memberships the cloud
/// group step has to skip, because Graph refuses to change them. Lists synced from AD are left to the AD group step.
/// </summary>
public sealed class RemoveFromDistributionListsStep : IOffboardingStep
{
    private const string PlanKey = "exchange.dl.plan";

    private readonly ExchangeService _exchange;
    private readonly GraphClient _graph;

    public RemoveFromDistributionListsStep(ExchangeService exchange, GraphClient graph)
    {
        _exchange = exchange;
        _graph = graph;
    }

    public string Id => "remove-from-distribution-lists";
    public string Title => "Remove from distribution lists";
    public StepSurface Surface => StepSurface.Exchange;
    public Capability? RequiredCapability => Capabilities.ManageMailboxes;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        if (!_exchange.IsAvailable) return StepPreview.Blocked(Id, Title, ExchangeStep.NotGranted);

        // Live, like the cloud group step: a list joined since the last delta must not be missed.
        var page = await _graph.GetPageAsync($"users/{ctx.UserId}/memberOf?$select={GraphSelects.MemberOf}&$top=999", ct).ConfigureAwait(false);
        var lists = page.Items
            .Where(e => e.GetString("@odata.type")?.Contains("group", StringComparison.OrdinalIgnoreCase) != false)
            .Select(DirectoryGroup.FromJson)
            .Where(g => g.Kind == GroupKind.DistributionOrMailSecurity)
            .ToList();

        var removable = lists.Where(g => !g.OnPremisesSyncEnabled).ToList();
        var synced = lists.Where(g => g.OnPremisesSyncEnabled).Select(g => $"{Label(g)} — synced from AD; the AD group step removes it").ToList();
        ctx.Set(PlanKey, removable);

        if (removable.Count == 0)
        {
            return StepPreview.NoOp(Id, lists.Count == 0 ? "Not in any distribution list" : "Every list they're in is synced from AD") with { Details = synced };
        }

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Remove from {ExchangeStep.Plural(removable.Count, "distribution list")}",
            Details = [.. removable.Select(Label), .. synced],
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var m = ExchangeStep.Ref(ctx);
        var writes = new List<(string, WriteOutcome)>();
        foreach (var g in ctx.Get<List<DirectoryGroup>>(PlanKey) ?? [])
        {
            ct.ThrowIfCancellationRequested();
            writes.Add((Label(g), await _exchange.SetDistributionGroupMemberAsync(m, g.Id, Label(g), member: false, ct).ConfigureAwait(false)));
        }

        return ExchangeStep.Combined(Id, n => $"Removed from {ExchangeStep.Plural(n, "distribution list")}", "lists", writes);
    }

    private static string Label(DirectoryGroup g) => g.DisplayName ?? g.Mail ?? g.Id;
}
