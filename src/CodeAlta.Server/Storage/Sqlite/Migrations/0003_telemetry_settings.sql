-- The telemetry setting of the organization (doc/fleet/observability.md): the exact text a controller device signed,
-- with its signature, which the server pushes to the machines as it is; and the headers of the collector that only the
-- server sends (the server route), which never leave it but toward the collector.

CREATE TABLE telemetry_settings (
    organization_id TEXT NOT NULL PRIMARY KEY REFERENCES organizations (id),
    seq INTEGER NOT NULL,
    payload TEXT NOT NULL,
    signature TEXT NOT NULL,
    signer_device_id TEXT NOT NULL,
    server_headers TEXT NULL,
    updated_at INTEGER NOT NULL
);
