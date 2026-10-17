# 05 — Data model & cache schema

## Principle
Every cached object is stored twice: a **projection** of columns needed for lists and search, and the **raw JSON** as returned by Graph (only the `$select`ed properties). Detail views read `raw`. Never write a column you don't query.

## SQLite schema (`Cache/Migrations/0001_init.sql`)
```sql
PRAGMA journal_mode=WAL;

CREATE TABLE sync_state (
  entity        TEXT PRIMARY KEY,   -- 'users' | 'groups' | 'devices' | 'managed_devices' | 'skus' | 'organization'
  delta_link    TEXT,
  last_full_sync TEXT,              -- ISO 8601 UTC
  last_delta_sync TEXT,
  row_count     INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE users (
  id            TEXT PRIMARY KEY,
  upn           TEXT NOT NULL,
  display_name  TEXT,
  mail          TEXT,
  given_name    TEXT,
  surname       TEXT,
  job_title     TEXT,
  department    TEXT,
  employee_id   TEXT,
  phones_digits TEXT,               -- concatenated digits of businessPhones+mobilePhone for phone search
  account_enabled INTEGER NOT NULL,
  user_type     TEXT,               -- Member | Guest
  synced        INTEGER NOT NULL,   -- onPremisesSyncEnabled
  licensed      INTEGER NOT NULL,   -- assignedLicenses.Count > 0
  created       TEXT,
  raw           TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);
CREATE INDEX ix_users_upn ON users(upn);
CREATE INDEX ix_users_display ON users(display_name);

CREATE VIRTUAL TABLE users_fts USING fts5(
  display_name, upn, mail, employee_id, job_title, department, phones_digits,
  content='users', content_rowid='rowid', tokenize='unicode61 remove_diacritics 2'
);
-- triggers: users_ai / users_ad / users_au keep users_fts in sync (standard FTS5 external-content pattern)

CREATE TABLE groups (
  id            TEXT PRIMARY KEY,
  display_name  TEXT,
  description   TEXT,
  mail          TEXT,
  kind          TEXT NOT NULL,      -- Security | M365 | DistributionOrMailSecurity (derived, see below)
  dynamic       INTEGER NOT NULL,
  synced        INTEGER NOT NULL,
  role_assignable INTEGER NOT NULL,
  member_count  INTEGER NOT NULL DEFAULT 0,
  raw           TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);
CREATE VIRTUAL TABLE groups_fts USING fts5(display_name, mail, description, content='groups', content_rowid='rowid', tokenize='unicode61 remove_diacritics 2');

CREATE TABLE group_members (
  group_id      TEXT NOT NULL,
  member_id     TEXT NOT NULL,
  member_type   TEXT NOT NULL,      -- user | group | device | servicePrincipal
  PRIMARY KEY (group_id, member_id)
);
CREATE INDEX ix_gm_member ON group_members(member_id);

CREATE TABLE devices (                -- Entra device objects
  id            TEXT PRIMARY KEY,
  device_id     TEXT NOT NULL,      -- deviceId (== Intune azureADDeviceId)
  display_name  TEXT,
  os            TEXT,
  os_version    TEXT,
  trust_type    TEXT,               -- AzureAd | ServerAd | Workplace
  compliant     INTEGER,
  managed       INTEGER,
  enabled       INTEGER,
  last_signin   TEXT,
  raw           TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);
CREATE UNIQUE INDEX ix_devices_deviceid ON devices(device_id);

CREATE TABLE managed_devices (        -- Intune
  id            TEXT PRIMARY KEY,
  azure_ad_device_id TEXT,
  device_name   TEXT,
  serial        TEXT,
  model         TEXT,
  manufacturer  TEXT,
  os            TEXT,
  os_version    TEXT,
  compliance    TEXT,               -- compliant | noncompliant | inGracePeriod | unknown | conflict | error
  encrypted     INTEGER,
  primary_user_id TEXT,
  primary_user_upn TEXT,
  primary_user_name TEXT,
  last_sync     TEXT,
  enrolled      TEXT,
  autopilot     INTEGER,
  owner_type    TEXT,
  raw           TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);
CREATE INDEX ix_md_user ON managed_devices(primary_user_id);
CREATE INDEX ix_md_aad ON managed_devices(azure_ad_device_id);
CREATE VIRTUAL TABLE devices_fts USING fts5(device_name, serial, model, primary_user_name, primary_user_upn, content='managed_devices', content_rowid='rowid', tokenize='unicode61 remove_diacritics 2');

CREATE TABLE skus (
  sku_id        TEXT PRIMARY KEY,
  part_number   TEXT NOT NULL,
  friendly_name TEXT,
  enabled       INTEGER NOT NULL,   -- prepaidUnits.enabled
  consumed      INTEGER NOT NULL,
  raw           TEXT NOT NULL
);

CREATE TABLE recent_objects (
  object_id     TEXT PRIMARY KEY,
  object_type   TEXT NOT NULL,
  opened_at     TEXT NOT NULL,
  open_count    INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE organization (
  id TEXT PRIMARY KEY, display_name TEXT, synced INTEGER, last_sync TEXT, has_p1 INTEGER, raw TEXT NOT NULL
);
```

