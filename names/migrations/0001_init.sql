-- Pairnets names (names/README.md). Additive migrations only from here on.

-- One row per name. status: 'creating' while its tunnel and DNS record are being made, 'active' once they work,
-- 'releasing' while they are being deleted, 'broken' when an undo or a give-back could not finish (the hourly sweep
-- deletes what is left, then the row). Only a SHA-256 of the manage key is kept; the tunnel token is never stored.
CREATE TABLE names (
  name            TEXT PRIMARY KEY,
  status          TEXT NOT NULL CHECK (status IN ('creating', 'active', 'releasing', 'broken')),
  tunnel_id       TEXT,
  dns_record_id   TEXT,
  manage_key_hash BLOB,
  email           TEXT NOT NULL,
  ip_bucket       TEXT NOT NULL,
  created_at      INTEGER NOT NULL,
  updated_at      INTEGER NOT NULL
);
-- One name per email address, whatever its status.
CREATE UNIQUE INDEX names_email ON names (email);
CREATE INDEX names_ip ON names (ip_bucket);
CREATE INDEX names_created ON names (created_at);

-- Email checks in progress: a 6-digit code (only its HMAC is kept), 15 minutes, 5 tries.
CREATE TABLE claims (
  id         TEXT PRIMARY KEY,
  name       TEXT NOT NULL,
  email      TEXT NOT NULL,
  code_mac   BLOB NOT NULL,
  attempts   INTEGER NOT NULL DEFAULT 0,
  ip_bucket  TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL
);
CREATE INDEX claims_expires ON claims (expires_at);

-- Fixed-window rate limits; the bucket is a keyed hash, never a raw IP or address.
CREATE TABLE rate_counters (
  bucket       TEXT NOT NULL,
  window_start INTEGER NOT NULL,
  count        INTEGER NOT NULL,
  PRIMARY KEY (bucket, window_start)
);

-- What happened, kept 90 days. `detail` never holds tokens, keys, codes or email addresses.
CREATE TABLE events (
  at     INTEGER NOT NULL,
  event  TEXT NOT NULL,
  name   TEXT,
  detail TEXT
);
CREATE INDEX events_at ON events (at);
