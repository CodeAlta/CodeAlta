-- Access control, its second part (doc/fleet/access-control.md): the person who started a session on a team machine, as the
-- machine says, decides who sees its facts (its maintainers, and that person among its members).

ALTER TABLE sessions ADD COLUMN person_id TEXT NULL;

-- The keys of a grant of sealed session content (access and keys): the metadata of the session sealed with a key made for the
-- grant, and that key wrapped to each device of the grantee; the server stores them without opening them, and drops them when
-- the grant is taken back.
CREATE TABLE grant_keys (
    grant_id TEXT NOT NULL REFERENCES grants (id),
    device_id TEXT NOT NULL,
    sealed TEXT NOT NULL,
    wrapped TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    PRIMARY KEY (grant_id, device_id)
) WITHOUT ROWID;

CREATE INDEX grant_keys_device ON grant_keys (device_id);
