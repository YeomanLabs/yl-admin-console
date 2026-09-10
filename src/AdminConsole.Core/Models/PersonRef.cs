using System.Text.Json;

namespace AdminConsole.Core.Models;

/// <summary>A user reference from <c>/manager</c>, <c>/directReports</c>, <c>/members</c>, <c>/owners</c> (doc 04 <c>PersonRef</c> select).</summary>
public sealed record PersonRef(string Id, string? DisplayName, string? UserPrincipalName, string? ODataType = null)
{
    public string Label => DisplayName ?? UserPrincipalName ?? Id;

    public bool IsUser => ODataType is null || ODataType.EndsWith(".user", StringComparison.OrdinalIgnoreCase);

    /// <summary>"user", "group", "device", "servicePrincipal"… from <c>@odata.type</c>; "user" when absent.</summary>
    public string Kind => ODataType is null ? "user" : ODataType[(ODataType.LastIndexOf('.') + 1)..];

    public static PersonRef FromJson(JsonElement e) => new(
        e.GetString("id") ?? throw new JsonException("reference without id"),
        e.GetString("displayName"),
        e.GetString("userPrincipalName"),
        e.GetString("@odata.type"));
}
