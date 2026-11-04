-- Access control, its second part (doc/fleet/access-control.md): the person who started a session on a team machine, as the
-- machine says, decides who sees its facts (its maintainers, and that person among its members).

ALTER TABLE sessions ADD COLUMN person_id TEXT NULL;
