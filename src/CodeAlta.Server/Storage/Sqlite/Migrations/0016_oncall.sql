-- The on-call rotations of teams (doc/fleet/orchestration.md, "Autonomy and approvals"): who of a team answers the approvals of
-- the automations that ask the person on call, in turns of a few hours from a start. Self-contained: it names no table
-- another migration of the same release adds.

CREATE TABLE oncall_rotations (
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    team_id TEXT NOT NULL,
    people TEXT NOT NULL,
    starts_at INTEGER NOT NULL,
    shift_hours INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    updated_by_person_id TEXT NOT NULL,
    PRIMARY KEY (organization_id, team_id)
);
