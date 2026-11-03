-- The metadata keys of people (doc/fleet/security-and-privacy.md, keys): they seal the descriptive metadata of the
-- sessions their devices publish (titles). The server keeps each key only wrapped to a device of its person, as
-- opaque text; it never holds a key in clear.

CREATE TABLE metadata_keys (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    person_id TEXT NOT NULL REFERENCES people (id),
    epoch INTEGER NOT NULL,
    created_by_device_id TEXT NOT NULL,
    created_at INTEGER NOT NULL
);

CREATE UNIQUE INDEX metadata_keys_person_epoch ON metadata_keys (person_id, epoch);

CREATE TABLE metadata_key_wraps (
    key_id TEXT NOT NULL REFERENCES metadata_keys (id),
    device_id TEXT NOT NULL REFERENCES devices (id),
    wrapped TEXT NOT NULL,
    wrapped_by_device_id TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    PRIMARY KEY (key_id, device_id)
);
