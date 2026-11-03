using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

/// <summary>The descriptive metadata of a session, sealed for the devices of its person: what the server never reads.</summary>
/// <param name="Title">The title of the session.</param>
public sealed record FleetSessionMetadata(string? Title);

/// <summary>
/// A metadata key in clear, on a device of its person (<c>doc/fleet/security-and-privacy.md</c>, keys): it seals the
/// descriptive metadata of the sessions that person's devices publish, with AES-256-GCM. A sealed text names the
/// algorithm and the key, and is bound to its session, so that a server cannot move a title from one session to another.
/// </summary>
public sealed class FleetMetadataKey
{
    /// <summary>The algorithm of sealed metadata: AES-256-GCM, a 96-bit random nonce, a 128-bit tag.</summary>
    public const string SealAlgorithm = "a256gcm";

    /// <summary>The length of a key, in bytes.</summary>
    public const int KeyLength = 32;

    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const string SealDomain = "codealta-sealed-metadata/1";

    private readonly byte[] _key;

    /// <summary>Initializes a new instance of the <see cref="FleetMetadataKey"/> class.</summary>
    /// <param name="id">The id of the key.</param>
    /// <param name="key">The 32 bytes of the key.</param>
    /// <exception cref="ArgumentException">The id is empty or the key is not 32 bytes.</exception>
    public FleetMetadataKey(string id, ReadOnlySpan<byte> key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (key.Length != KeyLength) throw new ArgumentException($"A metadata key is {KeyLength} bytes.", nameof(key));
        Id = id;
        _key = key.ToArray();
    }

    /// <summary>Gets the id of the key, which the server and sealed texts name.</summary>
    public string Id { get; }

    /// <summary>Makes a new key, with a random id.</summary>
    /// <returns>The key.</returns>
    public static FleetMetadataKey Generate()
    {
        Span<byte> id = stackalloc byte[16];
        RandomNumberGenerator.Fill(id);
        Span<byte> key = stackalloc byte[KeyLength];
        RandomNumberGenerator.Fill(key);
        try
        {
            return new FleetMetadataKey(Base64Url.EncodeToString(id), key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Seals the metadata of a session.</summary>
    /// <param name="sessionId">The session, to which the sealed text is bound.</param>
    /// <param name="metadata">The metadata.</param>
    /// <returns>The sealed text: <c>{ "alg", "kid", "iv", "ct" }</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    public string Seal(string sessionId, FleetSessionMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(metadata);
        var plain = FleetSealedJson.Write(writer =>
        {
            if (metadata.Title is not null) writer.WriteString("title", metadata.Title);
        });
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var sealedBytes = new byte[plain.Length + TagLength];
        using (var aes = new AesGcm(_key, TagLength))
        {
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), Associated(Id, sessionId));
        }

        return Encoding.UTF8.GetString(FleetSealedJson.Write(writer =>
        {
            writer.WriteString("alg", SealAlgorithm);
            writer.WriteString("kid", Id);
            writer.WriteString("iv", Base64Url.EncodeToString(nonce));
            writer.WriteString("ct", Base64Url.EncodeToString(sealedBytes));
        }));
    }

    /// <summary>Opens the sealed metadata of a session, when this key sealed it for this session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="sealedText">The sealed text, as the server gave it.</param>
    /// <param name="metadata">The metadata, or <see langword="null"/>.</param>
    /// <returns>Whether the text opened: another key, another session, a changed text or an unknown algorithm do not.</returns>
    public bool TryOpen(string sessionId, string? sealedText, out FleetSessionMetadata? metadata)
    {
        metadata = null;
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(sealedText) || sealedText.Length > 65536) return false;
        try
        {
            using var document = JsonDocument.Parse(sealedText);
            var root = document.RootElement;
            if (FleetSealedJson.Text(root, "alg") != SealAlgorithm || FleetSealedJson.Text(root, "kid") != Id) return false;
            var nonce = FleetSealedJson.Bytes(root, "iv");
            var sealedBytes = FleetSealedJson.Bytes(root, "ct");
            if (nonce is not { Length: NonceLength } || sealedBytes is not { Length: >= TagLength }) return false;
            var plain = new byte[sealedBytes.Length - TagLength];
            using (var aes = new AesGcm(_key, TagLength))
            {
                aes.Decrypt(nonce, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), plain, Associated(Id, sessionId));
            }

            using var opened = JsonDocument.Parse(plain);
            metadata = new FleetSessionMetadata(FleetSealedJson.Text(opened.RootElement, "title"));
            return true;
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Gets the bytes of the key, to wrap it to a device.</summary>
    internal ReadOnlySpan<byte> Material => _key;

