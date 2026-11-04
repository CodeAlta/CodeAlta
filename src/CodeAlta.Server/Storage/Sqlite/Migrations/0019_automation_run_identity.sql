-- The service identity a run of an automation of a team or of the organization acted as (doc/fleet/orchestration.md, who a run
-- acts as), as its machine reported it and the server checked it against the identities the owner turned on.

ALTER TABLE automation_runs ADD COLUMN acting_as TEXT NULL;
