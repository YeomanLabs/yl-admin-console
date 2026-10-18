# 08 — Entra app registration (one per tenant)

The console signs in through an app registration in the tenant it manages. **One registration per tenant**, created by
a script, and it belongs to that tenant — not to whoever deployed the console.

That choice is deliberate (DECISIONS.md, 2026-09-09). The alternative was one multi-tenant app owned by the deployer
that every client consents to, which is less setup and means one party owns an object holding `User.ReadWrite.All`
and `BitlockerKey.Read.All` against every client directory at once. A per-tenant registration survives a security
review, and revoking one client's access is deleting one object in their own tenant.

If the deployment is branded (see **Branding** in the README), pass `-Name` so the registration matches what the
console calls itself — an admin looking at their app registrations should recognise it.

## The short version

```powershell
# See what it would do. Signs in, resolves every permission, creates nothing.
.\scripts\New-AppRegistration.ps1 -WhatIf

# Do it.
.\scripts\New-AppRegistration.ps1
```

It prints a client ID. Start the console and paste it into the setup screen — or pass `-WriteConfig` and skip that.

**Already have a registration from an earlier version?** Don't create another. Bring it up to date:

```powershell
.\scripts\New-AppRegistration.ps1 -Update -WhatIf     # what it lacks
.\scripts\New-AppRegistration.ps1 -Update             # add it
```

`-Update` adds whatever this version uses that the registration lacks — Phase 3's Exchange Online permission, for a
registration made before it — and extends the consent grants to match. It never removes anything, and every machine
keeps the client ID it already has.

Undoing it is one command, which the script prints when it finishes:

```powershell
Remove-MgApplication -ApplicationId <the object id it printed>
```

That removes the service principal and the consent grant along with the application. Entra **soft-deletes** app
registrations, so it then sits in **Deleted applications** for 30 days — recoverable if you delete the wrong one, and
gone by itself after that. To remove it immediately:
`Invoke-MgGraphRequest -Method DELETE -Uri "v1.0/directory/deletedItems/<object id>"`.

## What you need

- The **Microsoft.Graph** PowerShell module: `Install-Module Microsoft.Graph -Scope CurrentUser`
- An account in the target tenant with:
  - **Application Administrator** — to create the registration
  - **Privileged Role Administrator** or **Global Administrator** — to grant admin consent
- If the person running it cannot consent, pass `-NoConsent`. The script prints a consent URL for someone who can.

## What it creates

| | |
|---|---|
| Type | Public client — **no secret**, and none can be added |
| Sign-in audience | Multi-tenant by default; `-SingleTenant` restricts it to the one tenant |
| Redirect URIs | `ms-appx-web://microsoft.aad.brokerplugin/{client-id}` (Windows broker), the native-client URI, and `http://localhost` |
| Permissions | 26 **delegated** Graph permissions, Exchange Online's `Exchange.Manage` and SharePoint's `AllSites.FullControl`, listed below. No application permissions, ever |
| Consent | Granted for the tenant unless `-NoConsent` — one grant per resource, Graph and Exchange |

The script resolves every permission against Microsoft Graph *before* it creates anything, so a typo or a permission
that only exists application-only fails with nothing half-built behind it. It also refuses to silently create a second
registration with a name already in use.

## Options

| Flag | For |
|---|---|
| `-WhatIf` | Sign in, resolve everything, report, change nothing. Run this first |
| `-TenantId contoso.onmicrosoft.com` | Target a specific tenant rather than being prompted |
| `-SingleTenant` | Only this tenant may sign in. Tighter, and right for a client who will only use their own |
| `-NoConsent` | Create it but leave consent to somebody who can grant it; prints the URL |
| `-WriteConfig` | Write the client ID into this machine's `tenant.json`, so there is nothing to paste |
| `-Name` | A different display name |
| `-Force` | Create another even though one with that name exists |
| `-Update` | Add what an existing registration lacks instead of creating one; `-ClientId` picks it when names collide |

## The permissions, and why each tier exists

These live in `scripts/New-AppRegistration.ps1` and in `Core/Auth/Scopes.cs`. A test fails the build if the two ever
disagree — without it, drift would surface as a consent error on a client's first write, long after anyone would
connect it to the change that caused it.

**Read (consented at first sign-in)**
```
openid  profile  offline_access
User.Read                                    Group.Read.All
User.Read.All                                Device.Read.All
Organization.Read.All                        AuditLog.Read.All
DeviceManagementManagedDevices.Read.All      UserAuthenticationMethod.Read.All
DeviceManagementConfiguration.Read.All
```

