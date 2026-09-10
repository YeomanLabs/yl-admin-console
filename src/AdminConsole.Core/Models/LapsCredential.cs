using System.Text;
using System.Text.Json;

namespace AdminConsole.Core.Models;

/// <summary>
/// One local administrator credential backed up by Windows LAPS (doc 04 §Security). Held in memory for the reveal only:
/// never cached, never logged, and the password is deliberately not part of the record's printed form.
/// </summary>
public sealed record LapsCredential
{
    public required string AccountName { get; init; }
    public required string Password { get; init; }
    public DateTimeOffset? BackupDateTime { get; init; }

    /// <summary>When the device is due to rotate this password; null when the tenant does not report it.</summary>
    public DateTimeOffset? RefreshDateTime { get; init; }

    public static LapsCredential FromJson(JsonElement e) => new()
    {
        AccountName = e.GetString("accountName") ?? "Administrator",
        Password = Decode(e.GetString("passwordBase64")),
        BackupDateTime = e.GetDate("backupDateTime"),
        RefreshDateTime = e.GetDate("refreshDateTime"),
    };

    /// <summary>
    /// Graph returns the password base64-encoded (doc 04). It is base64 of the UTF-8 bytes, not UTF-16; a value that
    /// is not valid base64 is passed through unchanged rather than throwing, so a tenant quirk cannot break the reveal.
    /// </summary>
    public static string Decode(string? passwordBase64)
    {
        if (string.IsNullOrEmpty(passwordBase64)) return string.Empty;
        Span<byte> buffer = new byte[passwordBase64.Length];
        if (!Convert.TryFromBase64String(passwordBase64, buffer, out var written)) return passwordBase64;
        return Encoding.UTF8.GetString(buffer[..written]);
    }

    /// <summary>Keeps the password out of any accidental string interpolation of the record.</summary>
    public override string ToString() => $"LapsCredential {{ AccountName = {AccountName}, Password = *** }}";
}
