using AdminConsole.Core.OnPrem;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Finding the AD object behind an offboarded user (M12.7), shared by the two on-prem steps.
/// </summary>
/// <remarks>
/// Both on-prem steps need the same object and can fail to get it for the same three reasons, each of which puts the
/// admin somewhere different: no domain is configured, no controller is reachable from where they are sitting, or the
/// account's source anchor matches nothing in the directory. A single "could not run" would be the same sentence for
/// all three, and only one of them is something they can do anything about right now.
/// </remarks>
internal static class OnPremStep
{
    internal readonly record struct Found(OnPremUser? User, string? Blocked);

    internal static async Task<Found> FindAsync(IDirectoryService directory, OffboardingContext ctx, CancellationToken ct)
    {
        DirectoryStatus status;
        try
        {
            status = await directory.GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Found(null, $"Active Directory could not be reached. {ex.Message}");
        }

        if (!status.IsConnected)
        {
            return new Found(null, status.Reason ?? "No domain controller is reachable from here.");
        }

        var immutableId = ctx.User.OnPremisesImmutableId;
        if (string.IsNullOrEmpty(immutableId))
        {
            return new Found(null,
                "This account is marked as synced but carries no source anchor, so the AD object behind it cannot be identified.");
        }

        var user = await directory.GetUserByImmutableIdAsync(immutableId, ct).ConfigureAwait(false);
        return user is null
            ? new Found(null, $"No account in {status.DomainName} matches this user's source anchor. Check that Entra Connect is still syncing it.")
            : new Found(user, null);
    }
}
