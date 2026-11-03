-- What the session of a run used, as its machine reported it: its tokens, and the cost its provider reported in one unit.
-- Facts, never content.

ALTER TABLE automation_runs ADD COLUMN input_tokens INTEGER NULL;
ALTER TABLE automation_runs ADD COLUMN output_tokens INTEGER NULL;
ALTER TABLE automation_runs ADD COLUMN cost REAL NULL;
ALTER TABLE automation_runs ADD COLUMN cost_unit TEXT NULL;

-- When a schedule of an automation is next due on its machine, as the machine reports it.
ALTER TABLE automations ADD COLUMN next_due INTEGER NULL;
