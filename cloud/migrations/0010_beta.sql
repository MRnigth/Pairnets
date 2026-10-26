-- Pairnets Cloud, migration 0010: the beta program (src/routes/beta.ts). Additive: one row per account that joined (and
-- kept after it leaves, with left_at), what it may get (public_allowed, developer_allowed: set on /admin/beta), and a
-- rev that goes up on every change. Each nest remembers the rev it was last told (beta_rev_pushed), so the hourly cron
-- tells the ones that missed a change. Bug reports gain the app's channel and build. Apply this migration before the
-- Worker that uses it is deployed.

CREATE TABLE beta_members (
  account_id         TEXT PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
  joined_at          INTEGER NOT NULL,
  left_at            INTEGER,
  terms_version      TEXT NOT NULL,
  public_allowed     INTEGER NOT NULL DEFAULT 0,
  developer_allowed  INTEGER NOT NULL DEFAULT 0,
  rev                INTEGER NOT NULL DEFAULT 1
);

ALTER TABLE nests ADD COLUMN beta_rev_pushed INTEGER NOT NULL DEFAULT 0;

ALTER TABLE bug_reports ADD COLUMN channel TEXT;

ALTER TABLE bug_reports ADD COLUMN build INTEGER;
