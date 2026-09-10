using System.Collections.Concurrent;

namespace AdminConsole.Core.OnPrem;

/// <summary>
/// An in-memory Active Directory (M12.1): enough of one to develop and test every Phase 2 screen without a domain
/// controller, and to demonstrate the console on a machine that has never seen a domain.
/// </summary>
/// <remarks>
/// <para>
/// It is deliberately not a permissive stub. It enforces the rules that actually catch people out, because a fake
/// that says yes to everything trains the code to assume yes: names are unique within a container, a container with
/// children cannot be deleted, a protected object cannot be deleted, the primary group cannot be removed through a
/// membership write, and a group cannot contain itself.
/// </para>
/// <para>
/// What it cannot do is LDAP semantics — encoding, controls, referrals, replication, the exact shape of a diagnostic
/// message. Those need a real directory, which is what the lab DC is for. Passing against this fake is necessary and
/// not sufficient, and no gate should ever be signed off on it alone.
/// </para>
/// </remarks>
public sealed class FakeDirectoryService : IDirectoryService
{
    private readonly ConcurrentDictionary<Guid, Entry> _objects = new();
    private readonly string _rootDn;
    private readonly string _domain;

    public FakeDirectoryService(string domain = "adlab.test")
    {
        _domain = domain;
        _rootDn = string.Join(",", domain.Split('.').Select(p => $"DC={p}"));
    }

    /// <summary>Set false to make every call behave as though the machine is off the corporate network.</summary>
    public bool IsConnected { get; set; } = true;

    public string RootDn => _rootDn;

    // ---- building a directory to test against --------------------------------------------------------------------

    public OnPremOu AddOu(string name, string? parentDn = null, bool protectedFromDeletion = false)
    {
        var parent = parentDn ?? _rootDn;
        var ou = new OnPremOu
        {
            ObjectGuid = Guid.NewGuid(),
            Name = name,
            DistinguishedName = DistinguishedNames.Child("OU", name, parent),
            ProtectedFromAccidentalDeletion = protectedFromDeletion,
        };
        Insert(ou.ObjectGuid, ou.DistinguishedName, ou);
        return ou;
    }

    public OnPremUser AddUser(string sam, string? parentDn = null, bool enabled = true, string? displayName = null)
    {
        var parent = parentDn ?? $"CN=Users,{_rootDn}";
        var name = displayName ?? sam;
        var user = new OnPremUser
        {
            ObjectGuid = Guid.NewGuid(),
            SamAccountName = sam,
            DisplayName = name,
            UserPrincipalName = $"{sam}@{_domain}",
            DistinguishedName = DistinguishedNames.Child("CN", name, parent),
            UserAccountControl = enabled ? AccountFlags.NewEnabledUser : AccountFlags.NewDisabledUser,
        };
        Insert(user.ObjectGuid, user.DistinguishedName, user);
        return user;
    }

    public OnPremGroup AddGroup(string sam, string? parentDn = null,
        GroupScope scope = GroupScope.Global, GroupCategory category = GroupCategory.Security, bool critical = false)
    {
        var parent = parentDn ?? $"CN=Users,{_rootDn}";
        var group = new OnPremGroup
        {
            ObjectGuid = Guid.NewGuid(),
            SamAccountName = sam,
            DisplayName = sam,
            DistinguishedName = DistinguishedNames.Child("CN", sam, parent),
            Scope = scope,
            Category = category,
            IsCritical = critical,
        };
        Insert(group.ObjectGuid, group.DistinguishedName, group);
        return group;
    }

    public OnPremComputer AddComputer(string name, string? parentDn = null, bool enabled = true)
    {
        var parent = parentDn ?? $"CN=Computers,{_rootDn}";
        var computer = new OnPremComputer
        {
            ObjectGuid = Guid.NewGuid(),
            Name = name,
            SamAccountName = $"{name}$",
            DistinguishedName = DistinguishedNames.Child("CN", name, parent),
            UserAccountControl = AccountFlags.WorkstationTrustAccount | (enabled ? 0 : AccountFlags.AccountDisabled),
        };
        Insert(computer.ObjectGuid, computer.DistinguishedName, computer);
        return computer;
    }

    // ---- IDirectoryService ---------------------------------------------------------------------------------------

    public Task<DirectoryStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(
        IsConnected
            ? new DirectoryStatus
            {
                IsConnected = true,
                DomainName = _domain,
                RootDn = _rootDn,
                DomainController = $"dc01.{_domain}",
                BoundAs = $"ADLAB\\\\labadmin",
            }
            : DirectoryStatus.Unreachable("This is the in-memory directory, and it has been switched off."));

