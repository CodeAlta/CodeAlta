using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CodeAlta.Catalog;

namespace CodeAlta.Fleet.Identity;

/// <summary>
/// The device key of this instance: an ECDSA P-256 key made on first use and kept in the fleet folder of its state root
/// (<see cref="CatalogOptions.FleetRoot"/>), so the developer instance is a device of its own.
/// </summary>
/// <remarks>
/// <para>
/// The private key is protected at rest with DPAPI for the current user on Windows, and by a file only its owner reads
/// (0600, in a 0700 folder) on macOS and Linux. Keys held by the TPM or the Secure Enclave can come later behind the same
/// type: nothing outside it reads the private key.
/// </para>
/// <para>
/// The device id is derived from the public key (the first 128 bits of the fingerprint, in lower-case base32), so a
/// device cannot claim the id of another key and a peer derives it from the key it was shown.
/// </para>
/// </remarks>
public sealed class DeviceIdentity : IDisposable
{
    /// <summary>The name of the key file in the fleet folder.</summary>
    public const string KeyFileName = "device-key.json";

    private const int KeyFileVersion = 1;
    private const string AlgorithmName = "ecdsa-p256";
    private const string DpapiProtection = "dpapi-current-user";
    private const string OwnerOnlyFileProtection = "owner-only-file";
    private const int DeviceIdBytes = 16;
    private const int DeviceIdLength = 26;
    private const string P256Oid = "1.2.840.10045.3.1.7";

    private readonly ECDsa _key;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _subjectPublicKeyInfo;
    private bool _disposed;

    private DeviceIdentity(ECDsa key, DateTimeOffset createdAt, string keyPath, TimeProvider timeProvider)
    {
        _key = key;
        _timeProvider = timeProvider;
        _subjectPublicKeyInfo = key.ExportSubjectPublicKeyInfo();
        CreatedAt = createdAt;
        KeyPath = keyPath;
        Fingerprint = DeviceFingerprint.FromSubjectPublicKeyInfo(_subjectPublicKeyInfo);
        DeviceId = DeviceIdOf(Fingerprint);
    }

    /// <summary>Gets the id of this device, derived from its public key.</summary>
    public string DeviceId { get; }

    /// <summary>Gets the fingerprint of the public key.</summary>
    public DeviceFingerprint Fingerprint { get; }

    /// <summary>Gets when the key was made.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the path of the key file.</summary>
    public string KeyPath { get; }

    /// <summary>Loads the device key of an instance, making it on first use.</summary>
    /// <param name="options">The catalog options of the instance; the key is in <see cref="CatalogOptions.FleetRoot"/>.</param>
    /// <returns>The identity; dispose it when the instance ends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The key file exists but cannot be read.</exception>
    /// <exception cref="IOException">The key file cannot be written.</exception>
    public static DeviceIdentity LoadOrCreate(CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return LoadOrCreate(options.FleetRoot, TimeProvider.System);
    }

    /// <summary>Loads the device key kept in a fleet folder, making it on first use.</summary>
    /// <param name="fleetRoot">The fleet folder.</param>
    /// <returns>The identity; dispose it when the instance ends.</returns>
    /// <exception cref="ArgumentException"><paramref name="fleetRoot"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The key file exists but cannot be read.</exception>
    /// <exception cref="IOException">The key file cannot be written.</exception>
    public static DeviceIdentity LoadOrCreate(string fleetRoot) => LoadOrCreate(fleetRoot, TimeProvider.System);

