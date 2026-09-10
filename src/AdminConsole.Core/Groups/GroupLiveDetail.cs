using AdminConsole.Core.Models;

namespace AdminConsole.Core.Groups;

/// <summary>What the group page hydrates live (UX §4.5, doc 04 §Groups): the group itself and its owners. Members are paged separately.</summary>
public sealed record GroupLiveDetail
{
    public required string GroupId { get; init; }

    public SectionResult<DirectoryGroup> Group { get; init; } = SectionResult<DirectoryGroup>.Pending;

    public SectionResult<IReadOnlyList<PersonRef>> Owners { get; init; } = SectionResult<IReadOnlyList<PersonRef>>.Pending;

    public double ElapsedMilliseconds { get; init; }
}
