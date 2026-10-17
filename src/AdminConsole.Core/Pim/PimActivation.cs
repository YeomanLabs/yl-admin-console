namespace AdminConsole.Core.Pim;

/// <summary>What the admin asked for when activating a role.</summary>
public sealed record PimActivation
{
    public required PimRole Role { get; init; }

    public required TimeSpan Duration { get; init; }

    public string? Justification { get; init; }

    public string? TicketNumber { get; init; }

    public string? TicketSystem { get; init; }
}
