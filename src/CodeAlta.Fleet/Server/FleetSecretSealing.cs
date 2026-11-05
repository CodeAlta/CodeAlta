using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// What a sealed copy of a named value is bound to: its id, kind, name, scope, version and whether it is host-only, as
/// the server lists them. A copy opens only for the same binding, so that a server can neither move a value to another
/// name, scope or version, nor make a host-only value reach the agent's shell.
/// </summary>
/// <param name="SecretId">The id of the value.</param>
/// <param name="Kind">Its kind (<see cref="FleetSecretKinds"/>).</param>
/// <param name="Name">Its name.</param>
/// <param name="ScopeKind">The kind of its scope (<see cref="FleetSecretScopeKinds"/>).</param>
/// <param name="ScopeId">The id of its scope; null for the person.</param>
/// <param name="Version">Its version.</param>
/// <param name="HostOnly">Whether it goes only to host-side tools.</param>
public sealed record FleetSecretBinding(string SecretId, string Kind, string Name, string ScopeKind, string? ScopeId, int Version, bool HostOnly)
{
    /// <summary>Gets the binding of a value as the server lists it.</summary>
    /// <param name="secret">The value.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="secret"/> is null.</exception>
    public static FleetSecretBinding Of(FleetSecretDto secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return new(secret.Id, secret.Kind, secret.Name, secret.Scope.Kind, secret.Scope.Id, secret.Version, secret.HostOnly);
    }
}

/// <summary>
/// Seals the named values of the fleet end to end (<c>doc/fleet/secrets.md</c>, where values live): the controller device
/// of the person who sets a value encrypts it with a key of its own per version (AES-256-GCM, bound to the value's
/// <see cref="FleetSecretBinding"/>), and wraps that key to each device allowed to use it and to the person's controller
/// devices, as metadata keys are wrapped (<see cref="FleetKeyWrap"/>), with a grant signed by that controller device. The
/// server stores and relays the copies only: it can neither read a value nor give one to a machine, since a copy it made
/// carries no grant a controller device of the person signed.
/// </summary>
public static class FleetSecretSealing
{
    /// <summary>The algorithm of a sealed value.</summary>
    public const string Algorithm = "a256gcm";

    /// <summary>The longest value sealed, in characters.</summary>
    public const int MaxValueLength = 64 * 1024;

    /// <summary>The longest sealed copy, in characters.</summary>
    public const int MaxCopyLength = 128 * 1024;

    /// <summary>The most devices one value is sealed for.</summary>
    public const int MaxRecipients = 64;

    private const string Domain = "codealta-fleet-secret/1";
    private const string GrantPrefix = "secret/";
    private const int NonceLength = 12;
    private const int TagLength = 16;

    /// <summary>Makes the id of a new value: 16 random bytes in base64url.</summary>
    /// <returns>The id.</returns>
    public static string NewId() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Gives the devices a value of a person goes to: the machines it names that are members of the list, of that person,
    /// and the person's controller devices, which manage it; never a device the list removed.
    /// </summary>
    /// <param name="list">The device list as the signer verified it.</param>
    /// <param name="personId">The person.</param>
    /// <param name="machines">The machines allowed to use the value.</param>
    /// <returns>The devices, the controller devices first.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<FleetDeviceListMember> Recipients(FleetDeviceListState list, string personId, IEnumerable<string> machines)
        => Recipients(list, null, personId, machines);

