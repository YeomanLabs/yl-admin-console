namespace AdminConsole.Core.Pim;

/// <summary>What a role's PIM policy asks for at activation (M18.2).</summary>
public sealed record PimPolicy
{
    /// <summary>The policy's default when it cannot be read: eight hours, a justification, nothing else.</summary>
    public static readonly PimPolicy Default = new() { MaximumDuration = TimeSpan.FromHours(8), RequiresJustification = true, IsAssumed = true };

    public required TimeSpan MaximumDuration { get; init; }

    public bool RequiresJustification { get; init; }

    public bool RequiresTicket { get; init; }

    public bool RequiresMfa { get; init; }

    public bool RequiresApproval { get; init; }

    /// <summary>True when the policy could not be read and these are Entra's defaults rather than this tenant's settings.</summary>
    public bool IsAssumed { get; init; }

    /// <summary>The durations to offer: whole hours up to the maximum, never more than the policy allows.</summary>
    public IReadOnlyList<TimeSpan> Durations
    {
        get
        {
            var hours = Math.Max(1, (int)Math.Floor(MaximumDuration.TotalHours));
            var offered = new[] { 1, 2, 4, 8, 12, 24 }.Where(h => h <= hours).Select(h => TimeSpan.FromHours(h)).ToList();
            if (offered.Count == 0 || offered[^1].TotalHours < hours) offered.Add(TimeSpan.FromHours(hours));
            if (MaximumDuration < TimeSpan.FromHours(1)) return [MaximumDuration];
            return offered;
        }
    }
}
