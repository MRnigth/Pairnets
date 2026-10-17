-- Pairnets Cloud, migration 0005: an account nobody used for a year is deleted, 30 days after a warning email
-- (src/inactive.ts). Additive: one nullable column on accounts, when that warning went out (NULL = not warned).
-- Apply this migration before the Worker that uses it is deployed.

ALTER TABLE accounts ADD COLUMN inactive_warned_at INTEGER;
