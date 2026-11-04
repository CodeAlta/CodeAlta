-- Ownership and access control (doc/fleet/access-control.md). People get a status (a suspended person's devices are
-- refused); teams have maintainers and members; service identities belong to a team or the organization; grants are
-- (subject, capability, scope). A resource whose owner is not the one the rest of its record implies (a team machine,
-- an automation given to a team, anything paused after its owner left) has a row in resource_owners, which also keeps
-- who made it. Transfers are offered and accepted. The signed access document is a chain, as the device list: the server
-- keeps every version, which nodes replay from their anchor. Proposals are changes of access an agent proposed, which a
-- person applies or dismisses.

ALTER TABLE people ADD COLUMN status TEXT NOT NULL DEFAULT 'active';
ALTER TABLE people ADD COLUMN suspended_at INTEGER NULL;

CREATE TABLE teams (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    name TEXT NOT NULL,
    runs_member_automations INTEGER NOT NULL,
    created_at INTEGER NOT NULL,
    created_by_person_id TEXT NOT NULL,
    dissolved_at INTEGER NULL,
    version INTEGER NOT NULL
);

CREATE INDEX teams_organization ON teams (organization_id);

CREATE TABLE team_members (
    team_id TEXT NOT NULL REFERENCES teams (id),
    person_id TEXT NOT NULL REFERENCES people (id),
    role TEXT NOT NULL,
    added_at INTEGER NOT NULL,
    PRIMARY KEY (team_id, person_id)
) WITHOUT ROWID;

CREATE INDEX team_members_person ON team_members (person_id);

CREATE TABLE service_identities (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    name TEXT NOT NULL,
    owner_kind TEXT NOT NULL,
    owner_id TEXT NOT NULL,
    enabled INTEGER NOT NULL,
    created_at INTEGER NOT NULL,
    created_by_person_id TEXT NOT NULL
);

CREATE TABLE grants (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    subject_kind TEXT NOT NULL,
    subject_id TEXT NOT NULL,
    capability TEXT NOT NULL,
    scope_kind TEXT NOT NULL,
    scope_id TEXT NULL,
    status TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    created_by_person_id TEXT NOT NULL,
    expires_at INTEGER NULL,
    revoked_at INTEGER NULL
);

CREATE INDEX grants_subject ON grants (organization_id, subject_kind, subject_id);

CREATE TABLE resource_owners (
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    resource_kind TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    owner_kind TEXT NOT NULL,
    owner_id TEXT NOT NULL,
    author_person_id TEXT NULL,
    author_session_id TEXT NULL,
    paused INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    PRIMARY KEY (organization_id, resource_kind, resource_id)
) WITHOUT ROWID;

CREATE INDEX resource_owners_owner ON resource_owners (organization_id, owner_kind, owner_id);

CREATE TABLE transfers (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    resource_kind TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    from_kind TEXT NOT NULL,
    from_id TEXT NOT NULL,
    to_kind TEXT NOT NULL,
    to_id TEXT NOT NULL,
    state TEXT NOT NULL,
    reason TEXT NOT NULL,
    offered_by_person_id TEXT NOT NULL,
    offered_at INTEGER NOT NULL,
    decided_by_person_id TEXT NULL,
    decided_at INTEGER NULL
);

CREATE INDEX transfers_organization ON transfers (organization_id, offered_at);
CREATE UNIQUE INDEX transfers_open ON transfers (organization_id, resource_kind, resource_id) WHERE state = 'offered';

CREATE TABLE access_documents (
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    number INTEGER NOT NULL,
    payload TEXT NOT NULL,
    signature TEXT NOT NULL,
    signer_device_id TEXT NOT NULL,
    hash TEXT NOT NULL,
    recorded_at INTEGER NOT NULL,
    PRIMARY KEY (organization_id, number)
);

CREATE TABLE access_proposals (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    person_id TEXT NOT NULL,
    session_id TEXT NULL,
    kind TEXT NOT NULL,
    summary TEXT NOT NULL,
    payload TEXT NOT NULL,
    state TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    decided_by_person_id TEXT NULL,
    decided_at INTEGER NULL
);

CREATE INDEX access_proposals_organization ON access_proposals (organization_id, created_at);
