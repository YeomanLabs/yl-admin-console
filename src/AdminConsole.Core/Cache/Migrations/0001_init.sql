-- 0001_init: schema from docs/05-DATA-MODEL.md.
-- journal_mode=WAL and synchronous=NORMAL are set by CacheDb on every connection open
-- (journal_mode cannot change inside the migration transaction).

CREATE TABLE sync_state (
  entity          TEXT PRIMARY KEY,   -- 'users' | 'groups' | 'devices' | 'managed_devices' | 'skus' | 'organization'
  delta_link      TEXT,
  last_full_sync  TEXT,               -- ISO 8601 UTC
  last_delta_sync TEXT,
  row_count       INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE users (
  id              TEXT PRIMARY KEY,
  upn             TEXT NOT NULL,
  display_name    TEXT,
  mail            TEXT,
  given_name      TEXT,
  surname         TEXT,
  job_title       TEXT,
  department      TEXT,
  employee_id     TEXT,
  phones_digits   TEXT,               -- concatenated digits of businessPhones+mobilePhone for phone search
  account_enabled INTEGER NOT NULL,
  user_type       TEXT,               -- Member | Guest
  synced          INTEGER NOT NULL,   -- onPremisesSyncEnabled
  licensed        INTEGER NOT NULL,   -- assignedLicenses.Count > 0
  created         TEXT,
  raw             TEXT NOT NULL,
  updated_at      TEXT NOT NULL
);
CREATE INDEX ix_users_upn ON users(upn);
CREATE INDEX ix_users_display ON users(display_name);

CREATE VIRTUAL TABLE users_fts USING fts5(
  display_name, upn, mail, employee_id, job_title, department, phones_digits,
  content='users', content_rowid='rowid', tokenize='unicode61 remove_diacritics 2'
);
CREATE TRIGGER users_ai AFTER INSERT ON users BEGIN
  INSERT INTO users_fts(rowid, display_name, upn, mail, employee_id, job_title, department, phones_digits)
  VALUES (new.rowid, new.display_name, new.upn, new.mail, new.employee_id, new.job_title, new.department, new.phones_digits);
END;
CREATE TRIGGER users_ad AFTER DELETE ON users BEGIN
  INSERT INTO users_fts(users_fts, rowid, display_name, upn, mail, employee_id, job_title, department, phones_digits)
  VALUES ('delete', old.rowid, old.display_name, old.upn, old.mail, old.employee_id, old.job_title, old.department, old.phones_digits);
END;
CREATE TRIGGER users_au AFTER UPDATE ON users BEGIN
  INSERT INTO users_fts(users_fts, rowid, display_name, upn, mail, employee_id, job_title, department, phones_digits)
  VALUES ('delete', old.rowid, old.display_name, old.upn, old.mail, old.employee_id, old.job_title, old.department, old.phones_digits);
  INSERT INTO users_fts(rowid, display_name, upn, mail, employee_id, job_title, department, phones_digits)
  VALUES (new.rowid, new.display_name, new.upn, new.mail, new.employee_id, new.job_title, new.department, new.phones_digits);
END;

CREATE TABLE groups (
  id              TEXT PRIMARY KEY,
  display_name    TEXT,
  description     TEXT,
  mail            TEXT,
  kind            TEXT NOT NULL,      -- Security | M365 | DistributionOrMailSecurity (derived)
  dynamic         INTEGER NOT NULL,
  synced          INTEGER NOT NULL,
  role_assignable INTEGER NOT NULL,
  member_count    INTEGER NOT NULL DEFAULT 0,
  raw             TEXT NOT NULL,
  updated_at      TEXT NOT NULL
);
CREATE VIRTUAL TABLE groups_fts USING fts5(
  display_name, mail, description,
  content='groups', content_rowid='rowid', tokenize='unicode61 remove_diacritics 2'
);
CREATE TRIGGER groups_ai AFTER INSERT ON groups BEGIN
  INSERT INTO groups_fts(rowid, display_name, mail, description)
  VALUES (new.rowid, new.display_name, new.mail, new.description);
END;
CREATE TRIGGER groups_ad AFTER DELETE ON groups BEGIN
  INSERT INTO groups_fts(groups_fts, rowid, display_name, mail, description)
  VALUES ('delete', old.rowid, old.display_name, old.mail, old.description);
END;
CREATE TRIGGER groups_au AFTER UPDATE ON groups BEGIN
  INSERT INTO groups_fts(groups_fts, rowid, display_name, mail, description)
  VALUES ('delete', old.rowid, old.display_name, old.mail, old.description);
  INSERT INTO groups_fts(rowid, display_name, mail, description)
  VALUES (new.rowid, new.display_name, new.mail, new.description);
END;

CREATE TABLE group_members (
  group_id        TEXT NOT NULL,
  member_id       TEXT NOT NULL,
  member_type     TEXT NOT NULL,      -- user | group | device | servicePrincipal
  PRIMARY KEY (group_id, member_id)
);
CREATE INDEX ix_gm_member ON group_members(member_id);

CREATE TABLE devices (                  -- Entra device objects
  id              TEXT PRIMARY KEY,
  device_id       TEXT NOT NULL,      -- deviceId (== Intune azureADDeviceId)
  display_name    TEXT,
  os              TEXT,
  os_version      TEXT,
  trust_type      TEXT,               -- AzureAd | ServerAd | Workplace
  compliant       INTEGER,
  managed         INTEGER,
  enabled         INTEGER,
  last_signin     TEXT,
  raw             TEXT NOT NULL,
  updated_at      TEXT NOT NULL
);
CREATE UNIQUE INDEX ix_devices_deviceid ON devices(device_id);

CREATE TABLE managed_devices (          -- Intune
  id                 TEXT PRIMARY KEY,
  azure_ad_device_id TEXT,
  device_name        TEXT,
  serial             TEXT,
  model              TEXT,
  manufacturer       TEXT,
  os                 TEXT,
  os_version         TEXT,
  compliance         TEXT,            -- compliant | noncompliant | inGracePeriod | unknown | conflict | error
  encrypted          INTEGER,
  primary_user_id    TEXT,
  primary_user_upn   TEXT,
  primary_user_name  TEXT,
  last_sync          TEXT,
  enrolled           TEXT,
  autopilot          INTEGER,
  owner_type         TEXT,
  raw                TEXT NOT NULL,
  updated_at         TEXT NOT NULL
);
CREATE INDEX ix_md_user ON managed_devices(primary_user_id);
CREATE INDEX ix_md_aad ON managed_devices(azure_ad_device_id);
CREATE VIRTUAL TABLE devices_fts USING fts5(
  device_name, serial, model, primary_user_name, primary_user_upn,
  content='managed_devices', content_rowid='rowid', tokenize='unicode61 remove_diacritics 2'
);
CREATE TRIGGER managed_devices_ai AFTER INSERT ON managed_devices BEGIN
  INSERT INTO devices_fts(rowid, device_name, serial, model, primary_user_name, primary_user_upn)
  VALUES (new.rowid, new.device_name, new.serial, new.model, new.primary_user_name, new.primary_user_upn);
END;
CREATE TRIGGER managed_devices_ad AFTER DELETE ON managed_devices BEGIN
  INSERT INTO devices_fts(devices_fts, rowid, device_name, serial, model, primary_user_name, primary_user_upn)
  VALUES ('delete', old.rowid, old.device_name, old.serial, old.model, old.primary_user_name, old.primary_user_upn);
END;
CREATE TRIGGER managed_devices_au AFTER UPDATE ON managed_devices BEGIN
  INSERT INTO devices_fts(devices_fts, rowid, device_name, serial, model, primary_user_name, primary_user_upn)
  VALUES ('delete', old.rowid, old.device_name, old.serial, old.model, old.primary_user_name, old.primary_user_upn);
  INSERT INTO devices_fts(rowid, device_name, serial, model, primary_user_name, primary_user_upn)
  VALUES (new.rowid, new.device_name, new.serial, new.model, new.primary_user_name, new.primary_user_upn);
END;

CREATE TABLE skus (
  sku_id          TEXT PRIMARY KEY,
  part_number     TEXT NOT NULL,
  friendly_name   TEXT,
  enabled         INTEGER NOT NULL,   -- prepaidUnits.enabled
  consumed        INTEGER NOT NULL,
  raw             TEXT NOT NULL
);

CREATE TABLE recent_objects (
  object_id       TEXT PRIMARY KEY,
  object_type     TEXT NOT NULL,
  opened_at       TEXT NOT NULL,
  open_count      INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE organization (
  id              TEXT PRIMARY KEY,
  display_name    TEXT,
  synced          INTEGER,
  last_sync       TEXT,
  has_p1          INTEGER,
  raw             TEXT NOT NULL
);
