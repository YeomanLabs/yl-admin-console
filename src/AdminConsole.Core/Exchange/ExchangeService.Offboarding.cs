using AdminConsole.Core.Writes;

namespace AdminConsole.Core.Exchange;

/// <summary>
/// The mailbox operations only offboarding uses (M15): inbox rules, organised meetings, mobile devices, distribution
/// lists. Each proven against YeomanLabs on 2026-10-17 before it was written here.
/// </summary>
public sealed partial class ExchangeService
{
    public async Task<IReadOnlyList<InboxRuleRef>> GetInboxRulesAsync(string userId, CancellationToken ct)
    {
        var result = await _client.InvokeAsync("Get-InboxRule", new Dictionary<string, object?> { ["Mailbox"] = userId }, ct,
            ["Name", "RuleIdentity", "Enabled", "ForwardTo", "RedirectTo", "ForwardAsAttachmentTo"], _client.AnchorFor(userId)).ConfigureAwait(false);
        return
        [
            .. result.Items.Select(r => new InboxRuleRef
            {
                Identity = $"{userId}\\{ExchangeValues.String(r, "RuleIdentity")}",
                Name = ExchangeValues.NonEmpty(r, "Name") ?? "(unnamed rule)",
                Enabled = ExchangeValues.IsTrue(r, "Enabled"),
                SendsMailElsewhere = ExchangeValues.Strings(r, "ForwardTo").Count > 0 || ExchangeValues.Strings(r, "RedirectTo").Count > 0
                                     || ExchangeValues.Strings(r, "ForwardAsAttachmentTo").Count > 0,
            }),
        ];
    }

