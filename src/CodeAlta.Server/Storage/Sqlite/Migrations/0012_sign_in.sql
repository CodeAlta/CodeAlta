-- Sign-in (doc/fleet/identity.md): the settings of the organization, the accounts linked to people (keyed on their
-- issuer and subject) and their passkeys. A sign-in grants nothing by itself: what makes a device a member is still an
-- entry of the device list signed by a device.

CREATE TABLE sign_in_settings (
    organization_id TEXT NOT NULL PRIMARY KEY REFERENCES organizations (id),
    settings TEXT NOT NULL,
    company_client_secret TEXT NULL,
    version INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE TABLE logins (
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    person_id TEXT NOT NULL REFERENCES people (id),
    method TEXT NOT NULL,
    issuer TEXT NOT NULL,
    subject TEXT NOT NULL,
    email TEXT NULL,
    name TEXT NOT NULL,
    groups TEXT NULL,
    created_at INTEGER NOT NULL,
    last_sign_in_at INTEGER NULL,
    PRIMARY KEY (organization_id, issuer, subject)
);

CREATE INDEX logins_person ON logins (person_id);

CREATE TABLE passkeys (
    credential_id TEXT NOT NULL PRIMARY KEY,
    organization_id TEXT NOT NULL REFERENCES organizations (id),
    person_id TEXT NOT NULL REFERENCES people (id),
    name TEXT NOT NULL,
    data TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    last_used_at INTEGER NULL
);

CREATE INDEX passkeys_person ON passkeys (person_id);
