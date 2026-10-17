using System.Text.Json;
using Microsoft.Extensions.Logging;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;
using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Exchange;

/// <summary>
/// Mailbox writes (M14, doc 13 §3). Every write is routed by its target mailbox, logged with its inverse, and confirmed
/// by reading the mailbox back until Exchange shows the change: a <c>Set-*</c> answers an empty 200 before the change has
/// reached every server, and two quick writes to one attribute can land out of order (doc 13 §5). Exchange reports no
/// no-ops, so each write first checks whether the mailbox is already as asked.
/// </summary>
public sealed partial class ExchangeService
{
    private static readonly TimeSpan[] ConfirmDelays =
    [
        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5),
    ];

    private readonly Dictionary<string, DateTimeOffset> _lastWrite = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The least time between two writes to one mailbox (doc 13 §5).</summary>
    public TimeSpan SettleGap { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Clock for the settle gap; tests substitute a fake.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    // ---- reading what a write needs -----------------------------------------------------------------------------

    /// <summary>
    /// Exchange names → people, in one <c>Get-Recipient -Filter</c> per 20 names. A name that does not resolve stays as
    /// itself rather than vanishing, so an entry is never hidden because its owner was deleted.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, RecipientRef>> ResolveRecipientsAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var distinct = names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var map = new Dictionary<string, RecipientRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in distinct.Chunk(20))
        {
            var filter = string.Join(" -or ", chunk.Select(n => $"Name -eq '{n.Replace("'", "''", StringComparison.Ordinal)}'"));
            try
            {
                var result = await _client.InvokeAsync("Get-Recipient", new Dictionary<string, object?> { ["Filter"] = filter }, ct,
                    ["Name", "DisplayName", "PrimarySmtpAddress", "ExternalDirectoryObjectId", "RecipientTypeDetails"]).ConfigureAwait(false);
                foreach (var row in result.Items)
                {
                    if (ExchangeValues.NonEmpty(row, "Name") is not { } name) continue;
                    map[name] = new RecipientRef
                    {
                        ExchangeName = name,
                        DisplayName = ExchangeValues.NonEmpty(row, "DisplayName"),
                        PrimarySmtp = ExchangeValues.NonEmpty(row, "PrimarySmtpAddress"),
                        ObjectId = ExchangeValues.NonEmpty(row, "ExternalDirectoryObjectId"),
                        RecipientType = ExchangeValues.NonEmpty(row, "RecipientTypeDetails"),
                    };
                }
            }
            catch (ExchangeException ex)
            {
                _logger.LogWarning("Resolving {Count} recipient name(s) failed: {Code}", chunk.Length, ex.ErrorCode);
            }
        }

        foreach (var name in distinct.Where(n => !map.ContainsKey(n))) map[name] = RecipientRef.Unresolved(name);
        return map;
    }

    /// <summary>Whether <paramref name="address"/> is free, taken (and by whom), or could not be checked.</summary>
    public async Task<AddressCheck> CheckAddressAsync(string address, CancellationToken ct)
    {
        try
        {
            var result = await _client.InvokeAsync("Get-Recipient", new Dictionary<string, object?> { ["Identity"] = address }, ct,
                ["DisplayName", "PrimarySmtpAddress"]).ConfigureAwait(false);
            return result.First is { } row
                ? AddressCheck.TakenBy(ExchangeValues.NonEmpty(row, "DisplayName") ?? ExchangeValues.NonEmpty(row, "PrimarySmtpAddress") ?? address)
                : AddressCheck.Free;
        }
        catch (ExchangeException ex) when (ex.IsNotFound)
        {
            return AddressCheck.Free;
        }
        catch (ExchangeException ex)
        {
            return AddressCheck.Unknown(ExchangeErrorText.Friendly(ex));
        }
    }

    /// <summary>The domains this tenant accepts mail for, default first — what an address may end in.</summary>
    public async Task<IReadOnlyList<string>> GetAcceptedDomainsAsync(CancellationToken ct)
    {
        var result = await _client.InvokeAsync("Get-AcceptedDomain", null, ct, ["DomainName", "Default"]).ConfigureAwait(false);
        return
        [
            .. result.Items
                .Where(r => ExchangeValues.NonEmpty(r, "DomainName") is not null)
                .OrderByDescending(r => ExchangeValues.IsTrue(r, "Default"))
                .ThenBy(r => ExchangeValues.String(r, "DomainName"), StringComparer.OrdinalIgnoreCase)
                .Select(r => ExchangeValues.String(r, "DomainName")!)
                // The .mail.onmicrosoft.com domain exists for hybrid routing; nobody gives a person an address in it.
                .Where(d => !d.EndsWith(".mail.onmicrosoft.com", StringComparison.OrdinalIgnoreCase)),
        ];
    }

    private async Task<MailboxDetail> ReadDetailAsync(string userId, CancellationToken ct)
    {
        var result = await _client.InvokeAsync("Get-Mailbox", Identity(userId), ct, MailboxDetail.Select, _client.AnchorFor(userId)).ConfigureAwait(false);
        return result.First is { } row
            ? MailboxDetail.FromJson(userId, row)
            : throw new ExchangeException(404, "NotFound", "Exchange returned no mailbox.", "ManagementObjectNotFoundException", cmdlet: "Get-Mailbox");
    }

    /// <summary>The Exchange name of a recipient given by object id, UPN or address — what the lists on a mailbox hold.</summary>
    private async Task<string?> NameOfAsync(string identity, CancellationToken ct)
    {
        try
        {
            var result = await _client.InvokeAsync("Get-Recipient", Identity(identity), ct, ["Name"]).ConfigureAwait(false);
            return result.First is { } row ? ExchangeValues.NonEmpty(row, "Name") : null;
        }
        catch (ExchangeException)
        {
            return null;
        }
    }

    // ---- addresses (M14.1) ---------------------------------------------------------------------------------------

    public async Task<WriteOutcome> AddAddressAsync(MailboxRef m, string address, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        address = address.Trim();
        var before = await ReadDetailAsync(m.UserId, ct).ConfigureAwait(false);
        if (before.EmailAddresses.Any(a => a.IsSmtp && a.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
        {
            return WriteOutcome.NoOp($"{address} is already an address of {m.Label}");
        }

        return await WriteAsync(m, "exchange.address.add", $"Added {address} to {m.Label}", $"Set-Mailbox EmailAddresses +smtp:{address}",
            "Set-Mailbox", Params(m, ("EmailAddresses", ExchangeMultiValue.Adding($"smtp:{address}"))),
            d => d.EmailAddresses.Any(a => a.Address.Equals(address, StringComparison.OrdinalIgnoreCase)),
            token => RemoveAddressAsync(m, address, token), "exchange.address.remove", Inverse(("address", address)), ct).ConfigureAwait(false);
    }

    public async Task<WriteOutcome> RemoveAddressAsync(MailboxRef m, string address, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var before = await ReadDetailAsync(m.UserId, ct).ConfigureAwait(false);
        var existing = before.EmailAddresses.FirstOrDefault(a => a.IsSmtp && a.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return WriteOutcome.NoOp($"{address} is not an address of {m.Label}");
        if (existing.IsPrimary)
        {
            return Refused(m, "exchange.address.remove", $"Remove {address} from {m.Label}",
                "That's the primary address. Make another address primary first, then remove this one.");
        }

        return await WriteAsync(m, "exchange.address.remove", $"Removed {address} from {m.Label}", $"Set-Mailbox EmailAddresses -smtp:{address}",
            "Set-Mailbox", Params(m, ("EmailAddresses", ExchangeMultiValue.Removing($"smtp:{existing.Address}"))),
            d => !d.EmailAddresses.Any(a => a.Address.Equals(address, StringComparison.OrdinalIgnoreCase)),
            token => AddAddressAsync(m, address, token), "exchange.address.add", Inverse(("address", address)), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>-WindowsEmailAddress</c>, because <c>-PrimarySmtpAddress</c> answers 403 through this endpoint and an upper-case
    /// <c>SMTP:</c> add of an existing alias does not promote it (both observed 2026-10-17). The old primary stays as an alias.
    /// </summary>
    public async Task<WriteOutcome> SetPrimaryAddressAsync(MailboxRef m, string address, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var before = await ReadDetailAsync(m.UserId, ct).ConfigureAwait(false);
        if (string.Equals(before.PrimarySmtp, address, StringComparison.OrdinalIgnoreCase))
        {
            return WriteOutcome.NoOp($"{address} is already the primary address");
        }

        var previous = before.PrimarySmtp;
        return await WriteAsync(m, "exchange.address.primary", $"Made {address} the primary address of {m.Label}",
            $"Set-Mailbox WindowsEmailAddress={address}",
            "Set-Mailbox", Params(m, ("WindowsEmailAddress", address)),
            d => string.Equals(d.PrimarySmtp, address, StringComparison.OrdinalIgnoreCase),
            previous is null ? null : token => SetPrimaryAddressAsync(m, previous, token),
            "exchange.address.primary", Inverse(("address", previous)), ct).ConfigureAwait(false);
    }

    // ---- flags and type (M14.2, M14.3, M14.4) --------------------------------------------------------------------

    public Task<WriteOutcome> SetHiddenAsync(MailboxRef m, bool hidden, CancellationToken ct) =>
        SetFlagAsync(m, "exchange.hidden", "HiddenFromAddressListsEnabled", hidden, d => d.HiddenFromAddressLists,
            hidden ? $"Hid {m.Label} from address lists" : $"Showed {m.Label} in address lists",
            (token, back) => SetHiddenAsync(m, back, token), ct);

    public Task<WriteOutcome> SetCopySentAsAsync(MailboxRef m, bool enabled, CancellationToken ct) =>
        SetFlagAsync(m, "exchange.copy.sendas", "MessageCopyForSentAsEnabled", enabled, d => d.CopySentAsToSentItems,
            enabled ? $"Mail sent as {m.Label} is now saved in its Sent Items" : $"Mail sent as {m.Label} is no longer saved in its Sent Items",
            (token, back) => SetCopySentAsAsync(m, back, token), ct);

    public Task<WriteOutcome> SetCopySendOnBehalfAsync(MailboxRef m, bool enabled, CancellationToken ct) =>
        SetFlagAsync(m, "exchange.copy.onbehalf", "MessageCopyForSendOnBehalfEnabled", enabled, d => d.CopySendOnBehalfToSentItems,
            enabled ? $"Mail sent on behalf of {m.Label} is now saved in its Sent Items" : $"Mail sent on behalf of {m.Label} is no longer saved in its Sent Items",
            (token, back) => SetCopySendOnBehalfAsync(m, back, token), ct);

    public Task<WriteOutcome> SetRequireSenderAuthenticationAsync(MailboxRef m, bool required, CancellationToken ct) =>
        SetFlagAsync(m, "exchange.restrict.auth", "RequireSenderAuthenticationEnabled", required, d => d.RequireSenderAuthentication,
            required ? $"{m.Label} now rejects mail from outside the organisation" : $"{m.Label} now accepts mail from outside the organisation",
            (token, back) => SetRequireSenderAuthenticationAsync(m, back, token), ct);

    /// <summary>Regular ↔ shared. Converting does not touch licences (doc 13 §5); the UI says so before the click.</summary>
    public async Task<WriteOutcome> ConvertAsync(MailboxRef m, bool toShared, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var before = await ReadDetailAsync(m.UserId, ct).ConfigureAwait(false);
        var target = toShared ? MailboxKind.Shared : MailboxKind.User;
        if (before.Kind == target) return WriteOutcome.NoOp($"{m.Label} is already a {MailboxKinds.Describe(target).ToLowerInvariant()}");
        if (before.Kind is not (MailboxKind.User or MailboxKind.Shared))
        {
            return Refused(m, "exchange.convert", $"Convert {m.Label}", $"Only user and shared mailboxes convert; this is a {MailboxKinds.Describe(before.Kind).ToLowerInvariant()}.");
        }

        return await WriteAsync(m, "exchange.convert", toShared ? $"Converted {m.Label} to a shared mailbox" : $"Converted {m.Label} to a user mailbox",
            $"Set-Mailbox Type={(toShared ? "Shared" : "Regular")}",
            "Set-Mailbox", Params(m, ("Type", toShared ? "Shared" : "Regular")),
            d => d.Kind == target,
            token => ConvertAsync(m, !toShared, token), "exchange.convert", Inverse(("toShared", !toShared)), ct).ConfigureAwait(false);
    }

    // ---- delegation (M14.3) --------------------------------------------------------------------------------------

    /// <param name="principal">A UPN — what <c>Get-MailboxPermission</c> reports back, so the result can be confirmed.</param>
    /// <param name="autoMapping">Whether the mailbox appears in the delegate's Outlook on its own.</param>
    public async Task<WriteOutcome> GrantFullAccessAsync(MailboxRef m, string principal, bool autoMapping, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var current = await GetFullAccessAsync(m.UserId, ct).ConfigureAwait(false);
        if (current.Value?.Any(e => !e.IsDeny && Same(e.Principal, principal)) == true) return WriteOutcome.NoOp($"{principal} already has Full Access to {m.Label}");

        return await WriteAsync(m, "exchange.fullaccess.add", $"Gave {principal} Full Access to {m.Label}",
            $"Add-MailboxPermission User={principal} FullAccess AutoMapping={autoMapping}",
            "Add-MailboxPermission", Params(m, ("User", principal), ("AccessRights", new[] { "FullAccess" }), ("AutoMapping", autoMapping)),
            null, token => RevokeFullAccessAsync(m, principal, token), "exchange.fullaccess.remove", Inverse(("principal", principal)), ct,
            confirm: async token => (await GetFullAccessAsync(m.UserId, token).ConfigureAwait(false)).Value?.Any(e => Same(e.Principal, principal)) == true)
            .ConfigureAwait(false);
    }

    public async Task<WriteOutcome> RevokeFullAccessAsync(MailboxRef m, string principal, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var current = await GetFullAccessAsync(m.UserId, ct).ConfigureAwait(false);
        if (current.Value is { } entries && !entries.Any(e => Same(e.Principal, principal))) return WriteOutcome.NoOp($"{principal} has no Full Access to {m.Label}");

        // Get-MailboxPermission does not report AutoMapping, so an undo restores the default (mapped) rather than guessing.
        return await WriteAsync(m, "exchange.fullaccess.remove", $"Removed {principal}'s Full Access to {m.Label}",
            $"Remove-MailboxPermission User={principal} FullAccess",
            "Remove-MailboxPermission", Params(m, ("User", principal), ("AccessRights", new[] { "FullAccess" }), ("Confirm", false)),
            null, token => GrantFullAccessAsync(m, principal, autoMapping: true, token), "exchange.fullaccess.add", Inverse(("principal", principal)), ct,
            confirm: async token => (await GetFullAccessAsync(m.UserId, token).ConfigureAwait(false)).Value?.Any(e => Same(e.Principal, principal)) == false)
            .ConfigureAwait(false);
    }

    public async Task<WriteOutcome> GrantSendAsAsync(MailboxRef m, string principal, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var current = await GetSendAsAsync(m.UserId, ct).ConfigureAwait(false);
        if (current.Value?.Any(e => !e.IsDeny && Same(e.Principal, principal)) == true) return WriteOutcome.NoOp($"{principal} can already send as {m.Label}");

        return await WriteAsync(m, "exchange.sendas.add", $"{principal} can now send as {m.Label}",
            $"Add-RecipientPermission Trustee={principal} SendAs",
            "Add-RecipientPermission", Params(m, ("Trustee", principal), ("AccessRights", new[] { "SendAs" }), ("Confirm", false)),
            null, token => RevokeSendAsAsync(m, principal, token), "exchange.sendas.remove", Inverse(("principal", principal)), ct,
            confirm: async token => (await GetSendAsAsync(m.UserId, token).ConfigureAwait(false)).Value?.Any(e => Same(e.Principal, principal)) == true)
            .ConfigureAwait(false);
    }

    public async Task<WriteOutcome> RevokeSendAsAsync(MailboxRef m, string principal, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var current = await GetSendAsAsync(m.UserId, ct).ConfigureAwait(false);
        if (current.Value is { } entries && !entries.Any(e => Same(e.Principal, principal))) return WriteOutcome.NoOp($"{principal} can't send as {m.Label}");

        return await WriteAsync(m, "exchange.sendas.remove", $"{principal} can no longer send as {m.Label}",
            $"Remove-RecipientPermission Trustee={principal} SendAs",
            "Remove-RecipientPermission", Params(m, ("Trustee", principal), ("AccessRights", new[] { "SendAs" }), ("Confirm", false)),
            null, token => GrantSendAsAsync(m, principal, token), "exchange.sendas.add", Inverse(("principal", principal)), ct,
            confirm: async token => (await GetSendAsAsync(m.UserId, token).ConfigureAwait(false)).Value?.Any(e => Same(e.Principal, principal)) == false)
            .ConfigureAwait(false);
    }

    /// <summary>Adds to or removes from one of the recipient lists, by add/remove so nobody unmentioned is dropped.</summary>
    /// <param name="identity">Object id or UPN to add; for a remove, the <see cref="RecipientRef.Identity"/> the read resolved.</param>
    public async Task<WriteOutcome> ChangeListAsync(MailboxRef m, MailboxList list, string identity, string displayAs, bool add, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var (parameter, noun) = list switch
        {
            MailboxList.SendOnBehalf => ("GrantSendOnBehalfTo", "Send on Behalf"),
            MailboxList.AcceptOnlyFrom => ("AcceptMessagesOnlyFromSendersOrMembers", "accepted senders"),
            _ => ("RejectMessagesFromSendersOrMembers", "rejected senders"),
        };

        // The lists hold Exchange names, so the name is what confirms the change.
        var name = await NameOfAsync(identity, ct).ConfigureAwait(false) ?? identity;
        var before = await ReadDetailAsync(m.UserId, ct).ConfigureAwait(false);
        var has = ListOf(before, list).Any(n => Same(n, name));
        if (has == add) return WriteOutcome.NoOp(add ? $"{displayAs} is already in {noun}" : $"{displayAs} isn't in {noun}");

        var action = $"exchange.list.{list.ToString().ToLowerInvariant()}.{(add ? "add" : "remove")}";
        return await WriteAsync(m, action, add ? $"Added {displayAs} to {noun} on {m.Label}" : $"Removed {displayAs} from {noun} on {m.Label}",
            $"Set-Mailbox {parameter} {(add ? "+" : "-")}{identity}",
            "Set-Mailbox", Params(m, (parameter, add ? ExchangeMultiValue.Adding(identity) : ExchangeMultiValue.Removing(identity))),
            d => ListOf(d, list).Any(n => Same(n, name)) == add,
            token => ChangeListAsync(m, list, identity, displayAs, !add, token),
            $"exchange.list.{list.ToString().ToLowerInvariant()}.{(add ? "remove" : "add")}", Inverse(("identity", identity)), ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> ListOf(MailboxDetail d, MailboxList list) => list switch
    {
        MailboxList.SendOnBehalf => d.SendOnBehalf,
        MailboxList.AcceptOnlyFrom => d.AcceptOnlyFrom,
        _ => d.RejectFrom,
    };

    // ---- delivery (M14.4) ----------------------------------------------------------------------------------------

    /// <summary>
    /// Forward to an internal recipient (<paramref name="internalIdentity"/>), an outside address (<paramref name="externalAddress"/>),
    /// or — both null — stop forwarding. Undo restores whatever was there before, including the keep-a-copy flag.
    /// </summary>
    public async Task<WriteOutcome> SetForwardingAsync(MailboxRef m, string? internalIdentity, string? externalAddress, bool keepCopy, string? displayAs, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        if (internalIdentity is not null && externalAddress is not null)
        {
            throw new ArgumentException("Forward to an internal recipient or an external address, not both.");
        }

        var before = await ReadDetailAsync(m.UserId, ct).ConfigureAwait(false);
        var internalName = internalIdentity is null ? null : await NameOfAsync(internalIdentity, ct).ConfigureAwait(false) ?? internalIdentity;
        var external = externalAddress is null ? null : $"smtp:{externalAddress.Trim()}";
        var stopping = internalIdentity is null && external is null;
        var unchanged = Same(before.ForwardingAddress, internalName) &&
                        Same(Bare(before.ForwardingSmtpAddress), Bare(external)) &&
                        (stopping || before.DeliverToMailboxAndForward == keepCopy);
        if (unchanged) return WriteOutcome.NoOp(stopping ? $"{m.Label} isn't forwarding" : $"{m.Label} already forwards there");

        var previousInternal = before.ForwardingAddress;
        var previousExternal = Bare(before.ForwardingSmtpAddress);
        var previousKeep = before.DeliverToMailboxAndForward;
        var summary = stopping ? $"Stopped forwarding {m.Label}"
            : $"{m.Label} now forwards to {displayAs ?? externalAddress ?? internalIdentity}" + (keepCopy ? " and keeps a copy" : string.Empty);

        return await WriteAsync(m, "exchange.forwarding", summary,
            stopping ? "Set-Mailbox ForwardingAddress=$null ForwardingSmtpAddress=$null" : $"Set-Mailbox Forwarding={internalIdentity ?? externalAddress} DeliverToMailboxAndForward={keepCopy}",
            "Set-Mailbox", Params(m, ("ForwardingAddress", internalIdentity), ("ForwardingSmtpAddress", external), ("DeliverToMailboxAndForward", !stopping && keepCopy)),
            d => Same(d.ForwardingAddress, internalName) && Same(Bare(d.ForwardingSmtpAddress), Bare(external)),
            token => SetForwardingAsync(m, previousInternal, previousExternal, previousKeep, previousInternal ?? previousExternal, token),
            "exchange.forwarding", Inverse(("forwardingAddress", previousInternal), ("forwardingSmtpAddress", previousExternal), ("keepCopy", previousKeep)), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Sets automatic replies to <paramref name="desired"/>. The messages are content: they never reach the action log.</summary>
    public async Task<WriteOutcome> SetAutoReplyAsync(MailboxRef m, AutoReplySettings desired, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var before = await GetAutoReplyAsync(m.UserId, ct).ConfigureAwait(false);
        if (!before.IsSuccess) return Failed(m, "exchange.autoreply", $"Set automatic replies on {m.Label}", before.Error!);
        var previous = before.Value!;

        var parameters = Params(m,
            ("AutoReplyState", desired.State),
            ("InternalMessage", desired.InternalMessage ?? string.Empty),
            ("ExternalMessage", desired.ExternalMessage ?? string.Empty),
            ("ExternalAudience", desired.ExternalAudience));
        if (desired.IsScheduled)
        {
            parameters["StartTime"] = desired.Start?.ToUniversalTime().ToString("o");
            parameters["EndTime"] = desired.End?.ToUniversalTime().ToString("o");
        }

        var summary = desired.IsOn ? $"Turned on automatic replies for {m.Label}" + (desired.IsScheduled ? " (scheduled)" : string.Empty)
                                   : $"Turned off automatic replies for {m.Label}";
        return await WriteAsync(m, "exchange.autoreply", summary,
            $"Set-MailboxAutoReplyConfiguration State={desired.State} Audience={desired.ExternalAudience} (messages not logged)",
            "Set-MailboxAutoReplyConfiguration", parameters, null,
            token => SetAutoReplyAsync(m, previous, token), "exchange.autoreply", Inverse(("state", previous.State)), ct,
            confirm: async token => (await GetAutoReplyAsync(m.UserId, token).ConfigureAwait(false)).Value?.State.Equals(desired.State, StringComparison.OrdinalIgnoreCase) == true)
            .ConfigureAwait(false);
    }

    // ---- regional (M14.2) ----------------------------------------------------------------------------------------

    /// <param name="localizeFolders">Rename the default folders into the new language. Irreversible in practice, so it is not undone.</param>
    public async Task<WriteOutcome> SetRegionalAsync(MailboxRef m, string language, string timeZone, bool localizeFolders, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var before = await GetRegionalAsync(m.UserId, ct).ConfigureAwait(false);
        if (!before.IsSuccess) return Failed(m, "exchange.regional", $"Set regional settings on {m.Label}", before.Error!);
        var previous = before.Value!;
        if (Same(previous.Language, language) && Same(previous.TimeZone, timeZone) && !localizeFolders)
        {
            return WriteOutcome.NoOp($"{m.Label} already uses {language} in {timeZone}");
        }

        var parameters = Params(m, ("Language", language), ("TimeZone", timeZone));
        // A date or time format belongs to a language, and the old one may not exist in the new: sending null asks Exchange
        // for the new language's defaults instead of failing on a format it no longer accepts.
        if (!Same(previous.Language, language))
        {
            parameters["DateFormat"] = null;
            parameters["TimeFormat"] = null;
        }

        if (localizeFolders) parameters["LocalizeDefaultFolderName"] = true;

        return await WriteAsync(m, "exchange.regional", $"Set {m.Label} to {language}, {timeZone}",
            $"Set-MailboxRegionalConfiguration Language={language} TimeZone={timeZone}{(localizeFolders ? " LocalizeDefaultFolderName" : string.Empty)}",
            "Set-MailboxRegionalConfiguration", parameters, null,
            previous.Language is null || previous.TimeZone is null ? null : token => SetRegionalAsync(m, previous.Language, previous.TimeZone, false, token),
            "exchange.regional", Inverse(("language", previous.Language), ("timeZone", previous.TimeZone)), ct,
            confirm: async token =>
            {
                var after = (await GetRegionalAsync(m.UserId, token).ConfigureAwait(false)).Value;
                return after is not null && Same(after.Language, language) && Same(after.TimeZone, timeZone);
            }).ConfigureAwait(false);
    }

    // ---- storage (M14.5) -----------------------------------------------------------------------------------------

    /// <summary>Not undoable: disabling an archive starts a 30-day countdown to deleting its contents (doc 13 §3).</summary>
    public async Task<WriteOutcome> EnableArchiveAsync(MailboxRef m, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var before = await ReadDetailAsync(m.UserId, ct).ConfigureAwait(false);
        if (before.HasArchive) return WriteOutcome.NoOp($"{m.Label} already has an archive");
        return await WriteAsync(m, "exchange.archive.enable", $"Enabled the archive for {m.Label}", "Enable-Mailbox Archive",
            "Enable-Mailbox", Params(m, ("Archive", true)), d => d.HasArchive, null, null, null, ct).ConfigureAwait(false);
    }

    /// <summary>Not undoable at all: Exchange offers no way to turn auto-expanding archiving off once on.</summary>
    public async Task<WriteOutcome> EnableAutoExpandingArchiveAsync(MailboxRef m, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var before = await ReadDetailAsync(m.UserId, ct).ConfigureAwait(false);
        if (before.AutoExpandingArchive) return WriteOutcome.NoOp($"{m.Label}'s archive already expands automatically");
        if (!before.HasArchive) return Refused(m, "exchange.archive.autoexpand", $"Auto-expand {m.Label}'s archive", "Enable the archive first.");
        return await WriteAsync(m, "exchange.archive.autoexpand", $"{m.Label}'s archive now expands automatically", "Enable-Mailbox AutoExpandingArchive",
            "Enable-Mailbox", Params(m, ("AutoExpandingArchive", true)), d => d.AutoExpandingArchive, null, null, null, ct).ConfigureAwait(false);
    }

    /// <param name="days">How long to hold; null for indefinitely.</param>
    public async Task<WriteOutcome> SetLitigationHoldAsync(MailboxRef m, bool enabled, int? days, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var before = await ReadDetailAsync(m.UserId, ct).ConfigureAwait(false);
        if (before.LitigationHold == enabled && (!enabled || DurationOf(before) == days))
        {
            return WriteOutcome.NoOp(enabled ? $"{m.Label} is already on litigation hold" : $"{m.Label} isn't on litigation hold");
        }

        var parameters = Params(m, ("LitigationHoldEnabled", enabled));
        if (enabled) parameters["LitigationHoldDuration"] = days is { } d ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : "Unlimited";
        var previousEnabled = before.LitigationHold;
        var previousDays = DurationOf(before);
        return await WriteAsync(m, "exchange.hold", enabled ? $"Put {m.Label} on litigation hold" + (days is { } n ? $" for {n} days" : " indefinitely") : $"Took {m.Label} off litigation hold",
            $"Set-Mailbox LitigationHoldEnabled={enabled}{(enabled ? $" Duration={(days?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unlimited")}" : string.Empty)}",
            "Set-Mailbox", parameters, d => d.LitigationHold == enabled,
            token => SetLitigationHoldAsync(m, previousEnabled, previousDays, token), "exchange.hold", Inverse(("enabled", previousEnabled), ("days", previousDays)), ct)
            .ConfigureAwait(false);
    }

    private static int? DurationOf(MailboxDetail d) =>
        int.TryParse(d.LitigationHoldDuration, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    // ---- folder permissions (M14.6) ------------------------------------------------------------------------------

    /// <summary>The roles the folder editor offers, least to most, as Exchange names them.</summary>
    public static readonly IReadOnlyList<string> FolderRoles =
        ["None", "AvailabilityOnly", "LimitedDetails", "Reviewer", "Contributor", "Author", "NonEditingAuthor", "PublishingAuthor", "Editor", "PublishingEditor", "Owner"];

    /// <summary>
    /// Gives <paramref name="principal"/> <paramref name="role"/> on a default folder, changes the role they have, or —
    /// <paramref name="role"/> null — removes them. <c>Default</c> and <c>Anonymous</c> can be changed but never removed.
    /// </summary>
    /// <param name="principal">An object id or UPN for someone new; for an existing row, the name the row shows.</param>
    /// <param name="displayAs">The name <c>Get-MailboxFolderPermission</c> will report, which is what confirms it.</param>
    public async Task<WriteOutcome> SetFolderPermissionAsync(MailboxRef m, string folderType, string principal, string displayAs, string? role, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var read = await GetFolderPermissionsAsync(m.UserId, folderType, ct).ConfigureAwait(false);
        if (!read.IsSuccess) return Failed(m, "exchange.folder", $"Change {folderType} permissions on {m.Label}", read.Error!);
        var folder = read.Value!.Folder;
        var existing = read.Value.Entries.FirstOrDefault(e => Same(e.Principal, displayAs) || Same(e.Principal, principal));
        var previousRole = existing?.Rights.FirstOrDefault();
        var isBuiltIn = existing is { IsDefault: true } or { IsAnonymous: true };

        if (role is null && isBuiltIn) return Refused(m, "exchange.folder", $"Remove {displayAs}", "Default and Anonymous can't be removed — set them to None instead.");
        if (role is null && existing is null) return WriteOutcome.NoOp($"{displayAs} has no access to {folder.Name}");
        if (role is not null && Same(previousRole, role)) return WriteOutcome.NoOp($"{displayAs} already has {role} on {folder.Name}");

        var identity = folder.IdentityFor(m.UserId);
        var (cmdlet, parameters, summary) = (existing, role) switch
        {
            (null, not null) => ("Add-MailboxFolderPermission", Params(identity, ("User", principal), ("AccessRights", new[] { role })),
                $"Gave {displayAs} {role} on {m.Label}'s {folder.Name}"),
            (not null, not null) => ("Set-MailboxFolderPermission", Params(identity, ("User", principal), ("AccessRights", new[] { role })),
                $"Changed {displayAs} to {role} on {m.Label}'s {folder.Name}"),
            _ => ("Remove-MailboxFolderPermission", Params(identity, ("User", principal), ("Confirm", false)),
                $"Removed {displayAs} from {m.Label}'s {folder.Name}"),
        };

        return await WriteAsync(m, "exchange.folder", summary, $"{cmdlet} {folder.Name} User={principal}{(role is null ? string.Empty : $" {role}")}",
            cmdlet, parameters, null,
            token => SetFolderPermissionAsync(m, folderType, principal, displayAs, previousRole, token),
            "exchange.folder", Inverse(("folder", folderType), ("principal", principal), ("role", previousRole)), ct,
            confirm: async token =>
            {
                var after = (await GetFolderPermissionsAsync(m.UserId, folderType, token).ConfigureAwait(false)).Value;
                var row = after?.Entries.FirstOrDefault(e => Same(e.Principal, displayAs) || Same(e.Principal, principal));
                return role is null ? after is not null && row is null : row is not null && row.Rights.Contains(role, StringComparer.OrdinalIgnoreCase);
            }).ConfigureAwait(false);
    }

    // ---- the one write path ----------------------------------------------------------------------------------------

    private async Task<WriteOutcome> SetFlagAsync(MailboxRef m, string action, string parameter, bool value, Func<MailboxDetail, bool> read,
        string summary, Func<CancellationToken, bool, Task<WriteOutcome>> setBack, CancellationToken ct)
    {
        await SettleAsync(m.UserId, ct).ConfigureAwait(false);
        var before = await ReadDetailAsync(m.UserId, ct).ConfigureAwait(false);
        if (read(before) == value) return WriteOutcome.NoOp($"{summary} — it already was");
        return await WriteAsync(m, action, summary, $"Set-Mailbox {parameter}={value}", "Set-Mailbox", Params(m, (parameter, value)),
            d => read(d) == value, token => setBack(token, !value), action, Inverse((parameter, !value)), ct).ConfigureAwait(false);
    }

    /// <param name="verify">Confirms the write from a fresh <c>Get-Mailbox</c>; or pass <paramref name="confirm"/> for writes another cmdlet reads back.</param>
    /// <param name="undo">Null when the write cannot be reversed.</param>
    private async Task<WriteOutcome> WriteAsync(
        MailboxRef m, string action, string summary, string request,
        string cmdlet, Dictionary<string, object?> parameters,
        Func<MailboxDetail, bool>? verify,
        Func<CancellationToken, Task<WriteOutcome>>? undo, string? inverseAction, string? inversePayload,
        CancellationToken ct,
        Func<CancellationToken, Task<bool>>? confirm = null)
    {
        ExchangeResult result;
        try
        {
            result = await _client.InvokeAsync(cmdlet, parameters, ct, anchorMailbox: _client.AnchorFor(m.UserId)).ConfigureAwait(false);
        }
        catch (ExchangeException ex)
        {
            MarkWritten(m.UserId);
            return Failed(m, action, summary, ex, request);
        }

        MarkWritten(m.UserId);
        confirm ??= verify is null ? null : async token => verify(await ReadDetailAsync(m.UserId, token).ConfigureAwait(false));
        var confirmed = confirm is null || await ConfirmAsync(confirm, ct).ConfigureAwait(false);
        var warning = result.Warnings.Count > 0 ? string.Join(" ", result.Warnings) : null;
        var message = confirmed ? summary : $"{summary} — Exchange accepted it but isn't showing it yet; it can take a minute.";

        var actionId = _actions.Record(NewAction(m, action, request, ActionOutcome.Success, warning is null ? message : $"{message} Exchange said: {warning}",
            undo is null ? null : inverseAction, undo is null ? null : inversePayload));
        _logger.LogInformation("{Action} on {UserId}: done, confirmed={Confirmed}", action, m.UserId, confirmed);

        return new WriteOutcome
        {
            Success = true,
            Summary = message,
            Warning = warning,
            ActionId = actionId,
            Undo = undo is null ? null : async token =>
            {
                var back = await undo(token).ConfigureAwait(false);
                if (back.Success) _actions.MarkUndone(actionId);
                return back;
            },
        };
    }

    /// <summary>
    /// Polls the read-back until two reads in a row agree, for up to about twenty seconds. One agreeing read is not enough:
    /// in the M14 gate a primary-address undo read back correctly once and was then overturned by the write before it
    /// (doc 13 §5). False means Exchange accepted the write but has not settled on it yet.
    /// </summary>
    private async Task<bool> ConfirmAsync(Func<CancellationToken, Task<bool>> check, CancellationToken ct)
    {
        var agreeing = 0;
        foreach (var delay in ConfirmDelays)
        {
            await Delay(delay, ct).ConfigureAwait(false);
            try
            {
                agreeing = await check(ct).ConfigureAwait(false) ? agreeing + 1 : 0;
                if (agreeing >= 2) return true;
            }
            catch (ExchangeException ex)
            {
                agreeing = 0;
                _logger.LogDebug("Confirm read-back failed: {Code}", ex.ErrorCode);
            }
        }

        return false;
    }

    /// <summary>
    /// How long before <paramref name="userId"/>'s mailbox may be written again. Two writes to one mailbox a few seconds
    /// apart can be applied out of order across Exchange's servers, and the earlier one can win: in the M14 gate a
    /// primary address set and undone five seconds apart flipped between the two for half a minute, and once settled on
    /// the wrong one. Fifteen seconds apart, it held from the first read (doc 13 §5).
    /// </summary>
    public TimeSpan TimeUntilSettled(string userId)
    {
        lock (_lastWrite)
        {
            if (!_lastWrite.TryGetValue(userId, out var last)) return TimeSpan.Zero;
            var wait = last + SettleGap - Time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
    }

    private async Task SettleAsync(string userId, CancellationToken ct)
    {
        var wait = TimeUntilSettled(userId);
        if (wait <= TimeSpan.Zero) return;
        _logger.LogInformation("Waiting {Seconds:F0}s for {UserId}'s mailbox to settle before the next write", wait.TotalSeconds, userId);
        await Delay(wait, ct).ConfigureAwait(false);
    }

    private void MarkWritten(string userId)
    {
        lock (_lastWrite) _lastWrite[userId] = Time.GetUtcNow();
    }

    private WriteOutcome Failed(MailboxRef m, string action, string summary, ExchangeException ex, string? request = null)
    {
        var id = _actions.Record(NewAction(m, action, request ?? action, ActionOutcome.Failed, ExchangeErrorText.Friendly(ex), requestId: ex.RequestId));
        _logger.LogWarning("{Action} on {UserId} failed: {Code} {Type}", action, m.UserId, ex.ErrorCode, ex.ExceptionName);
        return WriteOutcome.FailedExchange(summary, ex, id);
    }

    private WriteOutcome Refused(MailboxRef m, string action, string summary, string reason)
    {
        _actions.Record(NewAction(m, action, action, ActionOutcome.Skipped, reason));
        return new WriteOutcome { Success = false, Summary = summary, FriendlyError = reason };
    }

    private ActionRecord NewAction(MailboxRef m, string action, string request, ActionOutcome outcome, string? message,
        string? inverseAction = null, string? inversePayload = null, string? requestId = null) => new()
    {
        Actor = _tenant.SignedInUpn,
        TenantId = _tenant.TenantId,
        TargetType = ObjectType.User,
        TargetId = m.UserId,
        TargetName = m.Label,
        Action = action,
        RequestSummary = request,
        Outcome = outcome,
        Message = message,
        GraphRequestId = requestId,
        InverseAction = inverseAction,
        InversePayload = inversePayload,
    };

    private static Dictionary<string, object?> Params(MailboxRef m, params (string Key, object? Value)[] values) => Params(m.UserId, values);

    private static Dictionary<string, object?> Params(string identity, params (string Key, object? Value)[] values)
    {
        var d = new Dictionary<string, object?> { ["Identity"] = identity };
        foreach (var (key, value) in values) d[key] = value;
        return d;
    }

    private static string Inverse(params (string Key, object? Value)[] values) =>
        JsonSerializer.Serialize(values.ToDictionary(v => v.Key, v => v.Value));

    private static bool Same(string? a, string? b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string? Bare(string? smtp) =>
        smtp is null ? null : smtp.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase) ? smtp[5..] : smtp;
}
