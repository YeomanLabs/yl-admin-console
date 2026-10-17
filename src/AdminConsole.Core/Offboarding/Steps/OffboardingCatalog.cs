using AdminConsole.Core.Cache;
using AdminConsole.Core.Auth;
using AdminConsole.Core.Devices;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Groups;
using AdminConsole.Core.Licensing;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.Offboarding.Snapshot;
using AdminConsole.Core.Users;

namespace AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// Doc 06's catalog in run order (rewritten in M15). Snapshot first; then sign-in, credentials and groups; then the
/// mailbox, ahead of the licence step, because converting it and holding it both need the licence still there; then
/// the rest. Steps still waiting on a later milestone are listed, greyed, with the reason: the dialog has to show the
/// whole shape of an offboarding, not just the part built so far.
/// </summary>
public static class OffboardingCatalog
{
    public const string ExchangePhase = "Exchange Online integration arrives in Phase 3";
    public const string OnPremPhase = "On-prem Active Directory arrives in Phase 2";

    public static IReadOnlyList<IOffboardingStep> Build(
        UserService users,
        GroupService groups,
        LicenseService licenses,
        DeviceService devices,
        GraphClient graph,
        ActionLog actions,
        TenantContext tenant,
        IDirectoryService directory,
        LdapOptions onPrem,
        ExchangeService exchange,
        CacheDb db,
        SnapshotService snapshots,
        SnapshotStore store) =>
    [
        new SnapshotStep(snapshots, store, actions),                    // M15.1: first, required, halts

        new DisableAccountStep(users),                                  // 1
        new RevokeSessionsStep(users),                                  // 2
        new ResetPasswordStep(users),                                   // 3
        new ResetAdPasswordStep(directory),                             // M15.2: the reset that works without writeback
        new RemoveAuthMethodsStep(users, graph),                        // 4
        new RemoveManagerStep(graph, actions, tenant),                  // 5
        new RemoveGroupMembershipsStep(groups, graph, tenant),          // 6
        new RemoveAdGroupMembershipsStep(directory),                    // M15.2: the groups step 6 skips as synced
        new RemoveFromDistributionListsStep(exchange, graph),           // M15.3: the lists step 6 skips as mail lists

        // The mailbox (M15.3), all before step 7: a mailbox converted or held after losing its licence has already
        // started its 30-day countdown to deletion.
        new HideFromGalStep(exchange),                                  // 10
        new ConvertToSharedStep(exchange),                              // 11
        new LitigationHoldStep(exchange),
        new SetAutoReplyStep(exchange),                                 // 12
        new ForwardMailStep(exchange),                                  // 13
        new DelegateMailboxStep(exchange),                              // 14
        new RemoveMailboxPermissionsStep(exchange, db),
        new RemoveInboxRulesStep(exchange),
        new CancelOrganizedMeetingsStep(exchange),
        new RemoveMobileDevicesStep(exchange),

        new RemoveLicensesStep(licenses, graph),                        // 7
        new RetireDevicesStep(devices, graph),                          // 8

        // 9: no granular delegated scope exists for deleting an Entra device object (DECISIONS 2026-09-09).
        new NotInThisPhaseStep("remove-registered-devices", "Remove Entra device registrations", StepSurface.Graph,
            "Deleting Entra device objects needs Directory.AccessAsUser.All, which this app does not request."),

        // 15: M16 brings the SharePoint admin host this needs.
        new NotInThisPhaseStep("delegate-onedrive", "Grant the delegate access to OneDrive", StepSurface.Graph,
            "OneDrive arrives in M16, with the SharePoint admin permission it needs. Use the SharePoint admin centre for now."),

        new RemoveTeamsOwnershipStep(graph, actions, tenant),           // 16
        new UpdateAttributesStep(users),                                // 17

        // M12.7: the last two were greyed with "arrives in Phase 2" from M6 until Phase 2 existed. Doc 06 orders the
        // move before the on-prem disable, and it is left that way: sign-in is already blocked by step 1, so the
        // move happens against a live object where a mistake is visible and reversible.
        new MoveToDisabledOuStep(directory, onPrem),                    // 18
        new DisableAdAccountStep(directory),                            // 19
    ];
}
