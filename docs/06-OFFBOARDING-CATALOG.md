# 06 — Offboarding step catalog

Runner contract (`Offboarding/IOffboardingStep.cs`):
```csharp
public interface IOffboardingStep
{
    string Id { get; }                       // stable, kebab-case, used in saved profiles
    string Title { get; }
    Capability RequiredCapability { get; }   // Graph | Exchange | OnPremAd
    bool DefaultEnabled { get; }
    bool HaltsOnFailure { get; }
    IReadOnlyList<string> RequiredScopes { get; }
    Task<StepPreview> PreviewAsync(OffboardingContext ctx, CancellationToken ct); // concrete "what will change" + CanRun + Reason
    Task<StepResult> ExecuteAsync(OffboardingContext ctx, CancellationToken ct);  // Outcome, Message, Duration, Inverse? (for undo)
}
```
`OffboardingContext` carries the `DirectoryUser` (fresh from Graph, not cache), tenant context, services, options (e.g. chosen delegate for later Exchange steps), and a shared `Dictionary<string, object>` scratchpad so steps can pass data (e.g. removed license list → undo).

Steps run **in the order listed**. Preview runs all previews in parallel (batched where possible), execution is sequential.

| # | Id | Title | Cap | Default | Halts | What it does | Hybrid behavior | Reversible |
|---|----|-------|-----|---------|-------|--------------|-----------------|------------|
| 1 | `disable-account` | Disable sign-in | Graph | ✅ | ✅ | `PATCH accountEnabled=false` | Runs; warns it reverts on next sync unless also done in AD | ✅ re-enable |
| 2 | `revoke-sessions` | Revoke all sessions & refresh tokens | Graph | ✅ | ✗ | `POST /revokeSignInSessions` | Runs | ✗ |
| 3 | `reset-password` | Reset password to random | Graph | ✅ | ✗ | `PATCH passwordProfile` with 24-char random, `forceChangePasswordNextSignIn=true`. Password is never shown or logged. | Skips unless tenant reports password writeback (attempt; on 400 skip with reason) | ✗ |
| 4 | `remove-auth-methods` | Remove MFA methods | Graph | ✅ | ✗ | Enumerate `/authentication/methods`, delete every deletable one | Runs | ✗ |
| 5 | `remove-manager` | Clear manager | Graph | ✗ | ✗ | `DELETE /manager/$ref` | Skip: mastered on-prem | ✅ restore |
| 6 | `remove-group-memberships` | Remove from groups | Graph | ✅ | ✗ | For each cached membership: skip dynamic, skip synced, skip role-assignable unless scope+role present; `DELETE /groups/{g}/members/{u}/$ref` via batch | Cloud groups only; synced groups listed as skipped | ✅ re-add |
| 7 | `remove-licenses` | Remove all direct licenses | Graph | ✅ | ✗ | `assignLicense removeLicenses=[all direct skuIds]`; group-assigned licenses listed as "inherited — removed when group step runs" | Runs | ✅ restore (30-day mailbox soft-delete caveat shown) |
| 8 | `retire-devices` | Retire Intune devices | Graph | ✗ | ✗ | For each managed device where user is primary: `POST /retire`. Preview lists device names. Off by default — many orgs reassign hardware. | Runs | ✗ |
| 9 | `remove-registered-devices` | Remove Entra device registrations | Graph | ✗ | ✗ | For each `/registeredDevices` not Intune-managed: `DELETE /devices/{id}` | Runs (cloud objects) | ✗ |
| 10 | `hide-from-gal` | Hide from address lists | Exchange | ✅ | ✗ | EXO `Set-Mailbox -HiddenFromAddressListsEnabled $true` | Phase 3 | ✅ |
| 11 | `convert-to-shared` | Convert mailbox to shared | Exchange | ✅ | ✗ | EXO `Set-Mailbox -Type Shared` (must run **before** license removal in real life — the runner reorders Exchange steps ahead of step 7 once Exchange exists; document this in the preview) | Phase 3 | ✅ |
| 12 | `set-auto-reply` | Set out-of-office | Exchange | ✗ | ✗ | Template text with manager name | Phase 3 | ✅ |
| 13 | `forward-mail` | Forward mail to manager/delegate | Exchange | ✗ | ✗ | `Set-Mailbox -ForwardingAddress` | Phase 3 | ✅ |
| 14 | `delegate-mailbox` | Grant mailbox access to manager/delegate | Exchange | ✗ | ✗ | `Add-MailboxPermission FullAccess` | Phase 3 | ✅ |
| 15 | `delegate-onedrive` | Grant OneDrive access to manager | Graph | ✗ | ✗ | `PATCH /users/{id}/drive/root/permissions`… (verify endpoint; fallback: SPO admin — mark as investigate) | Runs | ✅ |
| 16 | `remove-teams-ownership` | Transfer Teams/M365 group ownership | Graph | ✗ | ✗ | For each M365 group where user is sole owner: add delegate as owner, remove user | Runs | partial |
| 17 | `update-attributes` | Stamp offboarding attributes | Graph | ✗ | ✗ | Set `employeeType=Offboarded`, `officeLocation=""`, `department=""` (configurable) | Skip: mastered on-prem | ✅ |
| 18 | `move-ou` | Move AD object to Disabled OU | OnPremAd | ✅ | ✗ | LDAP move | Phase 2 | ✅ |
| 19 | `disable-ad-account` | Disable on-prem AD account | OnPremAd | ✅ | ✅ | LDAP `userAccountControl` | Phase 2 | ✅ |

Phase 1 implements 1–9, 15 (if a clean endpoint exists — otherwise mark investigate in DECISIONS.md), 16, 17. Steps 10–14, 18–19 are registered with their capability so they render greyed in the preview with the standard reason; they have no `ExecuteAsync` body beyond throwing `NotSupportedException` (the runner never calls it when `CanRun == false`).

## Profiles
An offboarding profile is JSON: `{ "name": "Standard leaver", "steps": { "disable-account": true, "retire-devices": false, … }, "options": { "delegateToManager": true } }` saved under `templates\offboarding\*.json`. Ship a default "Standard leaver" profile. Users can save the current toggle set as a new profile from the preview dialog.

## Preview text examples (must be this concrete)
- "Disable sign-in for **jdoe@contoso.com** (currently enabled)"
- "Revoke sessions — user last signed in 3 hours ago"
- "Remove 2 MFA methods: Authenticator (iPhone 15), Phone (+1 ***-***-4412)"
- "Remove from 14 groups · 2 skipped: **All-Staff-Dynamic** (dynamic), **Finance-Users** (synced from AD)"
- "Remove 3 licenses: Microsoft 365 E3, Visio Plan 2, Power BI Pro · 1 inherited from group **Lic-M365-E3** (removed by group step)"
- "Retire 1 device: **LT-JDOE-01** (Surface Laptop 5, last check-in 2 days ago)"