**Write (requested the first time the admin changes something, not up front)**
```
User.ReadWrite.All                           GroupMember.ReadWrite.All
User-PasswordProfile.ReadWrite.All           Group.ReadWrite.All
User.RevokeSessions.All                      UserAuthenticationMethod.ReadWrite.All
DeviceManagementManagedDevices.ReadWrite.All
DeviceManagementManagedDevices.PrivilegedOperations.All
```

**Sensitive (requested only when a reveal button is clicked)**
```
BitlockerKey.Read.All        DeviceLocalCredential.Read.All
```

**Exchange Online (Phase 3, doc 13) — its own resource, `Office 365 Exchange Online`**
```
Exchange.Manage
```
One delegated permission covers reading and changing mailboxes, because Exchange's role-based access control decides
what the admin can actually run. It is user-consentable, but the script grants it for the tenant so nobody meets a
prompt. A tenant without Exchange Online has no service principal for it; the script says so and carries on, and the
console's Mailbox tab stays unavailable there.

`Directory.ReadWrite.All` and `Directory.AccessAsUser.All` are deliberately absent: the granular scopes above are
sufficient and review far better.

> `Device.ReadWrite.All` is **application-only** on Microsoft Graph — there is no delegated equivalent short of
> `Directory.AccessAsUser.All`. Offboarding step 9 (delete Entra device objects) is therefore deferred to Phase 2.
> See `DECISIONS.md`, 2026-09-09.

## Roles the admin using the console needs

Scopes are necessary, not sufficient — Entra still checks the signed-in admin's roles. The console degrades per UX §6
when one is missing, naming the role rather than showing a bare 403.

| To do this | They need |
|---|---|
| Edit users, reset passwords | User Administrator, or Helpdesk Administrator for non-admins |
| Change group membership | Groups Administrator |
| Act on devices | Intune Administrator |
| Assign licences | License Administrator |
| Reveal BitLocker keys and LAPS passwords | Cloud Device Administrator |
| Remove authentication methods | Authentication Administrator |
| Read and manage mailboxes | Exchange Administrator or Exchange Recipient Administrator |

## Doing it by hand

Only if the script cannot be used. Entra admin center → App registrations → New registration; multitenant; then
Authentication → Add a platform → Mobile and desktop applications with the three redirect URIs above and **Allow
public client flows: Yes**; then API permissions → Microsoft Graph → Delegated, adding all 26 above, SharePoint → Delegated → `AllSites.FullControl`, and Office 365 Exchange Online → Delegated →
`Exchange.Manage`; then Grant admin consent. Copy the Application (client) ID into the console's setup screen.

This is the six-step click-through the script replaced. It is easy to get subtly wrong — a missing broker redirect URI
fails only on the machines that use the broker, which is all of them.

## Publisher verification

Optional, and only relevant if you ever move to a shared multi-tenant app: Branding & properties → publisher domain,
then publisher verification through Partner Center, so the consent screen says "verified publisher" rather than
showing the unverified warning. A per-tenant registration created by the tenant's own admin does not need it.

## SharePoint Online and Files.Read.All (M16)

- **SharePoint Online — `AllSites.FullControl` (delegated).** OneDrive settings, site admins and lock state through the
  SharePoint tenant admin API (doc 14). The console asks for it as
  `00000003-0000-0ff1-ce00-000000000000/AllSites.FullControl`; SharePoint's own roles decide what the admin may change.
- **Microsoft Graph — `Files.Read.All` (delegated).** The OneDrive export. Asked for at the first export, not at sign-in.
- Existing registrations: `New-AppRegistration.ps1 -Update` adds both and extends the admin consent.

## More than one tenant (M20)

One installation can open several tenants: the title bar's tenant name is the switcher, and "Add a tenant…" asks for
the tenant's id or one of its domains, then the client ID of that tenant's own registration — made there by running the
script in that tenant, as for the first. Left empty, the current tenant's registration is used, which works only where
it has been consented; for that, have the other tenant's Global Administrator open

```
https://login.microsoftonline.com/{tenant}/adminconsent?client_id={client id}
```

Each tenant is signed in with its own account (a partner admin may use one account for several); the console remembers
which, and pins every token to that tenant's authority.

## PIM (M18.2)

**Microsoft Graph, delegated — asked for the first time the admin opens Settings › My roles**
```
RoleEligibilitySchedule.Read.Directory      RoleAssignmentSchedule.ReadWrite.Directory
RoleManagementPolicy.Read.Directory
```
Only the admin's own roles are read (`filterByCurrentUser`) and only `selfActivate` is sent; nothing here can change
another person's roles. A tenant without Entra ID P2 or ID Governance answers with `AadPremiumLicenseRequired`, and
the console says the tenant has no PIM rather than listing nothing.

