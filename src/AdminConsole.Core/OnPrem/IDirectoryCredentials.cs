using System.Net;

namespace AdminConsole.Core.OnPrem;

/// <summary>
/// Where a domain's sign-in comes from when it is not the Windows user: the admin's own other account (a separate admin
/// account, say), typed once per session. The password lives in memory only — never written, never logged — so a
/// profile stores who to sign in as, never how.
/// </summary>
public interface IDirectoryCredentials
{
    /// <summary>
    /// The credential for <paramref name="userName"/> on <paramref name="domain"/>: the one remembered this session, else
    /// asked for. Null when the admin declined, or when it cannot be asked for right now (another dialog is open);
    /// <paramref name="askAgain"/> asks even after a decline.
    /// </summary>
    Task<NetworkCredential?> GetAsync(string domain, string userName, bool askAgain, CancellationToken ct);

    /// <summary>Drops a credential the domain refused, so the next attempt asks again.</summary>
    void Forget(string domain, string userName);
}
