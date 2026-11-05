using Microsoft.Data.Sqlite;

namespace CodeAlta.Server.Storage.Sqlite;

// Sign-in: its settings, the accounts linked to people and their passkeys (the migration "sign_in").
public sealed partial class SqliteCoordinatorStore : ISignInStore
{
    private const string LoginColumns = "SELECT organization_id, person_id, method, issuer, subject, email, name, groups, created_at, last_sign_in_at, company_generation FROM logins";
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
    public async ValueTask<bool> SetSignInSettingsAsync(SignInSettingsRecord settings, AuditEntry audit, CancellationToken cancellationToken = default)
        => await SetSignInSettingsAsync(settings, null, audit, cancellationToken).ConfigureAwait(false) == SignInSettingsWriteOutcome.Set;

    /// <inheritdoc />
    public ValueTask<SignInSettingsWriteOutcome> SetSignInSettingsAsync(SignInSettingsRecord settings, string? breakGlassPersonId, AuditEntry audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(audit);
        var secret = Seal(settings.CompanyClientSecret, ClientSecretPurpose, settings.OrganizationId);
        return WriteAsync((connection, transaction) =>
        {
            // The settings change only from the version they name: two owners editing at once do not overwrite each other.
            var held = SettingsVersion(connection, transaction, settings.OrganizationId);
            if (settings.Version != held + 1) return SignInSettingsWriteOutcome.VersionMoved;
            // Company sign-in required leaves its break-glass owner a passkey, whatever was removed meanwhile.
            if (breakGlassPersonId is not null && CountWhere(connection, transaction, "passkeys", "person_id", breakGlassPersonId) == 0)
                return SignInSettingsWriteOutcome.BreakGlassWithoutPasskey;
            Execute(connection, transaction,
                "INSERT INTO sign_in_settings (organization_id, settings, company_client_secret_sealed, version, updated_at) VALUES ($org, $settings, $secret, $version, $at) " +
                "ON CONFLICT (organization_id) DO UPDATE SET settings = excluded.settings, company_client_secret = NULL, " +
                "company_client_secret_sealed = excluded.company_client_secret_sealed, version = excluded.version, updated_at = excluded.updated_at",
                ("$org", settings.OrganizationId), ("$settings", settings.Settings), ("$secret", secret), ("$version", settings.Version),
                ("$at", ToTime(settings.UpdatedAt)));
            AppendAudit(connection, transaction, audit);
            return SignInSettingsWriteOutcome.Set;
        }, cancellationToken);
    }

