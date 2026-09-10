using System.Text.Json;

namespace AdminConsole.Core.Models;

/// <summary>
/// One BitLocker recovery key (doc 04 §Security). Listing gives the id and volume type; the key itself only comes back
/// from a second, separately audited GET, so <see cref="Key"/> is null until the admin reveals it.
/// </summary>
public sealed record BitLockerKeyRef
{
    public required string Id { get; init; }
    public string? VolumeType { get; init; }
    public DateTimeOffset? CreatedDateTime { get; init; }

    /// <summary>The recovery password. Held in memory for the reveal only; never cached, never logged.</summary>
    public string? Key { get; init; }

    /// <summary>
    /// Graph serialises this enum as a string holding its numeric value ("1", "2", "3"), not the member name — verified
    /// against the dev tenant. Both forms are mapped so a future serialisation change cannot blank the label.
    /// </summary>
    public string VolumeLabel => VolumeType switch
    {
        "1" or "operatingSystemVolume" => "Operating system drive",
        "2" or "fixedDataVolume" => "Fixed data drive",
        "3" or "removableDataVolume" => "Removable drive",
        null or "0" or "unknown" => "Drive",
        _ => VolumeType,
    };

    public static BitLockerKeyRef FromJson(JsonElement e) => new()
    {
        Id = e.GetString("id") ?? throw new JsonException("bitlocker recovery key without id"),
        VolumeType = e.GetString("volumeType"),
        CreatedDateTime = e.GetDate("createdDateTime"),
        Key = e.GetString("key"),
    };
}