    private static byte[] Associated(string keyId, string sessionId) => Encoding.UTF8.GetBytes($"{SealDomain}\n{SealAlgorithm}\n{keyId}\n{sessionId}");
}

/// <summary>
/// Wraps a metadata key to one device (<c>doc/fleet/security-and-privacy.md</c>, keys): ECDH-ES on P-256 with an
/// ephemeral key and the device key, HKDF-SHA256, AES-256-GCM. Only the holder of the device key opens the copy; the
/// algorithm id travels in it so that other curves can come later.
/// </summary>
public static class FleetKeyWrap
{
    /// <summary>The algorithm of a wrapped key.</summary>
    public const string Algorithm = "ecdh-es-p256-hkdf-sha256-a256gcm";

    private const string Domain = "codealta-key-wrap/1";
    private const int NonceLength = 12;
    private const int TagLength = 16;

    /// <summary>Wraps a key to a device, with the public key of that device from the verified device list.</summary>
    /// <param name="key">The key.</param>
    /// <param name="recipient">The device's public key; its device id is the one the copy is for.</param>
    /// <returns>The wrapped key: <c>{ "alg", "kid", "to", "epk", "iv", "ct" }</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static string Wrap(FleetMetadataKey key, FleetDevicePublicKey recipient)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(recipient);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(recipient.ExportSubjectPublicKeyInfo(), out _);
        var secret = ephemeral.DeriveRawSecretAgreement(peer.PublicKey);
        var epk = ephemeral.ExportSubjectPublicKeyInfo();
        var associated = Associated(key.Id, recipient.DeviceId);
        var wrapping = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, FleetMetadataKey.KeyLength, SHA256.HashData(epk), associated);
        CryptographicOperations.ZeroMemory(secret);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var sealedBytes = new byte[FleetMetadataKey.KeyLength + TagLength];
        using (var aes = new AesGcm(wrapping, TagLength))
        {
            aes.Encrypt(nonce, key.Material, sealedBytes.AsSpan(0, FleetMetadataKey.KeyLength), sealedBytes.AsSpan(FleetMetadataKey.KeyLength), associated);
        }

        CryptographicOperations.ZeroMemory(wrapping);
        return Encoding.UTF8.GetString(FleetSealedJson.Write(writer =>
        {
            writer.WriteString("alg", Algorithm);
            writer.WriteString("kid", key.Id);
            writer.WriteString("to", recipient.DeviceId);
            writer.WriteString("epk", Base64Url.EncodeToString(epk));
            writer.WriteString("iv", Base64Url.EncodeToString(nonce));
            writer.WriteString("ct", Base64Url.EncodeToString(sealedBytes));
        }));
    }

    /// <summary>Opens a copy of a key wrapped to this device.</summary>
    /// <param name="wrapped">The wrapped key.</param>
    /// <param name="keyId">The id the key must have.</param>
    /// <param name="identity">The key of this device.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The copy is not for this device or this key, uses an unknown algorithm, or does not open.</exception>
    public static FleetMetadataKey Unwrap(string wrapped, string keyId, DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        ArgumentNullException.ThrowIfNull(keyId);
        ArgumentNullException.ThrowIfNull(identity);
        try
        {
            using var document = JsonDocument.Parse(wrapped);
            var root = document.RootElement;
            if (FleetSealedJson.Text(root, "alg") != Algorithm) throw new InvalidDataException("The wrapped key uses an algorithm this version does not know.");
            if (FleetSealedJson.Text(root, "kid") != keyId || FleetSealedJson.Text(root, "to") != identity.DeviceId)
                throw new InvalidDataException("The wrapped key is not the copy of this key for this device.");
            var epk = FleetSealedJson.Bytes(root, "epk");
            var nonce = FleetSealedJson.Bytes(root, "iv");
            var sealedBytes = FleetSealedJson.Bytes(root, "ct");
            if (epk is null || !FleetDevicePublicKey.TryParse(Convert.ToBase64String(epk), out _) || nonce is not { Length: NonceLength }
                || sealedBytes is not { Length: FleetMetadataKey.KeyLength + TagLength })
            {
                throw new InvalidDataException("The wrapped key does not read.");
            }

            var secret = identity.DeriveSharedSecret(epk);
            var associated = Associated(keyId, identity.DeviceId);
            var wrapping = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, FleetMetadataKey.KeyLength, SHA256.HashData(epk), associated);
            CryptographicOperations.ZeroMemory(secret);
            var key = new byte[FleetMetadataKey.KeyLength];
            try
            {
                using var aes = new AesGcm(wrapping, TagLength);
                aes.Decrypt(nonce, sealedBytes.AsSpan(0, FleetMetadataKey.KeyLength), sealedBytes.AsSpan(FleetMetadataKey.KeyLength), key, associated);
                return new FleetMetadataKey(keyId, key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapping);
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException)
        {
            throw new InvalidDataException("The wrapped key does not open with the key of this device.", exception);
        }
    }

    private static byte[] Associated(string keyId, string deviceId) => Encoding.UTF8.GetBytes($"{Domain}\n{Algorithm}\n{keyId}\n{deviceId}");
}

