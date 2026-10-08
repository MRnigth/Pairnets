-- Pairnets Cloud, migration 0001. Additive migrations only from here on.

CREATE TABLE accounts (
  id            TEXT PRIMARY KEY,                 -- acc_ + 26 base32-lower
  email         TEXT NOT NULL,                    -- verified, lowercase; where notices go
  created_at    INTEGER NOT NULL,
  updated_at    INTEGER NOT NULL
);
CREATE UNIQUE INDEX accounts_email ON accounts(email);

CREATE TABLE identities (
  provider      TEXT NOT NULL CHECK (provider IN ('google', 'email')),
  subject       TEXT NOT NULL,                    -- google: id_token sub; email: the lowercase address
  account_id    TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  email         TEXT NOT NULL,                    -- verified address seen at the last sign-in
  created_at    INTEGER NOT NULL,
  last_used_at  INTEGER NOT NULL,
  PRIMARY KEY (provider, subject)
);
CREATE INDEX identities_account ON identities(account_id);

CREATE TABLE sessions (
  id            TEXT PRIMARY KEY,                 -- ses_ + 26: public handle for lists and DELETE
  token_hash    BLOB NOT NULL UNIQUE,             -- SHA-256 of "pcs_..." (browser) or "pca_..." (app)
  account_id    TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  kind          TEXT NOT NULL CHECK (kind IN ('browser', 'app')),
  amr           TEXT NOT NULL,                    -- JSON array, e.g. ["google"]
  auth_time     INTEGER NOT NULL,
  created_at    INTEGER NOT NULL,
  last_seen_at  INTEGER NOT NULL,
  expires_at    INTEGER NOT NULL,
  user_agent    TEXT,                             -- at most 200 characters
  device_name   TEXT                              -- app sessions: the computer's name
);
CREATE INDEX sessions_account ON sessions(account_id);
CREATE INDEX sessions_expires ON sessions(expires_at);

CREATE TABLE login_challenges (
  id            TEXT PRIMARY KEY,                 -- chl_ + 26
  kind          TEXT NOT NULL CHECK (kind IN ('email')),
  email         TEXT NOT NULL,
  code_hash     BLOB NOT NULL UNIQUE,             -- SHA-256 of the emailed code
  binding_hash  BLOB NOT NULL,                    -- SHA-256 of the __Host-pn_el cookie value
  next          TEXT,                             -- allow-listed path (section 6.4)
  created_at    INTEGER NOT NULL,
  expires_at    INTEGER NOT NULL,
  used_at       INTEGER
);
CREATE INDEX login_challenges_expires ON login_challenges(expires_at);

CREATE TABLE device_logins (
  id                TEXT PRIMARY KEY,             -- dvl_ + 26
  device_code_hash  BLOB NOT NULL UNIQUE,         -- SHA-256 of "pcd_..."
  user_code         TEXT NOT NULL UNIQUE,         -- 8 base32-upper, no hyphen
  name              TEXT NOT NULL,
  system            TEXT,
  app_version       TEXT,
  status            TEXT NOT NULL CHECK (status IN ('pending', 'approved', 'denied', 'delivered')),
  account_id        TEXT REFERENCES accounts(id) ON DELETE CASCADE,
  created_at        INTEGER NOT NULL,
  expires_at        INTEGER NOT NULL,
  decided_at        INTEGER,
  last_poll_at      INTEGER
);
CREATE INDEX device_logins_expires ON device_logins(expires_at);

CREATE TABLE claim_codes (
  code_hash     BLOB PRIMARY KEY,                 -- SHA-256 of the canonical "PN-XXXX-XXXX-XXXX"
  account_id    TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  label         TEXT,
  created_at    INTEGER NOT NULL,
  expires_at    INTEGER NOT NULL,
  used_at       INTEGER,
  nest_id       TEXT                              -- the nest it created
);
CREATE INDEX claim_codes_account ON claim_codes(account_id, used_at, expires_at);

CREATE TABLE nests (
  id                 TEXT PRIMARY KEY,            -- nst_ + 26
  account_id         TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  label              TEXT NOT NULL,
  public_url         TEXT NOT NULL,               -- normalised (section 1.8)
  status             TEXT NOT NULL CHECK (status IN ('pending', 'active')),
  key_version        INTEGER NOT NULL DEFAULT 1,  -- heartbeat key = f(HB_MASTER, id, key_version); key not stored
  hosted_login       INTEGER NOT NULL DEFAULT 1 CHECK (hosted_login IN (0, 1)),
  created_at         INTEGER NOT NULL,
  confirmed_at       INTEGER,
  last_req_ts        INTEGER NOT NULL DEFAULT 0,  -- highest accepted X-Pairnets-Ts (replay guard)
  last_seen_at       INTEGER,                     -- last valid heartbeat
  last_version       TEXT,
  last_ready         INTEGER,
  last_public_host   TEXT,
  last_hosted_login  INTEGER
);
CREATE INDEX nests_account ON nests(account_id);
CREATE INDEX nests_status_created ON nests(status, created_at);

CREATE TABLE audit (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  at            INTEGER NOT NULL,
  account_id    TEXT REFERENCES accounts(id) ON DELETE CASCADE,
  event         TEXT NOT NULL,                    -- account_created, identity_linked, login, nest_claimed, ...
  nest_id       TEXT,
  detail        TEXT                              -- small JSON; never codes, tokens, keys or assertions
);
CREATE INDEX audit_account ON audit(account_id, at);
CREATE INDEX audit_at ON audit(at);

CREATE TABLE rate_counters (
  bucket        TEXT NOT NULL,                    -- event + ":" + keyed hash (section 6.10)
  window_start  INTEGER NOT NULL,
  count         INTEGER NOT NULL,
  PRIMARY KEY (bucket, window_start)
);
