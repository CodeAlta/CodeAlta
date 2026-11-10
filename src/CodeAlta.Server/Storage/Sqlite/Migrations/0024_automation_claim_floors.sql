-- The runs that took a due time or an event arbitrate the claims of the machines of an automation (one run per
-- occurrence). Once such runs are forgotten, the latest time they had started is kept per automation: a due time at or
-- before it is not claimed again, since whether a run took it can no longer be told.

CREATE TABLE automation_claim_floors (
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    automation_id TEXT NOT NULL,
    forgotten_before INTEGER NOT NULL,
    PRIMARY KEY (organization_id, automation_id)
);
