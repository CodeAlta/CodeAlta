namespace CodeAlta.Server.Storage;

/// <summary>
/// The sign-in settings of an organization as stored: the settings as JSON text, and the client secret of the company's
/// provider apart, which the server never gives back.
/// </summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Settings">The settings, as JSON text (<c>SignInSettingsDocument</c>).</param>
/// <param name="CompanyClientSecret">The client secret of the company's provider, or null.</param>
/// <param name="Version">The version, from 1.</param>
/// <param name="UpdatedAt">When they were set.</param>
public sealed record SignInSettingsRecord(string OrganizationId, string Settings, string? CompanyClientSecret, long Version, DateTimeOffset UpdatedAt);

/// <summary>A sign-in method linked to a person: an account of an identity provider, keyed on its issuer and subject.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="PersonId">The person.</param>
/// <param name="Method">The method (<c>FleetSignInMethods</c>).</param>
/// <param name="Issuer">The issuer of the account (for Microsoft, the issuer of its tenant).</param>
/// <param name="Subject">The stable id of the account at its issuer (Microsoft: tenant and object id; GitHub: the numeric id).</param>
/// <param name="Email">The email the provider gave, or null.</param>
/// <param name="Name">What the person sees of it: an email or a login.</param>
/// <param name="Groups">The groups of the last sign-in, as a JSON array, or null; kept for the mapping of groups to roles (milestone 4).</param>
/// <param name="CreatedAt">When it was linked.</param>
/// <param name="LastSignInAt">When it last signed in, or null.</param>
public sealed record LoginRecord(string OrganizationId, string PersonId, string Method, string Issuer, string Subject, string? Email, string Name, string? Groups,
    DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt);

