using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CodeAlta.Server.Identity.SignIn;

/// <summary>A person as ASP.NET Core Identity's passkey handler sees them.</summary>
/// <param name="Id">The person.</param>
/// <param name="OrganizationId">Their organization.</param>
/// <param name="DisplayName">Their name.</param>
public sealed record PasskeyPerson(string Id, string OrganizationId, string DisplayName);

/// <summary>A passkey that verified: its record as it is now, and its person.</summary>
/// <param name="Passkey">The record.</param>
/// <param name="PersonId">The person.</param>
public sealed record PasskeyOutcome(PasskeyRecord Passkey, string PersonId);

/// <summary>
/// The passkey ceremonies of the server's sign-in page, with ASP.NET Core Identity's passkey handler (WebAuthn level 3:
/// options, origin, challenge, signature and counter checks): registration for a person who signed in on one of their
/// devices, and sign-in with a discoverable credential, which names its person. The relying party is the host name the
/// browser opened, unless the options name one.
/// </summary>
public sealed class PasskeyCeremonies : IDisposable
{
    private readonly ISignInStore _store;
    private readonly ICoordinatorStore _people;
    private readonly UserManager<PasskeyPerson> _users;
    private readonly PasskeyHandler<PasskeyPerson> _handler;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="PasskeyCeremonies"/> class.</summary>
    /// <param name="store">The store of passkeys.</param>
    /// <param name="people">The store of people.</param>
    /// <param name="time">The clock.</param>
    /// <param name="serverDomain">The relying party id, or null for the host the browser opened.</param>
    /// <exception cref="ArgumentNullException">A store or the clock is null.</exception>
    public PasskeyCeremonies(ISignInStore store, ICoordinatorStore people, TimeProvider time, string? serverDomain = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _people = people;
        _time = time;
        var identity = new IdentityOptions();
        _users = new UserManager<PasskeyPerson>(new PasskeyUserStore(store, people), Options.Create(identity), null!, [], [], null!, new IdentityErrorDescriber(),
            null!, null!);
        _handler = new PasskeyHandler<PasskeyPerson>(_users, Options.Create(new IdentityPasskeyOptions
        {
            ServerDomain = serverDomain,
            // A discoverable credential: the passkey names its person, nobody types a name on a new device.
            ResidentKeyRequirement = "required",
            UserVerificationRequirement = "required",
            AttestationConveyancePreference = "none",
            // The page of the ceremony is the server's own: the origin is the one the browser opened it at, never a frame of
            // another site. Behind a proxy that ends TLS the browser sees https:// while the server sees http://.
            ValidateOrigin = static context =>
            {
                var request = context.HttpContext.Request;
                var origin = context.Origin;
                var valid = !context.CrossOrigin && (string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(origin, $"https://{request.Host}", StringComparison.OrdinalIgnoreCase));
                return ValueTask.FromResult(valid);
            },
        }));
    }

    /// <summary>Starts the registration of a passkey for a person.</summary>
    /// <param name="person">The person.</param>
    /// <param name="context">The request of the page.</param>
    /// <returns>The options for <c>navigator.credentials.create()</c> and the state the ceremony ends with.</returns>
    public async Task<(string OptionsJson, string State)> BeginRegistrationAsync(PersonRecord person, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(person);
        var result = await _handler.MakeCreationOptionsAsync(new PasskeyUserEntity { Id = person.Id, Name = person.DisplayName, DisplayName = person.DisplayName }, context)
            .ConfigureAwait(false);
        return (result.CreationOptionsJson, result.AttestationState ?? string.Empty);
    }

