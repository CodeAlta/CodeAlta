-- The machine's signature that the person of a session started it there (doc/fleet/access-control.md, access and keys): the
-- server relays it with the keys of a grant of the session's content, and the devices that open them verify it.
ALTER TABLE sessions ADD COLUMN person_signature TEXT NULL;
