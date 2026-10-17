using System.Text.Json;

namespace AdminConsole.Core.Exchange;

/// <summary>What one cmdlet returned: every row across every page, and anything Exchange said as a warning.</summary>
public sealed record ExchangeResult
{
    public static ExchangeResult Empty { get; } = new();

    /// <summary>The <c>value</c> rows, cloned so they outlive the response. Empty for a <c>Set-*</c> that succeeded.</summary>
    public IReadOnlyList<JsonElement> Items { get; init; } = [];

    /// <summary>
    /// <c>@adminapi.warnings</c>: what PowerShell would have printed in yellow. A write can succeed and still warn, and
    /// the warning is often the part an admin needs (doc 13 §1).
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public JsonElement? First => Items.Count > 0 ? Items[0] : null;
}
