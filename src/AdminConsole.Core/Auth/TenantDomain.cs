using System.Text.RegularExpressions;

namespace AdminConsole.Core.Auth;

/// <summary>
/// One on-prem domain of a tenant profile (M20.3), set in Settings → Active Directory or by hand in <c>tenant.json</c>.
/// The same fields as appsettings' <c>OnPrem</c>, less the credential: a profile's domains bind as the signed-in
/// Windows user.
/// </summary>
public sealed partial record TenantDomain
{
    public string Domain { get; init; } = string.Empty;

    public string Server { get; init; } = string.Empty;

    public int Port { get; init; } = 389;

    public bool UseTls { get; init; }

    public string DisabledOu { get; init; } = string.Empty;

    /// <summary>
    /// The options the directory service binds with. <paramref name="inherited"/> is appsettings' <c>OnPrem</c> section,
    /// passed for the first profile only: when it names this same domain, its credential and timeout come along. That is
    /// the lab case (a machine outside the domain); without it, connecting the lab domain in Settings would cut the lab
    /// off. A client deployment has no credential there, so nothing changes for it.
    /// </summary>
    public OnPrem.LdapOptions ToOptions(OnPrem.LdapOptions? inherited = null)
    {
        var same = inherited is not null && string.Equals(inherited.Domain.Trim(), Domain.Trim(), StringComparison.OrdinalIgnoreCase);
        var options = new OnPrem.LdapOptions
        {
            Domain = Domain.Trim(),
            Server = Server.Trim(),
            Port = Port,
            UseTls = UseTls,
            DisabledOu = DisabledOu.Trim(),
            Credential = same ? inherited!.Credential : null,
        };
        if (same) options.Timeout = inherited!.Timeout;
        return options;
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex DnsName();

    /// <summary>What is wrong with a typed domain name, or null. It must be the DNS name, which is what Windows finds a controller by.</summary>
    public static string? DomainProblem(string? domain)
    {
        var d = domain?.Trim() ?? string.Empty;
        if (d.Length == 0) return "Type the domain's DNS name, such as corp.example.com.";
        if (d.Contains('\\') || d.Contains('@')) return "That looks like an account, not a domain. Type the domain's DNS name, such as corp.example.com.";
        if (d.Contains("://", StringComparison.Ordinal)) return "Type the domain's DNS name without ldap:// or any other prefix.";
        return DnsName().IsMatch(d) ? null : "A domain name has only letters, digits, hyphens and dots, such as corp.example.com.";
    }

    /// <summary>What is wrong with a typed domain controller name, or null. Empty is fine: Windows finds one itself.</summary>
    public static string? ServerProblem(string? server)
    {
        var s = server?.Trim() ?? string.Empty;
        if (s.Length == 0) return null;
        if (s.Contains("://", StringComparison.Ordinal)) return "Type the domain controller's name without ldap:// or any other prefix.";
        if (s.Contains(':')) return "Type the domain controller's name alone; the port follows from LDAPS being on or off.";
        return DnsName().IsMatch(s) ? null : "A server name has only letters, digits, hyphens and dots, such as dc01.corp.example.com.";
    }

    /// <summary>
    /// A name with no dot is usually the NetBIOS name ("CONTOSO"), which finds no controller. Said as a hint rather than
    /// refused, because a single-label DNS domain, though rare and deprecated, is real.
    /// </summary>
    public static bool LooksLikeNetBiosName(string? domain) => domain?.Trim() is { Length: > 0 } d && !d.Contains('.');

    public override string ToString() => Domain;
}
