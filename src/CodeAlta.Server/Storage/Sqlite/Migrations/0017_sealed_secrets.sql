-- The secrets the server uses itself, sealed (doc/fleet/security-and-privacy.md): the client secret of the company's
-- sign-in and the headers the server sends to the telemetry collector. The columns of before keep nothing: the store
-- seals what an older version left in them at its start, and clears them.

ALTER TABLE sign_in_settings ADD COLUMN company_client_secret_sealed TEXT NULL;

ALTER TABLE telemetry_settings ADD COLUMN server_headers_sealed TEXT NULL;
