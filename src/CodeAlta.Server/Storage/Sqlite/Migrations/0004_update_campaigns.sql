-- The coordinated update of the fleet (doc/fleet/deployment.md, updates): the campaigns an owner's controller device
-- signs (the manifest as signed, which the server passes to the nodes as it is), or that the automatic policy starts
-- (the policy as signed, and the version the server found); the state of each machine in a campaign; and the update
-- policy of the organization, with its last daily check of the feed.

CREATE TABLE update_campaigns (
    id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    seq INTEGER NOT NULL,
    automatic INTEGER NOT NULL,
    target_version TEXT NOT NULL,
    feed TEXT NOT NULL,
    rollback INTEGER NOT NULL,
    not_before INTEGER NULL,
    time_zone TEXT NULL,
    payload TEXT NOT NULL,
    signature TEXT NOT NULL,
    created_by_device_id TEXT NULL,
    state TEXT NOT NULL,
    server_state TEXT NOT NULL,
    server_message TEXT NULL,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

-- A manifest's sequence is used once per organization; automatic campaigns carry the sequence of their policy.
CREATE UNIQUE INDEX update_campaigns_seq ON update_campaigns (organization_id, seq) WHERE automatic = 0;
CREATE INDEX update_campaigns_org ON update_campaigns (organization_id, created_at);

CREATE TABLE update_campaign_machines (
    campaign_id TEXT NOT NULL REFERENCES update_campaigns (id),
    device_id TEXT NOT NULL,
    state TEXT NOT NULL,
    running TEXT NULL,
    message TEXT NULL,
    instructed_at INTEGER NULL,
    updated_at INTEGER NOT NULL,
    PRIMARY KEY (campaign_id, device_id)
);

CREATE INDEX update_campaign_machines_device ON update_campaign_machines (device_id);

CREATE TABLE update_policies (
    organization_id TEXT NOT NULL PRIMARY KEY REFERENCES organizations (id),
    seq INTEGER NOT NULL,
    payload TEXT NOT NULL,
    signature TEXT NOT NULL,
    signer_device_id TEXT NOT NULL,
    last_check_at INTEGER NULL,
    last_check_message TEXT NULL,
    updated_at INTEGER NOT NULL
);