/// <summary>
/// The metadata key of this device's person at one server: it reads the copy wrapped to this device, and, on a
/// controller device, makes the key (the person's first controller device does) and wraps it to the person's devices
/// that lack it, taking their keys from the device list as the anchor of the link verified it
/// (<see cref="FleetOrganizationDevices"/>), never from the server's tables nor from a list that does not extend the
/// one this device verified before.
/// </summary>
/// <remarks>One caller at a time: the loop of a <see cref="FleetConnector"/>; <see cref="Current"/> is read from any thread.</remarks>
public sealed class FleetMetadataKeyring
{
    private readonly FleetOrganizationDevices _devices;
    private readonly DeviceIdentity _identity;
    private FleetMetadataKey? _current;

    /// <summary>Initializes a new instance of the <see cref="FleetMetadataKeyring"/> class.</summary>
    /// <param name="devices">The anchored device list of the server, whose key and HTTP handler the calls use too.</param>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is <see langword="null"/>.</exception>
    public FleetMetadataKeyring(FleetOrganizationDevices devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
        _identity = devices.Identity;
    }

    /// <summary>Gets the anchored device list the keyring wraps by.</summary>
    public FleetOrganizationDevices Devices => _devices;

    /// <summary>Gets the key of this device's person, once read; null before, or when none was given to this device yet.</summary>
    public FleetMetadataKey? Current => Volatile.Read(ref _current);

    /// <summary>
    /// Reads the key again: the copy of this device, and on a controller device, makes the key when the person has none
    /// and wraps it to the person's devices that lack it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The key, or null when this device has none yet.</returns>
    /// <exception cref="FleetDeviceListException">The device list does not verify, or does not extend the one this device verified: nothing is wrapped.</exception>
    /// <exception cref="InvalidDataException">The copy of this device does not open.</exception>
    /// <exception cref="FleetServerException">The server refused a call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetMetadataKey?> RefreshAsync(CancellationToken cancellationToken)
    {
        // The list first: nothing is read or wrapped through a server whose list does not extend the anchored one.
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        using var client = new FleetServerClient(_devices.ServerAddress, _identity, _devices.Handler) { DeviceList = _devices };
        var latest = (await client.ListMetadataKeysAsync(cancellationToken).ConfigureAwait(false)).MaxBy(static key => key.Epoch);
        var key = latest?.Wrapped is { } wrapped ? FleetKeyWrap.Unwrap(wrapped, latest.Id, _identity) : null;
        var me = _identity.DeviceId;
        if (list.IsController(me))
        {
            var person = list.Members[me].OwnerId;
            var devices = list.Members.Values.Where(member => member.DeviceId == me || person is not null && member.OwnerId == person).ToArray();
            if (latest is null)
            {
                // The person's first controller device makes the key; another one only once that device left the list.
                if (list.FirstDeviceId == me || list.FirstDeviceId is { } first && !list.Members.ContainsKey(first))
                {
                    var made = FleetMetadataKey.Generate();
                    try
                    {
                        await client.CreateMetadataKeyAsync(made.Id, 1, [.. devices.Select(device => new MetadataKeyWrapDto(device.DeviceId, FleetKeyWrap.Wrap(made, device.Key)))],
                            cancellationToken).ConfigureAwait(false);
                        key = made;
                    }
                    catch (FleetServerException exception) when (exception.Code == "metadata_key.exists")
                    {
                        // Another device made it first: its copy for this device is read at the next refresh.
                    }
                }
            }
            else if (key is not null && devices.Where(device => !latest.WrappedTo.Contains(device.DeviceId)).ToArray() is { Length: > 0 } missing)
            {
                await client.AddMetadataKeyWrapsAsync(latest.Id, [.. missing.Select(device => new MetadataKeyWrapDto(device.DeviceId, FleetKeyWrap.Wrap(key, device.Key)))],
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (key is not null && Current?.Id != key.Id) Volatile.Write(ref _current, key);
        return Current;
    }
}

/// <summary>The small JSON texts of sealed data, written and read without reflection.</summary>
internal static class FleetSealedJson
{
    public static byte[] Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static byte[]? Bytes(JsonElement element, string name)
        => Text(element, name) is { } text && Base64Url.IsValid(text) ? Base64Url.DecodeFromChars(text) : null;
}
