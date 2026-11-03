-- The statistics facts machines publish (doc/fleet/statistics.md): per machine and session, the set of the highest version
-- and its rows, one per measure, so that a query sums any measure by any dimension. A machine is the single writer of
-- its sets; a new generation of its publications replaces every set it published before.

CREATE TABLE statistics_publishers (
    device_id TEXT NOT NULL PRIMARY KEY REFERENCES devices (id),
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    generation TEXT NOT NULL,
    published_at INTEGER NOT NULL
);

CREATE TABLE statistics_sets (
    device_id TEXT NOT NULL REFERENCES devices (id),
    session_id TEXT NOT NULL,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    epoch INTEGER NOT NULL,
    version INTEGER NOT NULL,
    -- A blind index of the person's key: the server groups by it and cannot name it.
    project_key TEXT NULL,
    received_at INTEGER NOT NULL,
    PRIMARY KEY (device_id, session_id)
) WITHOUT ROWID;

-- The keys of a row besides the provider and the model are joined with U+001F; '' stands for an unknown provider or model.
CREATE TABLE statistics_rows (
    device_id TEXT NOT NULL,
    session_id TEXT NOT NULL,
    family TEXT NOT NULL,
    q INTEGER NOT NULL,
    provider TEXT NOT NULL,
    model TEXT NOT NULL,
    keys TEXT NOT NULL,
    measure TEXT NOT NULL,
    value INTEGER NOT NULL,
    PRIMARY KEY (device_id, session_id, family, q, provider, model, keys, measure)
) WITHOUT ROWID;

CREATE INDEX statistics_rows_q ON statistics_rows (q);
