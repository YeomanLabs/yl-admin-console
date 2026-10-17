using System.Globalization;
using System.Text.RegularExpressions;

namespace AdminConsole.Core.Exchange;

/// <summary>
/// A size as Exchange reports it: <c>"82.63 MB (86,646,374 bytes)"</c>, <c>"Unlimited"</c>, or nothing. The rounded
/// figure in front is for people; the byte count in brackets is the value (doc 13 §1).
/// </summary>
public readonly partial record struct ExchangeSize(long? Bytes, bool IsUnlimited)
{
    public static ExchangeSize Unknown { get; } = new(null, false);

    public static ExchangeSize Unlimited { get; } = new(null, true);

    public bool IsKnown => Bytes is not null || IsUnlimited;

    public static ExchangeSize Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Unknown;
        var trimmed = text.Trim();
        if (trimmed.Equals("Unlimited", StringComparison.OrdinalIgnoreCase)) return Unlimited;

        var match = BytesInBrackets().Match(trimmed);
        var digits = match.Success ? match.Groups[1].Value : trimmed;

        // The separator follows the server's culture, which is not ours to assume, so keep digits only.
        var onlyDigits = new string(digits.Where(char.IsAsciiDigit).ToArray());
        if (onlyDigits.Length == 0 || (!match.Success && onlyDigits.Length != digits.Length)) return Unknown;
        return long.TryParse(onlyDigits, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            ? new ExchangeSize(bytes, false)
            : Unknown;
    }

    /// <summary>Binary units, as Exchange itself rounds them, so the figure matches what the admin center shows.</summary>
    public string ToDisplay()
    {
        if (IsUnlimited) return "Unlimited";
        if (Bytes is not { } bytes) return "Not reported";
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} bytes"
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.##} {units[unit]}");
    }

    /// <summary>Share of <paramref name="limit"/> used, or null when either side is unknown or the limit is unlimited.</summary>
    public double? FractionOf(ExchangeSize limit) =>
        Bytes is { } used && limit.Bytes is { } max && max > 0 ? (double)used / max : null;

    [GeneratedRegex(@"\(([\d,.\s  ']+)\s*bytes\)", RegexOptions.IgnoreCase)]
    private static partial Regex BytesInBrackets();
}