    /// <summary>Loads the device key kept in a fleet folder, making it on first use.</summary>
    /// <param name="fleetRoot">The fleet folder.</param>
    /// <param name="timeProvider">The clock of the creation time and of the certificates.</param>
    /// <returns>The identity; dispose it when the instance ends.</returns>
    /// <exception cref="ArgumentException"><paramref name="fleetRoot"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The key file exists but cannot be read.</exception>
    /// <exception cref="IOException">The key file cannot be written.</exception>
    public static DeviceIdentity LoadOrCreate(string fleetRoot, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var path = Path.Combine(Path.GetFullPath(fleetRoot), KeyFileName);
        if (File.Exists(path))
        {
            return Load(path, timeProvider);
        }

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var now = timeProvider.GetUtcNow();
            var createdAt = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
            if (FleetFiles.TryWriteNew(path, Serialize(key, createdAt)))
            {
                return new DeviceIdentity(key, createdAt, path, timeProvider);
            }
        }
        catch
        {
            key.Dispose();
            throw;
        }

        // Another caller made the key first: that one is the key of this instance.
        key.Dispose();
        return Load(path, timeProvider);
    }

    /// <summary>Gets the device id of a key from its fingerprint.</summary>
    /// <param name="fingerprint">The fingerprint of the key.</param>
    /// <returns>The device id: 26 characters of lower-case base32.</returns>
    /// <exception cref="ArgumentException"><paramref name="fingerprint"/> is the default value.</exception>
    public static string DeviceIdOf(DeviceFingerprint fingerprint)
    {
        if (fingerprint.IsEmpty)
        {
            throw new ArgumentException("A fingerprint is required.", nameof(fingerprint));
        }

        return Base32.Encode(fingerprint.AsSpan()[..DeviceIdBytes]).ToLowerInvariant();
    }

    /// <summary>Gets whether a text has the form of a device id.</summary>
    /// <param name="deviceId">The text.</param>
    /// <returns>Whether it is 26 characters of lower-case base32.</returns>
    public static bool IsValidDeviceId(string? deviceId)
        => deviceId is { Length: DeviceIdLength } && deviceId.All(static c => c is >= 'a' and <= 'z' or >= '2' and <= '7');

    /// <summary>Gets the public key as a DER SubjectPublicKeyInfo.</summary>
    /// <returns>A copy of the public key.</returns>
    /// <exception cref="ObjectDisposedException">The identity is disposed.</exception>
    public byte[] ExportSubjectPublicKeyInfo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (byte[])_subjectPublicKeyInfo.Clone();
    }

    /// <summary>Signs data with the device key: ECDSA P-256 over SHA-256, as the 64 bytes of r and s.</summary>
    /// <param name="data">The data to sign.</param>
    /// <returns>The signature.</returns>
    /// <exception cref="ObjectDisposedException">The identity is disposed.</exception>
    public byte[] SignData(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    /// Agrees a secret with a peer key, ECDH on P-256 with the device key (<c>doc/fleet/security-and-privacy.md</c>: the
    /// device key receives wrapped keys): the raw shared secret, which the caller derives a key from (HKDF).
    /// </summary>
    /// <param name="peerSubjectPublicKeyInfo">The public key of the peer, as a DER SubjectPublicKeyInfo of a P-256 key.</param>
    /// <returns>The 32 bytes of the shared secret.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="peerSubjectPublicKeyInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="CryptographicException">The peer key is not a P-256 public key.</exception>
    /// <exception cref="ObjectDisposedException">The identity is disposed.</exception>
    public byte[] DeriveSharedSecret(byte[] peerSubjectPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(peerSubjectPublicKeyInfo);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerSubjectPublicKeyInfo, out var read);
        if (read != peerSubjectPublicKeyInfo.Length || peer.KeySize != 256) throw new CryptographicException("The peer key is not a P-256 public key.");
        var parameters = _key.ExportParameters(includePrivateParameters: true);
        try
        {
            using var own = ECDiffieHellman.Create(parameters);
            return own.DeriveRawSecretAgreement(peer.PublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }
    }

    /// <summary>
    /// Makes a self-signed certificate of the device key, for TLS. Peers check its key against a pinned fingerprint, never
    /// its chain, name or dates; it is valid from a day ago for a year so that clocks that differ do not matter.
    /// </summary>
    /// <returns>A certificate with its private key; dispose it when its connections end.</returns>
    /// <exception cref="ObjectDisposedException">The identity is disposed.</exception>
    public X509Certificate2 CreateTlsCertificate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new CertificateRequest($"CN=CodeAlta device {DeviceId}", _key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication"), new Oid("1.3.6.1.5.5.7.3.2", "Client Authentication")], false));
        var now = _timeProvider.GetUtcNow();
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));

        // SslStream on Windows (SChannel) refuses a key that lives only in memory, and macOS wants a keychain identity: a
        // PKCS#12 round trip gives each platform the key storage it needs. On Windows that copy of the key is a file of the
        // user's key store until the certificate is disposed.
        Span<byte> passwordBytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(passwordBytes);
        var password = Convert.ToHexString(passwordBytes);
        var pkcs12 = certificate.Export(X509ContentType.Pkcs12, password);
        try
        {
            return X509CertificateLoader.LoadPkcs12(pkcs12, password, X509KeyStorageFlags.UserKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _key.Dispose();
        }
    }

    internal static bool IsP256(ECDsa key)
    {
        if (key.KeySize != 256)
        {
            return false;
        }

        var curve = key.ExportParameters(includePrivateParameters: false).Curve;
        return curve.IsNamed
            && (curve.Oid.Value == P256Oid || curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256" or "secp256r1" or "prime256v1");
    }

    private static byte[] Serialize(ECDsa key, DateTimeOffset createdAt)
    {
        var privateKey = key.ExportPkcs8PrivateKey();
        try
        {
            var file = new DeviceKeyFile
            {
                Version = KeyFileVersion,
                Algorithm = AlgorithmName,
                Protection = OperatingSystem.IsWindows() ? DpapiProtection : OwnerOnlyFileProtection,
                PrivateKey = Convert.ToBase64String(OperatingSystem.IsWindows() ? WindowsDataProtection.Protect(privateKey) : privateKey),
                PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                CreatedAt = createdAt,
            };
            return JsonSerializer.SerializeToUtf8Bytes(file, FleetIdentityJsonContext.Default.DeviceKeyFile);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private static DeviceIdentity Load(string path, TimeProvider timeProvider)
    {
        DeviceKeyFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllBytes(path), FleetIdentityJsonContext.Default.DeviceKeyFile);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The device key file '{path}' is not valid JSON.", exception);
        }

        if (file is not { Version: KeyFileVersion, Algorithm: AlgorithmName, PrivateKey: not null, PublicKey: not null })
        {
            throw new InvalidDataException($"The device key file '{path}' has an unknown version or algorithm.");
        }

        var expectedProtection = OperatingSystem.IsWindows() ? DpapiProtection : OwnerOnlyFileProtection;
        if (!string.Equals(file.Protection, expectedProtection, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The device key file '{path}' is protected as '{file.Protection}', which this system cannot read.");
        }

        FleetFiles.RestrictToOwner(path);
        byte[]? privateKey = null;
        var key = ECDsa.Create();
        try
        {
            var stored = Convert.FromBase64String(file.PrivateKey);
            privateKey = OperatingSystem.IsWindows() ? WindowsDataProtection.Unprotect(stored) : stored;
            key.ImportPkcs8PrivateKey(privateKey, out var read);
            if (read != privateKey.Length || !IsP256(key))
            {
                throw new InvalidDataException($"The device key file '{path}' does not hold a P-256 key.");
            }

            if (!key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(Convert.FromBase64String(file.PublicKey)))
            {
                throw new InvalidDataException($"The private and public keys of the device key file '{path}' do not match.");
            }

            return new DeviceIdentity(key, file.CreatedAt, path, timeProvider);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            key.Dispose();
            throw new InvalidDataException($"The device key file '{path}' cannot be read by this user.", exception);
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            if (privateKey is not null)
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }
    }
}
