-- 0002_mailboxes: Exchange Online recipient facts per user (M13.4, docs/13-EXCHANGE-ONLINE.md §2).
-- A separate table rather than columns on users: users is rewritten by Graph delta, which knows nothing of Exchange,
-- and an upsert that does not mention a column must never be able to blank it. Exchange has no delta, so this table is
-- replaced by a periodic walk; a user with no row here is "not known yet", never "no mailbox".

CREATE TABLE mailboxes (
  user_id         TEXT PRIMARY KEY,   -- Entra object id (Exchange's ExternalDirectoryObjectId)
  recipient_type  TEXT NOT NULL,      -- RecipientTypeDetails exactly as Exchange reports it
  primary_smtp    TEXT,
  hidden          INTEGER NOT NULL DEFAULT 0,  -- HiddenFromAddressListsEnabled
  dir_synced      INTEGER NOT NULL DEFAULT 0,  -- IsDirSynced
  updated_at      TEXT NOT NULL
);
CREATE INDEX ix_mailboxes_type ON mailboxes(recipient_type);
