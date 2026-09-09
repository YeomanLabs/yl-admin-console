using YeomanLabs.AdminConsole.Core.Cache;
using YeomanLabs.AdminConsole.Core.Auth;
using YeomanLabs.AdminConsole.Core.Devices;
using YeomanLabs.AdminConsole.Core.Graph;
using YeomanLabs.AdminConsole.Core.Groups;
using YeomanLabs.AdminConsole.Core.Licensing;
using YeomanLabs.AdminConsole.Core.Users;

namespace YeomanLabs.AdminConsole.Core.Offboarding.Steps;

/// <summary>
/// The nineteen steps of doc 06 in catalog order. Steps that belong to a later phase are still listed, greyed, with
/// the reason: the dialog has to show the whole shape of an offboarding, not just the part built so far.
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
        TenantContext tenant) =>
    [
        new DisableAccountStep(users),                                  // 1
        new RevokeSessionsStep(users),                                  // 2
        new ResetPasswordStep(users),                                   // 3
        new RemoveAuthMethodsStep(users, graph),                        // 4
        new RemoveManagerStep(graph, actions, tenant),                  // 5
        new RemoveGroupMembershipsStep(groups, graph, tenant),          // 6
        new RemoveLicensesStep(licenses, graph),                        // 7
        new RetireDevicesStep(devices, graph),                          // 8

        // 9: no granular delegated scope exists for deleting an Entra device object (DECISIONS 2026-09-09).
        new NotInThisPhaseStep("remove-registered-devices", "Remove Entra device registrations", StepSurface.Graph,
            "Deleting Entra device objects needs Directory.AccessAsUser.All, which this app does not request."),

        new NotInThisPhaseStep("hide-from-gal", "Hide from address lists", StepSurface.Exchange, ExchangePhase, defaultEnabled: true),        // 10
        new NotInThisPhaseStep("convert-to-shared", "Convert the mailbox to shared", StepSurface.Exchange,                                     // 11
            $"{ExchangePhase}. It will run before the licence step, because a mailbox has to be converted while it is still licensed."),
        new NotInThisPhaseStep("set-auto-reply", "Set an out-of-office reply", StepSurface.Exchange, ExchangePhase),                          // 12
        new NotInThisPhaseStep("forward-mail", "Forward mail to the delegate", StepSurface.Exchange, ExchangePhase),                          // 13
        new NotInThisPhaseStep("delegate-mailbox", "Grant the delegate mailbox access", StepSurface.Exchange, ExchangePhase),                 // 14

        // 15: investigated 2026-09-09 — no clean endpoint without a Files scope this app does not request.
        new NotInThisPhaseStep("delegate-onedrive", "Grant the delegate access to OneDrive", StepSurface.Graph,
            "Sharing a leaver's OneDrive needs the Files.ReadWrite.All permission, which this app does not request. Use the SharePoint admin centre for now."),

        new RemoveTeamsOwnershipStep(graph, actions, tenant),           // 16
        new UpdateAttributesStep(users),                                // 17

        new NotInThisPhaseStep("move-ou", "Move the AD object to the disabled OU", StepSurface.OnPremAd, OnPremPhase, defaultEnabled: true),  // 18
        new NotInThisPhaseStep("disable-ad-account", "Disable the on-prem AD account", StepSurface.OnPremAd, OnPremPhase, defaultEnabled: true), // 19
    ];
}
