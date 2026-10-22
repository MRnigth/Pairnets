-- Pairnets Cloud, migration 0009: bug reports the apps send when the user presses "Send bug report" (src/routes/bugreport.ts).
-- One row per report: the app's bug report, cleaned of file, folder and computer names before it left, and an optional
-- note. Nothing here says who sent it: no IP address, account, device or nest. The hourly cron deletes rows older than
-- 90 days. Apply this migration before the Worker that uses it is deployed.

CREATE TABLE bug_reports (
  id          INTEGER PRIMARY KEY,
  app         TEXT NOT NULL,
  version     TEXT NOT NULL,
  system      TEXT NOT NULL,
  report      TEXT NOT NULL,
  note        TEXT,
  created_at  INTEGER NOT NULL
);

CREATE INDEX bug_reports_created ON bug_reports (created_at);
