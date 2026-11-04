using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Server.Identity.SignIn;

/// <summary>The stable codes of the refusals of sign-in.</summary>
public static class SignInErrorCodes
{
    /// <summary>The method is unknown, not enabled by the owner, or not available on this server.</summary>
    public const string MethodDisabled = "sign_in.method_disabled";

    /// <summary>The organization requires its company sign-in.</summary>
    public const string SsoRequired = "sign_in.sso_required";

    /// <summary>The attempt is unknown, used, expired, or of another device.</summary>
    public const string AttemptInvalid = "sign_in.invalid_attempt";

    /// <summary>The state the browser brought back is not the attempt's.</summary>
    public const string StateMismatch = "sign_in.state_mismatch";

    /// <summary>The redirect is not a loopback address.</summary>
    public const string RedirectInvalid = "sign_in.invalid_redirect";

    /// <summary>The provider does not answer, or its configuration does not read.</summary>
    public const string ProviderUnavailable = "sign_in.provider_unavailable";

    /// <summary>The provider refused the code (replayed, expired, wrong verifier).</summary>
    public const string CodeRefused = "sign_in.code_refused";

    /// <summary>The person refused the sign-in at the provider (a device code).</summary>
    public const string Denied = "sign_in.denied";

    /// <summary>The device code expired before the person entered it.</summary>
    public const string Expired = "sign_in.expired";

    /// <summary>The ID token does not verify.</summary>
    public const string TokenInvalid = "sign_in.invalid_token";

    /// <summary>The ID token is for another client.</summary>
    public const string WrongAudience = "sign_in.wrong_audience";

    /// <summary>The ID token is of another issuer.</summary>
    public const string WrongIssuer = "sign_in.wrong_issuer";

    /// <summary>The ID token does not carry the nonce of the attempt.</summary>
    public const string WrongNonce = "sign_in.wrong_nonce";

    /// <summary>The ID token expired.</summary>
    public const string TokenExpired = "sign_in.token_expired";

    /// <summary>No person of the server linked this account.</summary>
    public const string NotLinked = "sign_in.not_linked";

    /// <summary>The account is linked to another person.</summary>
    public const string LinkedElsewhere = "sign_in.linked_elsewhere";

    /// <summary>The passkey ceremony does not verify.</summary>
    public const string PasskeyInvalid = "sign_in.invalid_passkey";

    /// <summary>The grant of a sign-in is unknown, used, expired, or of another device.</summary>
    public const string GrantInvalid = "sign_in.invalid_grant";

    /// <summary>The settings do not hold (company provider missing, SSO required without it, version moved on).</summary>
    public const string SettingsInvalid = "sign_in.invalid_settings";

    /// <summary>Too many sign-ins wait at once.</summary>
    public const string Busy = "sign_in.busy";

    /// <summary>The company's provider proved an identity outside the domains and tenants the owner named.</summary>
    public const string IdentityNotAllowed = "sign_in.identity_not_allowed";
}

/// <summary>A sign-in that waits for the browser.</summary>
public sealed class SignInAttempt
{
    /// <summary>Gets the id of the attempt, a secret of the device and its browser.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the organization.</summary>
    public required string OrganizationId { get; init; }

    /// <summary>Gets the method.</summary>
    public required string Method { get; init; }

    /// <summary>Gets the purpose (<c>FleetSignInPurposes</c>).</summary>
    public required string Purpose { get; init; }

    /// <summary>Gets the device that started it: only its key completes it.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Gets the public key of that device, which signs the completion.</summary>
    public required string PublicKey { get; init; }

    /// <summary>Gets the person who links, for a link; null for an enrollment.</summary>
    public string? LinkingPersonId { get; init; }

    /// <summary>Gets the loopback address the browser comes back to; empty for a device code.</summary>
    public required string RedirectUri { get; init; }

    /// <summary>Gets the state.</summary>
    public required string State { get; init; }

    /// <summary>Gets the nonce of the ID token.</summary>
    public required string Nonce { get; init; }

    /// <summary>Gets the PKCE verifier, which never leaves the server.</summary>
    public required string CodeVerifier { get; init; }

    /// <summary>Gets when it stops working.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Gets the provider's device code, which never leaves the server; null for a redirect.</summary>
    public string? DeviceCode { get; init; }

    /// <summary>Gets or sets how many seconds pass at least between two polls of the provider.</summary>
    public int PollInterval { get; set; } = 5;

    /// <summary>Gets or sets when the provider may be polled again.</summary>
    public DateTimeOffset NextPollAt { get; set; }

    private int _polling;

