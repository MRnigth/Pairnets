-- Pairnets Cloud, migration 0006 (RELAY.md section 10): friends on a nest. The owner invites someone by a link (or an
-- email holding it); the person signs in and asks to join; the owner lets them in or declines. Only then is there a
-- member row, and only then can that person's computers get keys from the nest (its people, with access and folders).
-- Additive: two new tables, nothing else changes. Like 0002, nothing references nests(id) or accounts(id) with a foreign
-- key: the rows are removed by the code that removes a nest or an account (accounts.ts, servers.ts, cron.ts).
-- Apply this migration before the Worker that uses it is deployed.

-- Who may use a nest besides its owner. synced = 0: the nest still has to hear about a change of access or folders, or
-- (removed_at set) that the person was removed; the hourly cron retries, and a removed row goes once the nest confirmed.
CREATE TABLE nest_members (
  nest_id       TEXT NOT NULL,                    -- nst_ + 26, a relayed nest
  account_id    TEXT NOT NULL,                    -- acc_ + 26, the friend
  access        TEXT NOT NULL CHECK (access IN ('edit', 'view')),
  folders       TEXT,                             -- JSON array of top-level folder names; NULL = the whole nest
  added_at      INTEGER NOT NULL,
  synced        INTEGER NOT NULL DEFAULT 1 CHECK (synced IN (0, 1)),
  removed_at    INTEGER,
  PRIMARY KEY (nest_id, account_id)
);
CREATE INDEX nest_members_account ON nest_members(account_id);
CREATE INDEX nest_members_synced ON nest_members(synced);

-- Invites. Using the link only makes a request (requested_by); the owner decides. The token is kept as it is, so the
-- owner can copy the link again: on its own it lets nobody in. The email (when Pairnets sent the invite) goes with the
-- row when the invite ends (cron.ts).
CREATE TABLE nest_invites (
  id            TEXT PRIMARY KEY,                 -- inv_ + 26
  nest_id       TEXT NOT NULL,
  token         TEXT NOT NULL UNIQUE,             -- pni_ + 32 base32-lower, the link's ?t=
  email         TEXT,                             -- where Pairnets sent it, or NULL (the owner copied the link)
  access        TEXT NOT NULL CHECK (access IN ('edit', 'view')),
  folders       TEXT,                             -- as nest_members.folders
  created_by    TEXT NOT NULL,                    -- the owner's account id
  created_at    INTEGER NOT NULL,
  expires_at    INTEGER NOT NULL,                 -- created_at + 7 days; asking to join must happen before it
  requested_by  TEXT,                             -- the account that asked to join (once)
  requested_at  INTEGER,
  decided_at    INTEGER,
  decision      TEXT CHECK (decision IN ('in', 'declined')),
  cancelled_at  INTEGER
);
CREATE INDEX nest_invites_nest ON nest_invites(nest_id);
CREATE INDEX nest_invites_expires ON nest_invites(expires_at);
