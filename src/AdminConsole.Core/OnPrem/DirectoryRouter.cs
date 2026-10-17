using System.Collections.Concurrent;

namespace AdminConsole.Core.OnPrem;

/// <summary>
/// Several AD domains behind one <see cref="IDirectoryService"/> (M20.3, doc 11 §9.3). Reads go to every domain and
/// are merged; a write goes to the domain that holds the object. Which domain that is gets learnt from the reads — the
/// console always shows an object before it changes it — and is found by asking each domain when it is not known yet.
/// </summary>
/// <remarks>
/// <para>
/// Each domain is its own connection with its own bind, so one unreachable domain greys only its own objects. Status is
/// connected when any domain is, and names the ones that are not.
/// </para>
/// <para>
/// Two things stay inside one domain because AD makes them a different operation altogether: moving an object to
/// another domain (a cross-domain move, not an LDAP modify-DN), and adding a member from another domain to a group by
/// GUID (the group's domain cannot resolve it). Both are refused with that reason rather than attempted.
/// </para>
/// </remarks>
public sealed class DirectoryRouter : IDirectoryService
{
    private readonly IReadOnlyList<IDirectoryService> _domains;
    private readonly ConcurrentDictionary<Guid, IDirectoryService> _owner = new();
    private readonly ConcurrentDictionary<IDirectoryService, string> _roots = new();

    public DirectoryRouter(IReadOnlyList<IDirectoryService> domains)
    {
        if (domains.Count == 0) throw new ArgumentException("At least one domain is needed.", nameof(domains));
        _domains = domains;
    }

    public async Task<DirectoryStatus> GetStatusAsync(CancellationToken ct)
    {
        var statuses = await Task.WhenAll(_domains.Select(d => d.GetStatusAsync(ct))).ConfigureAwait(false);
        for (var i = 0; i < _domains.Count; i++)
        {
            if (statuses[i].RootDn is { Length: > 0 } root) _roots[_domains[i]] = root;
        }

        var up = statuses.Where(s => s.IsConnected).ToList();
        var down = statuses.Where(s => !s.IsConnected).ToList();
        if (up.Count == 0)
        {
            return new DirectoryStatus { IsConnected = false, Reason = string.Join(" ", statuses.Select(s => s.Reason).Distinct()) };
        }

        return up[0] with
        {
            DomainName = string.Join(", ", up.Select(s => s.DomainName)),
            Reason = down.Count == 0 ? null : "Not reachable: " + string.Join("; ", down.Select(s => $"{s.DomainName ?? "a domain"} ({s.Reason})")),
        };
    }

    // ---- reads ---------------------------------------------------------------------------------------------------

    public Task<OnPremUser?> GetUserAsync(Guid objectGuid, CancellationToken ct) => FirstAsync(d => d.GetUserAsync(objectGuid, ct), u => u.ObjectGuid);

    public Task<OnPremUser?> GetUserByImmutableIdAsync(string immutableId, CancellationToken ct) =>
        FirstAsync(d => d.GetUserByImmutableIdAsync(immutableId, ct), u => u.ObjectGuid);

    public Task<OnPremGroup?> GetGroupAsync(Guid objectGuid, CancellationToken ct) => FirstAsync(d => d.GetGroupAsync(objectGuid, ct), g => g.ObjectGuid);

    public Task<OnPremComputer?> GetComputerAsync(Guid objectGuid, CancellationToken ct) => FirstAsync(d => d.GetComputerAsync(objectGuid, ct), c => c.ObjectGuid);

    public Task<OnPremOu?> GetOrganisationalUnitAsync(Guid objectGuid, CancellationToken ct) =>
        FirstAsync(d => d.GetOrganisationalUnitAsync(objectGuid, ct), o => o.ObjectGuid);

    public Task<IReadOnlyList<OnPremUser>> SearchUsersAsync(string text, int limit, string? searchBaseDn, CancellationToken ct) =>
        SearchAsync(searchBaseDn, limit, d => d.SearchUsersAsync(text, limit, searchBaseDn, ct), u => u.ObjectGuid, ct);

    public Task<IReadOnlyList<OnPremGroup>> SearchGroupsAsync(string text, int limit, string? searchBaseDn, CancellationToken ct) =>
        SearchAsync(searchBaseDn, limit, d => d.SearchGroupsAsync(text, limit, searchBaseDn, ct), g => g.ObjectGuid, ct);

    public Task<IReadOnlyList<OnPremComputer>> SearchComputersAsync(string text, int limit, string? searchBaseDn, CancellationToken ct) =>
        SearchAsync(searchBaseDn, limit, d => d.SearchComputersAsync(text, limit, searchBaseDn, ct), c => c.ObjectGuid, ct);

    public async Task<IReadOnlyList<OnPremOu>> GetOrganisationalUnitsAsync(CancellationToken ct)
    {
        var all = new List<OnPremOu>();
        foreach (var d in _domains)
        {
            var ous = await d.GetOrganisationalUnitsAsync(ct).ConfigureAwait(false);
            foreach (var ou in ous) _owner[ou.ObjectGuid] = d;
            all.AddRange(ous);
        }

        return all;
    }

