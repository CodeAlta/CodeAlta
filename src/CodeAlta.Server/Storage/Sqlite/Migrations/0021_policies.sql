-- The signed policy of the organization and its kill switch (doc/fleet/security-and-privacy.md, policies and the kill switch):
-- versions chained by hash, each signed by a controller device of a signer; the server keeps them as they were signed and
-- relays them, machines verify them themselves.

CREATE TABLE policy_documents (
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    number INTEGER NOT NULL,
    payload TEXT NOT NULL,
    signature TEXT NOT NULL,
    signer_device_id TEXT NOT NULL,
    hash TEXT NOT NULL,
    recorded_at INTEGER NOT NULL,
    PRIMARY KEY (organization_id, number)
);
