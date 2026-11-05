-- Budgets and alerts, health notifications (doc/fleet/observability.md). A budget is a document its owner's controller
-- device signed; its allowances are the server's bookkeeping per period and machine, within its limit; a hold is a run
-- that waits for a reached budget, released by a Continue anyway a controller device of the owner signed. Notifications
-- are made once per problem: their key is unique per person. The health of machines keeps each problem a machine reports
-- while it lasts, so that a lasting one is told once.

-- The space of the session of a statistics set, as the server knows the space (an opaque id): budgets of a space count by it.
ALTER TABLE statistics_sets ADD COLUMN space_key TEXT NULL;

CREATE TABLE budgets (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    owner_person_id TEXT NOT NULL,
    number INTEGER NOT NULL,
    removed INTEGER NOT NULL,
    payload TEXT NOT NULL,
    signature TEXT NOT NULL,
    signer_device_id TEXT NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE INDEX budgets_owner ON budgets (organization_id, owner_person_id);

CREATE TABLE budget_allowances (
    budget_id TEXT NOT NULL REFERENCES budgets (id),
    period_start INTEGER NOT NULL,
    device_id TEXT NOT NULL,
    granted INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    PRIMARY KEY (budget_id, period_start, device_id)
) WITHOUT ROWID;

CREATE TABLE budget_holds (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    budget_id TEXT NOT NULL REFERENCES budgets (id),
    number INTEGER NOT NULL,
    period_start INTEGER NOT NULL,
    machine_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    nonce TEXT NOT NULL,
    title TEXT NULL,
    status TEXT NOT NULL,
    requested_at INTEGER NOT NULL,
    continue_payload TEXT NULL,
    continue_signature TEXT NULL,
    decided_by_device_id TEXT NULL,
    decided_at INTEGER NULL,
    UNIQUE (budget_id, machine_id, run_id)
);

CREATE INDEX budget_holds_budget ON budget_holds (budget_id, requested_at);

CREATE TABLE notifications (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    person_id TEXT NOT NULL,
    dedupe_key TEXT NOT NULL,
    kind TEXT NOT NULL,
    severity TEXT NOT NULL,
    budget_id TEXT NULL,
    machine_id TEXT NULL,
    problem TEXT NULL,
    subject TEXT NULL,
    detail TEXT NULL,
    created_at INTEGER NOT NULL,
    resolved_at INTEGER NULL,
    read_at INTEGER NULL,
    UNIQUE (organization_id, person_id, dedupe_key)
);

CREATE INDEX notifications_person ON notifications (organization_id, person_id, created_at);

CREATE TABLE machine_health (
    device_id TEXT NOT NULL,
    kind TEXT NOT NULL,
    subject TEXT NOT NULL,
    detail TEXT NULL,
    first_seen INTEGER NOT NULL,
    last_seen INTEGER NOT NULL,
    notification_id TEXT NULL,
    PRIMARY KEY (device_id, kind, subject)
) WITHOUT ROWID;
