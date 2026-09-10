using AdminConsole.App.Services;
using AdminConsole.Core.Cache.Browse;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>
/// A device as one row of the Devices list. An Entra-only device is missing most of this, and the row says so rather
/// than drawing blanks: "Not in Intune" is the answer to why there is no serial and no compliance state.
/// </summary>
public sealed class DeviceRow(DeviceListRow row)
{
    public DeviceListRow Model { get; } = row;

    public string Id => Model.Id;
    public string Primary => Model.Primary;

    /// <summary>Serial and model, or the reason there are none.</summary>
    public string SubtitleText => Model.Source == DeviceSource.Entra
        ? "Registered in Entra only"
        : string.Join(" · ", new[] { Model.Serial, Model.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string OwnerText => string.IsNullOrWhiteSpace(Model.PrimaryUserName) ? "No primary user" : Model.PrimaryUserName!;

    /// <summary>Entra answers "Unknown" for a device it holds no OS for. Repeating it twice reads like a bug, so an
    /// unknown becomes one honest phrase.</summary>
    public string OsText
    {
        get
        {
            var parts = new[] { Model.Os, Model.OsVersion }
                .Where(s => !string.IsNullOrWhiteSpace(s) && !string.Equals(s, "Unknown", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return parts.Length == 0 ? "Operating system not reported" : string.Join(" ", parts);
        }
    }

    public string LastSeenText => Model.LastActivity is null ? "Never seen" : TimeText.Relative(Model.LastActivity);

    public bool IsNonCompliant => Model.Compliance is "noncompliant";

    public bool IsEntraOnly => Model.Source == DeviceSource.Entra;

    /// <summary>Only claimed for a device Intune has actually reported on — an unknown is not a "no".</summary>
    public bool IsUnencrypted => Model.Encrypted == false;
}
