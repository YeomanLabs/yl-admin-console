using System.Text.Json.Serialization;

namespace AdminConsole.Core.Exchange;

/// <summary>
/// A multi-valued parameter changed in place: PowerShell's <c>@{Add="…"; Remove="…"}</c>. Sending a plain list instead
/// would replace the whole value, which on <c>EmailAddresses</c> or <c>GrantSendOnBehalfTo</c> silently drops every
/// entry nobody mentioned.
/// </summary>
public sealed class ExchangeMultiValue
{
    /// <summary>The annotation the endpoint needs to treat the object as a hashtable rather than a value.</summary>
    [JsonPropertyName("@odata.type")]
    [JsonPropertyOrder(-1)]
    public string ODataType => "#Exchange.GenericHashTable";

    [JsonPropertyName("add")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Add { get; init; }

    [JsonPropertyName("remove")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Remove { get; init; }

    public static ExchangeMultiValue Adding(params string[] values) => new() { Add = values };

    public static ExchangeMultiValue Removing(params string[] values) => new() { Remove = values };
}