/// <summary>A passkey of a person.</summary>
/// <param name="CredentialId">The credential id, base64url.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="PersonId">The person.</param>
/// <param name="Name">Its name for people.</param>
/// <param name="Data">What verifies it (public key, counter, flags), as JSON text.</param>
/// <param name="CreatedAt">When it was registered.</param>
/// <param name="LastUsedAt">When it last signed in, or null.</param>
public sealed record PasskeyRecord(string CredentialId, string OrganizationId, string PersonId, string Name, string Data, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>What happened to a link of a sign-in method.</summary>
public enum LoginLinkOutcome
{
    /// <summary>The method was linked to the person.</summary>
    Linked,

    /// <summary>It was linked to the person already.</summary>
    AlreadyLinked,

    /// <summary>It is linked to another person.</summary>
    LinkedElsewhere,

    /// <summary>The device that adds it is no longer an approved controller device of the person, or the person no longer acts.</summary>
    DeviceNotApproved,

    /// <summary>It was to be the first way in of the person, who has one now.</summary>
    NotFirst,
}

/// <summary>What happened to the addition of a passkey to a person.</summary>
public enum PasskeyAddOutcome
{
    /// <summary>It was added.</summary>
    Added,

    /// <summary>The credential is registered already.</summary>
    Exists,

    /// <summary>The device that adds it is no longer an approved controller device of the person, or the person no longer acts.</summary>
    DeviceNotApproved,

    /// <summary>It was to be the first way in of the person, who has one now.</summary>
    NotFirst,
}

/// <summary>
/// Who adds a way to sign in to a person, checked in the same write as the addition: the device must still be an approved
/// controller device of that person, the person must act, and a first way in must still be the first.
/// </summary>
/// <param name="DeviceId">The device that adds it.</param>
/// <param name="FirstWayIn">Whether it is the person's first way in, added without a confirmation.</param>
public sealed record WayInAddition(string DeviceId, bool FirstWayIn);

/// <summary>What happened to the removal of a way to sign in.</summary>
public enum LoginRemovalOutcome
{
    /// <summary>It was removed.</summary>
    Removed,

    /// <summary>It was not there (any more).</summary>
    NotFound,

    /// <summary>It is the last way in of the person, which the caller asked to keep.</summary>
    LastWayIn,
}

/// <summary>
/// The store of sign-in (<c>doc/fleet/identity.md</c>): the settings of the organization, the methods linked to people,
/// their passkeys, the people company sign-in creates, and the devices a sign-in registers.
/// </summary>
public interface ISignInStore
{
    /// <summary>Reads the sign-in settings of an organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The settings, or null for the defaults.</returns>
    ValueTask<SignInSettingsRecord?> GetSignInSettingsAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Sets the sign-in settings, when the version held is the one before.</summary>
    /// <param name="settings">The settings, numbered after the ones held (1 for the first).</param>
    /// <param name="audit">The audit event.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether they were set; false when the version moved on.</returns>
    ValueTask<bool> SetSignInSettingsAsync(SignInSettingsRecord settings, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Finds a linked account by its issuer and subject.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="issuer">The issuer.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The login, or null.</returns>
    ValueTask<LoginRecord?> FindLoginAsync(string organizationId, string issuer, string subject, CancellationToken cancellationToken = default);

    /// <summary>Lists the sign-in methods linked to a person.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The logins.</returns>
    ValueTask<IReadOnlyList<LoginRecord>> ListLoginsAsync(string personId, CancellationToken cancellationToken = default);

    /// <summary>Links an account to a person.</summary>
    /// <param name="login">The login.</param>
    /// <param name="audit">The audit event.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What happened.</returns>
    ValueTask<LoginLinkOutcome> LinkLoginAsync(LoginRecord login, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Links an account to a person from one of their devices, checked in the same write (<see cref="WayInAddition"/>).</summary>
    /// <param name="login">The login.</param>
    /// <param name="addition">The device that links it, and whether it is the person's first way in.</param>
    /// <param name="audit">The audit event.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What happened.</returns>
    ValueTask<LoginLinkOutcome> LinkLoginAsync(LoginRecord login, WayInAddition addition, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Removes an account linked to a person: it signs nobody in afterwards.</summary>
    /// <param name="login">The login.</param>
    /// <param name="keepLastWayIn">Whether to refuse when it is the last account or passkey of its person, checked in the same write.</param>
    /// <param name="audit">The audit event.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What happened.</returns>
    ValueTask<LoginRemovalOutcome> RemoveLoginAsync(LoginRecord login, bool keepLastWayIn, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Removes a passkey of a person: it signs nobody in afterwards.</summary>
    /// <param name="passkey">The passkey.</param>
    /// <param name="keepLastWayIn">Whether to refuse when it is the last account or passkey of its person, checked in the same write.</param>
    /// <param name="audit">The audit event.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What happened.</returns>
    ValueTask<LoginRemovalOutcome> RemovePasskeyAsync(PasskeyRecord passkey, bool keepLastWayIn, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Records a sign-in of a linked account: its email, name and groups as the provider gave them now.</summary>
    /// <param name="login">The login as it is now.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once it is written.</returns>
    ValueTask UpdateLoginAsync(LoginRecord login, CancellationToken cancellationToken = default);

    /// <summary>Creates a person at their first company sign-in, with the login that proved them; an existing login wins.</summary>
    /// <param name="person">The person.</param>
    /// <param name="login">Their login.</param>
    /// <param name="audit">The audit event.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The login now held: the new one, or the one another sign-in created in between.</returns>
    ValueTask<LoginRecord> CreatePersonWithLoginAsync(PersonRecord person, LoginRecord login, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Adds a passkey to a person.</summary>
    /// <param name="passkey">The passkey.</param>
    /// <param name="audit">The audit event.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was added; false when the credential exists.</returns>
    ValueTask<bool> AddPasskeyAsync(PasskeyRecord passkey, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Adds a passkey to a person from one of their devices, checked in the same write (<see cref="WayInAddition"/>).</summary>
    /// <param name="passkey">The passkey.</param>
    /// <param name="addition">The device that registers it, and whether it is the person's first way in.</param>
    /// <param name="audit">The audit event.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What happened.</returns>
    ValueTask<PasskeyAddOutcome> AddPasskeyAsync(PasskeyRecord passkey, WayInAddition addition, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Finds a passkey by its credential id.</summary>
    /// <param name="credentialId">The credential id, base64url.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The passkey, or null.</returns>
    ValueTask<PasskeyRecord?> FindPasskeyAsync(string credentialId, CancellationToken cancellationToken = default);

    /// <summary>Lists the passkeys of a person.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The passkeys.</returns>
    ValueTask<IReadOnlyList<PasskeyRecord>> ListPasskeysAsync(string personId, CancellationToken cancellationToken = default);

    /// <summary>Records a use of a passkey: its counter and flags as the assertion left them.</summary>
    /// <param name="passkey">The passkey as it is now.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once it is written.</returns>
    ValueTask UpdatePasskeyAsync(PasskeyRecord passkey, CancellationToken cancellationToken = default);

    /// <summary>Registers a pending controller device of a person who signed in on it.</summary>
    /// <param name="device">The device, pending, with its person.</param>
    /// <param name="audit">The audit event.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was registered; false when a device with this key exists.</returns>
    ValueTask<bool> RegisterSignedInDeviceAsync(DeviceRecord device, AuditEntry audit, CancellationToken cancellationToken = default);
}
