namespace AdminConsole.Core.OnPrem;

/// <summary>
/// Decides whether a synced user's on-prem-mastered attributes can be edited from the console (M12.4), and what to
/// say when they cannot.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 greyed twenty attributes on every synced account with one flat sentence, because there was nothing the
/// console could do about them. There now is, but only when four separate things are true at once: a domain
/// controller is reachable, the AD object behind the cloud one was found, the attribute exists in this forest's
/// schema, and this admin has been delegated it. Each of those failing is a different situation for the admin to be
/// in, and each gets its own sentence rather than a shared shrug.
/// </para>
/// <para>
/// It lives in Core and takes everything it needs as arguments so the branching can be tested. The ViewModel's job is
/// to gather the four inputs and apply the answer, which is the part that cannot go wrong quietly.
/// </para>
/// </remarks>
public static class HybridUnlock
{
    /// <summary>The caption while the console has not yet established whether it can reach a domain controller.</summary>
    public const string Checking = "Mastered in on-prem AD";

    /// <summary>
    /// What one field should do.
    /// </summary>
    /// <param name="graphProperty">The Graph property name the field carries.</param>
    /// <param name="status">Whether a domain controller answered.</param>
    /// <param name="onPremFound">Whether the AD object behind this cloud user was found by its source anchor.</param>
    /// <param name="writable">
    /// What AD says this admin may write on that object, from <c>allowedAttributesEffective</c>. Null means the
    /// directory did not say, in which case the field is offered rather than pre-emptively locked — the write itself
    /// will still be refused with a real reason if it turns out not to be allowed.
    /// </param>
    public static HybridFieldState ForField(string graphProperty, DirectoryStatus status, bool onPremFound, IReadOnlySet<string>? writable)
    {
        if (!status.IsConnected)
        {
            return new HybridFieldState(null, "Mastered in on-prem AD — no domain controller reachable");
        }

        if (!onPremFound)
        {
            return new HybridFieldState(null, "Mastered in on-prem AD — this account's AD object was not found");
        }

        if (OnPremAttributeMap.EditableLdapName(graphProperty) is not { } ldapName)
        {
            return new HybridFieldState(null, OnPremAttributeMap.Refusal(graphProperty) ?? Checking);
        }

        if (writable is not null && !writable.Contains(ldapName))
        {
            // AD answers "no such attribute" to both a schema gap and a missing delegation, and does not distinguish
            // them in what it returns, so the reason covers both rather than asserting the wrong one.
            return new HybridFieldState(null,
                $"Cannot be written here — {ldapName} is absent from this forest's schema, or you have not been delegated it");
        }

        return new HybridFieldState(ldapName, $"Writes to AD · {ldapName}");
    }

    /// <summary>The sentence above the identity sheet, explaining what the whole set of greyed fields is doing.</summary>
    /// <param name="probed">False until the on-prem lookup has finished, so the sheet does not claim either answer early.</param>
    public static string ForSheet(DirectoryStatus status, OnPremUser? onPrem, bool probed)
    {
        if (!probed)
        {
            return "This account is synced from on-prem AD. Checking whether a domain controller is reachable…";
        }

        if (onPrem is not null)
        {
            var where = DistinguishedNames.Friendly(onPrem.DistinguishedName);
            return $"Synced from {status.DomainName} ({where}). These fields are written straight to AD through " +
                   $"{status.DomainController}; Entra Connect brings them back to the cloud.";
        }

        if (status.IsConnected)
        {
            return "This account is synced from on-prem AD, but no AD object matches its source anchor, so the " +
                   "mastered fields cannot be edited here. Check that Entra Connect is still syncing this object.";
        }

        return $"This account is synced from on-prem AD and its mastered fields are edited there. {status.Reason}";
    }
}
