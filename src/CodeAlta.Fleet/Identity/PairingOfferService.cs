using System.Security.Cryptography;

namespace CodeAlta.Fleet.Identity;

/// <summary>The outcome of a secret presented to <see cref="PairingOfferService.Redeem"/>.</summary>
public enum PairingRedeemResult
{
    /// <summary>The secret is the one of the offer; the offer is used up.</summary>
    Accepted,

    /// <summary>The secret is not the one of the offer.</summary>
    WrongSecret,

    /// <summary>The offer has expired.</summary>
    Expired,

    /// <summary>Too many wrong secrets were presented: the offer refuses every secret, the right one too.</summary>
    TooManyAttempts,

    /// <summary>No offer is open: none was made, it was used, or it was cancelled.</summary>
    NoOffer,
}

/// <summary>
/// The pairing offer of a device (the node side): it makes a payload with a one-time secret and accepts that secret
/// once, before it expires, within a few attempts.
/// </summary>
/// <remarks>
/// One offer is open at a time; a new one replaces it. The service keeps only the SHA-256 of the secret and compares in
/// constant time. Thread safe.
/// </remarks>
public sealed class PairingOfferService
{
    /// <summary>The lifetime of an offer by default.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The longest lifetime of an offer.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

    /// <summary>The number of secrets an offer accepts to check by default.</summary>
    public const int DefaultMaxAttempts = 5;

    private readonly Lock _gate = new();
    private readonly DeviceIdentity _identity;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxAttempts;
    private OpenOffer? _offer;

    /// <summary>Creates the service of a device.</summary>
    /// <param name="identity">The device that offers pairings.</param>
    /// <param name="timeProvider">The clock of the expiries.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public PairingOfferService(DeviceIdentity identity, TimeProvider timeProvider)
        : this(identity, timeProvider, DefaultMaxAttempts)
    {
    }

    /// <summary>Creates the service of a device.</summary>
    /// <param name="identity">The device that offers pairings.</param>
    /// <param name="timeProvider">The clock of the expiries.</param>
    /// <param name="maxAttempts">The number of secrets an offer accepts to check.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxAttempts"/> is less than 1.</exception>
    public PairingOfferService(DeviceIdentity identity, TimeProvider timeProvider, int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _identity = identity;
        _timeProvider = timeProvider;
        _maxAttempts = maxAttempts;
    }

    /// <summary>Gets whether an offer is open and not expired.</summary>
    public bool HasOpenOffer
    {
        get
        {
            lock (_gate)
            {
                return _offer is { } offer && _timeProvider.GetUtcNow() < offer.ExpiresAt;
            }
        }
    }

    /// <summary>Opens an offer for <see cref="DefaultLifetime"/>, replacing the open one.</summary>
    /// <param name="deviceName">The name of this device, shown to the other one.</param>
    /// <param name="addresses">Where the other device reaches this one, as <c>host:port</c>.</param>
    /// <returns>The payload to show; the service keeps only the hash of its secret.</returns>
    /// <exception cref="ArgumentException">The name or the addresses are invalid.</exception>
    public PairingPayload CreateOffer(string deviceName, IReadOnlyList<string> addresses) => CreateOffer(deviceName, addresses, DefaultLifetime);

    /// <summary>Opens an offer, replacing the open one.</summary>
    /// <param name="deviceName">The name of this device, shown to the other one.</param>
    /// <param name="addresses">Where the other device reaches this one, as <c>host:port</c>.</param>
    /// <param name="lifetime">How long the offer stays open, at least a second and at most <see cref="MaxLifetime"/>.</param>
    /// <returns>The payload to show; the service keeps only the hash of its secret.</returns>
    /// <exception cref="ArgumentException">The name or the addresses are invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is out of range.</exception>
    public PairingPayload CreateOffer(string deviceName, IReadOnlyList<string> addresses, TimeSpan lifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lifetime, TimeSpan.FromSeconds(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lifetime, MaxLifetime);
        Span<byte> secret = stackalloc byte[PairingPayload.SecretLength];
        RandomNumberGenerator.Fill(secret);
        try
        {
            var payload = new PairingPayload(deviceName, _identity.Fingerprint, addresses, secret, _timeProvider.GetUtcNow() + lifetime);
            var offer = new OpenOffer(SHA256.HashData(secret), payload.ExpiresAt);
            lock (_gate)
            {
                _offer = offer;
            }

            return payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Checks a secret presented by a device that pairs, and uses the offer up when it matches.</summary>
    /// <param name="secret">The secret presented.</param>
    /// <returns>The outcome.</returns>
    public PairingRedeemResult Redeem(ReadOnlySpan<byte> secret)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(secret, hash);
        lock (_gate)
        {
            if (_offer is not { } offer)
            {
                return PairingRedeemResult.NoOffer;
            }

            if (_timeProvider.GetUtcNow() >= offer.ExpiresAt)
            {
                _offer = null;
                return PairingRedeemResult.Expired;
            }

            // A withdrawn offer stays until it expires or is replaced, so that it keeps saying why it refuses.
            if (offer.Attempts >= _maxAttempts)
            {
                return PairingRedeemResult.TooManyAttempts;
            }

            offer.Attempts++;
            var matches = CryptographicOperations.FixedTimeEquals(hash, offer.SecretHash) & (secret.Length == PairingPayload.SecretLength);
            if (matches)
            {
                _offer = null;
                return PairingRedeemResult.Accepted;
            }

            return PairingRedeemResult.WrongSecret;
        }
    }

    /// <summary>Closes the open offer, if any.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _offer = null;
        }
    }

    private sealed class OpenOffer(byte[] secretHash, DateTimeOffset expiresAt)
    {
        public byte[] SecretHash { get; } = secretHash;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public int Attempts { get; set; }
    }
}
