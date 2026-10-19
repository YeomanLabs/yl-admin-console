using Microsoft.Extensions.Logging;

namespace AdminConsole.Core.OnPrem;

/// <summary>What a connection test found: whether a controller answered, as whom, and the OUs it can see.</summary>
public sealed record DomainTestResult(DirectoryStatus Status, IReadOnlyList<OnPremOu> OrganisationalUnits);

/// <summary>
/// Settings → Active Directory's "Test connection": a throwaway connection with the options typed, before anything is
/// saved. The OUs come back too, so the leavers OU is picked from the real tree rather than typed as a distinguished
/// name.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class DomainConnectionTest
{
    public static Task<DomainTestResult> RunAsync(LdapOptions options, ILogger<LdapDirectoryService> logger, CancellationToken ct) =>
        // The LDAP bind is synchronous and waits out its timeout when nothing answers, so it runs off the caller's thread.
        Task.Run(async () =>
        {
            using var service = new LdapDirectoryService(options, logger);
            var status = await service.GetStatusAsync(ct).ConfigureAwait(false);
            if (!status.IsConnected) return new DomainTestResult(status, []);

            try
            {
                var ous = await service.GetOrganisationalUnitsAsync(ct).ConfigureAwait(false);
                return new DomainTestResult(status, ous.OrderBy(o => DistinguishedNames.FriendlyPath(o.DistinguishedName), StringComparer.OrdinalIgnoreCase).ToList());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Connected but the OU read failed: the connection is still the answer to "does this work".
                logger.LogWarning(ex, "Connected to {Domain} but could not list its OUs", options.Domain);
                return new DomainTestResult(status, []);
            }
        }, ct);
}
