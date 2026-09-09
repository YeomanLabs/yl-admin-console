using YeomanLabs.AdminConsole.Core.Auth;

namespace YeomanLabs.AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// A catalogued step that cannot run yet (doc 06: steps 9, 10–14, 15, 18–19). It is registered rather than omitted so
/// the dialog shows it greyed with its reason — an admin who cannot see a step has no way to know it exists.
/// <c>ExecuteAsync</c> throws, and the runner never reaches it because the preview always blocks.
/// </summary>
public sealed class NotInThisPhaseStep : IOffboardingStep
{
    public NotInThisPhaseStep(string id, string title, StepSurface surface, string reason, bool defaultEnabled = false)
    {
        Id = id;
        Title = title;
        Surface = surface;
        Reason = reason;
        DefaultEnabled = defaultEnabled;
    }

    public string Id { get; }
    public string Title { get; }
    public StepSurface Surface { get; }

    /// <summary>The UX §6 caption: why this cannot run, in words an admin can act on.</summary>
    public string Reason { get; }

    public Capability? RequiredCapability => null;
    public bool DefaultEnabled { get; }
    public bool HaltsOnFailure => false;

    public Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct) =>
        Task.FromResult(StepPreview.Blocked(Id, Title, Reason));

    public Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct) =>
        throw new NotSupportedException($"{Id} cannot run in this phase: {Reason}");
}
