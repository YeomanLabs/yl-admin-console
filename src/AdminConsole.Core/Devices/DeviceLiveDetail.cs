using AdminConsole.Core.Models;

namespace AdminConsole.Core.Devices;

/// <summary>What the device page hydrates live in one <c>$batch</c> (doc 04 §Intune): the full device, its registered users, and per-policy compliance.</summary>
public sealed record DeviceLiveDetail
{
    public required string DeviceId { get; init; }

    /// <summary>The Intune device re-read from Graph; written through to the cache.</summary>
    public SectionResult<ManagedDevice> Device { get; init; } = SectionResult<ManagedDevice>.Pending;

    /// <summary><c>/users</c>: registered users; the primary user is the one whose id matches <c>userId</c>.</summary>
    public SectionResult<IReadOnlyList<PersonRef>> Users { get; init; } = SectionResult<IReadOnlyList<PersonRef>>.Pending;

    /// <summary>Per-policy compliance; non-compliant rows carry their setting states when the follow-up read succeeded.</summary>
    public SectionResult<IReadOnlyList<CompliancePolicyState>> CompliancePolicies { get; init; } = SectionResult<IReadOnlyList<CompliancePolicyState>>.Pending;

    public double ElapsedMilliseconds { get; init; }
}
