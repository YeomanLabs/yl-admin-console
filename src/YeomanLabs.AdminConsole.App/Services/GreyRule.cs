using YeomanLabs.AdminConsole.Core.Auth;

namespace YeomanLabs.AdminConsole.App.Services;

/// <summary>
/// UX §6: every disabled control gets one inline reason. Missing consent wins over "not built yet", because that is the
/// one the admin can act on. Once the action exists, pass <c>notBuiltYet: null</c> and the control enables itself.
/// </summary>
public static class GreyRule
{
    public static string? Reason(PermissionChecker checker, Capability capability, string? notBuiltYet)
    {
        var result = checker.Check(capability);
        if (result.NeedsConsent) return result.Reason;
        return notBuiltYet ?? result.Reason;
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
