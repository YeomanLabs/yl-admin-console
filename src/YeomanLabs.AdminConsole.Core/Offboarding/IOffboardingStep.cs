using YeomanLabs.AdminConsole.Core.Auth;

namespace YeomanLabs.AdminConsole.Core.Offboarding;

/// <summary>
/// One step of an offboarding run (doc 06). Steps are ordered by the catalog, previewed in parallel, and executed in
/// order. A step never decides on its own whether it may run: it says so in the preview and the runner obeys.
/// </summary>
public interface IOffboardingStep
{
    /// <summary>Stable kebab-case id; saved profiles refer to it, so it must not change.</summary>
    string Id { get; }

    string Title { get; }

    /// <summary>Which system this step talks to. Anything but Graph is a later phase and previews as blocked.</summary>
    StepSurface Surface { get; }

    /// <summary>The permission this step needs, for the grey rule and incremental consent. Null needs nothing extra.</summary>
    Capability? RequiredCapability { get; }

    /// <summary>Whether the step is ticked when the dialog opens with no profile loaded.</summary>
    bool DefaultEnabled { get; }

    /// <summary>A failure here stops the run: later steps would be meaningless or unsafe.</summary>
    bool HaltsOnFailure { get; }

    /// <summary>
    /// Works out exactly what this step would change, naming the real objects. Must not write anything, and must not
    /// throw: a preview that cannot read Graph returns <see cref="StepPreview.Error"/>.
    /// </summary>
    Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct);

    /// <summary>
    /// Performs the step. Only ever called when the preview said <see cref="StepPreview.CanRun"/>, so an implementation
    /// that is not built yet may throw <see cref="NotSupportedException"/> here.
    /// </summary>
    Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct);
}