Group `kind` derivation: `groupTypes` contains `Unified` → `M365`; else `securityEnabled && !mailEnabled` → `Security`; else `DistributionOrMailSecurity`.

### `mailboxes` (`0002_mailboxes.sql`, M13.4)

Exchange Online's recipient facts per user, keyed by Entra object id (Exchange's `ExternalDirectoryObjectId`):
`recipient_type` (RecipientTypeDetails as Exchange reports it), `primary_smtp`, `hidden`, `dir_synced`, `updated_at`.

A separate table, not columns on `users`: `users` is rewritten by Graph delta, and an upsert that does not mention a
column must never be able to blank it. Exchange has no delta, so the table is replaced by a full walk on the device
interval (`Get-User` for every user, mailbox or not, joined to `Get-Mailbox`). **No row means "not known yet", never "no
mailbox"** — the "No mailbox" filter only matches users Exchange has actually reported on. The walk is skipped quietly
when the Exchange permission is not granted or the admin holds no Exchange role; neither is a sync error.

## Core models (C# records in `Models/`)
`DirectoryUser`, `DirectoryGroup`, `EntraDevice`, `ManagedDevice`, `LicenseSku`, `Organization`, `AuthMethod` (with `Kind` enum and `DeleteCollectionName`), `SearchHit` (`ObjectType`, `Id`, `Primary`, `Secondary`, `Badges`, `Score`). Each has `static X FromJson(JsonElement)` and the user/device records expose the computed badge booleans used by the UI.

## Hybrid read-only rule
When `DirectoryUser.OnPremisesSyncEnabled == true`, these properties are **read-only in the UI** (mastered on-prem; Graph rejects the PATCH anyway):
`displayName, givenName, surname, userPrincipalName*, mail*, mailNickname, proxyAddresses, jobTitle, department, companyName, officeLocation, employeeId, employeeType, businessPhones, mobilePhone, streetAddress, city, state, postalCode, country, manager, accountEnabled**`

\* UPN and mail can technically be changed in the cloud under some sync configurations; treat as read-only in Phase 1.
\*\* `accountEnabled` on a synced user *is* accepted by Graph but is overwritten by the next Entra Connect sync cycle. In Phase 1: allow the disable, and show a warning caption "Synced account — also disable in on-prem AD or this reverts within 30 minutes." Offboarding step catalog reflects this.

**Always writable regardless of sync:** `usageLocation, preferredLanguage, assignedLicenses, group membership (cloud groups), authentication methods, sign-in sessions, passwordProfile (only if password writeback is enabled — attempt and surface the Graph error), Intune device actions`.

## Object identity joins
- User ↔ Intune devices: `managed_devices.primary_user_id = users.id`; also `/managedDevices/{id}/users` for shared devices.
- Intune device ↔ Entra device: `managed_devices.azure_ad_device_id = devices.device_id`.
- Entra device ↔ owner: `/devices/{id}/registeredOwners` on demand (not cached in Phase 1).
- User ↔ groups: `group_members.member_id = users.id`.
- (Phase 2) Entra user ↔ AD user: `onPremisesImmutableId` == base64(`ms-DS-ConsistencyGuid`), fallback base64(`objectGUID`).
- (Phase 3) Entra user ↔ Exchange recipient: `mailboxes.user_id = users.id` = Exchange's `ExternalDirectoryObjectId`, which every cmdlet also accepts as `-Identity`.
