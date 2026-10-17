namespace AdminConsole.Core.Exchange;

/// <summary>
/// Whether an email address is free (M14.1). Three answers, never two: "could not check" is not "free" — M7's rule, because
/// an address cannot be quietly taken back once it is on somebody's mailbox.
/// </summary>
public sealed record AddressCheck
{
    public required AddressAvailability Availability { get; init; }

    /// <summary>Who holds it, when taken.</summary>
    public string? Owner { get; init; }

    /// <summary>Why it could not be checked.</summary>
    public string? Reason { get; init; }

    public static AddressCheck Free { get; } = new() { Availability = AddressAvailability.Free };
    public static AddressCheck TakenBy(string owner) => new() { Availability = AddressAvailability.Taken, Owner = owner };
    public static AddressCheck Unknown(string reason) => new() { Availability = AddressAvailability.CouldNotCheck, Reason = reason };
}
