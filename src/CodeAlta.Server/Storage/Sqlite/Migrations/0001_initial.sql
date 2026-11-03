-- The first tables of CodeAlta Server (doc/fleet/server.md, data). Portable by construction: text ids, UTC times as
-- 64-bit integers (Unix milliseconds), JSON as text, no function of one engine, so that PostgreSQL gets the same
-- schema in its own migrations.

CREATE TABLE organizations (
    id TEXT NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    created_at INTEGER NOT NULL
);

CREATE TABLE people (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    display_name TEXT NOT NULL,
    role TEXT NOT NULL,
    created_at INTEGER NOT NULL
);

CREATE INDEX people_organization ON people (organization_id);

CREATE TABLE devices (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    name TEXT NOT NULL,
    kind TEXT NOT NULL,
    public_key TEXT NOT NULL,
    fingerprint TEXT NOT NULL UNIQUE,
    owner_person_id TEXT NULL REFERENCES people (id),
    status TEXT NOT NULL,
    enrolled_by_device_id TEXT NULL,
    enrollment_token_id TEXT NULL,
    created_at INTEGER NOT NULL,
    approved_at INTEGER NULL,
    revoked_at INTEGER NULL,
    version INTEGER NOT NULL
);

CREATE INDEX devices_organization ON devices (organization_id);

CREATE TABLE device_list (
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    seq INTEGER NOT NULL,
    device_id TEXT NOT NULL,
    operation TEXT NOT NULL,
    signer_device_id TEXT NOT NULL,
    payload TEXT NOT NULL,
    signature TEXT NOT NULL,
    hash TEXT NOT NULL,
    recorded_at INTEGER NOT NULL,
    PRIMARY KEY (organization_id, seq)
);

CREATE TABLE enrollment_tokens (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    token_hash TEXT NOT NULL UNIQUE,
    label TEXT NULL,
    owner_person_id TEXT NULL,
    created_by_device_id TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    used_at INTEGER NULL,
    used_by_device_id TEXT NULL
);

CREATE TABLE machines (
    device_id TEXT NOT NULL PRIMARY KEY REFERENCES devices (id),
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    inventory TEXT NOT NULL,
    inventory_seq INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

-- The facts a server may read (doc/fleet/security-and-privacy.md, what the server sees), and the sealed copy of the
-- descriptive metadata it keeps without reading. No title, project, branch or path column, on purpose.
CREATE TABLE sessions (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    machine_id TEXT NOT NULL REFERENCES devices (id),
    epoch INTEGER NOT NULL,
    version INTEGER NOT NULL,
    state TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    provider_type TEXT NULL,
    model TEXT NULL,
    turns INTEGER NOT NULL,
    tool_calls INTEGER NOT NULL,
    input_tokens INTEGER NOT NULL,
    output_tokens INTEGER NOT NULL,
    sealed_metadata TEXT NULL,
    received_at INTEGER NOT NULL
);

CREATE INDEX sessions_machine ON sessions (machine_id);

-- The last acknowledged sequence of each publication stream of a device (facts, inventory).
CREATE TABLE publication_cursors (
    device_id TEXT NOT NULL,
    stream TEXT NOT NULL,
    seq INTEGER NOT NULL,
    PRIMARY KEY (device_id, stream)
);

-- Append-only: the store never updates or deletes a row; each row names the hash of the one before it.
CREATE TABLE audit (
    seq INTEGER NOT NULL PRIMARY KEY,
    recorded_at INTEGER NOT NULL,
    actor_person_id TEXT NULL,
    actor_device_id TEXT NULL,
    action TEXT NOT NULL,
    target TEXT NULL,
    result TEXT NOT NULL,
    source_address TEXT NULL,
    request_id TEXT NULL,
    details TEXT NULL,
    previous_hash TEXT NOT NULL,
    hash TEXT NOT NULL
);
