# 04 — Microsoft Graph reference for this app

All v1.0 unless marked **beta**. Verify against https://learn.microsoft.com/graph/api/overview if anything 404s — endpoints are stable but `$select` property support on delta changes occasionally.

## Headers
- `Authorization: Bearer …`
- `ConsistencyLevel: eventual` when using `$count`, `$search`, or `$filter` on `endsWith`/`not`.
- `Prefer: return=minimal` on PATCH where you don't need the body (we usually *do* want it for write-through, so omit).

## Tenant / org
- `GET /organization?$select=id,displayName,verifiedDomains,onPremisesSyncEnabled,onPremisesLastSyncDateTime,assignedPlans,countryLetterCode` — tenant name, sync state, P1 detection (`assignedPlans[].servicePlanId == 41781fb2-bc02-4b7c-bd55-b576c07bb09d` = AAD_PREMIUM, `eec0eb4f-6444-4f95-aba0-50c24d67f998` = AAD_PREMIUM_P2).
- `GET /me?$select=id,displayName,userPrincipalName`
- `GET /me/memberOf/microsoft.graph.directoryRole?$select=displayName,roleTemplateId` — for role hints in the grey rule.

## Users
Select used everywhere (keep in one constant `GraphSelects.User`):
```
id,displayName,givenName,surname,userPrincipalName,mail,mailNickname,otherMails,proxyAddresses,
accountEnabled,userType,createdDateTime,jobTitle,department,companyName,officeLocation,
employeeId,employeeType,employeeHireDate,businessPhones,mobilePhone,streetAddress,city,state,
postalCode,country,usageLocation,preferredLanguage,onPremisesSyncEnabled,onPremisesImmutableId,
onPremisesDistinguishedName,onPremisesSamAccountName,onPremisesLastSyncDateTime,
onPremisesProvisioningErrors,assignedLicenses,assignedPlans,lastPasswordChangeDateTime,
signInActivity
```
- **Delta:** `GET /users/delta?$select={select minus signInActivity}` (signInActivity is not supported on delta). Follow `@odata.nextLink` until `@odata.deltaLink`. Removed objects appear as `{ "id": "...", "@odata.removed": { "reason": "deleted" } }`.
- **Live detail refresh:** `GET /users/{id}?$select=…,signInActivity` (needs `AuditLog.Read.All` + P1; if 403/400, retry without `signInActivity`).
- `GET /users/{id}/manager?$select=id,displayName,userPrincipalName` (404 = no manager, not an error)
- `GET /users/{id}/directReports?$select=id,displayName`
- `GET /users/{id}/memberOf?$select=id,displayName,groupTypes,mailEnabled,securityEnabled,onPremisesSyncEnabled,membershipRule` (paged; includes directoryRoles — filter by `@odata.type`)
- `GET /users/{id}/authentication/methods` — returns polymorphic list; `@odata.type` distinguishes `#microsoft.graph.microsoftAuthenticatorAuthenticationMethod`, `phoneAuthenticationMethod`, `fido2AuthenticationMethod`, `windowsHelloForBusinessAuthenticationMethod`, `emailAuthenticationMethod`, `softwareOathAuthenticationMethod`, `passwordAuthenticationMethod`, `temporaryAccessPassAuthenticationMethod`. Delete: `DELETE /users/{id}/authentication/{collection}/{methodId}` where collection is `phoneMethods`, `microsoftAuthenticatorMethods`, `fido2Methods`, `windowsHelloForBusinessMethods`, `emailMethods`, `softwareOathMethods`, `temporaryAccessPassMethods`. Password method cannot be deleted.
- `PATCH /users/{id}` body = changed properties only. Hybrid: PATCHing an on-prem-mastered property on a synced user returns 400 `Request_BadRequest` "Unable to update the specified properties for on-premises mastered Directory Sync objects" — the UI prevents this via the grey rule; the service still guards.
- `PATCH /users/{id}` `{ "accountEnabled": false }` — disable.
- `POST /users/{id}/revokeSignInSessions` — returns `{ "value": true }`.
- `PATCH /users/{id}` `{ "passwordProfile": { "forceChangePasswordNextSignIn": true, "password": "…" } }` — reset. Requires `User-PasswordProfile.ReadWrite.All` (or `Directory.AccessAsUser.All`) **and** a role like User Administrator / Privileged Authentication Administrator depending on the target's roles. Expect 403 for admins resetting other admins.
- `POST /users/{id}/assignLicense` `{ "addLicenses": [{ "skuId": "…", "disabledPlans": [] }], "removeLicenses": ["skuId", …] }`. Requires `usageLocation` set first. Group-based licenses cannot be removed here — detect via `assignedLicenses` vs `licenseAssignmentStates[].assignedByGroup` (`GET /users/{id}?$select=licenseAssignmentStates`).
- `DELETE /users/{id}/manager/$ref`
- `POST /users` — create; body in `07-TEMPLATES.md`. Response includes `id`. Then `assignLicense`, then group adds.
- `GET /users/{id}/registeredDevices` and `/ownedDevices` — Entra device objects (not Intune). Join to Intune via `azureADDeviceId`.

