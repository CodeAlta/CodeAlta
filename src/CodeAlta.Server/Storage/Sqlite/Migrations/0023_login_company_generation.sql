-- The generation of the company's sign-in settings that judged the last sign-in of a company account (doc/fleet/identity.md,
-- company SSO): the account counts as a way in only while those settings are still the ones that decide who signs in.
ALTER TABLE logins ADD COLUMN company_generation INTEGER NULL;