    /// <summary>
    /// Gives the devices a value of a person goes to, as <see cref="Recipients(FleetDeviceListState, string, IEnumerable{string})"/>
    /// does, with the machines the signed access document gives that person (<see cref="FleetAccessState.PersonOf"/>) and the
    /// machines of the teams they maintain and, for an owner or an admin who signs, those of the organization
    /// (<see cref="FleetAccessState.HoldsValuesOf"/>), and without the machines it gives
    /// another person or a team they do not maintain.
    /// </summary>
    /// <param name="list">The device list as the signer verified it.</param>
    /// <param name="access">The access document as the signer verified it; null for none.</param>
    /// <param name="personId">The person.</param>
    /// <param name="machines">The machines allowed to use the value.</param>
    /// <returns>The devices, the controller devices first.</returns>
    /// <exception cref="ArgumentNullException">An argument but <paramref name="access"/> is null.</exception>
    public static IReadOnlyList<FleetDeviceListMember> Recipients(FleetDeviceListState list, FleetAccessState? access, string personId, IEnumerable<string> machines)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(personId);
        ArgumentNullException.ThrowIfNull(machines);
        var wanted = machines.ToHashSet(StringComparer.Ordinal);
        return [.. list.Members.Values
            .Where(member => !list.Removed.Contains(member.DeviceId) && FleetAccessState.HoldsValuesOf(access, member, personId)
                && (member.Kind == FleetDeviceKinds.Controller || wanted.Contains(member.DeviceId) && member.Kind != FleetDeviceKinds.Recovery))
            .OrderBy(static member => member.Kind == FleetDeviceKinds.Controller ? 0 : 1).ThenBy(static member => member.DeviceId, StringComparer.Ordinal)
            .Take(MaxRecipients)];
    }

    /// <summary>
    /// Gives the holders of the current version of a value that a controller device of the person gave it to: those whose
    /// copy carries a grant a controller device signed, for this value and version and the key of the copy of this device.
    /// The list of holders the server gives is not taken: a server could name there any machine of the person.
    /// </summary>
    /// <param name="secret">The value as the server lists it for a controller device, with the copies of its holders.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <returns>The device ids; none when this device holds no copy or the server gives no copies of the holders.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> GrantedHolders(FleetSecretDto secret, FleetDeviceListState list) => GrantedHolders(secret, list, null);

    /// <summary>
    /// Gives the holders of the current version of a value that a controller device of the person gave it to, as
    /// <see cref="GrantedHolders(FleetSecretDto, FleetDeviceListState)"/> does, a machine being of the person the signed access
    /// document names once it was given to another person.
    /// </summary>
    /// <param name="secret">The value as the server lists it for a controller device, with the copies of its holders.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <param name="access">The access document as this device verified it; null for none.</param>
    /// <returns>The device ids; none when this device holds no copy or the server gives no copies of the holders.</returns>
    /// <exception cref="ArgumentNullException">An argument but <paramref name="access"/> is null.</exception>
    public static IReadOnlyList<string> GrantedHolders(FleetSecretDto secret, FleetDeviceListState list, FleetAccessState? access)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(list);
        if (secret.HolderCopies is not { } copies || KeyIdOf(secret.Copy) is not { } keyId) return [];
        var granted = new List<string>(copies.Count);
        foreach (var copy in copies)
        {
            if (copy?.DeviceId is not { Length: > 0 } deviceId || copy.Sealed is not { Length: > 0 and <= MaxCopyLength } sealedCopy) continue;
            try
            {
                using var document = JsonDocument.Parse(sealedCopy);
                var root = document.RootElement;
                if (FleetSealedJson.Text(root, "alg") == Algorithm && FleetSealedJson.Text(root, "kid") == keyId && FleetSealedJson.Text(root, "wrap") is { } wrapped
                    && FleetKeyWrap.IsGranted(wrapped, keyId, secret.Version, GrantScope(secret.Id), deviceId, list, access))
                    granted.Add(deviceId);
            }
            catch (JsonException)
            {
                // A copy that does not read names no holder.
            }
        }

        return [.. granted.Distinct(StringComparer.Ordinal)];
    }

    // The id of the key of a copy: every copy of one version is of the same key.
    private static string? KeyIdOf(string? copy)
    {
        if (copy is not { Length: > 0 and <= MaxCopyLength }) return null;
        try
        {
            using var document = JsonDocument.Parse(copy);
            return FleetSealedJson.Text(document.RootElement, "kid") is { Length: > 0 } keyId ? keyId : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Seals a value for devices of the list, one copy each.</summary>
    /// <param name="value">The value.</param>
    /// <param name="binding">What the copies are bound to.</param>
    /// <param name="recipients">The devices (<see cref="Recipients(FleetDeviceListState, string, IEnumerable{string})"/>), which must be of the signer's person.</param>
    /// <param name="list">The device list as the signer verified it.</param>
    /// <param name="signer">The key of the controller device that seals and signs the grants.</param>
    /// <returns>The copies.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The value is empty or too long, the signer is not a controller device of the list with its person, a recipient is of another person, or the list names no organization.</exception>
    public static IReadOnlyList<FleetSecretCopyDto> Seal(string value, FleetSecretBinding binding, IReadOnlyList<FleetDeviceListMember> recipients, FleetDeviceListState list,
        DeviceIdentity signer)
        => Seal(value, binding, recipients, list, null, signer);

    /// <summary>
    /// Seals a value for devices of the list, one copy each, as
    /// <see cref="Seal(string, FleetSecretBinding, IReadOnlyList{FleetDeviceListMember}, FleetDeviceListState, DeviceIdentity)"/> does, a
    /// machine being of the person the signed access document names once it was given to another person.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="binding">What the copies are bound to.</param>
    /// <param name="recipients">The devices (<see cref="Recipients(FleetDeviceListState, FleetAccessState?, string, IEnumerable{string})"/>), which must be of the signer's person.</param>
    /// <param name="list">The device list as the signer verified it.</param>
    /// <param name="access">The access document as the signer verified it; null for none.</param>
    /// <param name="signer">The key of the controller device that seals and signs the grants.</param>
    /// <returns>The copies.</returns>
    /// <exception cref="ArgumentNullException">An argument but <paramref name="access"/> is null.</exception>
    /// <exception cref="ArgumentException">The value is empty or too long, the signer is not a controller device of the list with its person, a recipient is of another person, or the list names no organization.</exception>
    public static IReadOnlyList<FleetSecretCopyDto> Seal(string value, FleetSecretBinding binding, IReadOnlyList<FleetDeviceListMember> recipients, FleetDeviceListState list,
        FleetAccessState? access, DeviceIdentity signer)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(signer);
        if (value.Length is 0 or > MaxValueLength) throw new ArgumentException($"A value holds from 1 to {MaxValueLength} characters.", nameof(value));
        if (binding.Version < 1) throw new ArgumentException("A value has a version from 1.", nameof(binding));
        if (!list.Members.TryGetValue(signer.DeviceId, out var self) || self.Kind != FleetDeviceKinds.Controller || self.OwnerId is not { } person)
            throw new ArgumentException("Only a controller device of the list, with its person, seals a value.", nameof(signer));
        if (list.OrganizationId is not { } organization) throw new ArgumentException("The list names no organization yet.", nameof(list));
        if (recipients.Count is 0 or > MaxRecipients || recipients.Any(recipient => !FleetAccessState.HoldsValuesOf(access, recipient, person) || list.Removed.Contains(recipient.DeviceId)))
            throw new ArgumentException($"A value goes to 1 to {MaxRecipients} devices of the signer's person that the list holds.", nameof(recipients));
        var key = FleetMetadataKey.Generate(new FleetKeyGrant(organization, person, binding.Version, Math.Max(1, list.Sequence)) { SpaceId = GrantScope(binding.SecretId) });
        var plain = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var sealedBytes = new byte[plain.Length + TagLength];
        try
        {
            using var aes = new AesGcm(key.Material, TagLength);
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), Associated(key.Id, binding));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        var iv = Base64Url.EncodeToString(nonce);
        var ct = Base64Url.EncodeToString(sealedBytes);
        return [.. recipients.Select(recipient => new FleetSecretCopyDto(recipient.DeviceId, Write(key.Id, iv, ct, FleetKeyWrap.Wrap(key, recipient.Key, key.Grant!, signer))))];
    }

    /// <summary>
    /// Opens the copy of a value sealed for this device: its key must be wrapped to this device with a grant a controller
    /// device of its person signed for this value and version (<see cref="FleetKeyWrap.Unwrap(string, string, int, string?, DeviceIdentity, FleetDeviceListState)"/>),
    /// and the value must be the one of this binding.
    /// </summary>
    /// <param name="copy">The sealed copy.</param>
    /// <param name="binding">What the server lists the value as.</param>
    /// <param name="identity">The key of this device.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <param name="value">The value, when it opens.</param>
    /// <param name="reason">Why it does not open, or null.</param>
    /// <returns>Whether it opened.</returns>
    /// <exception cref="ArgumentNullException">An argument but <paramref name="copy"/> is null.</exception>
    public static bool TryOpen(string? copy, FleetSecretBinding binding, DeviceIdentity identity, FleetDeviceListState list,
        [NotNullWhen(true)] out string? value, [NotNullWhen(false)] out string? reason)
        => TryOpen(copy, binding, identity, list, null, out value, out reason);

    /// <summary>
    /// Opens the copy of a value sealed for this device, as <see cref="TryOpen(string?, FleetSecretBinding, DeviceIdentity, FleetDeviceListState, out string?, out string?)"/>
    /// does, with the person of this machine the signed access document names once it was given to another person: the values
    /// of its new owner open, those of the person the device list names no more.
    /// </summary>
    /// <param name="copy">The sealed copy.</param>
    /// <param name="binding">What the server lists the value as.</param>
    /// <param name="identity">The key of this device.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <param name="access">The access document as this device verified it; null for none.</param>
    /// <param name="value">The value, when it opens.</param>
    /// <param name="reason">Why it does not open, or null.</param>
    /// <returns>Whether it opened.</returns>
    /// <exception cref="ArgumentNullException">An argument but <paramref name="copy"/> or <paramref name="access"/> is null.</exception>
    public static bool TryOpen(string? copy, FleetSecretBinding binding, DeviceIdentity identity, FleetDeviceListState list, FleetAccessState? access,
        [NotNullWhen(true)] out string? value, [NotNullWhen(false)] out string? reason)
    {
        value = null;
        if (!TryOpenKey(copy, binding, identity, list, access, out var key, out var parts, out reason)) return false;
        value = Decrypt(key, parts, binding, keep: true);
        if (value is not null) return true;
        reason = "the value does not open for this name, scope and version";
        return false;
    }

    // The value of the parts of a copy with its key, when they are of this binding; null otherwise. Without keep, the
    // value is only checked: nothing of it is kept.
    private static string? Decrypt(FleetMetadataKey key, (byte[] Nonce, byte[] Sealed) parts, FleetSecretBinding binding, bool keep)
    {
        var plain = new byte[parts.Sealed.Length - TagLength];
        try
        {
            using var aes = new AesGcm(key.Material, TagLength);
            aes.Decrypt(parts.Nonce, parts.Sealed.AsSpan(0, plain.Length), parts.Sealed.AsSpan(plain.Length), plain, Associated(key.Id, binding));
            return keep ? Encoding.UTF8.GetString(plain) : string.Empty;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// Seals the value of the copy this controller device holds again, as the next version of its binding, for the devices
    /// given: a machine withdrawn is left out, and the grants it was given are of an older version, which no rotation takes.
    /// </summary>
    /// <param name="own">The copy of this device.</param>
    /// <param name="binding">What the server lists the value as now.</param>
    /// <param name="next">The binding of the next version.</param>
    /// <param name="recipients">The devices of the next version.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <param name="access">The access document as this device verified it; null for none.</param>
    /// <param name="signer">The key of this controller device.</param>
    /// <returns>The copies of the next version.</returns>
    /// <exception cref="ArgumentNullException">An argument but <paramref name="access"/> is null.</exception>
    /// <exception cref="ArgumentException">The next binding is not a newer version of the same value, or the devices do not hold (<see cref="Seal(string, FleetSecretBinding, IReadOnlyList{FleetDeviceListMember}, FleetDeviceListState, FleetAccessState?, DeviceIdentity)"/>).</exception>
    /// <exception cref="InvalidDataException">The copy of this device does not open.</exception>
    public static IReadOnlyList<FleetSecretCopyDto> Reseal(string own, FleetSecretBinding binding, FleetSecretBinding next, IReadOnlyList<FleetDeviceListMember> recipients,
        FleetDeviceListState list, FleetAccessState? access, DeviceIdentity signer)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(next);
        if (next.SecretId != binding.SecretId || next.Version <= binding.Version) throw new ArgumentException("The value is sealed again as a newer version of itself.", nameof(next));
        if (!TryOpen(own, binding, signer, list, null, out var value, out var reason)) throw new InvalidDataException($"The copy of this device does not open: {reason}.");
        return Seal(value, next, recipients, list, access, signer);
    }

    /// <summary>
    /// Gives a device allowed later its copy of the current version, from the copy this controller device holds: the key
    /// is wrapped to that device with a grant this device signs; the value is not sealed again.
    /// </summary>
    /// <param name="own">The copy of this device.</param>
    /// <param name="binding">What the server lists the value as.</param>
    /// <param name="recipient">The device that gets a copy, of the same person.</param>
    /// <param name="identity">The key of this controller device.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <returns>The copy of the recipient.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">This device is not a controller device of the list, or the recipient is of another person or removed.</exception>
    /// <exception cref="InvalidDataException">The copy of this device does not open.</exception>
    public static FleetSecretCopyDto Regrant(string own, FleetSecretBinding binding, FleetDeviceListMember recipient, DeviceIdentity identity, FleetDeviceListState list)
        => Regrant(own, binding, recipient, identity, list, null);

    /// <summary>
    /// Gives a device allowed later its copy of the current version, as
    /// <see cref="Regrant(string, FleetSecretBinding, FleetDeviceListMember, DeviceIdentity, FleetDeviceListState)"/> does, a machine
    /// being of the person the signed access document names once it was given to another person.
    /// </summary>
    /// <param name="own">The copy of this device.</param>
    /// <param name="binding">What the server lists the value as.</param>
    /// <param name="recipient">The device that gets a copy, of the same person.</param>
    /// <param name="identity">The key of this controller device.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <param name="access">The access document as this device verified it; null for none.</param>
    /// <returns>The copy of the recipient.</returns>
    /// <exception cref="ArgumentNullException">An argument but <paramref name="access"/> is null.</exception>
    /// <exception cref="ArgumentException">This device is not a controller device of the list, or the recipient is of another person or removed.</exception>
    /// <exception cref="InvalidDataException">The copy of this device does not open.</exception>
    public static FleetSecretCopyDto Regrant(string own, FleetSecretBinding binding, FleetDeviceListMember recipient, DeviceIdentity identity, FleetDeviceListState list,
        FleetAccessState? access)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(list);
        if (!list.Members.TryGetValue(identity.DeviceId, out var self) || self.Kind != FleetDeviceKinds.Controller || self.OwnerId is null)
            throw new ArgumentException("Only a controller device of the list gives a copy.", nameof(identity));
        if (!FleetAccessState.HoldsValuesOf(access, recipient, self.OwnerId) || list.Removed.Contains(recipient.DeviceId))
            throw new ArgumentException("A copy goes to a device of the same person that the list holds.", nameof(recipient));
        if (!TryOpenKey(own, binding, identity, list, null, out var key, out var parts, out var reason)) throw new InvalidDataException($"The copy of this device does not open: {reason}.");
        // The value is checked before it is given: a copy that does not open for this binding is never passed on.
        if (Decrypt(key, parts, binding, keep: false) is null) throw new InvalidDataException("The copy of this device does not open for this name, scope and version.");
        var grant = key.Grant! with { DeviceListSequence = Math.Max(1, list.Sequence) };
        return new FleetSecretCopyDto(recipient.DeviceId, Write(key.Id, Base64Url.EncodeToString(parts.Nonce), Base64Url.EncodeToString(parts.Sealed),
            FleetKeyWrap.Wrap(key, recipient.Key, grant, identity)));
    }

    private static bool TryOpenKey(string? copy, FleetSecretBinding binding, DeviceIdentity identity, FleetDeviceListState list, FleetAccessState? access,
        [NotNullWhen(true)] out FleetMetadataKey? key, out (byte[] Nonce, byte[] Sealed) parts, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(list);
        key = null;
        parts = default;
        if (string.IsNullOrEmpty(copy) || copy.Length > MaxCopyLength)
        {
            reason = "no copy was sealed for this device";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(copy);
            var root = document.RootElement;
            var keyId = FleetSealedJson.Text(root, "kid");
            var nonce = FleetSealedJson.Bytes(root, "iv");
            var sealedBytes = FleetSealedJson.Bytes(root, "ct");
            if (FleetSealedJson.Text(root, "alg") != Algorithm || string.IsNullOrEmpty(keyId) || nonce is not { Length: NonceLength } || sealedBytes is not { Length: > TagLength }
                || FleetSealedJson.Text(root, "wrap") is not { } wrapped)
            {
                reason = "the copy uses a form this version does not read";
                return false;
            }

            key = FleetKeyWrap.Unwrap(wrapped, keyId, binding.Version, GrantScope(binding.SecretId), identity, list, access);
            parts = (nonce, sealedBytes);
            reason = null;
            return true;
        }
        catch (InvalidDataException exception)
        {
            reason = "its key was not given by a controller device of the person (" + exception.Message.TrimEnd('.') + ")";
            return false;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            reason = "the copy does not read";
            return false;
        }
    }

    private static string Write(string keyId, string iv, string ct, string wrap)
        => Encoding.UTF8.GetString(FleetSealedJson.Write(writer =>
        {
            writer.WriteString("alg", Algorithm);
            writer.WriteString("kid", keyId);
            writer.WriteString("iv", iv);
            writer.WriteString("ct", ct);
            writer.WriteString("wrap", wrap);
        }));

    // The grant of a key names the value it is for: a copy of one value never opens as a copy of another.
    private static string GrantScope(string secretId) => GrantPrefix + secretId;

    // Whether the scope a grant names is the one of a value, which a team machine takes from its maintainers only.
    internal static bool IsValueScope(string? scope) => scope is not null && scope.StartsWith(GrantPrefix, StringComparison.Ordinal);

    private static byte[] Associated(string keyId, FleetSecretBinding binding)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{Domain}\n{Algorithm}\n{keyId}\n{binding.SecretId}\n{binding.Kind}\n{binding.Name.ToUpperInvariant()}\n{binding.ScopeKind}\n{binding.ScopeId}\n{binding.Version}\n{(binding.HostOnly ? 1 : 0)}"));
}
