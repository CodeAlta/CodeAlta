-- The hooks of the server (doc/fleet/orchestration.md, "Triggers"): an address a tool that only pushes (incident.io, a
-- webhook) sends its events to, for one machine of one person. The server holds no secret of the sender: it keeps the
-- raw request until the machine took it, then only the facts of the delivery. Self-contained: it names no table another
-- migration of the same release adds.

CREATE TABLE hooks (
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    hook_id TEXT NOT NULL PRIMARY KEY,
    owner_person_id TEXT NOT NULL,
    machine_id TEXT NOT NULL,
    source TEXT NOT NULL,
    label TEXT NOT NULL,
    created_by_device_id TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    last_received_at INTEGER NULL
);

CREATE INDEX hooks_owner ON hooks (organization_id, owner_person_id);

-- One row per event received: its body and headers while it waits for its machine, and the machine's answer. The key of
-- the event (the id its sender gives it, or the hash of its body) makes a repeated push one delivery.
CREATE TABLE hook_events (
    organization_id TEXT NOT NULL,
    delivery_id TEXT NOT NULL PRIMARY KEY,
    hook_id TEXT NOT NULL,
    machine_id TEXT NOT NULL,
    event_key TEXT NOT NULL,
    received_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    state TEXT NOT NULL,
    message TEXT NULL,
    answered_at INTEGER NULL,
    headers TEXT NULL,
    body BLOB NULL
);

CREATE UNIQUE INDEX hook_events_key ON hook_events (hook_id, event_key);
CREATE INDEX hook_events_pending ON hook_events (machine_id, received_at) WHERE state = 'pending';
CREATE INDEX hook_events_recent ON hook_events (hook_id, received_at DESC);
