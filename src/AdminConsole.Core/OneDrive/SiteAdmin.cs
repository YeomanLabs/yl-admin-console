using System.Text.Json;
using AdminConsole.Core.Models;

namespace AdminConsole.Core.OneDrive;

/// <summary>One site collection admin of a OneDrive, from <c>SPO.Tenant/GetSiteAdministrators</c> (M16).</summary>
public sealed record SiteAdmin
{
    public required string LoginName { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? UserPrincipalName { get; init; }

    public string Label => Name is { Length: > 0 } n ? UserPrincipalName is { Length: > 0 } u ? $"{n} ({u})" : n : LoginName;

    public static SiteAdmin FromJson(JsonElement e) => new()
    {
        LoginName = e.GetString("loginName") ?? string.Empty,
        Name = e.GetString("name"),
        Email = e.GetString("email"),
        UserPrincipalName = e.GetString("userPrincipalName"),
    };

    public bool Is(string upn) => string.Equals(UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(LoginName, SharePoint.SharePointClient.LoginNameOf(upn), StringComparison.OrdinalIgnoreCase);
}
