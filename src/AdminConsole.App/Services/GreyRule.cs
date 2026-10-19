using AdminConsole.Core.Auth;

namespace AdminConsole.App.Services;

/// <summary>
/// UX §6: the one inline caption a control carries. Missing consent wins over "not built yet", because that is the one
/// the admin can act on. Missing consent is a caption, not a lock: since 0.3.6 the controls stay enabled and the click
/// asks for the permission (every action goes through ConsentService first), so "click to grant" is true. Disabling them
/// left an admin in a tenant without the grant unable to do anything from the page, with no way to ask for it there.
/// </summary>
public static class GreyRule
{
    public static string? Reason(PermissionChecker checker, Capability capability, string? notBuiltYet)
    {
        var result = checker.Check(capability);
        if (result.NeedsConsent) return result.Reason;

        // A role warning is advisory, not a block: PermissionResult.CanRun is true and roles cannot be enumerated with
        // certainty, so let the admin try and let Graph give the real answer (UX §7 maps the 403).
        return notBuiltYet;
    }

    /// <summary>The first blocking reason across several capabilities, or the fallback.</summary>
    public static string? Reason(PermissionChecker checker, IReadOnlyList<Capability> capabilities, string? notBuiltYet)
    {
        foreach (var c in capabilities)
        {
            var result = checker.Check(c);
            if (result.NeedsConsent) return result.Reason;
        }

        return notBuiltYet;
    }
}