    /// <summary>
    /// Ends the ceremony of the registration of a passkey and verifies it, without keeping it: the device that started the
    /// registration adds it once it completes the sign-in with its signature (<see cref="ISignInStore.AddPasskeyAsync(PasskeyRecord, WayInAddition, AuditEntry, CancellationToken)"/>).
    /// </summary>
    /// <param name="person">The person.</param>
    /// <param name="credentialJson">What <c>navigator.credentials.create()</c> answered.</param>
    /// <param name="state">The state of the ceremony.</param>
    /// <param name="context">The request of the page.</param>
    /// <returns>The passkey, to add.</returns>
    /// <exception cref="ExternalSignInException">The attestation does not verify, or the credential exists.</exception>
    public async Task<PasskeyRecord> VerifyRegistrationAsync(PersonRecord person, string credentialJson, string state, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(person);
        var result = await _handler.PerformAttestationAsync(new PasskeyAttestationContext { HttpContext = context, CredentialJson = credentialJson, AttestationState = state })
            .ConfigureAwait(false);
        if (!result.Succeeded || result.Passkey is null || result.UserEntity?.Id != person.Id)
            throw new ExternalSignInException(SignInErrorCodes.PasskeyInvalid, $"The passkey does not verify: {result.Failure?.Message ?? "no passkey"}.");
        var now = _time.GetUtcNow();
        var record = new PasskeyRecord(Base64Url.EncodeToString(result.Passkey.CredentialId), person.OrganizationId, person.Id, Name(context), Serialize(result.Passkey), now, null);
        if (await _store.FindPasskeyAsync(record.CredentialId).ConfigureAwait(false) is not null)
            throw new ExternalSignInException(SignInErrorCodes.PasskeyInvalid, "This passkey is registered already.");
        return record;
    }

    /// <summary>Starts a sign-in with a passkey: any discoverable credential of this server.</summary>
    /// <param name="context">The request of the page.</param>
    /// <returns>The options for <c>navigator.credentials.get()</c> and the state the ceremony ends with.</returns>
    public async Task<(string OptionsJson, string State)> BeginAssertionAsync(HttpContext context)
    {
        var result = await _handler.MakeRequestOptionsAsync(null, context).ConfigureAwait(false);
        return (result.RequestOptionsJson, result.AssertionState ?? string.Empty);
    }

    /// <summary>Ends a sign-in with a passkey: the signature, the challenge, the origin and the counter verify, and the passkey names its person.</summary>
    /// <param name="credentialJson">What <c>navigator.credentials.get()</c> answered.</param>
    /// <param name="state">The state of the ceremony.</param>
    /// <param name="context">The request of the page.</param>
    /// <returns>The passkey and its person.</returns>
    /// <exception cref="ExternalSignInException">The assertion does not verify, or the passkey is unknown.</exception>
    public async Task<PasskeyOutcome> CompleteAssertionAsync(string credentialJson, string state, HttpContext context)
    {
        var result = await _handler.PerformAssertionAsync(new PasskeyAssertionContext { HttpContext = context, CredentialJson = credentialJson, AssertionState = state })
            .ConfigureAwait(false);
        if (!result.Succeeded || result.Passkey is null || result.User is null)
            throw new ExternalSignInException(SignInErrorCodes.PasskeyInvalid, $"The passkey does not verify: {result.Failure?.Message ?? "unknown passkey"}.");
        var id = Base64Url.EncodeToString(result.Passkey.CredentialId);
        var held = await _store.FindPasskeyAsync(id).ConfigureAwait(false)
            ?? throw new ExternalSignInException(SignInErrorCodes.PasskeyInvalid, "The passkey is not registered at this server.");
        var updated = held with { Data = Serialize(result.Passkey), LastUsedAt = _time.GetUtcNow() };
        await _store.UpdatePasskeyAsync(updated).ConfigureAwait(false);
        return new PasskeyOutcome(updated, held.PersonId);
    }

    /// <inheritdoc />
    public void Dispose() => _users.Dispose();

    private static string Name(HttpContext context)
    {
        var agent = context.Request.Headers.UserAgent.ToString();
        var name = agent.Contains("Windows", StringComparison.Ordinal) ? "Passkey on Windows"
            : agent.Contains("Mac OS", StringComparison.Ordinal) ? "Passkey on macOS"
            : agent.Contains("iPhone", StringComparison.Ordinal) || agent.Contains("Android", StringComparison.Ordinal) ? "Passkey on a phone" : "Passkey";
        return name;
    }

    internal static string Serialize(UserPasskeyInfo passkey)
        => JsonSerializer.Serialize(new PasskeyData(passkey.CredentialId, passkey.PublicKey, passkey.CreatedAt, passkey.SignCount, passkey.Transports, passkey.IsUserVerified,
            passkey.IsBackupEligible, passkey.IsBackedUp, passkey.AttestationObject, passkey.ClientDataJson), PasskeyJsonContext.Default.PasskeyData);

