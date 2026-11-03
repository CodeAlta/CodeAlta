-- A node asked to update before its campaign was cancelled keeps the cancellation due until it acknowledges it (it
-- reports the campaign done): the server sends it again on each connection of the node (doc/fleet/deployment.md, updates).

ALTER TABLE update_campaign_machines ADD COLUMN cancel_pending INTEGER NOT NULL DEFAULT 0;

CREATE INDEX update_campaign_machines_cancel ON update_campaign_machines (device_id) WHERE cancel_pending = 1;
