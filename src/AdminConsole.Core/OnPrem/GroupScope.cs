namespace AdminConsole.Core.OnPrem;

/// <summary>
/// An AD group's scope, which decides what may be a member of it and where it can be used. Entra has no equivalent,
/// so this is one of the few genuinely new concepts Phase 2 introduces rather than unlocks.
/// </summary>
public enum GroupScope
{
    /// <summary>Members from this domain only; usable for permissions anywhere in the forest.</summary>
    Global,

    /// <summary>Members from anywhere; usable for permissions in this domain only. The classic resource group.</summary>
    DomainLocal,

    /// <summary>Members from anywhere, usable anywhere — at the cost of living in the global catalogue.</summary>
    Universal,
}
