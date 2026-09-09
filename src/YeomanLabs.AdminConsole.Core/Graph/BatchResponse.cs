using System.Text.Json;

namespace YeomanLabs.AdminConsole.Core.Graph;

/// <summary>
/// One inner response of a <c>$batch</c>. Non-success statuses are returned, not thrown, because callers
/// routinely expect some (e.g. 404 on <c>/manager</c> means "no manager"). Call <see cref="EnsureSuccess"/> otherwise.
/// </summary>
public sealed record BatchResponse
{
    public required string Id { get; init; }
    public required int Status { get; init; }

    /// <summary>Cloned body, or null for empty responses.</summary>
    public JsonElement? Body { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public bool IsSuccess => Status is >= 200 and < 300;

    /// <summary>Returns <see cref="Body"/> or throws a <see cref="GraphException"/> built from it.</summary>
    public JsonElement? EnsureSuccess() =>
        IsSuccess ? Body : throw GraphException.FromBatchResponse(Status, Body, Headers);
}
