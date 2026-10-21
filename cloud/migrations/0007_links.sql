-- Pairnets Cloud, migration 0007 (RELAY.md section 11): linked nests. An invite can be for a nest instead of a person:
-- the friend asks to join with one of their own nests, and once the owner lets them in, that nest gets one key on the
-- owner's nest and keeps the shared folders in step there (docs/DECISIONS.md, "4 nests per account, and linked nests").
-- The friend is a member as for any invite (nest_members), so access, folders and removal work as for a person.
-- Additive: two columns and one table. Apply this migration before the Worker that uses it is deployed.

ALTER TABLE nest_invites ADD COLUMN kind TEXT NOT NULL DEFAULT 'person' CHECK (kind IN ('person', 'nest'));
-- For kind 'nest': the requester's own nest that is to join (set when they ask to join).
ALTER TABLE nest_invites ADD COLUMN joining_nest TEXT;

-- One row per linked pair. 'pending': let in, but the key is not handed over yet (a nest was offline; the hourly cron
-- tries again). 'active': the joining nest has its key. The key itself is never stored here.
CREATE TABLE nest_links (
  id            TEXT PRIMARY KEY,                 -- lnk_ + 26, also the link's id on the joining nest
  from_nest     TEXT NOT NULL,                    -- the nest that shares
  to_nest       TEXT NOT NULL,                    -- the nest that joins
  account_id    TEXT NOT NULL,                    -- the joining nest's owner (the member on from_nest)
  device_id     TEXT,                             -- the joining nest's key on from_nest, once made
  status        TEXT NOT NULL CHECK (status IN ('pending', 'active')),
  created_at    INTEGER NOT NULL,
  UNIQUE (from_nest, to_nest)
);
CREATE INDEX nest_links_to ON nest_links(to_nest);
CREATE INDEX nest_links_account ON nest_links(account_id);
