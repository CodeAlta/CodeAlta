-- The metadata keys of spaces (doc/fleet/security-and-privacy.md, keys): one key per space of a person, with its
-- epochs, instead of one per person. The space is an opaque id the devices make; which space it is on the devices is
-- sealed with the key itself (sealed_descriptor). The keys of a person made before spaces keep an empty space: the
-- devices still open what was sealed with them, and seal nothing new with them.

ALTER TABLE metadata_keys ADD COLUMN space_id TEXT NOT NULL DEFAULT '';

ALTER TABLE metadata_keys ADD COLUMN sealed_descriptor TEXT NULL;

DROP INDEX metadata_keys_person_epoch;

CREATE UNIQUE INDEX metadata_keys_space_epoch ON metadata_keys (person_id, space_id, epoch);
