using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Core.OnPrem;

/// <summary>
/// Talks to a real domain controller over LDAP (M12.2), as the signed-in Windows user.
/// </summary>
/// <remarks>
/// <para>
/// The bind is Negotiate with signing and sealing, so the admin's own Kerberos ticket authenticates and AD enforces
/// whatever they have been delegated — the on-prem equivalent of Phase 1's delegated-only rule. There is no service
/// account and no stored credential; explicit credentials exist only so the lab can be driven from a machine that is
/// not domain-joined.
/// </para>
/// <para>
/// Sealing also matters for a second reason: <c>unicodePwd</c> is refused over an unprotected connection. A sealed
/// Negotiate bind satisfies that on port 389 without needing a certificate, which is the difference between password
/// resets working out of the box and needing a PKI conversation first.
/// </para>
/// </remarks>
// Windows only, and always was: the bind is Negotiate through the platform's own SSPI, and reading the "protect from
// accidental deletion" ACE needs the Windows security-descriptor model. Saying so lets the analyser check callers
// rather than warning inside every method that touches either.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class LdapDirectoryService : IDirectoryService, IDisposable
{
    private readonly LdapOptions _options;
    private readonly ILogger<LdapDirectoryService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LdapConnection? _connection;
    private DirectoryStatus _status = DirectoryStatus.NotConfigured;

    public LdapDirectoryService(LdapOptions options, ILogger<LdapDirectoryService> logger)
    {
        _options = options;
        _logger = logger;
    }

    private const string UserFilter = "(&(objectCategory=person)(objectClass=user))";
    private const string GroupFilter = "(objectClass=group)";
    private const string ComputerFilter = "(objectClass=computer)";
    private const string OuFilter = "(objectClass=organizationalUnit)";

    private static readonly string[] UserAttributes =
    [
        "objectGUID", "distinguishedName", "sAMAccountName", "userPrincipalName", "displayName", "givenName", "sn",
        "mail", "title", "department", "company", "physicalDeliveryOfficeName", "employeeID", "description",
        "manager", "userAccountControl", "whenCreated", "lastLogonTimestamp", "pwdLastSet", "lockoutTime",
        "memberOf", "primaryGroupID", "mS-DS-ConsistencyGuid",
    ];

    private static readonly string[] GroupAttributes =
    [
        "objectGUID", "distinguishedName", "sAMAccountName", "displayName", "description", "mail",
        "groupType", "member", "memberOf", "isCriticalSystemObject",
    ];

    private static readonly string[] ComputerAttributes =
    [
        "objectGUID", "distinguishedName", "sAMAccountName", "name", "dNSHostName", "operatingSystem",
        "operatingSystemVersion", "description", "userAccountControl", "whenCreated", "lastLogonTimestamp",
    ];

    private static readonly string[] OuAttributes =
    [
        "objectGUID", "distinguishedName", "ou", "name", "description", "isCriticalSystemObject",
        "nTSecurityDescriptor",
    ];

    // ---- connection ----------------------------------------------------------------------------------------------

    public async Task<DirectoryStatus> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            return _status;
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException or InvalidOperationException)
        {
            // "No domain is configured" and "the configured domain did not answer" are different situations to be in,
            // and the page says whichever it is. Reporting an unconfigured install as unreachable would send an
            // admin looking for a network fault that does not exist.
            if (!_status.IsConnected && _status.Reason is not null && string.IsNullOrWhiteSpace(_options.Domain))
            {
                return _status;
            }

            _logger.LogWarning(ex, "No domain controller reachable");
            return DirectoryStatus.Unreachable(ex.Message);
        }
    }

    private async Task<LdapConnection> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connection is not null && _status.IsConnected) return _connection;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is not null && _status.IsConnected) return _connection;

            if (string.IsNullOrWhiteSpace(_options.Domain))
            {
                _status = DirectoryStatus.NotConfigured;
                throw new InvalidOperationException("No domain configured.");
            }

            // Serverless binding: handing the LDAP client a domain name rather than a hostname lets Windows locate a
            // domain controller through DNS SRV and site topology. Naming a specific DC would be a single point of
            // failure and would go stale the first time one is retired.
            // A named server is used verbatim; otherwise the domain goes in and Windows finds a controller itself.
            var hasServer = !string.IsNullOrWhiteSpace(_options.Server);
            var target = hasServer ? _options.Server : _options.Domain;
            var identifier = new LdapDirectoryIdentifier(target, _options.Port, fullyQualifiedDnsHostName: hasServer, connectionless: false);
            var connection = new LdapConnection(identifier)
            {
                // Negotiate whether or not credentials were supplied. The first version used Basic when a credential
                // was present, which a real domain controller refuses outright over an unprotected connection —
                // and Basic would send the password in the clear even where it were accepted. Explicit credentials
                // are a Kerberos/NTLM identity to bind as, not a different mechanism.
                AuthType = AuthType.Negotiate,
                Timeout = _options.Timeout,
            };
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

            if (_options.UseTls)
            {
                connection.SessionOptions.SecureSocketLayer = true;
            }
            else
            {
                // Signing and sealing, so the traffic is protected and unicodePwd is accepted on 389. This applies
                // with explicit credentials as well: the protection is a property of the connection, not of whose
                // identity is bound over it.
                connection.SessionOptions.Signing = true;
                connection.SessionOptions.Sealing = true;
            }

            if (_options.Credential is not null)
            {
                connection.Bind(_options.Credential);
            }
            else
            {
                connection.Bind();
            }

            var root = ReadRootDse(connection);
            _connection = connection;
            _status = new DirectoryStatus
            {
                IsConnected = true,
                DomainName = _options.Domain,
                RootDn = root.DefaultNamingContext,
                DomainController = root.DnsHostName ?? target,
                BoundAs = _options.Credential?.UserName ?? WindowsIdentityName(),
            };

            _logger.LogInformation("Bound to {Dc} for {Domain}", root.DnsHostName, _options.Domain);
            return connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static (string? DefaultNamingContext, string? DnsHostName) ReadRootDse(LdapConnection connection)
    {
        var request = new SearchRequest(null, "(objectClass=*)", SearchScope.Base, "defaultNamingContext", "dnsHostName");
        var response = (SearchResponse)connection.SendRequest(request);
        if (response.Entries.Count == 0) return (null, null);

        var entry = response.Entries[0];
        return (LdapAttributes.String(entry, "defaultNamingContext"), LdapAttributes.String(entry, "dnsHostName"));
    }

    private static string WindowsIdentityName()
    {
        // Core targets all platforms even though the LDAP path only runs on Windows, so the guard is real rather than
        // ceremonial — and it is what lets the analyser prove the call is safe.
        if (!OperatingSystem.IsWindows()) return "the signed-in user";

        try
        {
            return System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return "the signed-in user";
        }
    }

    // ---- reads ---------------------------------------------------------------------------------------------------

    public Task<OnPremUser?> GetUserAsync(Guid objectGuid, CancellationToken ct) =>
        FindOneAsync($"(&{UserFilter}(objectGUID={LdapAttributes.GuidFilter(objectGuid)}))", UserAttributes, ReadUser, ct);

    public Task<OnPremUser?> GetUserByImmutableIdAsync(string immutableId, CancellationToken ct)
    {
        // Entra's immutableId is base64 of the AD GUID; that is the whole hybrid join.
        if (!FakeDirectoryService.TryGuidFromImmutableId(immutableId, out var guid))
        {
            return Task.FromResult<OnPremUser?>(null);
        }

        return GetUserAsync(guid, ct);
    }

    public Task<IReadOnlyList<OnPremUser>> SearchUsersAsync(string text, int limit, string? searchBaseDn, CancellationToken ct) =>
        SearchAsync(Contains(UserFilter, text, "sAMAccountName", "displayName", "userPrincipalName", "mail"),
            UserAttributes, ReadUser, limit, ct, searchBaseDn);

    public Task<OnPremGroup?> GetGroupAsync(Guid objectGuid, CancellationToken ct) =>
        FindOneAsync($"(&{GroupFilter}(objectGUID={LdapAttributes.GuidFilter(objectGuid)}))", GroupAttributes, ReadGroup, ct);

    public Task<IReadOnlyList<OnPremGroup>> SearchGroupsAsync(string text, int limit, string? searchBaseDn, CancellationToken ct) =>
        SearchAsync(Contains(GroupFilter, text, "sAMAccountName", "displayName", "description"),
            GroupAttributes, ReadGroup, limit, ct, searchBaseDn);

    public async Task<IReadOnlyList<OnPremMember>> GetGroupMembersAsync(Guid objectGuid, CancellationToken ct)
    {
        var group = await GetGroupAsync(objectGuid, ct).ConfigureAwait(false);
        if (group is null) return [];

        var members = new List<OnPremMember>(group.MemberDns.Count);
        foreach (var dn in group.MemberDns)
        {
            ct.ThrowIfCancellationRequested();
            var entry = await ReadByDnAsync(dn, ["objectGUID", "distinguishedName", "objectClass", "sAMAccountName", "displayName", "name", "userAccountControl", "groupType"], ct).ConfigureAwait(false);
            if (entry is null) continue;

            var classes = LdapAttributes.Strings(entry, "objectClass");
            var kind = classes.Contains("computer", StringComparer.OrdinalIgnoreCase) ? OnPremMemberKind.Computer
                : classes.Contains("group", StringComparer.OrdinalIgnoreCase) ? OnPremMemberKind.Group
                : classes.Contains("user", StringComparer.OrdinalIgnoreCase) ? OnPremMemberKind.User
                : OnPremMemberKind.Other;

            var uac = LdapAttributes.Int(entry, "userAccountControl");
            members.Add(new OnPremMember
            {
                ObjectGuid = LdapAttributes.Guid(entry, "objectGUID") ?? Guid.Empty,
                DistinguishedName = entry.DistinguishedName,
                Name = LdapAttributes.String(entry, "displayName") ?? LdapAttributes.String(entry, "name")
                       ?? DistinguishedNames.LeafName(entry.DistinguishedName) ?? entry.DistinguishedName,
                SamAccountName = LdapAttributes.String(entry, "sAMAccountName"),
                Kind = kind,
                Enabled = kind == OnPremMemberKind.Group || !AccountFlags.IsDisabled(uac),
                Scope = kind == OnPremMemberKind.Group ? LdapAttributes.GroupType(LdapAttributes.Int(entry, "groupType")).Scope : null,
            });
        }

        return members;
    }

    public Task<OnPremComputer?> GetComputerAsync(Guid objectGuid, CancellationToken ct) =>
        FindOneAsync($"(&{ComputerFilter}(objectGUID={LdapAttributes.GuidFilter(objectGuid)}))", ComputerAttributes, ReadComputer, ct);

    public Task<IReadOnlyList<OnPremComputer>> SearchComputersAsync(string text, int limit, string? searchBaseDn, CancellationToken ct) =>
        SearchAsync(Contains(ComputerFilter, text, "name", "dNSHostName", "description"),
            ComputerAttributes, ReadComputer, limit, ct, searchBaseDn);

    public Task<IReadOnlyList<OnPremOu>> GetOrganisationalUnitsAsync(CancellationToken ct) =>
        SearchAsync(OuFilter, OuAttributes, ReadOu, 5000, ct);

    public Task<OnPremOu?> GetOrganisationalUnitAsync(Guid objectGuid, CancellationToken ct) =>
        FindOneAsync($"(&{OuFilter}(objectGUID={LdapAttributes.GuidFilter(objectGuid)}))", OuAttributes, ReadOu, ct);

    public async Task<IReadOnlySet<string>?> GetWritableAttributesAsync(Guid objectGuid, CancellationToken ct)
    {
        var dn = await ResolveDnAsync(objectGuid, ct).ConfigureAwait(false);
        if (dn is null) return null;

        // allowedAttributesEffective is constructed by the DC per object and per caller, so it is not returned by a
        // normal search and has to be asked for by name against that one entry.
        var entry = await ReadByDnAsync(dn, ["allowedAttributesEffective"], ct).ConfigureAwait(false);
        if (entry is null) return null;

        var names = LdapAttributes.Strings(entry, "allowedAttributesEffective");
        if (names.Count == 0) return null;

        return names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ---- writes --------------------------------------------------------------------------------------------------

    public async Task<DirectoryOutcome> SetAttributesAsync(Guid objectGuid, IReadOnlyDictionary<string, string?> attributes, CancellationToken ct)
    {
        var dn = await ResolveDnAsync(objectGuid, ct).ConfigureAwait(false);
        if (dn is null) return DirectoryOutcome.Failed("Set attributes", DirectoryErrorText.NoSuchObject, null, "that object");

        var request = new ModifyRequest(dn);
        foreach (var (name, value) in attributes)
        {
            // Clearing means deleting the attribute. An empty string is a value, and AD rejects it for most
            // attributes — exactly the trap Graph sprang with department accepting "" for officeLocation and not for
            // department.
            // Add, never the [0] indexer: the setter replaces an existing element and the collection starts empty,
            // so an object initialiser using [0] compiles and then throws at runtime on the first write.
            var modification = new DirectoryAttributeModification { Name = name };
            if (string.IsNullOrEmpty(value))
            {
                modification.Operation = DirectoryAttributeOperation.Delete;
            }
            else
            {
                modification.Operation = DirectoryAttributeOperation.Replace;
                modification.Add(value);
            }

            request.Modifications.Add(modification);
        }

        // NoSuchAttribute is benign for a delete and never for a replace, and the result code alone cannot tell the
        // two situations apart: a real DC answers 16 both for "that attribute is already absent" and for "this
        // schema has no such attribute at all" — the second being what a forest without the Exchange extension says
        // about mailNickname. Treating 16 as benign for every request reported a write that never happened as a
        // successful no-op, so the operation is what decides. A delete that returns 16 has the end state it wanted;
        // a replace that returns 16 wrote nothing and must say so.
        var benign = attributes.Values.All(string.IsNullOrEmpty) ? new[] { DirectoryErrorText.NoSuchAttribute } : [];

        return await SendAsync(request, $"Updated {attributes.Count} attribute{(attributes.Count == 1 ? string.Empty : "s")}", "that object", ct,
            benign).ConfigureAwait(false);
    }

    public async Task<DirectoryOutcome> SetEnabledAsync(Guid objectGuid, bool enabled, CancellationToken ct)
    {
        var entry = await ReadRawAsync(objectGuid, ["distinguishedName", "userAccountControl", "displayName", "name"], ct).ConfigureAwait(false);
        if (entry is null) return DirectoryOutcome.Failed("Set enabled", DirectoryErrorText.NoSuchObject, null, "that object");

        var current = LdapAttributes.Int(entry, "userAccountControl");
        var wanted = AccountFlags.SetEnabled(current, enabled);
        var label = LdapAttributes.String(entry, "displayName") ?? LdapAttributes.String(entry, "name") ?? "it";
        if (current == wanted) return DirectoryOutcome.NoOp($"{label} is already {(enabled ? "enabled" : "disabled")}");

        var request = new ModifyRequest(entry.DistinguishedName, DirectoryAttributeOperation.Replace,
            "userAccountControl", wanted.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await SendAsync(request, $"{(enabled ? "Enabled" : "Disabled")} {label}", "that object", ct).ConfigureAwait(false);
    }

    public async Task<DirectoryOutcome> UnlockAsync(Guid objectGuid, CancellationToken ct)
    {
        var dn = await ResolveDnAsync(objectGuid, ct).ConfigureAwait(false);
        if (dn is null) return DirectoryOutcome.Failed("Unlock", DirectoryErrorText.NoSuchObject, null, "that user");

        // Unlocking is writing 0 to lockoutTime; there is no dedicated operation.
        var request = new ModifyRequest(dn, DirectoryAttributeOperation.Replace, "lockoutTime", "0");
        return await SendAsync(request, "Unlocked the account", "that user", ct).ConfigureAwait(false);
    }

    public async Task<DirectoryOutcome> SetPasswordAsync(Guid objectGuid, string password, bool mustChangeAtNextLogon, CancellationToken ct)
    {
        var dn = await ResolveDnAsync(objectGuid, ct).ConfigureAwait(false);
        if (dn is null) return DirectoryOutcome.Failed("Set password", DirectoryErrorText.NoSuchObject, null, "that user");

        var request = new ModifyRequest(dn);
        var change = new DirectoryAttributeModification
        {
            Name = "unicodePwd",
            Operation = DirectoryAttributeOperation.Replace,
        };
        change.Add(LdapAttributes.EncodePassword(password));
        request.Modifications.Add(change);

        var outcome = await SendAsync(request, "Set the password", "that user", ct).ConfigureAwait(false);
        if (!outcome.Success || !mustChangeAtNextLogon) return outcome;

        // pwdLastSet = 0 forces a change at next sign-in. It has to be a second write: AD refuses both in one.
        var force = new ModifyRequest(dn, DirectoryAttributeOperation.Replace, "pwdLastSet", "0");
        var forced = await SendAsync(force, "Set the password", "that user", ct).ConfigureAwait(false);
        return forced.Success
            ? DirectoryOutcome.Ok("Set the password, and it must be changed at next sign-in")
            : outcome with { Summary = "Set the password, but could not require a change at next sign-in" };
    }

    public async Task<DirectoryOutcome> MoveAsync(Guid objectGuid, string targetOuDn, CancellationToken ct)
    {
        var dn = await ResolveDnAsync(objectGuid, ct).ConfigureAwait(false);
        if (dn is null) return DirectoryOutcome.Failed("Move", DirectoryErrorText.NoSuchObject, null, "that object");

        var leaf = DistinguishedNames.Split(dn).FirstOrDefault();
        if (leaf is null) return DirectoryOutcome.Refused("Move", "That object's name could not be read.");

        // A destination that does not exist and one that is not a valid distinguished name come back from AD as the
        // same thing: result code 80 with "problem 5012 (DIR_ERROR)". Nothing in that answer says which, so the
        // destination is checked first and the console can name the actual problem.
        if (await ReadByDnAsync(targetOuDn, ["distinguishedName"], ct).ConfigureAwait(false) is null)
        {
            // Echoing a path that is not a distinguished name back at the admin reads as nonsense, so it is only
            // named when it is one.
            var looksLikeDn = DistinguishedNames.Split(targetOuDn).Any(p => p.Contains('='));
            return DirectoryOutcome.Refused("Move", looksLikeDn
                ? $"The destination container was not found. Check that {DistinguishedNames.FriendlyPath(targetOuDn)} still exists and that you can see it."
                : "The destination is not a valid container path, so there is nowhere to move this to.");
        }

        var request = new ModifyDNRequest(dn, targetOuDn, leaf) { DeleteOldRdn = true };
        return await SendAsync(request, $"Moved to {DistinguishedNames.FriendlyPath(targetOuDn)}", "that object", ct).ConfigureAwait(false);
    }

    public async Task<DirectoryOutcome> RenameAsync(Guid objectGuid, string newName, CancellationToken ct)
    {
        var entry = await ReadRawAsync(objectGuid, ["distinguishedName", "objectClass"], ct).ConfigureAwait(false);
        if (entry is null) return DirectoryOutcome.Failed("Rename", DirectoryErrorText.NoSuchObject, null, "that object");

        var parent = DistinguishedNames.Parent(entry.DistinguishedName);
        if (parent is null) return DirectoryOutcome.Refused("Rename", "That object has no parent container.");

        var isOu = LdapAttributes.Strings(entry, "objectClass").Contains("organizationalUnit", StringComparer.OrdinalIgnoreCase);
        var rdn = $"{(isOu ? "OU" : "CN")}={DistinguishedNames.Escape(newName)}";

        var request = new ModifyDNRequest(entry.DistinguishedName, parent, rdn) { DeleteOldRdn = true };
        return await SendAsync(request, $"Renamed to {newName}", "that object", ct).ConfigureAwait(false);
    }

    public Task<DirectoryOutcome> AddToGroupAsync(Guid groupGuid, Guid memberGuid, CancellationToken ct) =>
        ChangeMembershipAsync(groupGuid, memberGuid, DirectoryAttributeOperation.Add, "Added to", ct);

    public Task<DirectoryOutcome> RemoveFromGroupAsync(Guid groupGuid, Guid memberGuid, CancellationToken ct) =>
        ChangeMembershipAsync(groupGuid, memberGuid, DirectoryAttributeOperation.Delete, "Removed from", ct);

    private async Task<DirectoryOutcome> ChangeMembershipAsync(Guid groupGuid, Guid memberGuid, DirectoryAttributeOperation operation, string verb, CancellationToken ct)
    {
        var group = await GetGroupAsync(groupGuid, ct).ConfigureAwait(false);
        if (group is null) return DirectoryOutcome.Failed($"{verb} group", DirectoryErrorText.NoSuchObject, null, "that group");

        var memberDn = await ResolveDnAsync(memberGuid, ct).ConfigureAwait(false);
        if (memberDn is null) return DirectoryOutcome.Failed($"{verb} group", DirectoryErrorText.NoSuchObject, null, "that member");

        // A primary group is not in the member list, so AD accepts the removal and changes nothing — the worst kind
        // of answer. Refuse it with the actual reason instead.
        if (operation == DirectoryAttributeOperation.Delete)
        {
            var member = await ReadRawAsync(memberGuid, ["primaryGroupID", "displayName", "name"], ct).ConfigureAwait(false);
            if (member is not null && await IsPrimaryGroupAsync(group, LdapAttributes.Int(member, "primaryGroupID"), ct).ConfigureAwait(false))
            {
                var who = LdapAttributes.String(member, "displayName") ?? LdapAttributes.String(member, "name") ?? "that member";
                return DirectoryOutcome.Refused($"{verb} group",
                    $"{group.Primary} is {who}'s primary group. Change the primary group first, or this does nothing.");
            }
        }

        // What may go in a group depends on the group's scope, and AD answers a breach of that with
        // "unwilling to perform" — true, unhelpful, and identical to half a dozen other refusals.
        string? warning = null;
        if (operation == DirectoryAttributeOperation.Add)
        {
            // A loop is allowed by the directory, so it is said out loud rather than refused.
            warning = GroupRules.CycleWarning(group, memberGuid, memberDn);

            var candidate = await ReadRawAsync(memberGuid, ["objectClass", "groupType"], ct).ConfigureAwait(false);
            if (candidate is not null)
            {
                var classes = LdapAttributes.Strings(candidate, "objectClass");
                var kind = classes.Contains("group", StringComparer.OrdinalIgnoreCase) ? OnPremMemberKind.Group : OnPremMemberKind.User;
                var scope = kind == OnPremMemberKind.Group
                    ? LdapAttributes.GroupType(LdapAttributes.Int(candidate, "groupType")).Scope
                    : (GroupScope?)null;

                if (GroupRules.Nesting(group.Scope, kind, scope) is { } nesting)
                {
                    return DirectoryOutcome.Refused($"{verb} group", nesting);
                }
            }
        }

        var request = new ModifyRequest(group.DistinguishedName, operation, "member", memberDn);
        var outcome = await SendAsync(request, $"{verb} {group.Primary}", "that group", ct,
            DirectoryErrorText.EntryAlreadyExists, DirectoryErrorText.NoSuchAttribute).ConfigureAwait(false);

        return outcome.Success && warning is not null ? outcome with { Warning = warning } : outcome;
    }

    public async Task<DirectoryOutcome> SetGroupTypeAsync(Guid groupGuid, GroupScope scope, GroupCategory category, CancellationToken ct)
    {
        const string what = "Change group type";

        var group = await GetGroupAsync(groupGuid, ct).ConfigureAwait(false);
        if (group is null) return DirectoryOutcome.Failed(what, DirectoryErrorText.NoSuchObject, null, "that group");

        if (group.Scope == scope && group.Category == category)
        {
            return DirectoryOutcome.NoOp($"{group.Primary} is already a {group.ScopeText.ToLowerInvariant()}");
        }

        if (GroupRules.Protected(group) is { } builtIn) return DirectoryOutcome.Refused(what, builtIn);

        // The scope rules depend on what the group holds, so the members are read once and asked about here rather
        // than being guessed at.
        var members = await GetGroupMembersAsync(groupGuid, ct).ConfigureAwait(false);
        var refusal = GroupRules.ScopeChange(
            group.Scope,
            scope,
            isMemberOfGlobalGroup: await IsMemberOfGlobalGroupAsync(group, ct).ConfigureAwait(false),
            containsDomainLocalGroup: members.Any(m => m.Scope == GroupScope.DomainLocal),
            containsUniversalGroup: members.Any(m => m.Scope == GroupScope.Universal));

        if (refusal is not null) return DirectoryOutcome.Refused(what, refusal);

        var modification = new DirectoryAttributeModification { Name = "groupType", Operation = DirectoryAttributeOperation.Replace };
        modification.Add(LdapAttributes.GroupTypeValue(scope, category).ToString());
        var request = new ModifyRequest(group.DistinguishedName);
        request.Modifications.Add(modification);

        var summary = $"{group.Primary} is now a {OnPremGroup.Describe(scope, category).ToLowerInvariant()}";
        return await SendAsync(request, summary, "that group", ct).ConfigureAwait(false);
    }

    /// <summary>Whether any group this one belongs to is global — the one thing that blocks becoming universal.</summary>
    private async Task<bool> IsMemberOfGlobalGroupAsync(OnPremGroup group, CancellationToken ct)
    {
        foreach (var dn in group.MemberOfDns)
        {
            var entry = await ReadByDnAsync(dn, ["groupType"], ct).ConfigureAwait(false);
            if (entry is null) continue;
            if (LdapAttributes.GroupType(LdapAttributes.Int(entry, "groupType")).Scope == GroupScope.Global) return true;
        }

        return false;
    }

    public async Task<DirectoryOutcome> DeleteAsync(Guid objectGuid, CancellationToken ct)
    {
        var entry = await ReadRawAsync(objectGuid, ["distinguishedName", "name", "isCriticalSystemObject"], ct).ConfigureAwait(false);
        if (entry is null) return DirectoryOutcome.Failed("Delete", DirectoryErrorText.NoSuchObject, null, "that object");

        if (string.Equals(LdapAttributes.String(entry, "isCriticalSystemObject"), "TRUE", StringComparison.OrdinalIgnoreCase))
        {
            var name = LdapAttributes.String(entry, "name") ?? "That object";
            return DirectoryOutcome.Refused("Delete", $"{name} is a built-in object and cannot be deleted.");
        }

        // A container with anything under it comes back as NotAllowedOnNonLeaf, which is accurate and says nothing
        // about what is in the way. Counting first turns it into a number the admin can go and look at.
        var children = await CountChildrenAsync(entry.DistinguishedName, ct).ConfigureAwait(false);
        if (children > 0)
        {
            var name = LdapAttributes.String(entry, "name") ?? "That container";
            return DirectoryOutcome.Refused("Delete",
                $"{name} still holds {children} object{(children == 1 ? string.Empty : "s")}. Move or delete what is in it first.");
        }

        // "Protect object from accidental deletion" is a deny ACE, and AD answers it with a bare access-denied that
        // looks exactly like a missing delegation. Naming the checkbox is the difference between a two-minute fix
        // and an afternoon spent auditing rights.
        if (await IsProtectedFromDeletionAsync(entry.DistinguishedName, ct).ConfigureAwait(false))
        {
            var name = LdapAttributes.String(entry, "name") ?? "That object";
            return DirectoryOutcome.Refused("Delete",
                $"{name} has \"Protect object from accidental deletion\" ticked. Untick it on the Object tab in " +
                "Active Directory Users and Computers, then try again.");
        }

        var request = new DeleteRequest(entry.DistinguishedName);
        return await SendAsync(request, $"Deleted {LdapAttributes.String(entry, "name") ?? "the object"}", "that object", ct).ConfigureAwait(false);
    }

    // ---- plumbing ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sends a request. <paramref name="alreadyDone"/> lists result codes that mean the directory is already in the
    /// state being asked for — AD reports those as errors, and the console should not.
    /// </summary>
    public async Task<DirectoryOutcome> CreateOuAsync(string name, string parentDn, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return DirectoryOutcome.Refused("Create OU", "An organisational unit needs a name.");

        // The name goes into the distinguished name, so it is escaped rather than trusted: a comma or a plus in a
        // name would otherwise be read as extra components and put the OU somewhere nobody asked for.
        var dn = $"OU={DistinguishedNames.Escape(name.Trim())},{parentDn}";
        var request = new AddRequest(dn, new DirectoryAttribute("objectClass", "organizationalUnit"));
        return await SendAsync(request, $"Created {name.Trim()}", "that container", ct).ConfigureAwait(false);
    }

    private async Task<int> CountChildrenAsync(string dn, CancellationToken ct)
    {
        try
        {
            var connection = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            var request = new SearchRequest(dn, "(objectClass=*)", SearchScope.OneLevel, "distinguishedName");
            var response = await Task.Run(() => (SearchResponse)connection.SendRequest(request), ct).ConfigureAwait(false);
            return response.Entries.Count;
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            // Not being able to count is not a reason to block the delete. AD still gets the final say.
            return 0;
        }
    }

    /// <summary>
    /// Whether AD's "protect from accidental deletion" checkbox is ticked, which it stores as a deny ACE rather
    /// than as an attribute anybody can read.
    /// </summary>
    private async Task<bool> IsProtectedFromDeletionAsync(string dn, CancellationToken ct)
    {
        try
        {
            var connection = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            var request = new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, "nTSecurityDescriptor");
            // Without this control the DC returns the SACL too, which needs a privilege the admin may not hold, and
            // the whole read fails rather than coming back without it.
            request.Controls.Add(new SecurityDescriptorFlagControl(SecurityMasks.Dacl));
            var response = await Task.Run(() => (SearchResponse)connection.SendRequest(request), ct).ConfigureAwait(false);
            if (response.Entries.Count == 0) return false;

            return LdapAttributes.DeniesDeletion(LdapAttributes.Bytes(response.Entries[0], "nTSecurityDescriptor"));
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            return false;
        }
    }

    private async Task<DirectoryOutcome> SendAsync(DirectoryRequest request, string summary, string what, CancellationToken ct,
        params int[] alreadyDone)
    {
        try
        {
            var connection = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await Task.Run(() => connection.SendRequest(request), ct).ConfigureAwait(false);
            return DirectoryOutcome.Ok(summary);
        }
        catch (DirectoryOperationException ex) when (alreadyDone.Contains((int)(ex.Response?.ResultCode ?? ResultCode.Other)))
        {
            // Clearing an attribute that is already absent, or adding a member who is already in the group. Both are
            // the wanted state, and both come back from a real DC as failures.
            return DirectoryOutcome.NoOp($"{summary} — nothing needed changing");
        }
        catch (DirectoryOperationException ex)
        {
            _logger.LogWarning(ex, "LDAP {Operation} failed: {Code}", summary, ex.Response?.ResultCode);
            return DirectoryOutcome.Failed(summary, (int)(ex.Response?.ResultCode ?? ResultCode.Other), ex.Response?.ErrorMessage ?? ex.Message, what);
        }
        catch (LdapException ex)
        {
            _logger.LogWarning(ex, "LDAP {Operation} failed: {Code}", summary, ex.ErrorCode);
            return DirectoryOutcome.Failed(summary, ex.ErrorCode, ex.ServerErrorMessage ?? ex.Message, what);
        }
    }

    private async Task<T?> FindOneAsync<T>(string filter, string[] attributes, Func<SearchResultEntry, T> read, CancellationToken ct)
        where T : class
    {
        var results = await SearchAsync(filter, attributes, read, 1, ct).ConfigureAwait(false);
        return results.Count == 0 ? null : results[0];
    }

    private async Task<IReadOnlyList<T>> SearchAsync<T>(string filter, string[] attributes, Func<SearchResultEntry, T> read, int limit, CancellationToken ct, string? searchBaseDn = null)
    {
        try
        {
            var connection = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            var request = new SearchRequest(searchBaseDn ?? _status.RootDn, filter, SearchScope.Subtree, attributes) { SizeLimit = limit };

            var response = await Task.Run(() => (SearchResponse)connection.SendRequest(request), ct).ConfigureAwait(false);
            var results = new List<T>(response.Entries.Count);
            foreach (SearchResultEntry entry in response.Entries) results.Add(read(entry));
            return results;
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.SizeLimitExceeded)
        {
            // Partial results are still useful; the caller shows a "narrow the search" hint.
            _logger.LogInformation("LDAP search hit the size limit of {Limit}", limit);
            return [];
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            _logger.LogWarning(ex, "LDAP search failed");
            return [];
        }
    }

    private async Task<SearchResultEntry?> ReadRawAsync(Guid objectGuid, string[] attributes, CancellationToken ct)
    {
        var entries = await SearchAsync($"(objectGUID={LdapAttributes.GuidFilter(objectGuid)})", attributes, e => e, 1, ct).ConfigureAwait(false);
        return entries.Count == 0 ? null : entries[0];
    }

    private async Task<SearchResultEntry?> ReadByDnAsync(string dn, string[] attributes, CancellationToken ct)
    {
        try
        {
            var connection = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            var request = new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, attributes);
            var response = await Task.Run(() => (SearchResponse)connection.SendRequest(request), ct).ConfigureAwait(false);
            return response.Entries.Count == 0 ? null : response.Entries[0];
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            return null;
        }
    }

    private async Task<string?> ResolveDnAsync(Guid objectGuid, CancellationToken ct) =>
        (await ReadRawAsync(objectGuid, ["distinguishedName"], ct).ConfigureAwait(false))?.DistinguishedName;

    private async Task<bool> IsPrimaryGroupAsync(OnPremGroup group, int primaryGroupId, CancellationToken ct)
    {
        if (primaryGroupId <= 0) return false;
        var entry = await ReadByDnAsync(group.DistinguishedName, ["objectSid"], ct).ConfigureAwait(false);
        var sid = LdapAttributes.Bytes(entry!, "objectSid");
        if (sid is null || sid.Length < 4) return false;

        // The RID is the last four bytes of the SID, little-endian.
        var rid = BitConverter.ToInt32(sid, sid.Length - 4);
        return rid == primaryGroupId;
    }

    private static string Contains(string baseFilter, string text, params string[] attributes)
    {
        if (string.IsNullOrWhiteSpace(text)) return baseFilter;

        var escaped = LdapAttributes.EscapeFilter(text);
        var ors = string.Concat(attributes.Select(a => $"({a}=*{escaped}*)"));
        return $"(&{baseFilter}(|{ors}))";
    }

    // ---- projections ---------------------------------------------------------------------------------------------

    private static OnPremUser ReadUser(SearchResultEntry e) => new()
    {
        ObjectGuid = LdapAttributes.Guid(e, "objectGUID") ?? Guid.Empty,
        DistinguishedName = e.DistinguishedName,
        SamAccountName = LdapAttributes.String(e, "sAMAccountName") ?? string.Empty,
        UserPrincipalName = LdapAttributes.String(e, "userPrincipalName"),
        DisplayName = LdapAttributes.String(e, "displayName"),
        GivenName = LdapAttributes.String(e, "givenName"),
        Surname = LdapAttributes.String(e, "sn"),
        Mail = LdapAttributes.String(e, "mail"),
        JobTitle = LdapAttributes.String(e, "title"),
        Department = LdapAttributes.String(e, "department"),
        Company = LdapAttributes.String(e, "company"),
        Office = LdapAttributes.String(e, "physicalDeliveryOfficeName"),
        EmployeeId = LdapAttributes.String(e, "employeeID"),
        Description = LdapAttributes.String(e, "description"),
        ManagerDn = LdapAttributes.String(e, "manager"),
        UserAccountControl = LdapAttributes.Int(e, "userAccountControl"),
        WhenCreated = LdapAttributes.GeneralizedTime(e, "whenCreated"),
        LastLogon = LdapAttributes.FileTime(e, "lastLogonTimestamp"),
        PasswordLastSet = LdapAttributes.FileTime(e, "pwdLastSet"),
        LockedOutUntil = LdapAttributes.FileTime(e, "lockoutTime"),
        MemberOfDns = LdapAttributes.Strings(e, "memberOf"),
        PrimaryGroupId = LdapAttributes.Int(e, "primaryGroupID", 513),
        ConsistencyGuid = LdapAttributes.Bytes(e, "mS-DS-ConsistencyGuid"),
    };

    private static OnPremGroup ReadGroup(SearchResultEntry e)
    {
        var (scope, category) = LdapAttributes.GroupType(LdapAttributes.Int(e, "groupType"));
        return new OnPremGroup
        {
            ObjectGuid = LdapAttributes.Guid(e, "objectGUID") ?? Guid.Empty,
            DistinguishedName = e.DistinguishedName,
            SamAccountName = LdapAttributes.String(e, "sAMAccountName") ?? string.Empty,
            DisplayName = LdapAttributes.String(e, "displayName"),
            Description = LdapAttributes.String(e, "description"),
            Mail = LdapAttributes.String(e, "mail"),
            Scope = scope,
            Category = category,
            MemberDns = LdapAttributes.Strings(e, "member"),
            MemberOfDns = LdapAttributes.Strings(e, "memberOf"),
            IsCritical = string.Equals(LdapAttributes.String(e, "isCriticalSystemObject"), "TRUE", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static OnPremComputer ReadComputer(SearchResultEntry e) => new()
    {
        ObjectGuid = LdapAttributes.Guid(e, "objectGUID") ?? Guid.Empty,
        DistinguishedName = e.DistinguishedName,
        SamAccountName = LdapAttributes.String(e, "sAMAccountName") ?? string.Empty,
        Name = LdapAttributes.String(e, "name") ?? DistinguishedNames.LeafName(e.DistinguishedName) ?? string.Empty,
        DnsHostName = LdapAttributes.String(e, "dNSHostName"),
        OperatingSystem = LdapAttributes.String(e, "operatingSystem"),
        OperatingSystemVersion = LdapAttributes.String(e, "operatingSystemVersion"),
        Description = LdapAttributes.String(e, "description"),
        UserAccountControl = LdapAttributes.Int(e, "userAccountControl"),
        WhenCreated = LdapAttributes.GeneralizedTime(e, "whenCreated"),
        LastLogon = LdapAttributes.FileTime(e, "lastLogonTimestamp"),
    };

    private static OnPremOu ReadOu(SearchResultEntry e) => new()
    {
        ObjectGuid = LdapAttributes.Guid(e, "objectGUID") ?? Guid.Empty,
        DistinguishedName = e.DistinguishedName,
        Name = LdapAttributes.String(e, "ou") ?? LdapAttributes.String(e, "name")
               ?? DistinguishedNames.LeafName(e.DistinguishedName) ?? string.Empty,
        Description = LdapAttributes.String(e, "description"),
        // The real flag is a deny ACE on the object's security descriptor; reading it needs the descriptor, which
        // this does not fetch. Left false until 12.6 needs it, rather than guessed at.
        ProtectedFromAccidentalDeletion = false,
    };

    public void Dispose()
    {
        _connection?.Dispose();
        _gate.Dispose();
    }
}
