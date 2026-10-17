using AdminConsole.Core.Auth;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Exchange;
using AdminConsole.Core.Graph;
using AdminConsole.Core.Groups;
using AdminConsole.Core.Licensing;
using AdminConsole.Core.OnPrem;
using AdminConsole.Core.OneDrive;
using AdminConsole.Core.Offboarding.Steps;
using AdminConsole.Core.Users;

namespace AdminConsole.Core.Offboarding.Restore;

/// <summary>
/// Restore from a snapshot (M15.4), for a rehire or for the wrong person: an offboarding in reverse, previewed and run
/// by the same runner and dialog. The snapshot and the target OU travel in the run's context
/// (<see cref="OffboardingContext.RestoreFrom"/>), so one catalog serves every snapshot.
/// </summary>
/// <remarks>
/// AD first, moved before enabled, so the account never comes back to life in the disabled OU; then sign-in and
/// licences; then the mailbox, which needs its licence to become a user mailbox again; then everything else.
/// </remarks>
public static class RestoreCatalog
{
    /// <summary>The last row: what cannot come back, said once rather than left for someone to discover.</summary>
    public const string NotRestorable =
        "Not put back: the password (set a new one or issue a Temporary Access Pass), MFA methods (they register again), " +
        "retired devices, removed phones, cancelled meetings and transferred group ownership. Litigation hold is left as it is.";

    public static IReadOnlyList<IOffboardingStep> Build(
        UserService users,
        GroupService groups,
        LicenseService licenses,
        GraphClient graph,
        ActionLog actions,
        TenantContext tenant,
        IDirectoryService directory,
        ExchangeService exchange,
        OneDriveService oneDrive) =>
    [
        new RestoreAdOuStep(directory),
        new RestoreAdAccountStep(directory),
        new RestoreAdGroupsStep(directory),
        new RestoreSignInStep(users),
        new RestoreLicencesStep(users, licenses),
        new RestoreMailboxTypeStep(exchange),
        new RestoreGroupsStep(users, groups),
        new RestoreDistributionListsStep(exchange, users),
        new RestoreManagerStep(users, graph, actions, tenant),
        new RestoreAttributesStep(users),
        new RestoreMailboxSettingsStep(exchange),
        new RestoreOtherMailboxAccessStep(exchange, actions),
        new RestoreOneDriveStep(oneDrive),
        new NotInThisPhaseStep("restore-not-restorable", "What a restore can't put back", StepSurface.Graph, NotRestorable),
    ];
}
