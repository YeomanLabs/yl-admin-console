using System.Text.Json;

namespace AdminConsole.Core.Graph;

/// <summary>One page of a Graph collection. <see cref="Items"/> are cloned and outlive the response document.</summary>
public sealed record GraphPage
{
    public required IReadOnlyList<JsonElement> Items { get; init; }

    /// <summary><c>@odata.nextLink</c>, absolute; null on the last page.</summary>
    public string? NextLink { get; init; }

    /// <summary><c>@odata.deltaLink</c>, absolute; only present on the last page of a delta query.</summary>
    public string? DeltaLink { get; init; }
}
