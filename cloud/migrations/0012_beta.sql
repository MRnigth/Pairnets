-- Pairnets Cloud, migration 0012: the beta program (src/routes/beta.ts). Additive: one row per account that signed up
-- (kept after it leaves, with left_at): when it asked for each beta (*_requested_at), whether that beta is approved
-- (public_allowed, developer_allowed: at once when BETA_AUTO_APPROVE is on, else on /admin/beta), whether an admin
-- removed it (blocked_at: its next sign-up waits), and a rev that goes up on every change. Each nest remembers the rev
-- it was last told (beta_rev_pushed), so the hourly cron tells the ones that missed a change. Bug reports gain the app's
-- channel and build. Nothing about the computers is kept here. Apply this migration before the Worker that uses it is
-- deployed.

CREATE TABLE beta_members (
  account_id              TEXT PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
  joined_at               INTEGER NOT NULL,
  left_at                 INTEGER,
  terms_version           TEXT NOT NULL,
  public_allowed          INTEGER NOT NULL DEFAULT 0,
  developer_allowed       INTEGER NOT NULL DEFAULT 0,
  rev                     INTEGER NOT NULL DEFAULT 1,
  public_requested_at     INTEGER,
  developer_requested_at  INTEGER,
  blocked_at              INTEGER
);

ALTER TABLE nests ADD COLUMN beta_rev_pushed INTEGER NOT NULL DEFAULT 0;

ALTER TABLE bug_reports ADD COLUMN channel TEXT;

ALTER TABLE bug_reports ADD COLUMN build INTEGER;
