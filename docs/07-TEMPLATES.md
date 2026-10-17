# 07 — User templates & creation

## Concept
A **template** is a JSON snapshot of the attributes, licenses, and group memberships that define a role in the org ("Sales rep — Madison"). Templates are created **from an existing user** (snapshot) or edited by hand. Creating a user from a template fills everything except the person-specific fields.

## Template file (`templates\users\{slug}.json`)
```json
{
  "schemaVersion": 1,
  "name": "Sales Rep — Madison",
  "createdFrom": { "userId": "…", "upn": "jdoe@contoso.com", "at": "2026-09-08T14:00:00Z" },
  "attributes": {
    "jobTitle": "Sales Representative",
    "department": "Sales",
    "companyName": "Contoso",
    "officeLocation": "Madison",
    "streetAddress": "…", "city": "Madison", "state": "WI", "postalCode": "53703", "country": "US",
    "usageLocation": "US",
    "preferredLanguage": "en-US",
    "employeeType": "Employee",
    "businessPhones": ["+1 608 555 0100"]
  },
  "managerId": "…",
  "licenses": [ { "skuId": "…", "skuPartNumber": "SPE_E3", "disabledPlans": [] } ],
  "groups": [ { "id": "…", "displayName": "Sales-Team" } ],
  "upnRule": "{first}.{last}@{domain}",
  "mailNicknameRule": "{first}.{last}",
  "displayNameRule": "{First} {Last}",
  "passwordPolicy": { "length": 16, "forceChangeOnFirstSignIn": true },
  "excludedOnSnapshot": ["dynamic groups", "synced groups", "group-assigned licenses"]
}
```

## Snapshot rules (Create template from user)
- Copy the attribute allow-list above. **Never** copy: names, UPN, mail, phones marked personal (mobilePhone), employeeId, employeeHireDate, address if the source has a different office than the template name suggests (just copy; let them edit).
- Licenses: direct assignments only (`licenseAssignmentStates` where `assignedByGroup == null`). Note group-assigned ones in `excludedOnSnapshot` detail.
- Groups: cloud, non-dynamic, non-role-assignable groups only. Skipped groups shown in a summary after snapshot.
- Manager: copied by ID with displayName cached for display.
- Preview the resulting template in an editor before saving. Slug = kebab-case of name.

## Create-from-template flow (Ctrl+N)
1. Pick template (list with "created from" and "last used").
2. Form: First name, Last name, (Display name auto from rule, editable), UPN (auto from rule with domain dropdown of `verifiedDomains`, live uniqueness check via `GET /users?$filter=userPrincipalName eq '{upn}'&$count=true` debounced 300 ms; also check `proxyAddresses` collision via `$filter=proxyAddresses/any(p:p eq 'SMTP:{mail}')`), Manager (prefilled, picker), Office/Department/Title (prefilled, editable), Employee ID (optional), Start date (optional → `employeeHireDate`), Mobile (optional), Licenses (prefilled checkboxes with seat counts; disabled with reason if 0 seats), Groups (prefilled chips, add/remove).
3. Preview (same pattern as offboarding): "Create user **Jane Doe** as **jane.doe@contoso.com** · usageLocation US · 2 licenses · 5 groups · manager Sam Smith".
4. Execute: `POST /users` → `assignLicense` → batch group adds → `PUT /users/{id}/manager/$ref`. Each step logged. On `POST /users` failure, halt. On later failures, continue and report.
5. Result screen: temporary password shown **once** with copy button and a "Send via…" hint (no email sending in Phase 1); "Open user" button; "Create another" button.

`POST /users` body:
```json
{
  "accountEnabled": true,
  "displayName": "Jane Doe", "givenName": "Jane", "surname": "Doe",
  "userPrincipalName": "jane.doe@contoso.com", "mailNickname": "jane.doe",
  "usageLocation": "US",
  "passwordProfile": { "password": "…", "forceChangePasswordNextSignIn": true },
  "jobTitle": "…", "department": "…", "companyName": "…", "officeLocation": "…", …
}
```

## Password generation
`RandomNumberGenerator`-based, template `length` (default 16), guaranteed ≥1 upper/lower/digit/symbol from a symbol set that avoids ambiguous glyphs (`0O1lI|`). Never logged, never persisted, shown once.

## Hybrid note
Phase 1 creates **cloud-only** users. If the tenant is hybrid (`organization.onPremisesSyncEnabled`), show an InfoBar on the template picker: "This tenant syncs from on-prem AD. Users created here are cloud-only. On-prem creation arrives in Phase 2." Do not block.

## Hybrid templates (M17)

A template with an `onPrem` section creates the person in AD (the dialog's "Create in Active Directory", on by default
when a DC answers) and finishes the cloud and mailbox halves after Entra Connect:

```json
"onPrem": {
  "ouDn": "OU=Staff,DC=contoso,DC=com",
  "groups": [ { "dn": "CN=VPN Users,OU=Groups,DC=contoso,DC=com", "name": "VPN Users" } ],
  "attributes": { "department": "Sales", "title": "Account Manager", "physicalDeliveryOfficeName": "Madison" },
  "samRule": "{first}.{last}",
  "upnSuffix": "contoso.com",
  "addressRules": [ "{alias}@contoso.co.uk" ]
},
"exchange": {
  "language": "en-GB", "timeZone": "GMT Standard Time",
  "mailboxAccess": [ { "mailboxId": "<object id>", "label": "sales@contoso.com", "fullAccess": true, "sendAs": false } ]
}
```

Snapshotting a synced user fills both from AD and Exchange. The OU must be inside Entra Connect's sync scope, or the cloud
half never happens. Pending people are on Home until finished or abandoned.

