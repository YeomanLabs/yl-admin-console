namespace AdminConsole.Core.Offboarding;

/// <summary>
/// Which system a step talks to (doc 06 "Cap" column). Doc 06 calls this the step's capability, but that name is
/// already taken by <see cref="Auth.Capability"/>, the permission model; a step needs both, so this one is the surface.
/// Only <see cref="Graph"/> runs in Phase 1 — the others register so they appear greyed with a reason (UX §8).
/// </summary>
public enum StepSurface
{
    Graph,

    /// <summary>Exchange Online. Phase 3.</summary>
    Exchange,

    /// <summary>On-prem Active Directory over LDAP. Phase 2.</summary>
    OnPremAd,
}
