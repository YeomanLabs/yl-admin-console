namespace AdminConsole.Core.SharePoint;

/// <summary>
/// A tenant's SharePoint hosts (M16): <c>{name}.sharepoint.com</c>, <c>{name}-admin</c> and <c>{name}-my</c>. The name is the
/// tenant's initial domain without <c>.onmicrosoft.com</c> — which SharePoint keeps even when the organisation is
/// renamed, so a renamed tenant is read from its OneDrive URLs instead.
/// </summary>
public sealed record SharePointHosts(string Name)
{
    public string Root => $"https://{Name}.sharepoint.com";
    public string Admin => $"https://{Name}-admin.sharepoint.com";
    public string My => $"https://{Name}-my.sharepoint.com";

    /// <summary>From the tenant's verified domains: the <c>*.onmicrosoft.com</c> one that is not the hybrid mail domain.</summary>
    public static SharePointHosts? FromVerifiedDomains(IEnumerable<string> domains)
    {
        var initial = domains
            .Where(d => d.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase) && !d.EndsWith(".mail.onmicrosoft.com", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Length)
            .FirstOrDefault();
        return initial is null ? null : new SharePointHosts(initial[..initial.IndexOf('.')].ToLowerInvariant());
    }

    /// <summary>From any OneDrive URL (<c>https://contoso-my.sharepoint.com/personal/...</c>), the authoritative source.</summary>
    public static SharePointHosts? FromOneDriveUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host;
        const string suffix = "-my.sharepoint.com";
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? new SharePointHosts(host[..^suffix.Length].ToLowerInvariant()) : null;
    }
}
