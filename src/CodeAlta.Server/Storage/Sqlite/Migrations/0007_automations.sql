-- The automations a person gives the machines of the organization (doc/fleet/orchestration.md): per automation and
-- machine, the last version a controller device of that person signed, as signed (its definition sealed for the machine
-- and the person's controller devices: the server reads only the facts beside it), and where its delivery stands.
-- A removal is a version too: it stays, so that the machine refuses older versions replayed later.

CREATE TABLE automations (
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    automation_id TEXT NOT NULL,
    machine_id TEXT NOT NULL,
    owner_person_id TEXT NOT NULL,
    number INTEGER NOT NULL,
    removed INTEGER NOT NULL,
    enabled INTEGER NOT NULL,
    triggers TEXT NOT NULL,
    payload TEXT NOT NULL,
    signature TEXT NOT NULL,
    signer_device_id TEXT NOT NULL,
    updated_at INTEGER NOT NULL,
    delivery_state TEXT NOT NULL,
    delivery_message TEXT NULL,
    PRIMARY KEY (organization_id, automation_id, machine_id)
);

CREATE INDEX automations_owner ON automations (owner_person_id);
CREATE INDEX automations_pending ON automations (machine_id) WHERE delivery_state = 'pending';

-- The runs the machines report, and the due times they claimed: one run per (automation, due time), whatever the
-- machine, so that an automation given to another machine does not run again what the first one took.
CREATE TABLE automation_runs (
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    run_id TEXT NOT NULL,
    automation_id TEXT NOT NULL,
    machine_id TEXT NOT NULL,
    number INTEGER NOT NULL,
    trigger TEXT NOT NULL,
    status TEXT NOT NULL,
    occurrence TEXT NULL,
    started_at INTEGER NOT NULL,
    ended_at INTEGER NULL,
    lease_until INTEGER NULL,
    session_id TEXT NULL,
    message TEXT NULL,
    updated_at INTEGER NOT NULL,
    PRIMARY KEY (organization_id, run_id)
);

CREATE UNIQUE INDEX automation_runs_occurrence ON automation_runs (organization_id, automation_id, occurrence) WHERE occurrence IS NOT NULL;
CREATE INDEX automation_runs_recent ON automation_runs (organization_id, automation_id, started_at DESC);
