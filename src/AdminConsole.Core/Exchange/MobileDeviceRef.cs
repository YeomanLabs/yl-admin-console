namespace AdminConsole.Core.Exchange;

/// <summary>A phone or tablet syncing a mailbox through ActiveSync or the Outlook app (<c>Get-MobileDevice</c>).</summary>
public sealed record MobileDeviceRef
{
    public required string Identity { get; init; }
    public string? Model { get; init; }
    public string? FriendlyName { get; init; }
    public string? ClientType { get; init; }
    public string? DeviceOs { get; init; }
    public DateTimeOffset? FirstSync { get; init; }

    public string Display =>
        string.Join(" · ", new[] { FriendlyName, Model, DeviceOs }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } d ? d : Identity;
}
