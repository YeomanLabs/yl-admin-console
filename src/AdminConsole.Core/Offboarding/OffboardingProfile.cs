using System.Text.Json.Serialization;

namespace AdminConsole.Core.Offboarding;

/// <summary>
/// An offboarding template (doc 06 §Offboarding templates; "profile" in the file format and the code, which predate the
/// Templates page): which steps run, and the options they run with. Steps are keyed by their stable id, and an id the
/// template does not mention falls back to the step's own default — so a template written today still works when a
/// step is added later.
/// </summary>
public sealed record OffboardingProfile
{
    public const string StandardLeaverName = "Standard leaver";

    public required string Name { get; init; }

    /// <summary>When to use it, in the admin's words ("Contractors: keep the mailbox, no licence removal").</summary>
    public string? Description { get; init; }

    /// <summary>The offboard dialog opens with this template. At most one saved template has it; none means the built-in.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Step id to on/off. Ids not listed take the step's <c>DefaultEnabled</c>.</summary>
    public IReadOnlyDictionary<string, bool> Steps { get; init; } = new Dictionary<string, bool>();

    public OffboardingOptions Options { get; init; } = new();

    /// <summary>File name this profile saves to; null for the built-in default, which is never written to disk.</summary>
    [JsonIgnore]
    public string? FileName { get; init; }

    [JsonIgnore]
    public bool IsBuiltIn => FileName is null;

    /// <summary>The name: what a screen reader says for it in a drop-down, rather than the record's every field.</summary>
    public override string ToString() => Name;

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

    /// <summary>
    /// What stops this template being saved, in words an admin can act on; empty when it is fine. <paramref name="others"/>
    /// are the templates already on this machine (the built-in included), so a second "Contractor" cannot shadow the first.
    /// </summary>
    public IReadOnlyList<string> Problems(IEnumerable<OffboardingProfile> others)
    {
        var problems = new List<string>();
        var name = Name.Trim();
        if (name.Length == 0)
        {
            problems.Add("Give the template a name.");
        }
        else if (others.Any(o => !IsSameTemplate(o) && string.Equals(o.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add(string.Equals(name, StandardLeaverName, StringComparison.OrdinalIgnoreCase)
                ? $"“{StandardLeaverName}” is the built-in template. Choose another name."
                : $"A template called “{name}” already exists.");
        }

        var unknownStamps = Options.AttributeStamps.Keys.Where(k => !StampableAttributes.Contains(k, StringComparer.Ordinal)).ToList();
        if (unknownStamps.Count > 0)
        {
            // The snapshot records only these, so a stamp on anything else could never be put back by Restore.
            problems.Add($"Only these attributes can be stamped, because only they are snapshotted for Restore: {string.Join(", ", StampableAttributes)}. Not: {string.Join(", ", unknownStamps)}.");
        }

        if (AutoReplyText.UnknownTokens(Options.AutoReplyMessage) is { Count: > 0 } unknown)
        {
            problems.Add($"The automatic reply has {string.Join(", ", unknown)}, which is not a placeholder. Use {string.Join(", ", AutoReplyText.Tokens.Select(t => t.Token))}.");
        }

        return problems;
    }

    /// <summary>The saved copy of this template, which is not a clash with itself.</summary>
    private bool IsSameTemplate(OffboardingProfile other) =>
        ReferenceEquals(other, this) || (other.FileName is not null && other.FileName == FileName);

    /// <summary>The attributes the stamp step may write: exactly those the snapshot records, so Restore can undo them.</summary>
    public static IReadOnlyList<string> StampableAttributes => Snapshot.SnapshotService.StampedAttributes;

    /// <summary>The profile that describes what is ticked right now, ready to save under a new name.</summary>
    public static OffboardingProfile From(string name, IEnumerable<IOffboardingStep> steps, IReadOnlyCollection<string> enabledIds, OffboardingOptions options) => new()
    {
        Name = name,
        Steps = steps.ToDictionary(s => s.Id, s => enabledIds.Contains(s.Id), StringComparer.Ordinal),
        Options = options,
    };
}

/// <summary>Run options a template remembers (doc 06 §Offboarding templates).</summary>
public sealed record OffboardingOptions
{
    /// <summary>Pre-select the leaver's own manager as the delegate when the dialog opens.</summary>
    public bool DelegateToManager { get; init; } = true;

    /// <summary>Overrides for the attribute-stamp step; empty uses doc 06's defaults.</summary>
    public IReadOnlyDictionary<string, string?> AttributeStamps { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// The automatic reply's text (plain, not HTML), with <see cref="AutoReplyText"/>'s placeholders; null uses the
    /// standard text, which names the delegate.
    /// </summary>
    public string? AutoReplyMessage { get; init; }

    /// <summary>A folder on this machine the OneDrive export step writes to (M16.4); null leaves the step blocked.</summary>
    public string? ExportFolder { get; init; }
}