    private static long SettingsVersion(SqliteConnection connection, SqliteTransaction transaction, string organizationId)
        => QuerySingle(connection, transaction, "SELECT version FROM sign_in_settings WHERE organization_id = $org", static reader => reader.GetInt64(0), ("$org", organizationId));

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
    public ValueTask<LoginLinkOutcome> LinkLoginAsync(LoginRecord login, WayInAddition addition, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(addition);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var existing = QuerySingle(connection, transaction, LoginColumns + " WHERE organization_id = $org AND issuer = $issuer AND subject = $subject", ReadLogin,
                ("$org", login.OrganizationId), ("$issuer", login.Issuer), ("$subject", login.Subject));
            if (existing is not null)
                return existing.PersonId == login.PersonId ? LoginLinkOutcome.AlreadyLinked : LoginLinkOutcome.LinkedElsewhere;
            if (!MayAddWayIn(connection, transaction, login.PersonId, addition.DeviceId)) return LoginLinkOutcome.DeviceNotApproved;
            if (addition.FirstWayIn && WaysIn(connection, transaction, login.PersonId) > 0) return LoginLinkOutcome.NotFirst;
            InsertLogin(connection, transaction, login);
            AppendAudit(connection, transaction, audit);
            return LoginLinkOutcome.Linked;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PasskeyAddOutcome> AddPasskeyAsync(PasskeyRecord passkey, WayInAddition addition, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passkey);
        ArgumentNullException.ThrowIfNull(addition);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            if (!MayAddWayIn(connection, transaction, passkey.PersonId, addition.DeviceId)) return PasskeyAddOutcome.DeviceNotApproved;
            if (addition.FirstWayIn && WaysIn(connection, transaction, passkey.PersonId) > 0) return PasskeyAddOutcome.NotFirst;
            if (InsertPasskey(connection, transaction, passkey) == 0) return PasskeyAddOutcome.Exists;
            AppendAudit(connection, transaction, audit);
            return PasskeyAddOutcome.Added;
        }, cancellationToken);
    }

    // A way in is added by a device that is, at the moment of the write, an approved controller device of its acting person:
    // a revocation or a suspension that came after the sign-in started leaves nothing added.
    private static bool MayAddWayIn(SqliteConnection connection, SqliteTransaction transaction, string personId, string deviceId)
    {
        var device = ReadDevice(connection, transaction, deviceId);
        if (device is not { Status: DeviceStatuses.Approved, Kind: CodeAlta.Fleet.Protocol.FleetDeviceKinds.Controller } || device.OwnerPersonId != personId) return false;
        var person = QuerySingle(connection, transaction, PersonColumns + " FROM people WHERE id = $id", ReadPerson, ("$id", personId));
        return person is { IsActive: true };
    }

    /// <inheritdoc />
    public ValueTask<LoginRemovalOutcome> RemoveLoginAsync(LoginRecord login, WayInRemoval rule, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var kept = Kept(connection, transaction, login.OrganizationId, login.PersonId, rule, login, null);
            if (kept != LoginRemovalOutcome.Removed) return kept;
            var removed = Execute(connection, transaction, "DELETE FROM logins WHERE organization_id = $org AND issuer = $issuer AND subject = $subject AND person_id = $person",
                ("$org", login.OrganizationId), ("$issuer", login.Issuer), ("$subject", login.Subject), ("$person", login.PersonId));
            if (removed == 0) return LoginRemovalOutcome.NotFound;
            AppendAudit(connection, transaction, audit);
            return LoginRemovalOutcome.Removed;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<LoginRemovalOutcome> RemovePasskeyAsync(PasskeyRecord passkey, WayInRemoval rule, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passkey);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            var kept = Kept(connection, transaction, passkey.OrganizationId, passkey.PersonId, rule, null, passkey);
            if (kept != LoginRemovalOutcome.Removed) return kept;
            var removed = Execute(connection, transaction, "DELETE FROM passkeys WHERE credential_id = $id AND person_id = $person",
                ("$id", passkey.CredentialId), ("$person", passkey.PersonId));
            if (removed == 0) return LoginRemovalOutcome.NotFound;
            AppendAudit(connection, transaction, audit);
            return LoginRemovalOutcome.Removed;
        }, cancellationToken);
    }

    // Whether a way in may go (Removed), or what keeps it: the settings it was judged by moved on, it is the last passkey of the
    // break-glass owner, or the last way in that still signs an owner without a recovery key in. A way in counts only when
    // its method signs in under the settings (an account of Microsoft while company sign-in is required signs nobody in).
    private static LoginRemovalOutcome Kept(SqliteConnection connection, SqliteTransaction transaction, string organizationId, string personId, WayInRemoval rule,
        LoginRecord? login, PasskeyRecord? passkey)
    {
        if (SettingsVersion(connection, transaction, organizationId) != rule.SettingsVersion) return LoginRemovalOutcome.SettingsChanged;
        var otherPasskeys = QueryList(connection, transaction, PasskeyColumns + " WHERE person_id = $person", ReadPasskey, ("$person", personId))
            .Count(other => other.CredentialId != passkey?.CredentialId);
        if (passkey is not null && rule.KeepBreakGlassPasskey && otherPasskeys == 0) return LoginRemovalOutcome.BreakGlassPasskey;
        if (!rule.KeepOwnersLastWayIn) return LoginRemovalOutcome.Removed;
        var usable = QueryList(connection, transaction, LoginColumns + " WHERE person_id = $person", ReadLogin, ("$person", personId))
            .Count(other => rule.UsableMethods.Contains(other.Method) && !(login is not null && other.Issuer == login.Issuer && other.Subject == login.Subject)
                && (other.Method != CodeAlta.Fleet.Protocol.Api.FleetSignInMethods.Company || rule.CompanyGeneration is not { } generation || other.CompanyGeneration == generation))
            + (rule.UsableMethods.Contains(CodeAlta.Fleet.Protocol.Api.FleetSignInMethods.Passkey) ? otherPasskeys : 0);
        if (usable > 0) return LoginRemovalOutcome.Removed;
        var recoveryKeys = QuerySingle(connection, transaction,
            "SELECT COUNT(*) FROM devices WHERE owner_person_id = $person AND kind = $kind AND status = $status", static reader => reader.GetInt64(0),
            ("$person", personId), ("$kind", CodeAlta.Fleet.Protocol.FleetDeviceKinds.Recovery), ("$status", DeviceStatuses.Approved));
        return recoveryKeys > 0 ? LoginRemovalOutcome.Removed : LoginRemovalOutcome.LastWayIn;
    }

    // The accounts and passkeys of a person: the ways they sign in, beside their devices and their recovery key.
    private static long WaysIn(SqliteConnection connection, SqliteTransaction transaction, string personId)
        => CountWhere(connection, transaction, "logins", "person_id", personId) + CountWhere(connection, transaction, "passkeys", "person_id", personId);

    /// <inheritdoc />
    public ValueTask UpdateLoginAsync(LoginRecord login, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        return new ValueTask(WriteAsync((connection, transaction) => Execute(connection, transaction,
            "UPDATE logins SET email = $email, name = $name, groups = $groups, last_sign_in_at = $at, company_generation = $generation " +
            "WHERE organization_id = $org AND issuer = $issuer AND subject = $subject",
            ("$email", login.Email), ("$name", login.Name), ("$groups", login.Groups), ("$at", ToNullableTime(login.LastSignInAt)), ("$generation", login.CompanyGeneration),
            ("$org", login.OrganizationId),
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
            if (InsertPasskey(connection, transaction, passkey) == 0) return false;
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    private static int InsertPasskey(SqliteConnection connection, SqliteTransaction transaction, PasskeyRecord passkey)
        => Execute(connection, transaction,
            "INSERT INTO passkeys (credential_id, organization_id, person_id, name, data, created_at, last_used_at) VALUES ($id, $org, $person, $name, $data, $created, $used) " +
            "ON CONFLICT (credential_id) DO NOTHING",
            ("$id", passkey.CredentialId), ("$org", passkey.OrganizationId), ("$person", passkey.PersonId), ("$name", passkey.Name), ("$data", passkey.Data),
            ("$created", ToTime(passkey.CreatedAt)), ("$used", ToNullableTime(passkey.LastUsedAt)));

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
            "INSERT INTO logins (organization_id, person_id, method, issuer, subject, email, name, groups, created_at, last_sign_in_at, company_generation) " +
            "VALUES ($org, $person, $method, $issuer, $subject, $email, $name, $groups, $created, $at, $generation)",
            ("$org", login.OrganizationId), ("$person", login.PersonId), ("$method", login.Method), ("$issuer", login.Issuer), ("$subject", login.Subject),
            ("$email", login.Email), ("$name", login.Name), ("$groups", login.Groups), ("$created", ToTime(login.CreatedAt)), ("$at", ToNullableTime(login.LastSignInAt)),
            ("$generation", login.CompanyGeneration));

    private static LoginRecord ReadLogin(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), GetNullableString(reader, 5), reader.GetString(6),
            GetNullableString(reader, 7), FromTime(reader.GetInt64(8)), GetNullableTime(reader, 9), reader.IsDBNull(10) ? null : reader.GetInt64(10));

    private static PasskeyRecord ReadPasskey(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), FromTime(reader.GetInt64(5)), GetNullableTime(reader, 6));
}
