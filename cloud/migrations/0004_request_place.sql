-- Pairnets Cloud, migration 0004: where an app's sign-in request came from, shown on the Allow page and in the notice email.
-- Additive: two nullable columns on device_logins. Only the country (two letters) and the city name, never an address.
-- Apply this migration before the Worker that uses it is deployed.

ALTER TABLE device_logins ADD COLUMN request_country TEXT;
ALTER TABLE device_logins ADD COLUMN request_city TEXT;
