namespace AdminConsole.Core.OnPrem;

/// <summary>
/// Everything the console does to on-prem Active Directory (M12.1), in the console's vocabulary rather than LDAP's.
/// </summary>
/// <remarks>
/// <para>
/// One interface so the WPF layer never sees an <c>LdapConnection</c>, and so the whole of Phase 2 can be tested
/// against <see cref="FakeDirectoryService"/> without a domain controller. The LDAP implementation is the only thing
/// that knows about result codes, controls and distinguished-name syntax.
/// </para>
/// <para>
/// Objects are addressed by <c>objectGUID</c>, never by distinguished name. A DN changes the moment somebody renames
/// or moves the object, and acting on a stale one either fails or — worse — succeeds against whatever is at that path
/// now. The GUID is what Entra's <c>onPremisesImmutableId</c> is derived from, so it is also the join.
/// </para>
/// </remarks>
public interface IDirectoryService
{
    /// <summary>Whether a domain controller is reachable, and which one. Cheap enough to call on page load.</summary>
    Task<DirectoryStatus> GetStatusAsync(CancellationToken ct);

    // ---- reads ---------------------------------------------------------------------------------------------------

    Task<OnPremUser?> GetUserAsync(Guid objectGuid, CancellationToken ct);

    /// <summary>The AD user behind an Entra one, found from its <c>onPremisesImmutableId</c>.</summary>
    Task<OnPremUser?> GetUserByImmutableIdAsync(string immutableId, CancellationToken ct);

    /// <param name="searchBaseDn">Restricts the search to one OU and everything under it. Null searches the domain.</param>
    Task<IReadOnlyList<OnPremUser>> SearchUsersAsync(string text, int limit, string? searchBaseDn, CancellationToken ct);

    Task<OnPremGroup?> GetGroupAsync(Guid objectGuid, CancellationToken ct);

    /// <param name="searchBaseDn">Restricts the search to one OU and everything under it. Null searches the domain.</param>
    Task<IReadOnlyList<OnPremGroup>> SearchGroupsAsync(string text, int limit, string? searchBaseDn, CancellationToken ct);

    /// <summary>Direct members of a group, resolved enough to draw a row for each.</summary>
    Task<IReadOnlyList<OnPremMember>> GetGroupMembersAsync(Guid objectGuid, CancellationToken ct);

    Task<OnPremComputer?> GetComputerAsync(Guid objectGuid, CancellationToken ct);

    /// <param name="searchBaseDn">Restricts the search to one OU and everything under it. Null searches the domain.</param>
    Task<IReadOnlyList<OnPremComputer>> SearchComputersAsync(string text, int limit, string? searchBaseDn, CancellationToken ct);

    /// <summary>Every OU in the domain, flat. The tree is assembled from distinguished names by the caller.</summary>
    Task<IReadOnlyList<OnPremOu>> GetOrganisationalUnitsAsync(CancellationToken ct);

    Task<OnPremOu?> GetOrganisationalUnitAsync(Guid objectGuid, CancellationToken ct);

    /// <summary>
    /// The local administrator password AD holds for a machine (M12.6), from whichever LAPS this forest uses.
    /// </summary>
    /// <remarks>
    /// Windows LAPS and the older downloadable LAPS store it in different attributes with different encodings, and a
    /// forest can have either, both, or neither. Reading only one and reporting nothing when it is absent would tell
    /// an admin the machine has no password when it may simply have the other kind.
    /// </remarks>
    Task<LapsReadResult> GetLapsPasswordAsync(Guid computerGuid, CancellationToken ct);

    /// <summary>
    /// Which attributes the signed-in admin may actually write on this object, from the directory's own
    /// <c>allowedAttributesEffective</c>.
    /// </summary>
    /// <remarks>
    /// It answers two different questions at once, and both matter before a field is offered as editable: whether
    /// this forest's schema has the attribute at all — <c>mailNickname</c> does not exist without the Exchange
    /// extension — and whether this admin has been delegated the right to write it. Asking first is the difference
    /// between a greyed field with a reason and a field that accepts a value and then refuses it.
    /// </remarks>
    /// <returns>Null when the directory did not answer, which means unknown rather than none.</returns>
    Task<IReadOnlySet<string>?> GetWritableAttributesAsync(Guid objectGuid, CancellationToken ct);

    // ---- writes --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sets attributes on an object. A null value clears the attribute — as with Graph, empty is not how you say
    /// "no value", and the implementation is responsible for turning one into the other.
    /// </summary>
    Task<DirectoryOutcome> SetAttributesAsync(Guid objectGuid, IReadOnlyDictionary<string, string?> attributes, CancellationToken ct);

    /// <summary>Enables or disables an account, preserving every other <c>userAccountControl</c> bit.</summary>
    Task<DirectoryOutcome> SetEnabledAsync(Guid objectGuid, bool enabled, CancellationToken ct);

    Task<DirectoryOutcome> UnlockAsync(Guid objectGuid, CancellationToken ct);

    /// <summary>Sets a password. Needs an encrypted connection; the failure when there is not one says so.</summary>
    Task<DirectoryOutcome> SetPasswordAsync(Guid objectGuid, string password, bool mustChangeAtNextLogon, CancellationToken ct);

    /// <summary>Moves an object to another OU. The GUID does not change, so anything holding one stays valid.</summary>
    Task<DirectoryOutcome> MoveAsync(Guid objectGuid, string targetOuDn, CancellationToken ct);

    Task<DirectoryOutcome> RenameAsync(Guid objectGuid, string newName, CancellationToken ct);

    Task<DirectoryOutcome> AddToGroupAsync(Guid groupGuid, Guid memberGuid, CancellationToken ct);

    Task<DirectoryOutcome> RemoveFromGroupAsync(Guid groupGuid, Guid memberGuid, CancellationToken ct);

    /// <summary>
    /// Changes a group's scope and category, which AD stores together in the <c>groupType</c> bit field (M12.5).
    /// </summary>
    /// <remarks>
    /// One call for both because they are one attribute: writing scope without carrying the category through would
    /// silently convert a security group to a distribution one and take away every permission granted through it.
    /// The refusals AD makes here are checked in advance by <see cref="GroupRules"/>, because "the server is
    /// unwilling to perform" does not tell an admin which of three different rules they have met.
    /// </remarks>
    Task<DirectoryOutcome> SetGroupTypeAsync(Guid groupGuid, GroupScope scope, GroupCategory category, CancellationToken ct);

    /// <summary>Creates an organisational unit under <paramref name="parentDn"/> (M12.6).</summary>
    Task<DirectoryOutcome> CreateOuAsync(string name, string parentDn, CancellationToken ct);

    /// <summary>Deletes an object. Refuses a non-empty container and a protected one, with a reason rather than a code.</summary>
    Task<DirectoryOutcome> DeleteAsync(Guid objectGuid, CancellationToken ct);
}
