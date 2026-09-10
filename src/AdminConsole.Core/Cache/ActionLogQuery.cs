using AdminConsole.Core.Models;

namespace AdminConsole.Core.Cache;

/// <summary>Filters for <see cref="ActionLog.Query"/>; every property is optional and ANDed. Results are newest first.</summary>
public sealed record ActionLogQuery
{
    public string? TargetId { get; init; }
    public ObjectType? TargetType { get; init; }
    public string? Actor { get; init; }

    /// <summary>Prefix match on <see cref="ActionRecord.Action"/>, e.g. <c>user.</c> or <c>license.assign</c>.</summary>
    public string? ActionPrefix { get; init; }

    public ActionOutcome? Outcome { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset? Since { get; init; }
    public DateTimeOffset? Until { get; init; }

    /// <summary>Only rows that can still be undone.</summary>
    public bool? Reversible { get; init; }

    public int Limit { get; init; } = 200;
}
