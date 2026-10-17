namespace AdminConsole.Core.Auth;

/// <summary>One on-prem domain of a tenant profile (M20.3). The same fields as appsettings' <c>OnPrem</c>, less the credential.</summary>
public sealed record TenantDomain
{
    public string Domain { get; init; } = string.Empty;

    public string Server { get; init; } = string.Empty;

    public int Port { get; init; } = 389;

    public bool UseTls { get; init; }

    public string DisabledOu { get; init; } = string.Empty;

    public OnPrem.LdapOptions ToOptions() => new()
    {
        Domain = Domain,
        Server = Server,
        Port = Port,
        UseTls = UseTls,
        DisabledOu = DisabledOu,
    };
}
