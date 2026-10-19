using AdminConsole.Core.Models;

namespace AdminConsole.Core.Users;

/// <summary>
/// "Reset MFA" and the offboarding step that removes authentication methods: which of a person's methods go, in which
/// order, and which stay with the reason. Ordered because Entra refuses some orders — Microsoft documents that a user
/// cannot keep an alternate mobile number without a mobile one, so the alternate has to go before the mobile.
/// </summary>
public static class AuthMethodReset
{
    /// <summary>The removable methods, in an order Entra accepts: everything else first, then office, alternate, mobile.</summary>
    public static IReadOnlyList<AuthMethod> RemovalOrder(IEnumerable<AuthMethod> methods) =>
        methods.Where(m => m.CanDelete)
            .OrderBy(Rank)
            .ThenBy(m => m.CreatedDateTime ?? DateTimeOffset.MaxValue)
            .ToList();

    /// <summary>What a reset leaves in place, and why — said in the preview so nobody expects the password to go too.</summary>
    public static IReadOnlyList<(AuthMethod Method, string Reason)> Kept(IEnumerable<AuthMethod> methods) =>
        methods.Where(m => !m.CanDelete)
            .Select(m => (m, m.Kind == AuthMethodKind.Password
                ? "The password stays; Reset password changes it."
                : $"{m.KindLabel} can't be removed through Graph."))
            .ToList();

    private static int Rank(AuthMethod m) => m.Kind switch
    {
        AuthMethodKind.TemporaryAccessPass => 0,
        AuthMethodKind.Email => 1,
        AuthMethodKind.SoftwareOath => 2,
        AuthMethodKind.Fido2 => 3,
        AuthMethodKind.WindowsHelloForBusiness => 4,
        AuthMethodKind.MicrosoftAuthenticator => 5,
        AuthMethodKind.Phone => m.Detail?.ToLowerInvariant() switch
        {
            "office" => 6,
            "alternatemobile" => 7,
            _ => 8,
        },
        _ => 5,
    };
}
