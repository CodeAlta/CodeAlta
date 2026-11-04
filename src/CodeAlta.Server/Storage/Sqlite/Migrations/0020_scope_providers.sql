-- The providers of the teams and of the organization (doc/fleet/orchestration.md, credentials of unattended runs): defined once
-- by who answers for the scope, with the key of an account sealed for the machines of that scope; the server stores the
-- sealed key and one copy of its sealing key per machine, and never reads the key.

CREATE TABLE scope_providers (
    id TEXT PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    owner_kind TEXT NOT NULL,
    owner_id TEXT NOT NULL,
    name TEXT NOT NULL,
    type TEXT NOT NULL,
    endpoint TEXT NULL,
    models TEXT NOT NULL,
    version INTEGER NOT NULL,
    sealed TEXT NOT NULL,
    updated_by_person_id TEXT NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE TABLE scope_provider_keys (
    provider_id TEXT NOT NULL REFERENCES scope_providers (id),
    device_id TEXT NOT NULL,
    wrapped TEXT NOT NULL,
    PRIMARY KEY (provider_id, device_id)
) WITHOUT ROWID;

CREATE INDEX scope_provider_keys_device ON scope_provider_keys (device_id);
