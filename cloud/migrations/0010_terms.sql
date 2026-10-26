-- Pairnets Cloud, migration 0010: which Terms of service an account agreed to, and when (src/accounts.ts TERMS_VERSION).
-- Signing in says "By continuing, you agree to the Terms", so every sign-in records the version then in force.
-- Additive: two nullable columns on accounts (NULL = signed in before this was recorded).
-- Apply this migration before the Worker that uses it is deployed.

ALTER TABLE accounts ADD COLUMN terms_version TEXT;
ALTER TABLE accounts ADD COLUMN terms_accepted_at INTEGER;
