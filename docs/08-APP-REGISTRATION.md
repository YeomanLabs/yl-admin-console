# 08 — Entra app registration (one per tenant)

The console signs in through an app registration in the tenant it manages. **One registration per tenant**, created by
a script, and it belongs to that tenant — not to YeomanLabs.

That choice is deliberate (DECISIONS.md, 2026-09-09). The alternative was one multi-tenant app owned by YeomanLabs
that every client consents to, which is less setup and means YeomanLabs owns an object holding `User.ReadWrite.All`
and `BitlockerKey.Read.All` against every client directory. A per-tenant registration survives a security review, and
revoking one client's access is deleting one object in their own tenant.

## The short version

```powershell
# See what it would do. Signs in, resolves every permission, creates nothing.
.\scripts\New-AppRegistration.ps1 -WhatIf

# Do it.
.\scripts\New-AppRegistration.ps1
```

It prints a client ID. Start the console and paste it into the setup screen — or pass `-WriteConfig` and skip that.

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
| Permissions | 22 **delegated** Graph permissions, listed below. No application permissions, ever |
| Consent | Granted for the tenant unless `-NoConsent` |

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

## Doing it by hand

Only if the script cannot be used. Entra admin center → App registrations → New registration; multitenant; then
Authentication → Add a platform → Mobile and desktop applications with the three redirect URIs above and **Allow
public client flows: Yes**; then API permissions → Microsoft Graph → Delegated, adding all 22 above; then Grant admin
consent. Copy the Application (client) ID into the console's setup screen.

This is the six-step click-through the script replaced. It is easy to get subtly wrong — a missing broker redirect URI
fails only on the machines that use the broker, which is all of them.

## Publisher verification

Optional, and only relevant if you ever move to a shared multi-tenant app: Branding & properties → publisher domain,
then publisher verification through Partner Center, so the consent screen says "verified publisher" rather than
showing the unverified warning. A per-tenant registration created by the tenant's own admin does not need it.
