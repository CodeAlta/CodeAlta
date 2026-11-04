-- The secrets and variables of people (doc/fleet/secrets.md): what is known of each value (name, scope, version, who set
-- it and when), and its copies sealed end to end by a controller device of its person for the devices that use it and
-- for that person's controller devices. The server never holds a value: only these opaque copies, one per device, of
-- the current version; a rotation replaces them.

CREATE TABLE fleet_secrets (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    owner_person_id TEXT NOT NULL REFERENCES people (id),
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    name_key TEXT NOT NULL,
    scope_kind TEXT NOT NULL,
    scope_id TEXT NOT NULL,
    host_only INTEGER NOT NULL,
    version INTEGER NOT NULL,
    set_by_device_id TEXT NOT NULL,
    set_by_person_id TEXT NULL,
    set_at INTEGER NOT NULL,
    created_at INTEGER NOT NULL
);

-- One value per name in a scope, whatever the case of the name, as for environment variables.
CREATE UNIQUE INDEX fleet_secrets_name ON fleet_secrets (owner_person_id, kind, scope_kind, scope_id, name_key);

CREATE TABLE fleet_secret_copies (
    secret_id TEXT NOT NULL REFERENCES fleet_secrets (id),
    device_id TEXT NOT NULL REFERENCES devices (id),
    version INTEGER NOT NULL,
    sealed TEXT NOT NULL,
    sealed_by_device_id TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    PRIMARY KEY (secret_id, device_id)
);

CREATE INDEX fleet_secret_copies_device ON fleet_secret_copies (device_id);

-- The environments of people: their protection rules as a controller device of the person signed them, and the facts
-- the server reads beside them. A removal is a version too, so that a machine refuses an older one replayed later.
CREATE TABLE fleet_environments (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    owner_person_id TEXT NOT NULL REFERENCES people (id),
    name TEXT NOT NULL,
    number INTEGER NOT NULL,
    removed INTEGER NOT NULL,
    payload TEXT NOT NULL,
    signature TEXT NOT NULL,
    signer_device_id TEXT NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE UNIQUE INDEX fleet_environments_name ON fleet_environments (owner_person_id, name);

-- The approvals runs ask before they use an environment that requires one, and the approval an approver's device signed.
CREATE TABLE fleet_approvals (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    environment_id TEXT NOT NULL REFERENCES fleet_environments (id),
    environment_number INTEGER NOT NULL,
    machine_id TEXT NOT NULL REFERENCES devices (id),
    run_id TEXT NOT NULL,
    nonce TEXT NOT NULL,
    automation_id TEXT NULL,
    title TEXT NULL,
    status TEXT NOT NULL,
    requested_at INTEGER NOT NULL,
    approval_payload TEXT NULL,
    approval_signature TEXT NULL,
    approver_device_id TEXT NULL,
    approved_at INTEGER NULL
);

CREATE UNIQUE INDEX fleet_approvals_run ON fleet_approvals (machine_id, run_id);
CREATE INDEX fleet_approvals_environment ON fleet_approvals (environment_id, requested_at DESC);

-- The uses the machines publish: which run used which value at which version, never the value.
CREATE TABLE fleet_secret_uses (
    seq INTEGER NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    secret_id TEXT NOT NULL,
    machine_id TEXT NOT NULL,
    name TEXT NOT NULL,
    version INTEGER NOT NULL,
    run_id TEXT NOT NULL,
    used_at INTEGER NOT NULL,
    received_at INTEGER NOT NULL
);

CREATE UNIQUE INDEX fleet_secret_uses_once ON fleet_secret_uses (secret_id, machine_id, run_id, version);
CREATE INDEX fleet_secret_uses_recent ON fleet_secret_uses (secret_id, used_at DESC);
