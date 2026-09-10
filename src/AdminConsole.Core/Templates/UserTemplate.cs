using System.Text.Json.Serialization;

namespace AdminConsole.Core.Templates;

/// <summary>
/// A role snapshot (doc 07): the attributes, licences and groups that define "Sales rep — Madison", with rules for the
/// person-specific fields. Created from an existing user or edited by hand.
/// </summary>
public sealed record UserTemplate
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Name { get; init; }

    /// <summary>The user this was snapshotted from, kept so an admin can see where a template came from.</summary>
    public TemplateSource? CreatedFrom { get; init; }

    /// <summary>Graph property name to value. Only the doc 07 allow-list is ever snapshotted.</summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; init; } = new Dictionary<string, object?>();

    public string? ManagerId { get; init; }

    /// <summary>Display name at snapshot time, so the picker can show who without a lookup.</summary>
    public string? ManagerDisplayName { get; init; }

    public IReadOnlyList<TemplateLicense> Licenses { get; init; } = [];

    public IReadOnlyList<TemplateGroup> Groups { get; init; } = [];

    public string UpnRule { get; init; } = "{first}.{last}@{domain}";

    public string MailNicknameRule { get; init; } = "{first}.{last}";

    public string DisplayNameRule { get; init; } = "{First} {Last}";

    public PasswordPolicy PasswordPolicy { get; init; } = new();

    /// <summary>What the snapshot deliberately left out, in words, so nobody wonders where a group went.</summary>
    public IReadOnlyList<string> ExcludedOnSnapshot { get; init; } = [];

    /// <summary>File name under <c>templates\users</c>; null for a template that has not been saved yet.</summary>
    [JsonIgnore]
    public string? FileName { get; init; }

    [JsonIgnore]
    public bool IsSaved => FileName is not null;

    public DateTimeOffset? LastUsed { get; init; }
}

/// <summary>Where a template was snapshotted from (doc 07 <c>createdFrom</c>).</summary>
public sealed record TemplateSource
{
    public required string UserId { get; init; }
    public string? Upn { get; init; }
    public DateTimeOffset At { get; init; }
}

/// <summary>One licence the template assigns. The part number is kept so the file stays readable after a SKU renames.</summary>
public sealed record TemplateLicense
{
    public required string SkuId { get; init; }
    public string? SkuPartNumber { get; init; }
    public IReadOnlyList<string> DisabledPlans { get; init; } = [];
}

/// <summary>One group the template joins. The display name is a convenience for the file and the preview.</summary>
public sealed record TemplateGroup
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
}

/// <summary>Doc 07 §Password generation. Sixteen characters by default, changed on first sign-in.</summary>
public sealed record PasswordPolicy
{
    public int Length { get; init; } = 16;
    public bool ForceChangeOnFirstSignIn { get; init; } = true;
}