    public Task<OnPremUser?> GetUserAsync(Guid objectGuid, CancellationToken ct) =>
        Task.FromResult(Get<OnPremUser>(objectGuid));

    public Task<OnPremUser?> GetUserByImmutableIdAsync(string immutableId, CancellationToken ct)
    {
        // Entra's immutableId is base64 of the AD GUID, which is the whole point of the join.
        if (!TryGuidFromImmutableId(immutableId, out var guid)) return Task.FromResult<OnPremUser?>(null);
        return Task.FromResult(Get<OnPremUser>(guid));
    }

    public Task<IReadOnlyList<OnPremUser>> SearchUsersAsync(string text, int limit, string? searchBaseDn, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OnPremUser>>(
        [
            .. All<OnPremUser>()
                .Where(u => Matches(text, u.SamAccountName, u.DisplayName, u.UserPrincipalName, u.Mail) && Under(u.DistinguishedName, searchBaseDn))
                .OrderBy(u => u.Primary, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
        ]);

    public Task<OnPremGroup?> GetGroupAsync(Guid objectGuid, CancellationToken ct) =>
        Task.FromResult(Get<OnPremGroup>(objectGuid));

    public Task<IReadOnlyList<OnPremGroup>> SearchGroupsAsync(string text, int limit, string? searchBaseDn, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OnPremGroup>>(
        [
            .. All<OnPremGroup>()
                .Where(g => Matches(text, g.SamAccountName, g.DisplayName, g.Description) && Under(g.DistinguishedName, searchBaseDn))
                .OrderBy(g => g.Primary, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
        ]);

    public Task<IReadOnlyList<OnPremMember>> GetGroupMembersAsync(Guid objectGuid, CancellationToken ct)
    {
        var group = Get<OnPremGroup>(objectGuid);
        if (group is null) return Task.FromResult<IReadOnlyList<OnPremMember>>([]);

        var members = new List<OnPremMember>();
        foreach (var dn in group.MemberDns)
        {
            var entry = _objects.Values.FirstOrDefault(e => string.Equals(e.Dn, dn, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            members.Add(entry.Value switch
            {
                OnPremUser u => new OnPremMember { ObjectGuid = u.ObjectGuid, DistinguishedName = u.DistinguishedName, Name = u.Primary, Kind = OnPremMemberKind.User, SamAccountName = u.SamAccountName, Enabled = u.Enabled },
                OnPremGroup g => new OnPremMember { ObjectGuid = g.ObjectGuid, DistinguishedName = g.DistinguishedName, Name = g.Primary, Kind = OnPremMemberKind.Group, SamAccountName = g.SamAccountName },
                OnPremComputer c => new OnPremMember { ObjectGuid = c.ObjectGuid, DistinguishedName = c.DistinguishedName, Name = c.Name, Kind = OnPremMemberKind.Computer, SamAccountName = c.SamAccountName, Enabled = c.Enabled },
                _ => new OnPremMember { ObjectGuid = Guid.Empty, DistinguishedName = dn, Name = DistinguishedNames.LeafName(dn) ?? dn, Kind = OnPremMemberKind.Other },
            });
        }

        return Task.FromResult<IReadOnlyList<OnPremMember>>(members);
    }

    public Task<OnPremComputer?> GetComputerAsync(Guid objectGuid, CancellationToken ct) =>
        Task.FromResult(Get<OnPremComputer>(objectGuid));

    public Task<IReadOnlyList<OnPremComputer>> SearchComputersAsync(string text, int limit, string? searchBaseDn, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OnPremComputer>>(
        [
            .. All<OnPremComputer>()
                .Where(c => Matches(text, c.Name, c.DnsHostName, c.Description) && Under(c.DistinguishedName, searchBaseDn))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
        ]);

    public Task<IReadOnlyList<OnPremOu>> GetOrganisationalUnitsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OnPremOu>>(
            [.. All<OnPremOu>().OrderBy(o => o.DistinguishedName, StringComparer.OrdinalIgnoreCase)]);

    public Task<DirectoryOutcome> SetAttributesAsync(Guid objectGuid, IReadOnlyDictionary<string, string?> attributes, CancellationToken ct)
    {
        if (Get<OnPremUser>(objectGuid) is not { } user)
        {
            return Task.FromResult(DirectoryOutcome.Failed("Set attributes", DirectoryErrorText.NoSuchObject, null, "that user"));
        }

        var updated = user;
        foreach (var (name, value) in attributes)
        {
            // Empty is not how AD is told "no value" either; the LDAP implementation deletes the attribute instead.
            var v = string.IsNullOrEmpty(value) ? null : value;
            updated = name.ToLowerInvariant() switch
            {
                "displayname" => updated with { DisplayName = v },
                "givenname" => updated with { GivenName = v },
                "sn" or "surname" => updated with { Surname = v },
                "mail" => updated with { Mail = v },
                "title" => updated with { JobTitle = v },
                "department" => updated with { Department = v },
                "company" => updated with { Company = v },
                "physicaldeliveryofficename" or "office" => updated with { Office = v },
                "employeeid" => updated with { EmployeeId = v },
                "description" => updated with { Description = v },
                "manager" => updated with { ManagerDn = v },
                _ => updated,
            };
        }

        if (updated == user) return Task.FromResult(DirectoryOutcome.NoOp("Nothing needed changing"));

        Replace(objectGuid, updated.DistinguishedName, updated);
        return Task.FromResult(DirectoryOutcome.Ok($"Updated {attributes.Count} attribute{(attributes.Count == 1 ? string.Empty : "s")} on {updated.Primary}"));
    }

    public Task<DirectoryOutcome> SetEnabledAsync(Guid objectGuid, bool enabled, CancellationToken ct)
    {
        if (Get<OnPremUser>(objectGuid) is { } user)
        {
            if (user.Enabled == enabled) return Task.FromResult(DirectoryOutcome.NoOp($"{user.Primary} is already {(enabled ? "enabled" : "disabled")}"));
            Replace(objectGuid, user.DistinguishedName, user with { UserAccountControl = AccountFlags.SetEnabled(user.UserAccountControl, enabled) });
            return Task.FromResult(DirectoryOutcome.Ok($"{(enabled ? "Enabled" : "Disabled")} {user.Primary}"));
        }

        if (Get<OnPremComputer>(objectGuid) is { } computer)
        {
            if (computer.Enabled == enabled) return Task.FromResult(DirectoryOutcome.NoOp($"{computer.Name} is already {(enabled ? "enabled" : "disabled")}"));
            Replace(objectGuid, computer.DistinguishedName, computer with { UserAccountControl = AccountFlags.SetEnabled(computer.UserAccountControl, enabled) });
            return Task.FromResult(DirectoryOutcome.Ok($"{(enabled ? "Enabled" : "Disabled")} {computer.Name}"));
        }

        return Task.FromResult(DirectoryOutcome.Failed("Set enabled", DirectoryErrorText.NoSuchObject, null, "that object"));
    }

    public Task<DirectoryOutcome> UnlockAsync(Guid objectGuid, CancellationToken ct)
    {
        if (Get<OnPremUser>(objectGuid) is not { } user)
        {
            return Task.FromResult(DirectoryOutcome.Failed("Unlock", DirectoryErrorText.NoSuchObject, null, "that user"));
        }

        if (!user.IsLockedOut) return Task.FromResult(DirectoryOutcome.NoOp($"{user.Primary} is not locked out"));
        Replace(objectGuid, user.DistinguishedName, user with { LockedOutUntil = null });
        return Task.FromResult(DirectoryOutcome.Ok($"Unlocked {user.Primary}"));
    }

    public Task<DirectoryOutcome> SetPasswordAsync(Guid objectGuid, string password, bool mustChangeAtNextLogon, CancellationToken ct)
    {
        if (Get<OnPremUser>(objectGuid) is not { } user)
        {
            return Task.FromResult(DirectoryOutcome.Failed("Set password", DirectoryErrorText.NoSuchObject, null, "that user"));
        }

        // A crude stand-in for the domain policy, so the caller has to handle the refusal rather than assume success.
        if (password.Length < 8)
        {
            return Task.FromResult(DirectoryOutcome.Failed($"Set the password for {user.Primary}",
                DirectoryErrorText.ConstraintViolation, "0000052D: the password does not meet requirements, data 52d", "that user"));
        }

        Replace(objectGuid, user.DistinguishedName, user with { PasswordLastSet = DateTimeOffset.UtcNow });
        return Task.FromResult(DirectoryOutcome.Ok($"Set the password for {user.Primary}"));
    }

    public Task<DirectoryOutcome> MoveAsync(Guid objectGuid, string targetOuDn, CancellationToken ct)
    {
        if (!_objects.TryGetValue(objectGuid, out var entry))
        {
            return Task.FromResult(DirectoryOutcome.Failed("Move", DirectoryErrorText.NoSuchObject, null, "that object"));
        }

        if (!All<OnPremOu>().Any(o => string.Equals(o.DistinguishedName, targetOuDn, StringComparison.OrdinalIgnoreCase))
            && !string.Equals(targetOuDn, _rootDn, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(DirectoryOutcome.Failed("Move", DirectoryErrorText.NoSuchObject, null, "the destination"));
        }

        // Moving a container into its own subtree would orphan the branch; AD refuses this and so does the fake.
        if (DistinguishedNames.IsUnder(targetOuDn, entry.Dn))
        {
            return Task.FromResult(DirectoryOutcome.Refused("Move", "That would move a container inside itself."));
        }

        var leaf = DistinguishedNames.Split(entry.Dn).First();
        var newDn = $"{leaf},{targetOuDn}";
        if (Exists(newDn)) return Task.FromResult(DirectoryOutcome.Failed("Move", DirectoryErrorText.EntryAlreadyExists, null));

        Rebase(objectGuid, entry, newDn);
        return Task.FromResult(DirectoryOutcome.Ok($"Moved to {DistinguishedNames.Friendly(targetOuDn)}"));
    }

    public Task<DirectoryOutcome> RenameAsync(Guid objectGuid, string newName, CancellationToken ct)
    {
        if (!_objects.TryGetValue(objectGuid, out var entry))
        {
            return Task.FromResult(DirectoryOutcome.Failed("Rename", DirectoryErrorText.NoSuchObject, null, "that object"));
        }

        var prefix = entry.Value is OnPremOu ? "OU" : "CN";
        var parent = DistinguishedNames.Parent(entry.Dn)!;
        var newDn = DistinguishedNames.Child(prefix, newName, parent);
        if (Exists(newDn)) return Task.FromResult(DirectoryOutcome.Failed("Rename", DirectoryErrorText.EntryAlreadyExists, null));

        Rebase(objectGuid, entry, newDn, newName);
        return Task.FromResult(DirectoryOutcome.Ok($"Renamed to {newName}"));
    }

    public Task<DirectoryOutcome> AddToGroupAsync(Guid groupGuid, Guid memberGuid, CancellationToken ct)
    {
        if (Get<OnPremGroup>(groupGuid) is not { } group)
        {
            return Task.FromResult(DirectoryOutcome.Failed("Add to group", DirectoryErrorText.NoSuchObject, null, "that group"));
        }

        if (!_objects.TryGetValue(memberGuid, out var member))
        {
            return Task.FromResult(DirectoryOutcome.Failed("Add to group", DirectoryErrorText.NoSuchObject, null, "that member"));
        }

        if (groupGuid == memberGuid) return Task.FromResult(DirectoryOutcome.Refused("Add to group", "A group cannot be a member of itself."));

        if (group.MemberDns.Contains(member.Dn, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(DirectoryOutcome.NoOp($"Already a member of {group.Primary}"));
        }

        Replace(groupGuid, group.DistinguishedName, group with { MemberDns = [.. group.MemberDns, member.Dn] });
        return Task.FromResult(DirectoryOutcome.Ok($"Added to {group.Primary}"));
    }

    public Task<DirectoryOutcome> RemoveFromGroupAsync(Guid groupGuid, Guid memberGuid, CancellationToken ct)
    {
        if (Get<OnPremGroup>(groupGuid) is not { } group)
        {
            return Task.FromResult(DirectoryOutcome.Failed("Remove from group", DirectoryErrorText.NoSuchObject, null, "that group"));
        }

        if (!_objects.TryGetValue(memberGuid, out var member))
        {
            return Task.FromResult(DirectoryOutcome.Failed("Remove from group", DirectoryErrorText.NoSuchObject, null, "that member"));
        }

        // The primary group is not in the member list and cannot be removed this way. AD says nothing useful about
        // this — the write appears to succeed and changes nothing — so it is refused here with the actual reason.
        if (member.Value is OnPremUser u && IsPrimaryGroup(group, u))
        {
            return Task.FromResult(DirectoryOutcome.Refused("Remove from group",
                $"{group.Primary} is {u.Primary}'s primary group. Change the primary group first, or this does nothing."));
        }

        if (!group.MemberDns.Contains(member.Dn, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(DirectoryOutcome.NoOp($"Not a member of {group.Primary}"));
        }

        Replace(groupGuid, group.DistinguishedName,
            group with { MemberDns = [.. group.MemberDns.Where(d => !string.Equals(d, member.Dn, StringComparison.OrdinalIgnoreCase))] });
        return Task.FromResult(DirectoryOutcome.Ok($"Removed from {group.Primary}"));
    }

    public Task<DirectoryOutcome> DeleteAsync(Guid objectGuid, CancellationToken ct)
    {
        if (!_objects.TryGetValue(objectGuid, out var entry))
        {
            return Task.FromResult(DirectoryOutcome.Failed("Delete", DirectoryErrorText.NoSuchObject, null, "that object"));
        }

        if (entry.Value is OnPremOu { ProtectedFromAccidentalDeletion: true } ou)
        {
            return Task.FromResult(DirectoryOutcome.Refused("Delete",
                $"{ou.Name} is protected from accidental deletion. Untick that in its properties first."));
        }

        if (entry.Value is OnPremGroup { IsCritical: true } critical)
        {
            return Task.FromResult(DirectoryOutcome.Refused("Delete", $"{critical.Primary} is a built-in group and cannot be deleted."));
        }

        if (_objects.Values.Any(e => DistinguishedNames.IsUnder(e.Dn, entry.Dn)))
        {
            return Task.FromResult(DirectoryOutcome.Failed("Delete", DirectoryErrorText.NotAllowedOnNonLeaf, null));
        }

        _objects.TryRemove(objectGuid, out _);
        return Task.FromResult(DirectoryOutcome.Ok($"Deleted {DistinguishedNames.LeafName(entry.Dn)}", awaitingSync: true));
    }

    // ---- internals -----------------------------------------------------------------------------------------------

    private sealed record Entry(string Dn, object Value);

    private void Insert(Guid guid, string dn, object value) => _objects[guid] = new Entry(dn, value);

    private void Replace(Guid guid, string dn, object value) => _objects[guid] = new Entry(dn, value);

    private bool Exists(string dn) =>
        _objects.Values.Any(e => string.Equals(e.Dn, dn, StringComparison.OrdinalIgnoreCase));

    private T? Get<T>(Guid guid) where T : class =>
        _objects.TryGetValue(guid, out var e) ? e.Value as T : null;

    private IEnumerable<T> All<T>() where T : class => _objects.Values.Select(e => e.Value).OfType<T>();

    private static bool IsPrimaryGroup(OnPremGroup group, OnPremUser user) =>
        user.PrimaryGroupId == 513 && group.SamAccountName.Equals("Domain Users", StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves or renames an object and everything beneath it, keeping every GUID stable.</summary>
    private void Rebase(Guid guid, Entry entry, string newDn, string? newName = null)
    {
        var oldDn = entry.Dn;

        foreach (var (childGuid, child) in _objects.ToArray())
        {
            if (!DistinguishedNames.IsUnder(child.Dn, oldDn)) continue;
            var rebased = child.Dn[..^oldDn.Length] + newDn;
            _objects[childGuid] = child with { Dn = rebased, Value = WithDn(child.Value, rebased) };
        }

        var value = WithDn(entry.Value, newDn, newName);
        _objects[guid] = new Entry(newDn, value);
    }

    private static object WithDn(object value, string dn, string? newName = null) => value switch
    {
        OnPremUser u => u with { DistinguishedName = dn, DisplayName = newName ?? u.DisplayName },
        OnPremGroup g => g with { DistinguishedName = dn, DisplayName = newName ?? g.DisplayName },
        OnPremComputer c => c with { DistinguishedName = dn, Name = newName ?? c.Name },
        OnPremOu o => o with { DistinguishedName = dn, Name = newName ?? o.Name },
        _ => value,
    };

    /// <summary>An LDAP subtree search: the base itself and everything beneath it.</summary>
    private static bool Under(string dn, string? baseDn) =>
        string.IsNullOrWhiteSpace(baseDn)
        || string.Equals(dn, baseDn, StringComparison.OrdinalIgnoreCase)
        || DistinguishedNames.IsUnder(dn, baseDn);

    private static bool Matches(string text, params string?[] fields) =>
        string.IsNullOrWhiteSpace(text)
        || fields.Any(f => f is not null && f.Contains(text, StringComparison.OrdinalIgnoreCase));

    internal static bool TryGuidFromImmutableId(string? immutableId, out Guid guid)
    {
        guid = Guid.Empty;
        if (string.IsNullOrWhiteSpace(immutableId)) return false;

        try
        {
            var bytes = Convert.FromBase64String(immutableId);
            if (bytes.Length != 16) return false;
            guid = new Guid(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
