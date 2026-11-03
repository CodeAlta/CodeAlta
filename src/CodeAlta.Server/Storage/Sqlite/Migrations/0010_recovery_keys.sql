-- The recovery keys of people (doc/fleet/security-and-privacy.md, keys): a recovery key is a member of the device list
-- (a device of kind 'recovery', without a machine), and the server keeps the anchor of the list sealed with it, which only
-- the recovery key opens, so that a device that recovers pins the list the person's devices signed.

CREATE TABLE recovery_keys (
    device_id TEXT NOT NULL PRIMARY KEY REFERENCES devices (id),
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    person_id TEXT NOT NULL REFERENCES people (id),
    sealed_anchor TEXT NOT NULL,
    created_at INTEGER NOT NULL
);
