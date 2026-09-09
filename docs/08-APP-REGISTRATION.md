# 08 — Entra app registration (one-time, human task)

Do this in the **YeomanLabs dev tenant** first; later a customer tenant admin consents to the same multi-tenant app.

1. Entra admin center → App registrations → **New registration**
   - Name: `YeomanLabs Admin Console` (working name)
   - Supported account types: **Accounts in any organizational directory (multitenant)**
   - Redirect URI: leave blank for now
2. **Authentication** → Add a platform → **Mobile and desktop applications**:
   - Check `https://login.microsoftonline.com/common/oauth2/nativeclient`
   - Add custom: `ms-appx-web://microsoft.aad.brokerplugin/{ClientId}` (WAM broker) — paste the Application (client) ID
   - Add custom: `http://localhost` (fallback system-browser flow)
   - **Allow public client flows: Yes**
3. **API permissions** → Microsoft Graph → **Delegated**. Add all of the following now (consent happens in-app incrementally; adding them here just makes them available):

**Read tier (requested at first run)**
```
openid  profile  offline_access
User.Read
User.Read.All
Group.Read.All
Device.Read.All
Organization.Read.All
DeviceManagementManagedDevices.Read.All
DeviceManagementConfiguration.Read.All
UserAuthenticationMethod.Read.All
AuditLog.Read.All
```
**Write tier (incremental, first write)**
```
User.ReadWrite.All
User-PasswordProfile.ReadWrite.All
User.RevokeSessions.All
GroupMember.ReadWrite.All
Group.ReadWrite.All
UserAuthenticationMethod.ReadWrite.All
Device.ReadWrite.All
DeviceManagementManagedDevices.ReadWrite.All
DeviceManagementManagedDevices.PrivilegedOperations.All
```
**Sensitive tier (requested only when the reveal button is clicked)**
```
BitLockerKey.Read.All
DeviceLocalCredential.Read.All
```
Do **not** add `Directory.ReadWrite.All` or `Directory.AccessAsUser.All`; the granular scopes above are sufficient and review better.

4. Grant admin consent for the dev tenant (button on the API permissions page) so first-run testing skips the consent prompt. For customer tenants, the app's own consent screen handles it.
5. Copy **Application (client) ID** into `src/YeomanLabs.AdminConsole.App/appsettings.json` → `Auth.ClientId`. No client secret — this is a public client.
6. Optional but recommended: **Branding & properties** → publisher domain `yeomanlabs.co`, and complete **publisher verification** (requires the MPN/Partner Center ID) so the consent screen shows "verified publisher" instead of the unverified warning. Blocked until Partner Center enrollment clears.

Roles the signed-in admin needs for full functionality (scopes are necessary, not sufficient): User Administrator (or Helpdesk Administrator for password resets on non-admins), Groups Administrator, Intune Administrator, License Administrator, Cloud Device Administrator (LAPS/BitLocker), Authentication Administrator (MFA method removal). The app must degrade per §6 of the UX spec when any are missing.
