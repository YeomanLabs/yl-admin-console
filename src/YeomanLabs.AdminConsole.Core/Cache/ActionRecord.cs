using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.Core.Cache;

/// <summary>
/// One row of <c>actions.db</c> (doc 03 §Action log): every write this admin performed, with outcome and — when
/// reversible — the inverse payload the Undo toast replays. Never contains passwords or tokens (see <see cref="ActionLog.Redact"/>).
/// </summary>
public sealed record ActionRecord
{
    /// <summary>Assigned by the database on <see cref="ActionLog.Record"/>; 0 before that.</summary>
    public long Id { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    /// <summary>UPN of the signed-in admin.</summary>
    public required string Actor { get; init; }

    public required string TenantId { get; init; }

    public required ObjectType TargetType { get; init; }
    public required string TargetId { get; init; }

    /// <summary>Display name or UPN at the time of the action, so the Activity page reads well after the object is gone.</summary>
    public string? TargetName { get; init; }

    /// <summary>Stable dotted verb, e.g. <c>user.disable</c>, <c>user.patch</c>, <c>license.assign</c>, <c>group.member.remove</c>, <c>device.wipe</c>.</summary>
    public required string Action { get; init; }

    /// <summary>What was sent, in one line (or JSON), e.g. <c>PATCH accountEnabled=false</c>.</summary>
    public string? RequestSummary { get; init; }

    public required ActionOutcome Outcome { get; init; }

    /// <summary>Human-readable result or error text.</summary>
    public string? Message { get; init; }

    public string? GraphRequestId { get; init; }

    /// <summary>The action that reverses this one (e.g. <c>user.patch</c>), or null when irreversible.</summary>
    public string? InverseAction { get; init; }

    /// <summary>JSON body for <see cref="InverseAction"/>. Redacted on write.</summary>
    public string? InversePayload { get; init; }

    /// <summary>Groups the steps of one offboarding run or one multi-step create.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Set when the Undo toast (or Activity page) replayed the inverse.</summary>
    public DateTimeOffset? UndoneAt { get; init; }

    public bool IsReversible => InverseAction is not null && InversePayload is not null && UndoneAt is null && Outcome == ActionOutcome.Success;
}
