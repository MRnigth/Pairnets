-- Pairnets Cloud, migration 0003: a username the owner chooses on the account page (CONTRACT.md section 6.7).
-- Additive: one nullable column. NULL means not set; the account then shows the part of its email before the @.
-- It is a display name only (never used to sign in), so it is not unique.

ALTER TABLE accounts ADD COLUMN username TEXT;