    public async Task<IReadOnlyList<OnPremMember>> GetGroupMembersAsync(Guid objectGuid, CancellationToken ct) =>
        await OwnerAsync(objectGuid, ct).ConfigureAwait(false) is { } d ? await d.GetGroupMembersAsync(objectGuid, ct).ConfigureAwait(false) : [];

    public async Task<LapsReadResult> GetLapsPasswordAsync(Guid computerGuid, CancellationToken ct) =>
        await OwnerAsync(computerGuid, ct).ConfigureAwait(false) is { } d
            ? await d.GetLapsPasswordAsync(computerGuid, ct).ConfigureAwait(false)
            : LapsReadResult.None("That computer was not found in any configured domain.");

    public async Task<IReadOnlySet<string>?> GetWritableAttributesAsync(Guid objectGuid, CancellationToken ct) =>
        await OwnerAsync(objectGuid, ct).ConfigureAwait(false) is { } d ? await d.GetWritableAttributesAsync(objectGuid, ct).ConfigureAwait(false) : null;

    // ---- writes, each to the object's own domain ---------------------------------------------------------------------

    public Task<DirectoryOutcome> SetAttributesAsync(Guid objectGuid, IReadOnlyDictionary<string, string?> attributes, CancellationToken ct) =>
        OnOwnerAsync(objectGuid, "Set attributes", d => d.SetAttributesAsync(objectGuid, attributes, ct), ct);

    public Task<DirectoryOutcome> SetEnabledAsync(Guid objectGuid, bool enabled, CancellationToken ct) =>
        OnOwnerAsync(objectGuid, "Set enabled", d => d.SetEnabledAsync(objectGuid, enabled, ct), ct);

    public Task<DirectoryOutcome> UnlockAsync(Guid objectGuid, CancellationToken ct) => OnOwnerAsync(objectGuid, "Unlock", d => d.UnlockAsync(objectGuid, ct), ct);

    public Task<DirectoryOutcome> SetPasswordAsync(Guid objectGuid, string password, bool mustChangeAtNextLogon, CancellationToken ct) =>
        OnOwnerAsync(objectGuid, "Set password", d => d.SetPasswordAsync(objectGuid, password, mustChangeAtNextLogon, ct), ct);

    public Task<DirectoryOutcome> SetAccountOptionsAsync(Guid objectGuid, bool? passwordNeverExpires, bool? mustChangePasswordAtNextLogon, CancellationToken ct) =>
        OnOwnerAsync(objectGuid, "Set account options", d => d.SetAccountOptionsAsync(objectGuid, passwordNeverExpires, mustChangePasswordAtNextLogon, ct), ct);

    public Task<DirectoryOutcome> SetAccountExpiryAsync(Guid objectGuid, DateTimeOffset? expires, CancellationToken ct) =>
        OnOwnerAsync(objectGuid, "Set account expiry", d => d.SetAccountExpiryAsync(objectGuid, expires, ct), ct);

    public async Task<DirectoryOutcome> SetManagerAsync(Guid userGuid, Guid? managerGuid, CancellationToken ct)
    {
        if (managerGuid is { } m && !await SameDomainAsync(userGuid, m, ct).ConfigureAwait(false))
        {
            return DirectoryOutcome.Refused("Set manager", "The manager is in another domain. Set a manager from another domain in AD Users and Computers.");
        }

        return await OnOwnerAsync(userGuid, "Set manager", d => d.SetManagerAsync(userGuid, managerGuid, ct), ct).ConfigureAwait(false);
    }

    public Task<DirectoryOutcome> ResetComputerAccountAsync(Guid computerGuid, CancellationToken ct) =>
        OnOwnerAsync(computerGuid, "Reset computer account", d => d.ResetComputerAccountAsync(computerGuid, ct), ct);

    public async Task<DirectoryOutcome> MoveAsync(Guid objectGuid, string targetOuDn, CancellationToken ct)
    {
        var owner = await OwnerAsync(objectGuid, ct).ConfigureAwait(false);
        var target = await ByDnAsync(targetOuDn, ct).ConfigureAwait(false);
        if (owner is null) return NotFound("Move");
        if (target is not null && !ReferenceEquals(owner, target))
        {
            return DirectoryOutcome.Refused("Move", "That OU is in another domain. Moving between domains is a cross-domain move, done with ADMT or Move-ADObject -TargetServer.");
        }

        return await owner.MoveAsync(objectGuid, targetOuDn, ct).ConfigureAwait(false);
    }

    public Task<DirectoryOutcome> RenameAsync(Guid objectGuid, string newName, CancellationToken ct) =>
        OnOwnerAsync(objectGuid, "Rename", d => d.RenameAsync(objectGuid, newName, ct), ct);

