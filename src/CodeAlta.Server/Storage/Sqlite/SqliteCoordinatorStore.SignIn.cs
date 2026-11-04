using Microsoft.Data.Sqlite;

namespace CodeAlta.Server.Storage.Sqlite;

// Sign-in: its settings, the accounts linked to people and their passkeys (the migration "sign_in").
public sealed partial class SqliteCoordinatorStore : ISignInStore
{
    private const string LoginColumns = "SELECT organization_id, person_id, method, issuer, subject, email, name, groups, created_at, last_sign_in_at FROM logins";
    private const string PasskeyColumns = "SELECT credential_id, organization_id, person_id, name, data, created_at, last_used_at FROM passkeys";

    /// <inheritdoc />
    public ValueTask<SignInSettingsRecord?> GetSignInSettingsAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QuerySingle(connection, null,
            "SELECT organization_id, settings, company_client_secret_sealed, version, updated_at FROM sign_in_settings WHERE organization_id = $org",
            reader => new SignInSettingsRecord(reader.GetString(0), reader.GetString(1), Unseal(GetNullableString(reader, 2), ClientSecretPurpose, reader.GetString(0)),
                reader.GetInt64(3), FromTime(reader.GetInt64(4))),
            ("$org", organizationId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> SetSignInSettingsAsync(SignInSettingsRecord settings, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(audit);
        var secret = Seal(settings.CompanyClientSecret, ClientSecretPurpose, settings.OrganizationId);
        return WriteAsync((connection, transaction) =>
        {
            // The settings change only from the version they name: two owners editing at once do not overwrite each other.
            var held = QuerySingle(connection, transaction, "SELECT version FROM sign_in_settings WHERE organization_id = $org", static reader => reader.GetInt64(0),
                ("$org", settings.OrganizationId));
            if (settings.Version != held + 1) return false;
            Execute(connection, transaction,
                "INSERT INTO sign_in_settings (organization_id, settings, company_client_secret_sealed, version, updated_at) VALUES ($org, $settings, $secret, $version, $at) " +
                "ON CONFLICT (organization_id) DO UPDATE SET settings = excluded.settings, company_client_secret = NULL, " +
                "company_client_secret_sealed = excluded.company_client_secret_sealed, version = excluded.version, updated_at = excluded.updated_at",
                ("$org", settings.OrganizationId), ("$settings", settings.Settings), ("$secret", secret), ("$version", settings.Version),
                ("$at", ToTime(settings.UpdatedAt)));
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<LoginRecord?> FindLoginAsync(string organizationId, string issuer, string subject, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(issuer);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        return ReadAsync(connection => QuerySingle(connection, null, LoginColumns + " WHERE organization_id = $org AND issuer = $issuer AND subject = $subject", ReadLogin,
            ("$org", organizationId), ("$issuer", issuer), ("$subject", subject)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<LoginRecord>> ListLoginsAsync(string personId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(personId);
        return ReadAsync(connection => QueryList(connection, null, LoginColumns + " WHERE person_id = $person ORDER BY created_at, issuer", ReadLogin, ("$person", personId)),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<LoginLinkOutcome> LinkLoginAsync(LoginRecord login, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var existing = QuerySingle(connection, transaction, LoginColumns + " WHERE organization_id = $org AND issuer = $issuer AND subject = $subject", ReadLogin,
                ("$org", login.OrganizationId), ("$issuer", login.Issuer), ("$subject", login.Subject));
            if (existing is not null)
                return existing.PersonId == login.PersonId ? LoginLinkOutcome.AlreadyLinked : LoginLinkOutcome.LinkedElsewhere;
            InsertLogin(connection, transaction, login);
            AppendAudit(connection, transaction, audit);
            return LoginLinkOutcome.Linked;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask UpdateLoginAsync(LoginRecord login, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        return new ValueTask(WriteAsync((connection, transaction) => Execute(connection, transaction,
            "UPDATE logins SET email = $email, name = $name, groups = $groups, last_sign_in_at = $at WHERE organization_id = $org AND issuer = $issuer AND subject = $subject",
            ("$email", login.Email), ("$name", login.Name), ("$groups", login.Groups), ("$at", ToNullableTime(login.LastSignInAt)), ("$org", login.OrganizationId),
            ("$issuer", login.Issuer), ("$subject", login.Subject)), cancellationToken).AsTask());
    }

    /// <inheritdoc />
    public ValueTask<LoginRecord> CreatePersonWithLoginAsync(PersonRecord person, LoginRecord login, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var existing = QuerySingle(connection, transaction, LoginColumns + " WHERE organization_id = $org AND issuer = $issuer AND subject = $subject", ReadLogin,
                ("$org", login.OrganizationId), ("$issuer", login.Issuer), ("$subject", login.Subject));
            if (existing is not null) return existing;
            InsertPerson(connection, transaction, person);
            InsertLogin(connection, transaction, login);
            AppendAudit(connection, transaction, audit);
            return login;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> AddPasskeyAsync(PasskeyRecord passkey, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passkey);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var added = Execute(connection, transaction,
                "INSERT INTO passkeys (credential_id, organization_id, person_id, name, data, created_at, last_used_at) VALUES ($id, $org, $person, $name, $data, $created, $used) " +
                "ON CONFLICT (credential_id) DO NOTHING",
                ("$id", passkey.CredentialId), ("$org", passkey.OrganizationId), ("$person", passkey.PersonId), ("$name", passkey.Name), ("$data", passkey.Data),
                ("$created", ToTime(passkey.CreatedAt)), ("$used", ToNullableTime(passkey.LastUsedAt)));
            if (added == 0) return false;
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PasskeyRecord?> FindPasskeyAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(credentialId);
        return ReadAsync(connection => QuerySingle(connection, null, PasskeyColumns + " WHERE credential_id = $id", ReadPasskey, ("$id", credentialId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PasskeyRecord>> ListPasskeysAsync(string personId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(personId);
        return ReadAsync(connection => QueryList(connection, null, PasskeyColumns + " WHERE person_id = $person ORDER BY created_at", ReadPasskey, ("$person", personId)),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask UpdatePasskeyAsync(PasskeyRecord passkey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passkey);
        return new ValueTask(WriteAsync((connection, transaction) => Execute(connection, transaction,
            "UPDATE passkeys SET data = $data, last_used_at = $used WHERE credential_id = $id",
            ("$data", passkey.Data), ("$used", ToNullableTime(passkey.LastUsedAt)), ("$id", passkey.CredentialId)), cancellationToken).AsTask());
    }

    /// <inheritdoc />
    public ValueTask<bool> RegisterSignedInDeviceAsync(DeviceRecord device, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            if (ReadDevice(connection, transaction, device.Id) is not null || CountWhere(connection, transaction, "devices", "fingerprint", device.Fingerprint) > 0) return false;
            InsertDevice(connection, transaction, device);
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    private static void InsertLogin(SqliteConnection connection, SqliteTransaction transaction, LoginRecord login)
        => Execute(connection, transaction,
            "INSERT INTO logins (organization_id, person_id, method, issuer, subject, email, name, groups, created_at, last_sign_in_at) " +
            "VALUES ($org, $person, $method, $issuer, $subject, $email, $name, $groups, $created, $at)",
            ("$org", login.OrganizationId), ("$person", login.PersonId), ("$method", login.Method), ("$issuer", login.Issuer), ("$subject", login.Subject),
            ("$email", login.Email), ("$name", login.Name), ("$groups", login.Groups), ("$created", ToTime(login.CreatedAt)), ("$at", ToNullableTime(login.LastSignInAt)));

    private static LoginRecord ReadLogin(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), GetNullableString(reader, 5), reader.GetString(6),
            GetNullableString(reader, 7), FromTime(reader.GetInt64(8)), GetNullableTime(reader, 9));

    private static PasskeyRecord ReadPasskey(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), FromTime(reader.GetInt64(5)), GetNullableTime(reader, 6));
}