    /// <summary>Takes the right to poll the provider now: one poll at a time, and none before <see cref="NextPollAt"/>.</summary>
    /// <param name="now">The time now.</param>
    /// <returns>Whether this caller polls; false while another poll runs or the interval has not passed.</returns>
    public bool TryBeginPoll(DateTimeOffset now)
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return false;
        if (now >= NextPollAt) return true;
        Volatile.Write(ref _polling, 0);
        return false;
    }

    /// <summary>Ends a poll and sets when the next may run.</summary>
    /// <param name="now">The time now.</param>
    public void EndPoll(DateTimeOffset now)
    {
        NextPollAt = now + TimeSpan.FromSeconds(PollInterval);
        Volatile.Write(ref _polling, 0);
    }

    /// <summary>Gets or sets the state of the passkey ceremony the page started, or null.</summary>
    public string? PasskeyState { get; set; }

    /// <summary>Gets or sets the hash of the one-time code the passkey page gave the browser, or null.</summary>
    public string? PasskeyCodeHash { get; set; }

    /// <summary>Gets or sets the person the passkey proved (or the passkey registered for), or null.</summary>
    public string? PasskeyPersonId { get; set; }
}

/// <summary>The grant of a sign-in: it lets one device enroll as a controller device of one person, once.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="PersonId">The person.</param>
/// <param name="DeviceId">The device that signed in.</param>
/// <param name="Method">The method that proved the person.</param>
/// <param name="ExpiresAt">When it stops working.</param>
public sealed record SignInGrant(string OrganizationId, string PersonId, string DeviceId, string Method, DateTimeOffset ExpiresAt);

/// <summary>
/// The sign-ins in progress and the grants they gave, in memory: both live minutes, a restart forgets them (the person
/// signs in again), and only their hashes are kept for grants. One instance per server; no state is static.
/// </summary>
public sealed class SignInAttempts
{
    private const string GrantPrefix = "asg_";

    private readonly ConcurrentDictionary<string, SignInAttempt> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SignInGrant> _grants = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly int _capacity;

    /// <summary>Initializes a new instance of the <see cref="SignInAttempts"/> class.</summary>
    /// <param name="time">The clock.</param>
    /// <param name="capacity">How many attempts may wait at once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is null.</exception>
    public SignInAttempts(TimeProvider time, int capacity)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>Gets a new random secret, base64url, of 32 bytes.</summary>
    /// <returns>The secret.</returns>
    public static string NewSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>Gets the S256 challenge of a PKCE verifier.</summary>
    /// <param name="verifier">The verifier.</param>
    /// <returns>The challenge.</returns>
    public static string Challenge(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Gets the hash a secret is kept by.</summary>
    /// <param name="secret">The secret.</param>
    /// <returns>Its hash.</returns>
    public static string Hash(string secret) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Keeps an attempt.</summary>
    /// <param name="attempt">The attempt.</param>
    /// <returns>Whether it is kept; false when too many wait.</returns>
    public bool Add(SignInAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        Sweep();
        if (_attempts.Count >= _capacity) return false;
        return _attempts.TryAdd(attempt.Id, attempt);
    }

    /// <summary>Finds an attempt that has not expired.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>The attempt, or null.</returns>
    public SignInAttempt? Find(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_attempts.TryGetValue(id, out var attempt)) return null;
        if (attempt.ExpiresAt > _time.GetUtcNow()) return attempt;
        _attempts.TryRemove(id, out _);
        return null;
    }

    /// <summary>Takes an attempt to complete it: it works once, whatever the answer of the provider.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>The attempt, or null when it is unknown, taken or expired.</returns>
    public SignInAttempt? Take(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_attempts.TryRemove(id, out var attempt)) return null;
        return attempt.ExpiresAt > _time.GetUtcNow() ? attempt : null;
    }

    /// <summary>Gives the grant of a completed sign-in.</summary>
    /// <param name="grant">The grant.</param>
    /// <returns>Its value, shown once to the device.</returns>
    public string Grant(SignInGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        Sweep();
        var value = GrantPrefix + NewSecret();
        _grants[Hash(value)] = grant;
        return value;
    }

    /// <summary>Takes a grant for the device it was given to; it works once.</summary>
    /// <param name="value">The value of the grant.</param>
    /// <param name="deviceId">The device that enrolls.</param>
    /// <returns>The grant, or null when it is unknown, used, expired, or of another device.</returns>
    public SignInGrant? TakeGrant(string? value, string deviceId)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128) return null;
        var hash = Hash(value);
        if (!_grants.TryGetValue(hash, out var grant) || !string.Equals(grant.DeviceId, deviceId, StringComparison.Ordinal)) return null;
        if (!_grants.TryRemove(hash, out grant)) return null;
        return grant.ExpiresAt > _time.GetUtcNow() ? grant : null;
    }

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var pair in _attempts)
        {
            if (pair.Value.ExpiresAt <= now) _attempts.TryRemove(pair.Key, out _);
        }

        foreach (var pair in _grants)
        {
            if (pair.Value.ExpiresAt <= now) _grants.TryRemove(pair.Key, out _);
        }
    }
}
