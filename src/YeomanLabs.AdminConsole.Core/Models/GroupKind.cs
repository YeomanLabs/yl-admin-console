namespace YeomanLabs.AdminConsole.Core.Models;

/// <summary>Derived from groupTypes/securityEnabled/mailEnabled per docs/05-DATA-MODEL.md.</summary>
public enum GroupKind
{
    Security,
    M365,
    DistributionOrMailSecurity,
}