    internal static UserPasskeyInfo Deserialize(string data)
    {
        var value = JsonSerializer.Deserialize(data, PasskeyJsonContext.Default.PasskeyData) ?? throw new InvalidDataException("A passkey of the store does not read.");
        return new UserPasskeyInfo(value.CredentialId, value.PublicKey, value.CreatedAt, value.SignCount, value.Transports, value.IsUserVerified, value.IsBackupEligible,
            value.IsBackedUp, value.AttestationObject, value.ClientDataJson);
    }

    /// <summary>The store the passkey handler reads: people by id, passkeys by credential.</summary>
    private sealed class PasskeyUserStore(ISignInStore store, ICoordinatorStore people) : IUserPasskeyStore<PasskeyPerson>
    {
        public async Task AddOrUpdatePasskeyAsync(PasskeyPerson user, UserPasskeyInfo passkey, CancellationToken cancellationToken)
        {
            var id = Base64Url.EncodeToString(passkey.CredentialId);
            if (await store.FindPasskeyAsync(id, cancellationToken).ConfigureAwait(false) is { } held)
                await store.UpdatePasskeyAsync(held with { Data = Serialize(passkey) }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IList<UserPasskeyInfo>> GetPasskeysAsync(PasskeyPerson user, CancellationToken cancellationToken)
            => [.. (await store.ListPasskeysAsync(user.Id, cancellationToken).ConfigureAwait(false)).Select(static passkey => Deserialize(passkey.Data))];

        public async Task<PasskeyPerson?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken cancellationToken)
        {
            var passkey = await store.FindPasskeyAsync(Base64Url.EncodeToString(credentialId), cancellationToken).ConfigureAwait(false);
            return passkey is null ? null : await FindByIdAsync(passkey.PersonId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<UserPasskeyInfo?> FindPasskeyAsync(PasskeyPerson user, byte[] credentialId, CancellationToken cancellationToken)
        {
            var passkey = await store.FindPasskeyAsync(Base64Url.EncodeToString(credentialId), cancellationToken).ConfigureAwait(false);
            return passkey is null || passkey.PersonId != user.Id ? null : Deserialize(passkey.Data);
        }

        public Task RemovePasskeyAsync(PasskeyPerson user, byte[] credentialId, CancellationToken cancellationToken)
            => throw new NotSupportedException("Passkeys are removed through the API of the server.");

        public Task<string> GetUserIdAsync(PasskeyPerson user, CancellationToken cancellationToken) => Task.FromResult(user.Id);

        public Task<string?> GetUserNameAsync(PasskeyPerson user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.DisplayName);

        public Task SetUserNameAsync(PasskeyPerson user, string? userName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> GetNormalizedUserNameAsync(PasskeyPerson user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Id);

        public Task SetNormalizedUserNameAsync(PasskeyPerson user, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IdentityResult> CreateAsync(PasskeyPerson user, CancellationToken cancellationToken)
            => throw new NotSupportedException("People are created by the setup and by company sign-in.");

        public Task<IdentityResult> UpdateAsync(PasskeyPerson user, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);

        public Task<IdentityResult> DeleteAsync(PasskeyPerson user, CancellationToken cancellationToken)
            => throw new NotSupportedException("People are not deleted through passkeys.");

        public async Task<PasskeyPerson?> FindByIdAsync(string userId, CancellationToken cancellationToken)
        {
            var person = string.IsNullOrEmpty(userId) ? null : await people.GetPersonAsync(userId, cancellationToken).ConfigureAwait(false);
            return person is null ? null : new PasskeyPerson(person.Id, person.OrganizationId, person.DisplayName);
        }

        public Task<PasskeyPerson?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => FindByIdAsync(normalizedUserName, cancellationToken);

        public void Dispose()
        {
        }
    }
}

/// <summary>What verifies a passkey, as the store keeps it.</summary>
internal sealed record PasskeyData(byte[] CredentialId, byte[] PublicKey, DateTimeOffset CreatedAt, uint SignCount, string[]? Transports, bool IsUserVerified,
    bool IsBackupEligible, bool IsBackedUp, byte[] AttestationObject, byte[] ClientDataJson);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PasskeyData))]
internal sealed partial class PasskeyJsonContext : JsonSerializerContext;
