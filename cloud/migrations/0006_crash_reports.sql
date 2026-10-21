-- Pairnets Cloud, migration 0006: crash reports the apps send when the user presses "Send crash report" (src/routes/crash.ts).
-- One row per kind of crash and app version; the same crash sent again adds to `count`. Nothing here says who sent it:
-- no IP address, account, device or nest. The hourly cron deletes rows not seen for 90 days.
-- Apply this migration before the Worker that uses it is deployed.

CREATE TABLE crash_reports (
  id          INTEGER PRIMARY KEY,
  crash_id    TEXT NOT NULL,
  app         TEXT NOT NULL,
  version     TEXT NOT NULL,
  system      TEXT NOT NULL,
  place       TEXT NOT NULL,
  error       TEXT NOT NULL,
  message     TEXT NOT NULL,
  details     TEXT NOT NULL,
  closed      INTEGER NOT NULL,
  count       INTEGER NOT NULL,
  reports     INTEGER NOT NULL,
  notes       TEXT NOT NULL,
  first_at    INTEGER NOT NULL,
  last_at     INTEGER NOT NULL,
  UNIQUE (crash_id, version)
);

CREATE INDEX crash_reports_last ON crash_reports (last_at);
