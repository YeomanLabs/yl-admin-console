using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Models;
using YeomanLabs.AdminConsole.Core.Users;
using YeomanLabs.AdminConsole.Core.Writes;

namespace YeomanLabs.AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06 step 17. Stamps the account so a later reader can see it was offboarded rather than merely disabled. Every
/// attribute here is mastered on-prem for a synced account, so the whole step is blocked in that case.
/// </summary>
public sealed class UpdateAttributesStep : IOffboardingStep
{
    /// <summary>Doc 06's defaults. A profile can override them through <see cref="OffboardingContext.AttributeStamps"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string?> Defaults = new Dictionary<string, string?>
    {
        ["employeeType"] = "Offboarded",
        ["officeLocation"] = string.Empty,
        ["department"] = string.Empty,
    };

    private readonly UserService _users;

    public UpdateAttributesStep(UserService users) => _users = users;

    public string Id => "update-attributes";
    public string Title => "Stamp offboarding attributes";
    public StepSurface Surface => StepSurface.Graph;
    public Capability? RequiredCapability => Capabilities.EditUser;
    public bool DefaultEnabled => false;
    public bool HaltsOnFailure => false;

    public Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var stamps = Stamps(ctx);
        var blocked = stamps.Keys.Where(ctx.User.IsReadOnly).ToList();
        if (blocked.Count == stamps.Count)
        {
            return Task.FromResult(StepPreview.Blocked(Id, "Stamp offboarding attributes", DirectoryUser.HybridReadOnlyReason));
        }

        var writable = stamps.Where(s => !ctx.User.IsReadOnly(s.Key)).ToList();
        return Task.FromResult(new StepPreview
        {
            StepId = Id,
            Summary = $"Set {writable.Count} attribute{(writable.Count == 1 ? string.Empty : "s")}: {string.Join(", ", writable.Select(Describe))}",
            Details =
            [
                .. writable.Select(Describe),
                .. blocked.Select(b => $"{UserService.FriendlyProperty(b)} — {DirectoryUser.HybridReadOnlyReason}"),
            ],
        });
    }

    public async Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct)
    {
        var stamps = Stamps(ctx).Where(s => !ctx.User.IsReadOnly(s.Key)).ToList();
        var done = new List<string>();
        var failed = new List<string>();
        var undos = new List<Func<CancellationToken, Task<WriteOutcome>>>();

        foreach (var (property, value) in stamps)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await _users.UpdateAsync(ctx.UserId, property, value, ct).ConfigureAwait(false);
            if (outcome.Success)
            {
                done.Add(UserService.FriendlyProperty(property));
                if (outcome.Undo is { } undo) undos.Add(undo);
            }
            else
            {
                failed.Add($"{UserService.FriendlyProperty(property)} ({outcome.FriendlyError})");
            }
        }

        Func<CancellationToken, Task<WriteOutcome>>? undoAll = undos.Count == 0
            ? null
            : async token =>
            {
                var back = 0;
                foreach (var undo in undos)
                {
                    var outcome = await undo(token).ConfigureAwait(false);
                    if (outcome.Success) back++;
                }

                return new WriteOutcome
                {
                    Success = back == undos.Count,
                    Summary = $"Restored {back} of {undos.Count} attribute{(undos.Count == 1 ? string.Empty : "s")}",
                    FriendlyError = back == undos.Count ? null : "Some attributes could not be restored.",
                };
            };

        if (done.Count == 0 && failed.Count > 0) return StepResult.Failed(Id, $"Could not stamp any attribute: {string.Join("; ", failed)}");

        var message = $"Stamped {done.Count} attribute{(done.Count == 1 ? string.Empty : "s")}: {string.Join(", ", done)}";
        if (failed.Count > 0) return StepResult.Failed(Id, $"{message}, but {failed.Count} failed: {string.Join("; ", failed)}") with { Undo = undoAll };
        return StepResult.Success(Id, message, undoAll);
    }

    private static IReadOnlyDictionary<string, string?> Stamps(OffboardingContext ctx) =>
        ctx.AttributeStamps.Count > 0 ? ctx.AttributeStamps : Defaults;

    private static string Describe(KeyValuePair<string, string?> stamp) =>
        string.IsNullOrEmpty(stamp.Value)
            ? $"clear {UserService.FriendlyProperty(stamp.Key)}"
            : $"{UserService.FriendlyProperty(stamp.Key)} = {stamp.Value}";
}
