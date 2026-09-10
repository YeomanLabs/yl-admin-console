using System.Net;

namespace AdminConsole.Core.OnPrem;

/// <summary>How to reach the domain (M12.2). Bound from the <c>OnPrem</c> section of configuration.</summary>
public sealed class LdapOptions
{
    public const string SectionName = "OnPrem";

    /// <summary>
    /// The DNS domain, e.g. <c>corp.example.com</c>. Not a server name: handing the LDAP client the domain lets
    /// Windows find a domain controller through DNS SRV and site topology, so a retired DC does not strand anyone.
    /// Empty means on-prem features stay off.
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// A specific domain controller, bypassing DNS SRV discovery. Normally empty — serverless binding is better,
    /// because it survives a DC being retired. It is needed when the machine cannot resolve the domain through DNS
    /// (a workstation outside the forest, or a lab), and when troubleshooting one controller in particular.
    /// </summary>
    public string Server { get; set; } = string.Empty;

    public int Port { get; set; } = 389;

    /// <summary>
    /// LDAPS on 636. Off by default because a signed and sealed Negotiate bind already protects the traffic on 389
    /// and — unlike LDAPS — needs no certificate the client has to trust.
    /// </summary>
    public bool UseTls { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Explicit credentials. Null in normal use, where the admin's own Kerberos ticket is the point. This exists so a
    /// machine that is not domain-joined can drive a lab, and it should never be how a client deployment runs.
    /// </summary>
    public NetworkCredential? Credential { get; set; }
}
