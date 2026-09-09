using System.Text.Json;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Licensing;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Writes;

namespace YeomanLabs.AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 7. Direct assignments only: a group-assigned licence goes when the group membership does, and saying
/// so in the preview is the difference between an admin trusting the count and wondering why one survived.
/// </summary>
public sealed class RemoveLicensesStep : IOffboardingStep
{
    private const string ScratchKey = "direct-skus";

    private readonly LicenseService _licenses;
    private readonly GraphClient _graph;

    public RemoveLicensesStep(LicenseService licenses, GraphClient graph)
    {
        _licenses = licenses;
        _graph = graph;
    }

    public string Id => "remove-licenses";
    public string Title => "Remove licences";
    public StepSurface Surface => StepSurface.Graph;
    public Capability? RequiredCapability => Capabilities.AssignLicense;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var states = await ReadStatesAsync(ctx.UserId, ct).ConfigureAwait(false);
        var direct = states.Where(s => !s.IsGroupAssigned).Select(s => s.SkuId).ToList();
        var inherited = states.Where(s => s.IsGroupAssigned).ToList();
        ctx.Set(ScratchKey, direct);

        if (direct.Count == 0)
        {
            var nothing = inherited.Count == 0
                ? StepPreview.NoOp(Id, "No licences are assigned")
                : StepPreview.NoOp(Id, $"No direct licences · {inherited.Count} inherited from a group, removed when the group step runs");
            return nothing with { Details = inherited.Select(s => $"{Name(s.SkuId)} — inherited from a group").ToList() };
        }

        var summary = $"Remove {direct.Count} licence{(direct.Count == 1 ? string.Empty : "s")}: {string.Join(", ", direct.Select(Name))}";
        if (inherited.Count > 0) summary += $" · {inherited.Count} inherited from a group (removed by the group step)";

        return new StepPreview
        {
            StepId = Id,
            Summary = summary,
            Details = [.. direct.Select(Name), .. inherited.Select(s => $"{Name(s.SkuId)} — inherited from a group")],
            Reason = "Mailboxes are kept for 30 days after the last licence is removed; restoring a licence within that window brings the mailbox back.",
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var direct = ctx.Get<List<string>>(ScratchKey) ?? [];
        var removed = new List<string>();
        var failed = new List<string>();

        foreach (var skuId in direct)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await _licenses.RemoveAsync(ctx.UserId, skuId, ct).ConfigureAwait(false);
            if (outcome.Success) removed.Add(skuId);
            else failed.Add($"{Name(skuId)} ({outcome.FriendlyError})");
        }

        Func<CancellationToken, Task<WriteOutcome>>? undo = removed.Count == 0
            ? null
            : async token =>
            {
                var back = 0;
                foreach (var skuId in removed)
                {
                    var outcome = await _licenses.AssignAsync(ctx.UserId, skuId, token).ConfigureAwait(false);
                    if (outcome.Success) back++;
                }

                return new WriteOutcome
                {
                    Success = back == removed.Count,
                    Summary = $"Restored {back} of {removed.Count} licence{(removed.Count == 1 ? string.Empty : "s")}",
                    FriendlyError = back == removed.Count ? null : "Some licences could not be restored — check the available seats.",
                };
            };

        if (removed.Count == 0 && failed.Count > 0) return StepResult.Failed(Id, $"Could not remove any licence: {string.Join("; ", failed)}");

        var message = $"Removed {removed.Count} licence{(removed.Count == 1 ? string.Empty : "s")}: {string.Join(", ", removed.Select(Name))}";
        if (failed.Count > 0) return StepResult.Failed(Id, $"{message}, but {failed.Count} failed: {string.Join("; ", failed)}") with { Undo = undo };
        return StepResult.Success(Id, message, undo);
    }

    /// <summary>
    /// Live, because <c>licenseAssignmentStates</c> is what says whether a licence is direct or inherited, and the
    /// cached projection only carries the assigned SKU ids.
    /// </summary>
    private async Task<IReadOnlyList<LicenseAssignmentState>> ReadStatesAsync(string userId, CancellationToken ct)
    {
        using var doc = await _graph.SendAsync(HttpMethod.Get, $"users/{userId}?$select=licenseAssignmentStates", null, ct).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("licenseAssignmentStates", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray().Select(LicenseAssignmentState.FromJson).ToList();
    }

    private string Name(string skuId) => _licenses.GetSku(skuId)?.DisplayName ?? skuId;
}