    public async Task<DirectoryOutcome> AddToGroupAsync(Guid groupGuid, Guid memberGuid, CancellationToken ct)
    {
        if (!await SameDomainAsync(groupGuid, memberGuid, ct).ConfigureAwait(false))
        {
            return DirectoryOutcome.Refused("Add to group", "The member is in another domain. Add members from another domain in AD Users and Computers.");
        }

        return await OnOwnerAsync(groupGuid, "Add to group", d => d.AddToGroupAsync(groupGuid, memberGuid, ct), ct).ConfigureAwait(false);
    }

    public Task<DirectoryOutcome> RemoveFromGroupAsync(Guid groupGuid, Guid memberGuid, CancellationToken ct) =>
        OnOwnerAsync(groupGuid, "Remove from group", d => d.RemoveFromGroupAsync(groupGuid, memberGuid, ct), ct);

    public Task<DirectoryOutcome> SetGroupTypeAsync(Guid groupGuid, GroupScope scope, GroupCategory category, CancellationToken ct) =>
        OnOwnerAsync(groupGuid, "Set group type", d => d.SetGroupTypeAsync(groupGuid, scope, category, ct), ct);

    public async Task<CreatedAdUser> CreateUserAsync(NewAdUser user, CancellationToken ct)
    {
        if (await ByDnAsync(user.ParentOuDn, ct).ConfigureAwait(false) is not { } d)
        {
            return new CreatedAdUser(DirectoryOutcome.Refused("Create user", $"{user.ParentOuDn} is not in any configured domain."), null);
        }

        var created = await d.CreateUserAsync(user, ct).ConfigureAwait(false);
        if (created.User is { } u) _owner[u.ObjectGuid] = d;
        return created;
    }

    public async Task<DirectoryOutcome> CreateOuAsync(string name, string parentDn, CancellationToken ct) =>
        await ByDnAsync(parentDn, ct).ConfigureAwait(false) is { } d
            ? await d.CreateOuAsync(name, parentDn, ct).ConfigureAwait(false)
            : DirectoryOutcome.Refused("Create OU", $"{parentDn} is not in any configured domain.");

    public Task<DirectoryOutcome> DeleteAsync(Guid objectGuid, CancellationToken ct) => OnOwnerAsync(objectGuid, "Delete", d => d.DeleteAsync(objectGuid, ct), ct);

    // ---- routing -----------------------------------------------------------------------------------------------------

    private async Task<T?> FirstAsync<T>(Func<IDirectoryService, Task<T?>> read, Func<T, Guid> guidOf) where T : class
    {
        foreach (var d in _domains)
        {
            if (await read(d).ConfigureAwait(false) is { } found)
            {
                _owner[guidOf(found)] = d;
                return found;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<T>> SearchAsync<T>(string? baseDn, int limit, Func<IDirectoryService, Task<IReadOnlyList<T>>> search, Func<T, Guid> guidOf, CancellationToken ct)
    {
        var domains = baseDn is null ? _domains : await ByDnAsync(baseDn, ct).ConfigureAwait(false) is { } one ? [one] : [];
        var all = new List<T>();
        foreach (var d in domains)
        {
            foreach (var item in await search(d).ConfigureAwait(false))
            {
                _owner[guidOf(item)] = d;
                all.Add(item);
            }
        }

        return all.Count <= limit ? all : all.Take(limit).ToList();
    }

    /// <summary>The domain holding an object: known from an earlier read, or found by asking each domain for it.</summary>
    private async Task<IDirectoryService?> OwnerAsync(Guid objectGuid, CancellationToken ct)
    {
        if (_owner.TryGetValue(objectGuid, out var known)) return known;
        foreach (var d in _domains)
        {
            if (await d.GetUserAsync(objectGuid, ct).ConfigureAwait(false) is not null
                || await d.GetGroupAsync(objectGuid, ct).ConfigureAwait(false) is not null
                || await d.GetComputerAsync(objectGuid, ct).ConfigureAwait(false) is not null
                || await d.GetOrganisationalUnitAsync(objectGuid, ct).ConfigureAwait(false) is not null)
            {
                _owner[objectGuid] = d;
                return d;
            }
        }

        return null;
    }

    private async Task<IDirectoryService?> ByDnAsync(string dn, CancellationToken ct)
    {
        if (_roots.Count < _domains.Count) await GetStatusAsync(ct).ConfigureAwait(false);
        return _domains
            .Where(d => _roots.TryGetValue(d, out var root) && dn.EndsWith(root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => _roots[d].Length) // a child domain's root is longer than its parent's
            .FirstOrDefault();
    }

    private async Task<bool> SameDomainAsync(Guid a, Guid b, CancellationToken ct) =>
        ReferenceEquals(await OwnerAsync(a, ct).ConfigureAwait(false), await OwnerAsync(b, ct).ConfigureAwait(false));

    private async Task<DirectoryOutcome> OnOwnerAsync(Guid objectGuid, string summary, Func<IDirectoryService, Task<DirectoryOutcome>> write, CancellationToken ct) =>
        await OwnerAsync(objectGuid, ct).ConfigureAwait(false) is { } d ? await write(d).ConfigureAwait(false) : NotFound(summary);

    private static DirectoryOutcome NotFound(string summary) => DirectoryOutcome.Refused(summary, "That object was not found in any configured domain.");
}
