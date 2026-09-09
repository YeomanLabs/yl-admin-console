using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Models;

namespace YeomanLabs.AdminConsole.Core.Users;

/// <summary>
/// Everything the User 360 page hydrates live in one <c>$batch</c> (doc 04 §Batching): manager, memberOf, auth methods,
/// registered devices, license assignment states, direct reports, and a fresh copy of the user with sign-in activity.
/// Each section carries its own error so tabs degrade independently.
/// </summary>
public sealed record UserLiveDetail
{
    public required string UserId { get; init; }

    /// <summary>The user re-read from Graph (with <c>signInActivity</c> when the tenant has P1). Also written through to the cache.</summary>
    public SectionResult<DirectoryUser> User { get; init; } = SectionResult<DirectoryUser>.Pending;

    /// <summary>True when the refresh had to drop <c>signInActivity</c> (403/400 → no P1 or no AuditLog.Read.All).</summary>
    public bool SignInActivityUnavailable { get; init; }

    public SignInActivity? SignInActivity { get; init; }

    /// <summary><c>/manager</c>; a 404 is "no manager" and is reported as Ok(null).</summary>
    public SectionResult<PersonRef?> Manager { get; init; } = SectionResult<PersonRef?>.Pending;

    public SectionResult<IReadOnlyList<DirectoryGroup>> Groups { get; init; } = SectionResult<IReadOnlyList<DirectoryGroup>>.Pending;

    /// <summary>Directory roles found in <c>/memberOf</c> (filtered by <c>@odata.type</c>).</summary>
    public SectionResult<IReadOnlyList<DirectoryRoleRef>> Roles { get; init; } = SectionResult<IReadOnlyList<DirectoryRoleRef>>.Pending;

    public SectionResult<IReadOnlyList<AuthMethod>> AuthMethods { get; init; } = SectionResult<IReadOnlyList<AuthMethod>>.Pending;

    public SectionResult<IReadOnlyList<EntraDevice>> RegisteredDevices { get; init; } = SectionResult<IReadOnlyList<EntraDevice>>.Pending;

    public SectionResult<IReadOnlyList<LicenseAssignmentState>> LicenseStates { get; init; } = SectionResult<IReadOnlyList<LicenseAssignmentState>>.Pending;

    public SectionResult<IReadOnlyList<PersonRef>> DirectReports { get; init; } = SectionResult<IReadOnlyList<PersonRef>>.Pending;

    public double ElapsedMilliseconds { get; init; }
}