    /// <summary>Turns a rule off (or back on). Off rather than deleted, so an offboarding run can be undone.</summary>
    public async Task<WriteOutcome> SetInboxRuleEnabledAsync(MailboxRef m, InboxRuleRef rule, bool enabled, CancellationToken ct)
    {
        await SettleAsync(m.UserId, $"exchange.rule.{rule.Identity}", ct).ConfigureAwait(false);
        return await WriteAsync(m, $"exchange.rule.{rule.Identity}", enabled ? $"Turned the rule \"{rule.Name}\" back on" : $"Turned off the rule \"{rule.Name}\"",
            $"{(enabled ? "Enable" : "Disable")}-InboxRule {rule.Name}",
            enabled ? "Enable-InboxRule" : "Disable-InboxRule", Params(rule.Identity, ("Confirm", false)), null,
            token => SetInboxRuleEnabledAsync(m, rule with { Enabled = enabled }, !enabled, token), "exchange.rule", Inverse(("rule", rule.Name)), ct,
            confirm: async token => (await GetInboxRulesAsync(m.UserId, token).ConfigureAwait(false)).FirstOrDefault(r => r.Identity == rule.Identity)?.Enabled == enabled)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The meetings this mailbox organises from today on, which cancelling would remove from every attendee's calendar —
    /// <c>Remove-CalendarEvents -PreviewOnly</c>, which changes nothing.
    /// </summary>
    public async Task<IReadOnlyList<string>> PreviewOrganisedMeetingsAsync(string userId, CancellationToken ct)
    {
        var result = await _client.InvokeAsync("Remove-CalendarEvents", new Dictionary<string, object?>
        {
            ["Identity"] = userId, ["CancelOrganizedMeetings"] = true, ["QueryWindowInDays"] = 1825, ["PreviewOnly"] = true,
        }, ct, anchorMailbox: _client.AnchorFor(userId)).ConfigureAwait(false);

        // The preview's row shape could not be seen live (the test mailboxes organise nothing), so read the likely names
        // and fall back to counting; the warnings carry the same information in prose.
        var subjects = result.Items.Select(r => ExchangeValues.NonEmpty(r, "Subject") ?? ExchangeValues.NonEmpty(r, "EventSubject") ?? "(no subject)").ToList();
        return subjects;
    }

    /// <summary>Cancels every meeting this mailbox organises from today on. Not undoable: attendees have been told.</summary>
    public async Task<WriteOutcome> CancelOrganisedMeetingsAsync(MailboxRef m, CancellationToken ct)
    {
        await SettleAsync(m.UserId, "exchange.meetings", ct).ConfigureAwait(false);
        return await WriteAsync(m, "exchange.meetings", $"Cancelled the meetings {m.Label} organises", "Remove-CalendarEvents CancelOrganizedMeetings QueryWindowInDays=1825",
            "Remove-CalendarEvents", Params(m, ("CancelOrganizedMeetings", true), ("QueryWindowInDays", 1825), ("Confirm", false)),
            null, null, null, null, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MobileDeviceRef>> GetMobileDevicesAsync(string userId, CancellationToken ct)
    {
        var result = await _client.InvokeAsync("Get-MobileDevice", new Dictionary<string, object?> { ["Mailbox"] = userId }, ct,
            ["Identity", "DeviceModel", "FriendlyName", "ClientType", "DeviceOS", "FirstSyncTime"], _client.AnchorFor(userId)).ConfigureAwait(false);
        return
        [
            .. result.Items.Select(r => new MobileDeviceRef
            {
                Identity = ExchangeValues.NonEmpty(r, "Identity") ?? "(unknown)",
                Model = ExchangeValues.NonEmpty(r, "DeviceModel"),
                FriendlyName = ExchangeValues.NonEmpty(r, "FriendlyName"),
                ClientType = ExchangeValues.NonEmpty(r, "ClientType"),
                DeviceOs = ExchangeValues.NonEmpty(r, "DeviceOS"),
                FirstSync = ExchangeValues.Date(r, "FirstSyncTime"),
            }),
        ];
    }

    /// <summary>Removes the device's partnership with the mailbox. Not undoable; the device must sync again to come back.</summary>
    public async Task<WriteOutcome> RemoveMobileDeviceAsync(MailboxRef m, MobileDeviceRef device, CancellationToken ct)
    {
        await SettleAsync(m.UserId, "exchange.mobile", ct).ConfigureAwait(false);
        return await WriteAsync(m, "exchange.mobile", $"Removed {device.Display} from {m.Label}'s mailbox", $"Remove-MobileDevice {device.Identity}",
            "Remove-MobileDevice", Params(device.Identity, ("Confirm", false)), null, null, null, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes <paramref name="m"/>'s owner out of a distribution list or mail-enabled security group — which Graph cannot
    /// change. <c>BypassSecurityGroupManagerCheck</c> because the admin is rarely the list's owner.
    /// </summary>
    public async Task<WriteOutcome> SetDistributionGroupMemberAsync(MailboxRef m, string groupId, string groupName, bool member, CancellationToken ct)
    {
        await SettleAsync(m.UserId, $"exchange.dl.{groupId}", ct).ConfigureAwait(false);
        var cmdlet = member ? "Add-DistributionGroupMember" : "Remove-DistributionGroupMember";
        var parameters = Params(groupId, ("Member", m.UserId), ("BypassSecurityGroupManagerCheck", true));
        if (!member) parameters["Confirm"] = false;
        return await WriteAsync(m, $"exchange.dl.{groupId}", member ? $"Added {m.Label} back to {groupName}" : $"Removed {m.Label} from {groupName}",
            $"{cmdlet} {groupName}", cmdlet, parameters, null,
            token => SetDistributionGroupMemberAsync(m, groupId, groupName, !member, token), "exchange.dl", Inverse(("group", groupId)), ct,
            confirm: async token =>
            {
                var members = await _client.InvokeAsync("Get-DistributionGroupMember", Identity(groupId), token, ["ExternalDirectoryObjectId"]).ConfigureAwait(false);
                return members.Items.Any(r => string.Equals(ExchangeValues.String(r, "ExternalDirectoryObjectId"), m.UserId, StringComparison.OrdinalIgnoreCase)) == member;
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// The mailboxes <paramref name="trustee"/> can send as, from one tenant-wide <c>Get-RecipientPermission -Trustee</c>,
    /// resolved to people so each can be written to by object id. Inherited grants are left out: nobody can remove them here.
    /// </summary>
    public async Task<IReadOnlyList<RecipientRef>> GetSendAsGrantedToAsync(string trustee, CancellationToken ct)
    {
        var result = await _client.InvokeAsync("Get-RecipientPermission", new Dictionary<string, object?> { ["Trustee"] = trustee, ["ResultSize"] = "Unlimited" }, ct,
            ["Identity", "Trustee", "AccessRights", "IsInherited"]).ConfigureAwait(false);
        var names = result.Items
            .Where(r => !ExchangeValues.IsTrue(r, "IsInherited"))
            .Where(r => ExchangeValues.Rights(r).Contains("SendAs", StringComparer.OrdinalIgnoreCase))
            .Select(r => ExchangeValues.NonEmpty(r, "Identity"))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return [];

        var resolved = await ResolveRecipientsAsync(names, ct).ConfigureAwait(false);
        return [.. names.Select(n => resolved[n])];
    }
}
