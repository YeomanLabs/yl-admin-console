namespace AdminConsole.Core.Offboarding;

/// <summary>How one pasted line of a leaver list resolved (M15.5).</summary>
public enum ListMatch
{
    /// <summary>Exactly one person: the sign-in name or mail, or a display name nobody else has.</summary>
    Found,

    /// <summary>Several people share the name. The admin picks; the console never does.</summary>
    Ambiguous,

    /// <summary>Nobody matches exactly. Close matches are offered, never chosen.</summary>
    NotFound,

    /// <summary>The same person as an earlier line.</summary>
    Duplicate,
}
