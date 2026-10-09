-- Pairnets Cloud, migration 0002 (RELAY.md): servers reached through the service, "one address for everyone".
-- Additive for the data: every row and column of 0001 is kept. SQLite cannot change a CHECK constraint in place, so the
-- nests table is rebuilt once (same columns plus three new ones, status 'broken' allowed) and its rows are copied over.
-- Nothing references nests(id) with a foreign key, so dropping the old table cascades nothing.

PRAGMA defer_foreign_keys = ON;

CREATE TABLE nests_v2 (
  id                 TEXT PRIMARY KEY,            -- nst_ + 26
  account_id         TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  label              TEXT NOT NULL,
  public_url         TEXT NOT NULL,               -- normalised (CONTRACT.md 1.8); '' for relay nests
  status             TEXT NOT NULL CHECK (status IN ('pending', 'active', 'broken')),
  key_version        INTEGER NOT NULL DEFAULT 1,  -- nest key = f(HB_MASTER, id, key_version); key not stored
  hosted_login       INTEGER NOT NULL DEFAULT 1 CHECK (hosted_login IN (0, 1)),
  created_at         INTEGER NOT NULL,
  confirmed_at       INTEGER,                     -- url: signed confirm; relay: first relayed answer
  last_req_ts        INTEGER NOT NULL DEFAULT 0,  -- highest accepted X-Pairnets-Ts (replay guard)
  last_seen_at       INTEGER,                     -- url: last valid heartbeat; relay: last answered status call
  last_version       TEXT,
  last_ready         INTEGER,
  last_public_host   TEXT,
  last_hosted_login  INTEGER,
  mode               TEXT NOT NULL DEFAULT 'url' CHECK (mode IN ('url', 'relay')),
  tunnel_id          TEXT,                        -- relay: the nest's own Cloudflare Tunnel (no hostname routed)
  routed_version     INTEGER                      -- relay: router_state.version of the last router deploy that had it
);

INSERT INTO nests_v2 (id, account_id, label, public_url, status, key_version, hosted_login, created_at, confirmed_at,
                      last_req_ts, last_seen_at, last_version, last_ready, last_public_host, last_hosted_login, mode)
  SELECT id, account_id, label, public_url, status, key_version, hosted_login, created_at, confirmed_at,
         last_req_ts, last_seen_at, last_version, last_ready, last_public_host, last_hosted_login, 'url'
  FROM nests;

DROP TABLE nests;
ALTER TABLE nests_v2 RENAME TO nests;
CREATE INDEX nests_account ON nests(account_id);
CREATE INDEX nests_status_created ON nests(status, created_at);
CREATE INDEX nests_mode_status ON nests(mode, status);

-- The server an app sign-in was approved for (RELAY.md section 6).
ALTER TABLE device_logins ADD COLUMN nest_id TEXT;

-- The installer's device-code flow (RELAY.md section 2).
CREATE TABLE server_links (
  id                TEXT PRIMARY KEY,             -- svl_ + 26
  device_code_hash  BLOB NOT NULL UNIQUE,         -- SHA-256 of "psd_..."
  user_code         TEXT NOT NULL UNIQUE,         -- 8 base32-upper, no hyphen
  hostname          TEXT,
  server_version    TEXT,
  status            TEXT NOT NULL CHECK (status IN ('pending', 'approving', 'approved', 'denied', 'delivered')),
  account_id        TEXT REFERENCES accounts(id) ON DELETE CASCADE,
  nest_id           TEXT,                         -- the nest made on approval
  created_at        INTEGER NOT NULL,
  expires_at        INTEGER NOT NULL,
  decided_at        INTEGER,
  last_poll_at      INTEGER
);
CREATE INDEX server_links_expires ON server_links(expires_at);

-- Router deploys (RELAY.md section 5): one row. A lease younger than 2 minutes means a deploy is in progress.
CREATE TABLE router_state (
  id            INTEGER PRIMARY KEY CHECK (id = 1),
  version       INTEGER NOT NULL DEFAULT 0,       -- counts successful deploys
  lease_id      TEXT,                             -- random id of the deploy in progress, or NULL
  lease_until   INTEGER,                          -- that deploy's lease ends here
  deployed_at   INTEGER,                          -- last successful deploy
  last_error    TEXT                              -- last failed step and status (never the API token)
);
INSERT INTO router_state (id, version) VALUES (1, 0);
