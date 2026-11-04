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
    private const string SpaceDomain = "codealta-sealed-space/1";
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
        return SealText(SealDomain, sessionId, FleetSealedJson.Write(writer =>
        {
            if (metadata.Title is not null) writer.WriteString("title", metadata.Title);
        }));
    }

    /// <summary>Gets the id of the key a sealed text names, without opening it.</summary>
    /// <param name="sealedText">The sealed text.</param>
    /// <returns>The id of its key, or <see langword="null"/> when the text does not read.</returns>
    public static string? KeyIdOf(string? sealedText)
    {
        if (string.IsNullOrEmpty(sealedText) || sealedText.Length > 65536) return null;
        try
        {
            using var document = JsonDocument.Parse(sealedText);
            return FleetSealedJson.Text(document.RootElement, "kid");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string SealText(string domain, string binding, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var sealedBytes = new byte[plain.Length + TagLength];
        using (var aes = new AesGcm(_key, TagLength))
        {
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), Associated(domain, Id, binding));
        }

        return Encoding.UTF8.GetString(FleetSealedJson.Write(writer =>
        {
            writer.WriteString("alg", SealAlgorithm);
            writer.WriteString("kid", Id);
            writer.WriteString("iv", Base64Url.EncodeToString(nonce));
            writer.WriteString("ct", Base64Url.EncodeToString(sealedBytes));
        }));
    }

    // The plain bytes, when this key sealed the text for this binding; null for another key, binding, a changed text or an unknown algorithm.
    private byte[]? OpenText(string domain, string binding, string? sealedText)
    {
        if (string.IsNullOrEmpty(sealedText) || sealedText.Length > 65536) return null;
        try
        {
            using var document = JsonDocument.Parse(sealedText);
            var root = document.RootElement;
            if (FleetSealedJson.Text(root, "alg") != SealAlgorithm || FleetSealedJson.Text(root, "kid") != Id) return null;
            var nonce = FleetSealedJson.Bytes(root, "iv");
            var sealedBytes = FleetSealedJson.Bytes(root, "ct");
            if (nonce is not { Length: NonceLength } || sealedBytes is not { Length: >= TagLength }) return null;
            var plain = new byte[sealedBytes.Length - TagLength];
            using var aes = new AesGcm(_key, TagLength);
            aes.Decrypt(nonce, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), plain, Associated(domain, Id, binding));
            return plain;
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Opens the sealed metadata of a session, when this key sealed it for this session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="sealedText">The sealed text, as the server gave it.</param>
    /// <param name="metadata">The metadata, or <see langword="null"/>.</param>
    /// <returns>Whether the text opened: another key, another session, a changed text or an unknown algorithm do not.</returns>
    public bool TryOpen(string sessionId, string? sealedText, out FleetSessionMetadata? metadata)
    {
        metadata = null;
        if (string.IsNullOrEmpty(sessionId) || OpenText(SealDomain, sessionId, sealedText) is not { } plain) return false;
        try
        {
            using var opened = JsonDocument.Parse(plain);
            metadata = new FleetSessionMetadata(FleetSealedJson.Text(opened.RootElement, "title"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Seals which space a key is for on the devices of its person (the id of the space there, such as <c>default</c>),
    /// bound to the opaque id the server knows the space by, so that a server can neither read it nor move it to another space.
    /// </summary>
    /// <param name="spaceId">The opaque id of the space at the server.</param>
    /// <param name="localSpace">The id of the space on the devices.</param>
    /// <returns>The sealed text.</returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    public string SealSpace(string spaceId, string localSpace)
    {
        ArgumentException.ThrowIfNullOrEmpty(spaceId);
        ArgumentException.ThrowIfNullOrEmpty(localSpace);
        return SealText(SpaceDomain, spaceId, FleetSealedJson.Write(writer => writer.WriteString("space", localSpace)));
    }

    /// <summary>Opens what <see cref="SealSpace"/> sealed, when this key sealed it for this space.</summary>
    /// <param name="spaceId">The opaque id of the space at the server.</param>
    /// <param name="sealedText">The sealed text, as the server gave it.</param>
    /// <param name="localSpace">The id of the space on the devices, or <see langword="null"/>.</param>
    /// <returns>Whether the text opened.</returns>
    public bool TryOpenSpace(string spaceId, string? sealedText, out string? localSpace)
    {
        localSpace = null;
        if (string.IsNullOrEmpty(spaceId) || OpenText(SpaceDomain, spaceId, sealedText) is not { } plain) return false;
        try
        {
            using var opened = JsonDocument.Parse(plain);
            localSpace = FleetSealedJson.Text(opened.RootElement, "space");
            return !string.IsNullOrEmpty(localSpace);
        }
        catch (JsonException)
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

    private static byte[] Associated(string domain, string keyId, string binding) => Encoding.UTF8.GetBytes($"{domain}\n{SealAlgorithm}\n{keyId}\n{binding}");
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
public sealed record FleetKeyGrant(string OrganizationId, string PersonId, int Epoch, long DeviceListSequence)
{
    /// <summary>
    /// Gets the opaque id of the space the key is for, signed with the grant so that a server cannot give the key of one
    /// space as the key of another; null for a key of the person, made before spaces had keys.
    /// </summary>
    public string? SpaceId { get; init; }
}

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
    private const string SpaceGrantDomain = "codealta-space-key-grant/1";
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
        if (!IsName(grant.OrganizationId) || !IsName(grant.PersonId) || grant.Epoch < 1 || grant.DeviceListSequence < 1 || grant.SpaceId is not null && !IsName(grant.SpaceId))
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
            if (grant.SpaceId is not null) writer.WriteString("space", grant.SpaceId);
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
    /// epoch, the organization of the list and its person, by a list no newer than the one verified. The signer is a
    /// controller of the list at an entry from the one the grant names on (<see cref="FleetDeviceListState.MemberFrom"/>):
    /// the copies a controller gave before it left the list still open; the key is then stale and seals nothing new.
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
        => Unwrap(wrapped, keyId, epoch, null, identity, list);

    /// <summary>
    /// Opens a copy of the key of a space wrapped to this device, once its grant holds against the device list this
    /// device verified (<see cref="Unwrap(string, string, int, DeviceIdentity, FleetDeviceListState)"/>) and names the
    /// space the server lists the key for.
    /// </summary>
    /// <param name="wrapped">The wrapped key.</param>
    /// <param name="keyId">The id the key must have.</param>
    /// <param name="epoch">The epoch the server lists the key at, which the grant must name.</param>
    /// <param name="space">The space the server lists the key for, which the grant must name; null for a key of the person.</param>
    /// <param name="identity">The key of this device.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <returns>The key, with its grant.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The copy is not for this device, this key, this epoch or this space, its signer is not a controller device of this device's person, it uses an unknown algorithm, or it does not open.</exception>
    public static FleetMetadataKey Unwrap(string wrapped, string keyId, int epoch, string? space, DeviceIdentity identity, FleetDeviceListState list)
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
            if (!string.Equals(grant.SpaceId, space, StringComparison.Ordinal)) throw new InvalidDataException("The wrapped key was given for another space than the one the server lists.");
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

    /// <summary>
    /// Tells, without opening it, whether a copy of a key was given to a device by a controller device of that device's
    /// person, by the device list this device verified, for this key, epoch and space: what a controller device checks of
    /// the copies other devices hold, so that it trusts only the holders a controller gave the key to.
    /// </summary>
    /// <param name="wrapped">The wrapped key.</param>
    /// <param name="keyId">The id the key must have.</param>
    /// <param name="epoch">The epoch the grant must name.</param>
    /// <param name="space">The space the grant must name; null for a key of the person.</param>
    /// <param name="deviceId">The device the copy must be for.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <returns>Whether the grant holds.</returns>
    /// <exception cref="ArgumentNullException">An argument but <paramref name="space"/> is null.</exception>
    public static bool IsGranted(string wrapped, string keyId, int epoch, string? space, string deviceId, FleetDeviceListState list)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        ArgumentNullException.ThrowIfNull(keyId);
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(list);
        try
        {
            using var document = JsonDocument.Parse(wrapped);
            var root = document.RootElement;
            if (FleetSealedJson.Text(root, "alg") != Algorithm || FleetSealedJson.Text(root, "kid") != keyId || FleetSealedJson.Text(root, "to") != deviceId) return false;
            if (ReadGrant(root) is not { } grant || grant.Epoch != epoch || !string.Equals(grant.SpaceId, space, StringComparison.Ordinal)) return false;
            CheckGrant(root, keyId, grant, deviceId, list);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or CryptographicException or FormatException)
        {
            return false;
        }
    }

    // The fields of a grant are signed one per line: a name never holds a control character.
    private static bool IsName(string? text) => !string.IsNullOrEmpty(text) && text.Length <= 256 && !text.Any(char.IsControl);

    private static FleetKeyGrant? ReadGrant(JsonElement root)
        => FleetSealedJson.Text(root, "org") is { } organization && IsName(organization) && FleetSealedJson.Text(root, "person") is { } person && IsName(person)
            && FleetSealedJson.Integer(root, "epoch") is { } epoch and >= 1 and <= int.MaxValue && FleetSealedJson.Integer(root, "seq") is { } sequence and >= 1
            && FleetSealedJson.Text(root, "space") is var space && (space is null || IsName(space))
                ? new FleetKeyGrant(organization, person, (int)epoch, sequence) { SpaceId = space }
                : null;

    // The signer vouches for the copy: a controller device of the verified list, of this device's person, for this list.
    private static void CheckGrant(JsonElement root, string keyId, FleetKeyGrant grant, string deviceId, FleetDeviceListState list)
    {
        if (!list.Members.TryGetValue(deviceId, out var self)) throw new InvalidDataException("This device is not a member of the device list it verified.");
        // A controller device gives keys; the recovery key gives the copies it holds to the device it signed in. The signer is
        // read from the history of the list: one that left later still vouches for the copies it gave by the list of an entry
        // before its removal (they stay readable, and are stale: they seal nothing new), never for a list from its removal on.
        if (FleetSealedJson.Text(root, "signer") is not { Length: > 0 } signerId || list.MemberFrom(signerId, grant.DeviceListSequence) is not { } signer
            || signer.Kind is not (FleetDeviceKinds.Controller or FleetDeviceKinds.Recovery))
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

    // A grant of a space signs its space too, under a domain of its own: a grant of a person's key never reads as one of a space.
    private static byte[] GrantBytes(string keyId, string deviceId, FleetKeyGrant grant, string signerId, string epk, string iv, string ct)
        => Encoding.UTF8.GetBytes(grant.SpaceId is null
            ? string.Create(CultureInfo.InvariantCulture,
                $"{GrantDomain}\n{Algorithm}\n{keyId}\n{deviceId}\n{grant.OrganizationId}\n{grant.PersonId}\n{grant.Epoch}\n{grant.DeviceListSequence}\n{signerId}\n{epk}\n{iv}\n{ct}")
            : string.Create(CultureInfo.InvariantCulture,
                $"{SpaceGrantDomain}\n{Algorithm}\n{keyId}\n{deviceId}\n{grant.OrganizationId}\n{grant.PersonId}\n{grant.SpaceId}\n{grant.Epoch}\n{grant.DeviceListSequence}\n{signerId}\n{epk}\n{iv}\n{ct}"));

    private static byte[] Associated(string keyId, string deviceId) => Encoding.UTF8.GetBytes($"{Domain}\n{Algorithm}\n{keyId}\n{deviceId}");
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
