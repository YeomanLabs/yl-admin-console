using System.Text.Json.Serialization;

namespace YeomanLabs.AdminConsole.Core.Offboarding;

/// <summary>
/// A saved set of step toggles (doc 06 §Profiles). Steps are keyed by their stable id, and an id the profile does not
/// mention falls back to the step's own default — so a profile written today still works when a step is added later.
/// </summary>
public sealed record OffboardingProfile
{
    public const string StandardLeaverName = "Standard leaver";

    public required string Name { get; init; }

    /// <summary>Step id to on/off. Ids not listed take the step's <c>DefaultEnabled</c>.</summary>
    public IReadOnlyDictionary<string, bool> Steps { get; init; } = new Dictionary<string, bool>();

    public OffboardingOptions Options { get; init; } = new();

    /// <summary>File name this profile saves to; null for the built-in default, which is never written to disk.</summary>
    [JsonIgnore]
    public string? FileName { get; init; }

    [JsonIgnore]
    public bool IsBuiltIn => FileName is null;

    /// <summary>Whether the step runs under this profile.</summary>
    public bool IsEnabled(IOffboardingStep step) => Steps.TryGetValue(step.Id, out var on) ? on : step.DefaultEnabled;

    public IReadOnlyList<string> EnabledIds(IEnumerable<IOffboardingStep> steps) =>
        steps.Where(IsEnabled).Select(s => s.Id).ToList();

    /// <summary>Doc 06's shipped default: every step the catalog turns on, and nothing else.</summary>
    public static OffboardingProfile StandardLeaver(IEnumerable<IOffboardingStep> steps) => new()
    {
        Name = StandardLeaverName,
        Steps = steps.ToDictionary(s => s.Id, s => s.DefaultEnabled, StringComparer.Ordinal),
    };

    /// <summary>The profile that describes what is ticked right now, ready to save under a new name.</summary>
    public static OffboardingProfile From(string name, IEnumerable<IOffboardingStep> steps, IReadOnlyCollection<string> enabledIds, OffboardingOptions options) => new()
    {
        Name = name,
        Steps = steps.ToDictionary(s => s.Id, s => enabledIds.Contains(s.Id), StringComparer.Ordinal),
        Options = options,
    };
}

/// <summary>Run options a profile remembers (doc 06 §Profiles).</summary>
public sealed record OffboardingOptions
{
    /// <summary>Pre-select the leaver's own manager as the delegate when the dialog opens.</summary>
    public bool DelegateToManager { get; init; } = true;

    /// <summary>Overrides for the attribute-stamp step; empty uses doc 06's defaults.</summary>
    public IReadOnlyDictionary<string, string?> AttributeStamps { get; init; } = new Dictionary<string, string?>();
}
