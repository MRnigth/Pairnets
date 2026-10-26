-- Pairnets Cloud, migration 0011: signing an app in with a link (CONTRACT.md section 6.9, "Sign in with a link"). The
-- app shows a long one-time link; it is opened in any browser, the person signs in and presses Allow, and the page
-- shows a long code. Only that code, pasted into the app, finishes the sign-in: in this mode the app's poll never hands
-- over a key. Additive: five columns on device_logins. Only hashes of the link and the code are kept.
-- Apply this migration before the Worker that uses it is deployed.

-- 'poll': today's sign-in (the app polls and gets its key once allowed). 'code': a link sign-in, finished by the code.
ALTER TABLE device_logins ADD COLUMN mode TEXT NOT NULL DEFAULT 'poll' CHECK (mode IN ('poll', 'code'));
-- SHA-256 of the link's secret (pnr_ + base64url(32 bytes), the link's ?r=); NULL for 'poll'.
ALTER TABLE device_logins ADD COLUMN link_hash BLOB;
-- SHA-256 of the long code, made when the person presses Allow, and when it stops working (Allow + 5 minutes).
ALTER TABLE device_logins ADD COLUMN code_hash BLOB;
ALTER TABLE device_logins ADD COLUMN code_expires_at INTEGER;
-- Wrong codes pasted so far; the fifth stops the sign-in (status 'denied').
ALTER TABLE device_logins ADD COLUMN tries INTEGER NOT NULL DEFAULT 0;
CREATE UNIQUE INDEX device_logins_link ON device_logins(link_hash) WHERE link_hash IS NOT NULL;
