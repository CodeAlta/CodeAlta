using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The keys of a grant of sealed content (<c>doc/fleet/access-control.md</c>, access and keys): granting a person or a team
/// <c>session.view-content</c> on a session of a Private space gives them exactly that session's metadata, never a key that opens
/// more. A controller device of the owner of the session opens the session's metadata with the key of its space, seals it again
/// with a fresh key made for the grant, and wraps that key to each device of the grantee (ECDH-ES on P-256, HKDF-SHA256,
/// AES-256-GCM), signing each copy. The server keeps the copies and cannot open them; a grant taken back drops them.
/// </summary>
public static class FleetGrantKeys
{
    private const string Domain = "codealta-grant-key/1";
    private const string SignDomain = "codealta-grant-key-sign/1";
    private const int NonceLength = 12;
    private const int TagLength = 16;

    /// <summary>Seals the metadata of a session for a grant and wraps its key to each device of the grantee.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="grantId">The grant.</param>
    /// <param name="sessionId">The session it opens.</param>
    /// <param name="metadata">The metadata of the session, as the owner's device opened it.</param>
    /// <param name="recipients">The devices of the grantee, from the device list this device verified.</param>
    /// <param name="listSequence">The entry of the device list this device read.</param>
    /// <param name="signer">The controller device of the owner of the session.</param>
    /// <returns>The sealed metadata, and the copy of its key for each device.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">An id is empty or no device receives the key.</exception>
    public static (string Sealed, IReadOnlyList<(string DeviceId, string Wrapped)> Wraps) Seal(string organizationId, string grantId, string sessionId,
        FleetSessionMetadata metadata, IReadOnlyList<FleetDevicePublicKey> recipients, long listSequence, DeviceIdentity signer)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(grantId);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(signer);
        if (recipients.Count == 0) throw new ArgumentException("A grant gives its key to at least one device.", nameof(recipients));
        var key = FleetMetadataKey.Generate();
        var sealedText = key.Seal(sessionId, metadata);
        var wraps = new List<(string, string)>(recipients.Count);
        foreach (var recipient in recipients) wraps.Add((recipient.DeviceId, Wrap(key, recipient, organizationId, grantId, sessionId, listSequence, signer)));
        return (sealedText, wraps);
    }

    /// <summary>
    /// Opens the metadata a grant gives this device, once the copy holds against the device list this device verified: for this
    /// device, this grant and this session, signed by a controller device of the person who owns the machine of the session.
    /// </summary>
    /// <param name="sealedText">The sealed metadata.</param>
    /// <param name="wrapped">The copy of its key for this device.</param>
    /// <param name="organizationId">The organization.</param>
    /// <param name="grantId">The grant.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="machineId">The machine of the session, whose owner signs the copy.</param>
    /// <param name="identity">The key of this device.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <returns>The metadata.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidDataException">The copy is not for this device, grant or session, is not signed by the owner of the session, or does not open.</exception>
    public static FleetSessionMetadata Open(string sealedText, string wrapped, string organizationId, string grantId, string sessionId, string machineId, DeviceIdentity identity,
        FleetDeviceListState list)
    {
        ArgumentNullException.ThrowIfNull(sealedText);
        ArgumentNullException.ThrowIfNull(wrapped);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(list);
        try
        {
            using var document = JsonDocument.Parse(wrapped);
            var root = document.RootElement;
            string Field(string name) => FleetSealedJson.Text(root, name) ?? throw new InvalidDataException($"The copy of the key of the grant has no {name}.");
            if (Field("alg") != FleetKeyWrap.Algorithm) throw new InvalidDataException("The copy of the key of the grant uses an algorithm this version does not know.");
            var keyId = Field("kid");
            if (Field("to") != identity.DeviceId || Field("org") != organizationId || Field("grant") != grantId || Field("session") != sessionId)
                throw new InvalidDataException("The copy of the key is not the one of this grant and session for this device.");
            var sequence = FleetSealedJson.Integer(root, "seq") ?? 0;
            if (sequence < 1 || sequence > list.Sequence) throw new InvalidDataException("The copy names a device list this device does not hold.");
            var signerId = Field("signer");
            if (list.MemberFrom(signerId, sequence) is not { Kind: FleetDeviceKinds.Controller } signer)
                throw new InvalidDataException("The copy was not given by a controller device of the device list.");
            if (!list.Members.TryGetValue(machineId, out var machine) || machine.OwnerId is null || machine.OwnerId != signer.OwnerId)
                throw new InvalidDataException("The copy was not given by a device of the owner of the session.");
            var epkText = Field("epk");
            var ivText = Field("iv");
            var ctText = Field("ct");
            if (!signer.Key.Verify(SignedBytes(keyId, identity.DeviceId, organizationId, grantId, sessionId, sequence, signerId, epkText, ivText, ctText), Field("sig")))
                throw new InvalidDataException("The signature of the copy does not verify with the key of its signer.");
            var epk = Base64Url.DecodeFromChars(epkText);
            var nonce = Base64Url.DecodeFromChars(ivText);
            var sealedBytes = Base64Url.DecodeFromChars(ctText);
            if (nonce.Length != NonceLength || sealedBytes.Length != FleetMetadataKey.KeyLength + TagLength) throw new InvalidDataException("The copy of the key does not read.");
            var secret = identity.DeriveSharedSecret(epk);
            var associated = Associated(keyId, identity.DeviceId, grantId, sessionId);
            var wrapping = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, FleetMetadataKey.KeyLength, SHA256.HashData(epk), associated);
            CryptographicOperations.ZeroMemory(secret);
            var material = new byte[FleetMetadataKey.KeyLength];
            try
            {
                using (var aes = new AesGcm(wrapping, TagLength))
                    aes.Decrypt(nonce, sealedBytes.AsSpan(0, FleetMetadataKey.KeyLength), sealedBytes.AsSpan(FleetMetadataKey.KeyLength), material, associated);
                var key = new FleetMetadataKey(keyId, material);
                return key.TryOpen(sessionId, sealedText, out var metadata) && metadata is not null
                    ? metadata
                    : throw new InvalidDataException("The metadata of the grant does not open with its key.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapping);
                CryptographicOperations.ZeroMemory(material);
            }
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException)
        {
            throw new InvalidDataException("The copy of the key of the grant does not open with the key of this device.", exception);
        }
    }

    /// <summary>
    /// Gives a grant that waits for its keys what it opens, from a controller device of the owner of the content: for a grant of a
    /// session, opens the session's metadata here with the key of its space, seals it for the grant and wraps that key to each
    /// controller device of the grantee in the device list this device verified, then sends them; other grants are confirmed as such.
    /// </summary>
    /// <param name="client">The client of the server, as this device.</param>
    /// <param name="keys">The metadata keys this device holds.</param>
    /// <param name="grantId">The grant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The grant, active.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="grantId"/> is empty.</exception>
    /// <exception cref="KeyNotFoundException">No grant or session has this id.</exception>
    /// <exception cref="InvalidOperationException">This device does not hold the key of the session, or the grantee has no device.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    public static async Task<GrantDto> ConfirmAsync(FleetServerClient client, FleetMetadataKeyring keys, string grantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrEmpty(grantId);
        var grant = (await client.ListGrantsAsync(cancellationToken).ConfigureAwait(false)).Items.FirstOrDefault(other => other.Id == grantId)
            ?? throw new KeyNotFoundException("No grant has this id.");
        if (grant.Capability != "session.view-content" || grant.ScopeId is not { } scope || !scope.StartsWith(SessionScope, StringComparison.Ordinal))
            return await client.ConfirmGrantKeysAsync(grantId, cancellationToken).ConfigureAwait(false);
        var sessionId = scope[SessionScope.Length..];
        var session = (await client.ListSessionsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(other => other.Id == sessionId)
            ?? throw new KeyNotFoundException("The session of the grant is not published.");
        if (!keys.TryOpen(sessionId, session.SealedMetadata, out var metadata) || metadata is null)
            throw new InvalidOperationException("This computer does not hold the key of the session: confirm the grant from a computer of its owner that opens it.");

        var people = grant.SubjectKind == FleetPrincipalKinds.Team
            ? (await client.ListTeamsAsync(cancellationToken).ConfigureAwait(false)).Items.FirstOrDefault(team => team.Id == grant.SubjectId)?.Members
                .Select(static member => member.PersonId).ToHashSet(StringComparer.Ordinal) ?? []
            : new HashSet<string>([grant.SubjectId], StringComparer.Ordinal);
        var list = await keys.Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var recipients = list.Members.Values.Where(member => member.Kind == FleetDeviceKinds.Controller && member.OwnerId is { } owner && people.Contains(owner))
            .Select(static member => member.Key).ToArray();
        if (recipients.Length == 0) throw new InvalidOperationException("The grantee has no device in the device list yet.");
        var (sealedText, wraps) = Seal(list.OrganizationId ?? string.Empty, grantId, sessionId, metadata, recipients, list.Sequence, keys.Devices.Identity);
        return await client.ConfirmGrantKeysAsync(grantId, new ConfirmGrantKeysRequest(sealedText, [.. wraps.Select(static wrap => new GrantKeyWrapDto(wrap.DeviceId, wrap.Wrapped))]),
            cancellationToken).ConfigureAwait(false);
    }

    private const string SessionScope = "session:";

    private static string Wrap(FleetMetadataKey key, FleetDevicePublicKey recipient, string organizationId, string grantId, string sessionId, long sequence, DeviceIdentity signer)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(recipient.ExportSubjectPublicKeyInfo(), out _);
        var secret = ephemeral.DeriveRawSecretAgreement(peer.PublicKey);
        var epk = ephemeral.ExportSubjectPublicKeyInfo();
        var associated = Associated(key.Id, recipient.DeviceId, grantId, sessionId);
        var wrapping = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, FleetMetadataKey.KeyLength, SHA256.HashData(epk), associated);
        CryptographicOperations.ZeroMemory(secret);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var sealedBytes = new byte[FleetMetadataKey.KeyLength + TagLength];
        using (var aes = new AesGcm(wrapping, TagLength))
            aes.Encrypt(nonce, key.Material, sealedBytes.AsSpan(0, FleetMetadataKey.KeyLength), sealedBytes.AsSpan(FleetMetadataKey.KeyLength), associated);
        CryptographicOperations.ZeroMemory(wrapping);
        var (epkText, ivText, ctText) = (Base64Url.EncodeToString(epk), Base64Url.EncodeToString(nonce), Base64Url.EncodeToString(sealedBytes));
        var signature = signer.SignData(SignedBytes(key.Id, recipient.DeviceId, organizationId, grantId, sessionId, sequence, signer.DeviceId, epkText, ivText, ctText));
        return Encoding.UTF8.GetString(FleetSealedJson.Write(writer =>
        {
            writer.WriteString("alg", FleetKeyWrap.Algorithm);
            writer.WriteString("kid", key.Id);
            writer.WriteString("to", recipient.DeviceId);
            writer.WriteString("org", organizationId);
            writer.WriteString("grant", grantId);
            writer.WriteString("session", sessionId);
            writer.WriteNumber("seq", sequence);
            writer.WriteString("signer", signer.DeviceId);
            writer.WriteString("epk", epkText);
            writer.WriteString("iv", ivText);
            writer.WriteString("ct", ctText);
            writer.WriteString("sig", Base64Url.EncodeToString(signature));
        }));
    }

    private static byte[] SignedBytes(string keyId, string deviceId, string organizationId, string grantId, string sessionId, long sequence, string signerId, string epk, string iv,
        string ct)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{SignDomain}\n{FleetKeyWrap.Algorithm}\n{keyId}\n{deviceId}\n{organizationId}\n{grantId}\n{sessionId}\n{sequence}\n{signerId}\n{epk}\n{iv}\n{ct}"));

    private static byte[] Associated(string keyId, string deviceId, string grantId, string sessionId)
        => Encoding.UTF8.GetBytes($"{Domain}\n{FleetKeyWrap.Algorithm}\n{keyId}\n{deviceId}\n{grantId}\n{sessionId}");
}
