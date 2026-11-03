-- What the session of a run used, as its machine reported it: its tokens, and the cost its provider reported in one unit.
-- Facts, never content.

ALTER TABLE automation_runs ADD COLUMN input_tokens INTEGER NULL;
ALTER TABLE automation_runs ADD COLUMN output_tokens INTEGER NULL;
ALTER TABLE automation_runs ADD COLUMN cost REAL NULL;
ALTER TABLE automation_runs ADD COLUMN cost_unit TEXT NULL;
