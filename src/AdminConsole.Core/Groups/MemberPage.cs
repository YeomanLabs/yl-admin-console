using AdminConsole.Core.Models;

namespace AdminConsole.Core.Groups;

/// <summary>One page of group members (doc 04: <c>/groups/{id}/members?$top=999</c>, fetched on demand). <see cref="NextLink"/> drives "Load more".</summary>
public sealed record MemberPage
{
    public required IReadOnlyList<PersonRef> Members { get; init; }
    public string? NextLink { get; init; }
    public bool HasMore => NextLink is not null;
}
