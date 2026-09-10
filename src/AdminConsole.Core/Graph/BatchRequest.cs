namespace AdminConsole.Core.Graph;

/// <summary>One inner request of a <c>POST /$batch</c>. <see cref="Url"/> is relative to the version root, e.g. <c>/users/{id}/manager</c>.</summary>
public sealed record BatchRequest
{
    public required string Id { get; init; }
    public required HttpMethod Method { get; init; }
    public required string Url { get; init; }
    public object? Body { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public static BatchRequest Get(string id, string url) => new() { Id = id, Method = HttpMethod.Get, Url = url };

    /// <summary>The client adds the <c>Content-Type</c> Graph requires on an inner request that carries a body.</summary>
    public static BatchRequest Post(string id, string url, object body) =>
        new() { Id = id, Method = HttpMethod.Post, Url = url, Body = body };
}
