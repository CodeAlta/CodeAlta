using System.Buffers.Text;
using System.Globalization;
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
    private const string BlindIndexDomain = "codealta-blind-index/1:";

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

    /// <summary>Gets what the controller device that gave the key vouched for: its organization, person, epoch and device list; null for a key not given yet.</summary>
    public FleetKeyGrant? Grant { get; init; }

    /// <summary>Makes a new key, with a random id.</summary>
    /// <returns>The key.</returns>
    public static FleetMetadataKey Generate() => Generate(null);

    /// <summary>Makes a new key, with a random id, for a grant.</summary>
    /// <param name="grant">What the key is given for; null for none yet.</param>
    /// <returns>The key.</returns>
    public static FleetMetadataKey Generate(FleetKeyGrant? grant)
    {
        Span<byte> id = stackalloc byte[16];
        RandomNumberGenerator.Fill(id);
        Span<byte> key = stackalloc byte[KeyLength];
        RandomNumberGenerator.Fill(key);
        try
        {
            return new FleetMetadataKey(Base64Url.EncodeToString(id), key) { Grant = grant };
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

    /// <summary>
    /// Gives the blind index of a value: a keyed hash that the server can group by but not reverse, the same on every device
    /// of the person for the same value, and another for another purpose (a project key is not a session key).
    /// </summary>
    /// <param name="purpose">What the index is of, such as <c>project</c>.</param>
    /// <param name="value">The value, such as the repository key of a project.</param>
    /// <returns>The index: 22 characters of base64url (128 bits).</returns>
    /// <exception cref="ArgumentException"><paramref name="purpose"/> or <paramref name="value"/> is empty.</exception>
    public string BlindIndex(string purpose, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentException.ThrowIfNullOrEmpty(value);
        // A key of its own per purpose, derived from the metadata key: an index never reveals a seal, nor another index.
        Span<byte> derived = stackalloc byte[KeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, derived, salt: [], info: Encoding.UTF8.GetBytes(BlindIndexDomain + purpose));
        Span<byte> hash = stackalloc byte[32];
        try
        {
            HMACSHA256.HashData(derived, Encoding.UTF8.GetBytes(value), hash);
            return Base64Url.EncodeToString(hash[..16]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    /// <summary>Gets the bytes of the key, to wrap it to a device.</summary>
    internal ReadOnlySpan<byte> Material => _key;

    private static byte[] Associated(string keyId, string sessionId) => Encoding.UTF8.GetBytes($"{SealDomain}\n{SealAlgorithm}\n{keyId}\n{sessionId}");
}

/// <summary>
/// What a controller device vouches for when it gives a metadata key to a device (<c>doc/fleet/security-and-privacy.md</c>,
/// keys): the organization and the person the key is for, its epoch, and the sequence of the device list it gave the
/// key by. Each wrapped copy carries it, signed by that controller with the copy, so that a server can neither give a
/// device a key of its own, nor relabel a copy for another epoch, person or device.
/// </summary>
/// <param name="OrganizationId">The organization of the device list.</param>
/// <param name="PersonId">The person whose devices hold the key.</param>
/// <param name="Epoch">The epoch of the key, from 1; a removal from the device list makes a new one.</param>
/// <param name="DeviceListSequence">The sequence of the last entry of the device list the key was made by: the devices it was given to are those of that list.</param>
public sealed record FleetKeyGrant(string OrganizationId, string PersonId, int Epoch, long DeviceListSequence);

/// <summary>
/// Wraps a metadata key to one device (<c>doc/fleet/security-and-privacy.md</c>, keys): ECDH-ES on P-256 with an
/// ephemeral key and the device key, HKDF-SHA256, AES-256-GCM. Only the holder of the device key opens the copy; the
/// algorithm id travels in it so that other curves can come later. The copy carries its <see cref="FleetKeyGrant"/>
/// and the signature of the controller device that made it, which the recipient checks against the device list it
/// verified before it takes the key.
/// </summary>
public static class FleetKeyWrap
{
    /// <summary>The algorithm of a wrapped key.</summary>
    public const string Algorithm = "ecdh-es-p256-hkdf-sha256-a256gcm";

    private const string Domain = "codealta-key-wrap/1";
    private const string GrantDomain = "codealta-key-grant/1";
    private const int NonceLength = 12;
    private const int TagLength = 16;

    /// <summary>Wraps a key to a device, with the public key of that device from the verified device list, and signs the copy.</summary>
    /// <param name="key">The key.</param>
    /// <param name="recipient">The device's public key; its device id is the one the copy is for.</param>
    /// <param name="grant">What the copy is for: the organization, the person, the epoch and the device list.</param>
    /// <param name="signer">The key of the controller device that gives the copy.</param>
    /// <returns>The wrapped key: <c>{ "alg", "kid", "to", "org", "person", "epoch", "seq", "signer", "epk", "iv", "ct", "sig" }</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The grant names an empty organization or person, or one with a control character, or an epoch or a sequence below 1.</exception>
    public static string Wrap(FleetMetadataKey key, FleetDevicePublicKey recipient, FleetKeyGrant grant, DeviceIdentity signer)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(signer);
        if (!IsName(grant.OrganizationId) || !IsName(grant.PersonId) || grant.Epoch < 1 || grant.DeviceListSequence < 1)
            throw new ArgumentException("A grant names its organization and person, without control characters, and an epoch and a sequence from 1.", nameof(grant));
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
        var epkText = Base64Url.EncodeToString(epk);
        var ivText = Base64Url.EncodeToString(nonce);
        var ctText = Base64Url.EncodeToString(sealedBytes);
        var signature = signer.SignData(GrantBytes(key.Id, recipient.DeviceId, grant, signer.DeviceId, epkText, ivText, ctText));
        return Encoding.UTF8.GetString(FleetSealedJson.Write(writer =>
        {
            writer.WriteString("alg", Algorithm);
            writer.WriteString("kid", key.Id);
            writer.WriteString("to", recipient.DeviceId);
            writer.WriteString("org", grant.OrganizationId);
            writer.WriteString("person", grant.PersonId);
            writer.WriteNumber("epoch", grant.Epoch);
            writer.WriteNumber("seq", grant.DeviceListSequence);
            writer.WriteString("signer", signer.DeviceId);
            writer.WriteString("epk", epkText);
            writer.WriteString("iv", ivText);
            writer.WriteString("ct", ctText);
            writer.WriteString("sig", Base64Url.EncodeToString(signature));
        }));
    }

    /// <summary>
    /// Opens a copy of a key wrapped to this device, once its grant holds against the device list this device verified:
    /// signed by a controller device of the list of the same person as this device, for this device, this key, this
    /// epoch, the organization of the list and its person, by a list no newer than the one verified.
    /// </summary>
    /// <param name="wrapped">The wrapped key.</param>
    /// <param name="keyId">The id the key must have.</param>
    /// <param name="epoch">The epoch the server lists the key at, which the grant must name.</param>
    /// <param name="identity">The key of this device.</param>
    /// <param name="list">The device list as this device verified it (<see cref="FleetOrganizationDevices"/>).</param>
    /// <returns>The key, with its grant.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The copy is not for this device, this key or this epoch, its signer is not a controller device of this device's person, it uses an unknown algorithm, or it does not open.</exception>
    public static FleetMetadataKey Unwrap(string wrapped, string keyId, int epoch, DeviceIdentity identity, FleetDeviceListState list)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        ArgumentNullException.ThrowIfNull(keyId);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(list);
        try
        {
            using var document = JsonDocument.Parse(wrapped);
            var root = document.RootElement;
            if (FleetSealedJson.Text(root, "alg") != Algorithm) throw new InvalidDataException("The wrapped key uses an algorithm this version does not know.");
            if (FleetSealedJson.Text(root, "kid") != keyId || FleetSealedJson.Text(root, "to") != identity.DeviceId)
                throw new InvalidDataException("The wrapped key is not the copy of this key for this device.");
            var grant = ReadGrant(root) ?? throw new InvalidDataException("The wrapped key carries no signed grant.");
            if (grant.Epoch != epoch) throw new InvalidDataException("The wrapped key was given for another epoch than the one the server lists.");
            CheckGrant(root, keyId, grant, identity.DeviceId, list);
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
                return new FleetMetadataKey(keyId, key) { Grant = grant };
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

    // The fields of a grant are signed one per line: a name never holds a control character.
    private static bool IsName(string? text) => !string.IsNullOrEmpty(text) && text.Length <= 256 && !text.Any(char.IsControl);

    private static FleetKeyGrant? ReadGrant(JsonElement root)
        => FleetSealedJson.Text(root, "org") is { } organization && IsName(organization) && FleetSealedJson.Text(root, "person") is { } person && IsName(person)
            && FleetSealedJson.Integer(root, "epoch") is { } epoch and >= 1 and <= int.MaxValue && FleetSealedJson.Integer(root, "seq") is { } sequence and >= 1
                ? new FleetKeyGrant(organization, person, (int)epoch, sequence)
                : null;

    // The signer vouches for the copy: a controller device of the verified list, of this device's person, for this list.
    private static void CheckGrant(JsonElement root, string keyId, FleetKeyGrant grant, string deviceId, FleetDeviceListState list)
    {
        if (!list.Members.TryGetValue(deviceId, out var self)) throw new InvalidDataException("This device is not a member of the device list it verified.");
        if (FleetSealedJson.Text(root, "signer") is not { Length: > 0 } signerId || !list.Members.TryGetValue(signerId, out var signer)
            || signer.Kind != FleetDeviceKinds.Controller)
        {
            throw new InvalidDataException("The wrapped key was not given by a controller device of the device list.");
        }

        // The first device names its person by the first entry it signs: until then, it gives keys to itself only.
        if (signer.DeviceId != deviceId && (signer.OwnerId is null || signer.OwnerId != self.OwnerId))
            throw new InvalidDataException("The wrapped key was given by a controller device of another person.");
        if (self.OwnerId is not null && self.OwnerId != grant.PersonId || signer.OwnerId is not null && signer.OwnerId != grant.PersonId)
            throw new InvalidDataException("The wrapped key was given for another person.");
        if (list.OrganizationId is not null && list.OrganizationId != grant.OrganizationId) throw new InvalidDataException("The wrapped key was given for another organization.");
        if (grant.DeviceListSequence > list.Sequence) throw new InvalidDataException("The wrapped key names a device list newer than the one this device verified.");
        if (FleetSealedJson.Text(root, "sig") is not { Length: > 0 } signature
            || !signer.Key.Verify(GrantBytes(keyId, deviceId, grant, signerId, FleetSealedJson.Text(root, "epk") ?? "", FleetSealedJson.Text(root, "iv") ?? "",
                FleetSealedJson.Text(root, "ct") ?? ""), signature))
        {
            throw new InvalidDataException("The signature of the wrapped key does not verify with the key of its signer.");
        }
    }

    private static byte[] GrantBytes(string keyId, string deviceId, FleetKeyGrant grant, string signerId, string epk, string iv, string ct)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{GrantDomain}\n{Algorithm}\n{keyId}\n{deviceId}\n{grant.OrganizationId}\n{grant.PersonId}\n{grant.Epoch}\n{grant.DeviceListSequence}\n{signerId}\n{epk}\n{iv}\n{ct}"));

    private static byte[] Associated(string keyId, string deviceId) => Encoding.UTF8.GetBytes($"{Domain}\n{Algorithm}\n{keyId}\n{deviceId}");
}

/// <summary>
/// The metadata key of this device's person at one server: it reads the copies wrapped to this device and takes the
/// newest one a controller device of its person signed (<see cref="FleetKeyWrap.Unwrap"/>); on a controller device, it
/// makes the key (the person's first controller device does), makes a new epoch once a device was removed from the
/// list after the key was given, and wraps the key to the person's devices that lack it. The devices and their keys
/// come from the device list as the anchor of the link verified it (<see cref="FleetOrganizationDevices"/>), never from
/// the server's tables nor from a list that does not extend the one this device verified before.
/// </summary>
/// <remarks>
/// A key given before the last removal of the list may be held by the removed device: it seals nothing more, and
/// <see cref="Current"/> is null until the new epoch reaches this device, never an older key.
/// One caller at a time: the loop of a <see cref="FleetConnector"/>; <see cref="Current"/> is read from any thread.
/// </remarks>
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

    /// <summary>Gets the key that seals the titles of this device's person; null before it is read, when none was given to this device yet, or while a removal waits for its new epoch.</summary>
    public FleetMetadataKey? Current => Volatile.Read(ref _current);

    /// <summary>
    /// Reads the key again: the copies of this device, and on a controller device, makes the key when the person has
    /// none or a new epoch after a removal, and wraps it to the person's devices that lack it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The key that seals, or null when this device has none yet.</returns>
    /// <exception cref="FleetDeviceListException">The device list does not verify, or does not extend the one this device verified: nothing is read or wrapped.</exception>
    /// <exception cref="FleetServerException">The server refused a call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetMetadataKey?> RefreshAsync(CancellationToken cancellationToken)
    {
        // The list first: nothing is read or wrapped through a server whose list does not extend the anchored one.
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        using var client = new FleetServerClient(_devices.ServerAddress, _identity, _devices.Handler) { DeviceList = _devices };
        var keys = await client.ListMetadataKeysAsync(cancellationToken).ConfigureAwait(false);
        var newest = keys.MaxBy(static key => key.Epoch);
        var (key, held) = Open(keys, list);
        var me = _identity.DeviceId;
        if (list.IsController(me))
        {
            var self = list.Members[me];
            var devices = list.Members.Values.Where(member => member.DeviceId == me || self.OwnerId is not null && member.OwnerId == self.OwnerId).ToArray();
            var make = newest is null
                // The person's first controller device makes the key; another one only once that device left the list.
                ? list.FirstDeviceId == me || list.FirstDeviceId is { } first && !list.Members.ContainsKey(first)
                // A new epoch when a device left after the key was given, or when no copy of this device holds (its maker left the list).
                : key is null ? newest.Wrapped is not null : IsStale(key, list);
            if (make)
            {
                var made = await MakeAsync(client, list, self, devices, (newest?.Epoch ?? 0) + 1, cancellationToken).ConfigureAwait(false);
                if (made is not null) (key, held) = (made, null);
            }
            else if (key is not null && held is not null && devices.Where(device => !held.WrappedTo.Contains(device.DeviceId)).ToArray() is { Length: > 0 } missing)
            {
                await client.AddMetadataKeyWrapsAsync(held.Id, [.. missing.Select(device => new MetadataKeyWrapDto(device.DeviceId, FleetKeyWrap.Wrap(key, device.Key, key.Grant!, _identity)))],
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // Never back to an older epoch, nor to a key the removed device may hold.
        var current = Current;
        if (key is not null && !IsStale(key, list) && (current is null || key.Grant!.Epoch >= current.Grant!.Epoch)) current = key;
        if (current is not null && IsStale(current, list)) current = null;
        Volatile.Write(ref _current, current);
        return current;
    }

    // The newest copy of this device whose grant holds; a copy the server made or relabelled is passed over.
    private (FleetMetadataKey? Key, MetadataKeyDto? Held) Open(IReadOnlyList<MetadataKeyDto> keys, FleetDeviceListState list)
    {
        foreach (var candidate in keys.OrderByDescending(static key => key.Epoch))
        {
            if (candidate.Wrapped is not { } wrapped) continue;
            try
            {
                return (FleetKeyWrap.Unwrap(wrapped, candidate.Id, candidate.Epoch, _identity, list), candidate);
            }
            catch (InvalidDataException)
            {
                // Not a copy a controller device of this person gave: refused.
            }
        }

        return (null, null);
    }

    private async Task<FleetMetadataKey?> MakeAsync(FleetServerClient client, FleetDeviceListState list, FleetDeviceListMember self, IReadOnlyList<FleetDeviceListMember> devices,
        int epoch, CancellationToken cancellationToken)
    {
        // The list names the person of the first device once it signed an entry; before, the server's tables do.
        var person = self.OwnerId ?? (await client.GetDeviceAsync(null, cancellationToken).ConfigureAwait(false)).OwnerId;
        if (person is null) return null;
        var organization = list.OrganizationId ?? (await client.GetOrganizationAsync(cancellationToken).ConfigureAwait(false)).Id;
        var made = FleetMetadataKey.Generate(new FleetKeyGrant(organization, person, epoch, list.Sequence));
        try
        {
            await client.CreateMetadataKeyAsync(made.Id, epoch, [.. devices.Select(device => new MetadataKeyWrapDto(device.DeviceId, FleetKeyWrap.Wrap(made, device.Key, made.Grant!, _identity)))],
                cancellationToken).ConfigureAwait(false);
            return made;
        }
        catch (FleetServerException exception) when (exception.Code == "metadata_key.exists")
        {
            // Another device made it first: its copy for this device is read at the next refresh.
            return null;
        }
    }

    private static bool IsStale(FleetMetadataKey key, FleetDeviceListState list) => key.Grant is not { } grant || grant.DeviceListSequence < list.LastRemovalSequence;
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

    public static long? Integer(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    public static byte[]? Bytes(JsonElement element, string name)
        => Text(element, name) is { } text && Base64Url.IsValid(text) ? Base64Url.DecodeFromChars(text) : null;
}
