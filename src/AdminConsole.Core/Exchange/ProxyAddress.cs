namespace AdminConsole.Core.Exchange;

/// <summary>
/// One entry of <c>EmailAddresses</c>: <c>SMTP:adele@contoso.com</c>. The prefix's case is meaningful — upper-case
/// <c>SMTP</c> is the primary address, lower-case <c>smtp</c> an alias — so it is kept exactly as Exchange sent it.
/// </summary>
public sealed record ProxyAddress(string Prefix, string Address)
{
    public bool IsSmtp => Prefix.Equals("smtp", StringComparison.OrdinalIgnoreCase);

    public bool IsPrimary => string.Equals(Prefix, "SMTP", StringComparison.Ordinal);

    /// <summary>What the address is for, in words. Most mailboxes carry SIP, SPO and X500 entries nobody typed.</summary>
    public string Kind => Prefix.ToUpperInvariant() switch
    {
        "SMTP" => IsPrimary ? "Primary email" : "Email alias",
        "SIP" => "Teams / SIP",
        "X500" => "X.500 (legacy routing)",
        "SPO" => "SharePoint (system)",
        "EUM" => "Unified messaging",
        "" => "Address",
        _ => Prefix,
    };

    public static ProxyAddress Parse(string raw)
    {
        var colon = raw.IndexOf(':');
        return colon > 0 ? new ProxyAddress(raw[..colon], raw[(colon + 1)..]) : new ProxyAddress(string.Empty, raw);
    }

    public override string ToString() => Prefix.Length == 0 ? Address : $"{Prefix}:{Address}";
}