## Groups
Select: `id,displayName,description,mail,mailEnabled,mailNickname,securityEnabled,groupTypes,membershipRule,membershipRuleProcessingState,onPremisesSyncEnabled,visibility,createdDateTime,isAssignableToRole`
- **Delta:** `GET /groups/delta?$select={select},members` — `members@delta` array on each group carries `{ "@odata.type": "#microsoft.graph.user", "id": "…" }` for adds and the same with `"@removed"` for removes. The initial walk returns full membership; subsequent calls return only changes. **This is the only way to keep memberships in the cache cheaply — do not enumerate `/groups/{id}/members` per group.**
- `GET /groups/{id}/members?$select=id,displayName,userPrincipalName&$top=999` — on-demand for the group page (cache is authoritative for counts).
- `POST /groups/{id}/members/$ref` `{ "@odata.id": "https://graph.microsoft.com/v1.0/directoryObjects/{userId}" }`
- `DELETE /groups/{id}/members/{userId}/$ref`
- Skip rules: `groupTypes` contains `DynamicMembership` → no manual changes; `onPremisesSyncEnabled == true` → no cloud changes; `isAssignableToRole` → needs Privileged Role Administrator.

## Entra devices
Select: `id,deviceId,displayName,operatingSystem,operatingSystemVersion,trustType,isCompliant,isManaged,accountEnabled,approximateLastSignInDateTime,registrationDateTime,manufacturer,model,enrollmentType,managementType,profileType`
- **Delta:** `GET /devices/delta?$select={select}`
- `GET /devices/{id}/registeredOwners?$select=id,displayName,userPrincipalName`
- `deviceId` here == `azureADDeviceId` on the Intune object. Join on that.

## Intune (managed devices)
Select: `id,deviceName,userId,userPrincipalName,userDisplayName,azureADDeviceId,serialNumber,manufacturer,model,operatingSystem,osVersion,complianceState,isEncrypted,lastSyncDateTime,enrolledDateTime,managementAgent,deviceEnrollmentType,enrollmentProfileName,azureADRegistered,managedDeviceOwnerType,totalStorageSpaceInBytes,freeStorageSpaceInBytes,physicalMemoryInBytes,wiFiMacAddress,ethernetMacAddress,emailAddress,deviceCategoryDisplayName`
- `GET /deviceManagement/managedDevices?$select={select}&$top=1000` — **no delta**; full walk on interval. ~2k devices ≈ 2–3 pages. `autopilotEnrolled` and `joinType` are **beta-only**; on v1.0 derive Autopilot from `enrollmentProfileName != null` and join type from `deviceEnrollmentType` (DECISIONS 2026-09-09).
- `GET /deviceManagement/managedDevices/{id}/users` — registered users (primary user is `userId`).
- `GET /deviceManagement/managedDevices/{id}/deviceCompliancePolicyStates` — per-policy state; `/deviceCompliancePolicyStates/{id}/settingStates` for setting-level failure reasons.
- Actions: `POST /deviceManagement/managedDevices/{id}/syncDevice`, `/rebootNow`, `/retire`, `/wipe` (body `{ "keepEnrollmentData": false, "keepUserData": false }`), `/createDeviceLogCollectionRequest` (beta-ish behavior; v1.0 exists as `createDeviceLogCollectionRequest` with body `{ "templateType": "predefined" }`). Actions need `DeviceManagementManagedDevices.PrivilegedOperations.All`.
- Primary user change: `POST /deviceManagement/managedDevices/{id}/users/$ref` `{ "@odata.id": "https://graph.microsoft.com/v1.0/users/{userId}" }` — **beta** in practice for some tenants; try v1.0 first, fall back to beta, comment why.
- **BitLocker keys:** `GET /informationProtection/bitlocker/recoveryKeys?$filter=deviceId eq '{azureADDeviceId}'` lists key IDs; `GET /informationProtection/bitlocker/recoveryKeys/{keyId}?$select=key` reveals (needs `BitLockerKey.Read.All` + an appropriate role; every reveal is audited by Microsoft and by our action log).
- **LAPS:** `GET /directory/deviceLocalCredentials/{entraDeviceObjectId}?$select=credentials` — **beta** until GA is confirmed on the tenant; needs `DeviceLocalCredential.Read.All` + Cloud Device Administrator or higher. Response `credentials[]` with `accountName`, `passwordBase64`, `backupDateTime`. Decode base64 → UTF-16LE? **No** — it is base64 of the UTF-8 password string; decode with `Encoding.UTF8`. Verify on first real device.

## Licenses
- `GET /subscribedSkus?$select=skuId,skuPartNumber,prepaidUnits,consumedUnits,capabilityStatus,servicePlans` — cache; friendly names via a static map of common `skuPartNumber` → display name (`SPE_E3` → "Microsoft 365 E3", `ENTERPRISEPACK` → "Office 365 E3", `EMS` → "EMS E3", `AAD_PREMIUM` → "Entra ID P1", etc.). Unknown SKUs show the part number.

## Audit / activity
- `GET /auditLogs/directoryAudits?$filter=targetResources/any(t:t/id eq '{userId}')&$top=20&$orderby=activityDateTime desc` — needs `AuditLog.Read.All`. May be slow (2–5 s); load lazily on the Activity tab only.
- `GET /auditLogs/signIns?$filter=userId eq '{id}'&$top=10&$orderby=createdDateTime desc` — P1 only.

## Batching
`POST /$batch` `{ "requests": [{ "id": "1", "method": "GET", "url": "/users/{id}/manager" }, …] }` — max 20; inner `url` is relative to the version root; responses in `responses[]` each with `id`, `status`, `body`. Use for User 360 hydration: manager, memberOf page 1, auth methods, registeredDevices, licenseAssignmentStates in a single round-trip.

## Throttling
Limits are per app per tenant and per resource. Practical rules: ≤ 4 concurrent requests, honor `Retry-After` (seconds, integer), `$select` always, `$top=999` on directory collections, `$top=1000` on managedDevices, never poll faster than the configured delta interval.
