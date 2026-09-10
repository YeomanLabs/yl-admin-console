namespace AdminConsole.Core.Cache.Browse;

/// <summary>Which directory a device row came from. An Entra-only device is registered but not Intune-enrolled.</summary>
public enum DeviceSource
{
    Intune,
    Entra,
}
