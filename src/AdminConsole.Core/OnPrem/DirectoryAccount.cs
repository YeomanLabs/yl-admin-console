using System.Net;
using System.Text.RegularExpressions;

namespace AdminConsole.Core.OnPrem;

/// <summary>
/// The account a domain connection signs in as, when it is not the Windows user: "CORP\admin" or "admin@corp.example.com".
/// A bare name is refused, because which domain it belongs to would be a guess.
/// </summary>
public static partial class DirectoryAccount
{
    [GeneratedRegex(@"^[^\\/:*?""<>|\s]{1,15}\\[^\\/\[\]:;|=,+*?<>@""\s]{1,104}$")]
    private static partial Regex DownLevel();

    [GeneratedRegex(@"^[^@\s\\]+@[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$")]
    private static partial Regex Upn();

    /// <summary>What is wrong with a typed account, or null.</summary>
    public static string? Problem(string? account)
    {
        var a = account?.Trim() ?? string.Empty;
        if (a.Length == 0) return "Type the account to connect as, like CORP\\admin or admin@corp.example.com.";
        if (DownLevel().IsMatch(a) || Upn().IsMatch(a)) return null;
        return a.Contains('\\') || a.Contains('@')
            ? "That account is not in a form AD accepts. Use CORP\\admin or admin@corp.example.com."
            : "Include the domain: CORP\\admin or admin@corp.example.com.";
    }

    /// <summary>The credential to bind with. "CORP\admin" splits into user and domain; a UPN is passed whole.</summary>
    public static NetworkCredential ToCredential(string account, string password)
    {
        var a = account.Trim();
        var slash = a.IndexOf('\\');
        return slash > 0
            ? new NetworkCredential(a[(slash + 1)..], password, a[..slash])
            : new NetworkCredential(a, password);
    }
}
