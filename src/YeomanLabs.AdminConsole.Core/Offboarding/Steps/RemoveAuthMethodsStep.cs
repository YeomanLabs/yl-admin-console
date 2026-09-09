using System.Text.Json;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 4. Deletes every method Graph allows, so a leaver cannot satisfy MFA with a phone or an authenticator
/// they still hold. The password method cannot be deleted and is left alone; step 3 dealt with it.
/// </summary>
public sealed class RemoveAuthMethodsStep : IOffboardingStep
{
    private const string ScratchKey = "auth-methods";

    private readonly UserService _users;
    private readonly GraphClient _graph;

    public RemoveAuthMethodsStep(UserService users, GraphClient graph)
    {
        _users = users;
        _graph = graph;
    }

    public string Id => "remove-auth-methods";
    public string Title => "Remove authentication methods";
    public StepSurface Surface => StepSurface.Graph;
    public Capability? RequiredCapability => Capabilities.DeleteAuthMethod;
    public bool DefaultEnabled => true;
    public bool HaltsOnFailure => false;

    public async Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var methods = await ReadAsync(ctx.UserId, ct).ConfigureAwait(false);
        var deletable = methods.Where(m => m.CanDelete).ToList();
        ctx.Set(ScratchKey, deletable);

        if (deletable.Count == 0)
        {
            return StepPreview.NoOp(Id, "No removable authentication methods are registered");
        }

        return new StepPreview
        {
            StepId = Id,
            Summary = $"Remove {deletable.Count} authentication method{(deletable.Count == 1 ? string.Empty : "s")}",
            Details = deletable.Select(Describe).ToList(),
        };
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        // The preview already read them; re-reading would double the calls and could disagree with what was shown.
        var methods = ctx.Get<List<AuthMethod>>(ScratchKey) ?? (await ReadAsync(ctx.UserId, ct).ConfigureAwait(false)).Where(m => m.CanDelete).ToList();
        var removed = new List<string>();
        var failed = new List<string>();

        foreach (var method in methods)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await _users.DeleteAuthMethodAsync(ctx.UserId, method, ct).ConfigureAwait(false);
            if (outcome.Success) removed.Add(method.KindLabel);
            else failed.Add($"{method.KindLabel} ({outcome.FriendlyError})");
        }

        if (failed.Count == 0) return StepResult.Success(Id, $"Removed {removed.Count} method{(removed.Count == 1 ? string.Empty : "s")}: {string.Join(", ", removed)}");
        if (removed.Count == 0) return StepResult.Failed(Id, $"Could not remove any method: {string.Join("; ", failed)}");
        return StepResult.Failed(Id, $"Removed {removed.Count}, but {failed.Count} failed: {string.Join("; ", failed)}");
    }

    private async Task<IReadOnlyList<AuthMethod>> ReadAsync(string userId, CancellationToken ct)
    {
        var page = await _graph.GetPageAsync($"users/{userId}/authentication/methods", ct).ConfigureAwait(false);
        return page.Items.Select(AuthMethod.FromJson).ToList();
    }

    private static string Describe(AuthMethod m) =>
        m.Detail is null ? $"{m.KindLabel} ({m.DisplayName})" : $"{m.KindLabel} ({m.DisplayName} · {m.Detail})";
}
