using AdminConsole.Core.Auth;
using AdminConsole.Core.OnPrem;

namespace AdminConsole.App.ViewModels.Pages;

/// <summary>One on-prem domain in Settings → Active Directory: its name and how the console reaches it.</summary>
public sealed record DomainSettingRow(TenantDomain Domain)
{
    public string Name => Domain.Domain;

    public string Detail
    {
        get
        {
            var via = Domain.Server.Length > 0 ? $"via {Domain.Server}" : "Windows finds a controller";
            var tls = Domain.UseTls ? " · LDAPS" : string.Empty;
            var leavers = Domain.DisabledOu.Length > 0 ? $"leavers to {DistinguishedNames.FriendlyPath(Domain.DisabledOu)}" : "no leavers OU";
            return $"{via}{tls} · {leavers}";
        }
    }

    public bool Equals(DomainSettingRow? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => $"{Name}, {Detail}";
}
