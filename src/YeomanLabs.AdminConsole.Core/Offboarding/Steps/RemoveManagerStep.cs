using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 5. Off by default: the manager link is often what the next person needs in order to work out who owned
/// what. On a synced account the manager is mastered on-prem, so the step is blocked rather than attempted.
/// </summary>
public sealed class RemoveManagerStep : IOffboardingStep
{
    private const string ScratchKey = "manager";

    private readonly GraphClient _graph;
    private readonly ActionLog _actions;
    private readonly TenantContext _tenant;

    public RemoveManagerStep(GraphClient graph, ActionLog actions, TenantContext tenant)
    {
        _graph = graph;
        _actions = actions;
        _tenant = tenant;
    }

    public string Id => "remove-manager";
    public string Title => "Clear the manager";
    public StepSurface Surface => StepSurface.Graph;
    public Capability? RequiredCapability => Capabilities.EditUser;
    public bool DefaultEnabled => false;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        if (ctx.User.IsReadOnly("manager"))
        {
            return StepPreview.Blocked(Id, "Clear the manager", DirectoryUser.HybridReadOnlyReason);
        }

        var manager = await ReadManagerAsync(ctx.UserId, ct).ConfigureAwait(false);
        if (manager is null) return StepPreview.NoOp(Id, "No manager is set");

        ctx.Set(ScratchKey, manager);
        return new StepPreview { StepId = Id, Summary = $"Clear the manager ({manager.Label})" };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var manager = ctx.Get<PersonRef>(ScratchKey) ?? await ReadManagerAsync(ctx.UserId, ct).ConfigureAwait(false);
        if (manager is null) return StepResult.Skipped(Id, "No manager is set");

        await _graph.SendAsync(HttpMethod.Delete, $"users/{ctx.UserId}/manager/$ref", null, ct).ConfigureAwait(false);
        var actionId = Record(ctx, "user.manager.remove", $"DELETE manager/$ref ({manager.Id})", ActionOutcome.Success, $"Manager cleared (was {manager.Label})", "user.manager.set");

        return StepResult.Success(Id, $"Manager cleared (was {manager.Label})", async token =>
        {
            var body = new Dictionary<string, object?> { ["@odata.id"] = $"{GraphClient.V1BaseAddress}directoryObjects/{manager.Id}" };
            await _graph.SendAsync(HttpMethod.Put, $"users/{ctx.UserId}/manager/$ref", body, token).ConfigureAwait(false);
            Record(ctx, "user.manager.set", $"PUT manager/$ref ({manager.Id})", ActionOutcome.Success, $"Manager restored to {manager.Label}");
            _actions.MarkUndone(actionId);
            return new Writes.WriteOutcome { Success = true, Summary = $"Manager restored to {manager.Label}" };
        });
    }

    private async Task<PersonRef?> ReadManagerAsync(string userId, CancellationToken ct)
    {
        try
        {
            using var doc = await _graph.SendAsync(HttpMethod.Get, $"users/{userId}/manager?$select={GraphSelects.PersonRef}", null, ct).ConfigureAwait(false);
            return doc is null ? null : PersonRef.FromJson(doc.RootElement);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Graph answers 404 rather than an empty body when nobody is assigned.
            return null;
        }
    }

    private long Record(OffboardingContext ctx, string action, string request, ActionOutcome outcome, string message, string? inverseAction = null) =>
        _actions.Record(new ActionRecord
        {
            Actor = _tenant.SignedInUpn,
            TenantId = _tenant.TenantId,
            TargetType = ObjectType.User,
            TargetId = ctx.UserId,
            TargetName = ctx.UserLabel,
            Action = action,
            RequestSummary = request,
            Outcome = outcome,
            Message = message,
            InverseAction = inverseAction,
            InversePayload = inverseAction is null ? null : "{}",
        });
}
