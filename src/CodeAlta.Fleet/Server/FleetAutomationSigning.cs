using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>What an automation version holds in clear or sealed: the automation and the repository of its project.</summary>
/// <param name="Definition">The automation as one table of a configuration file.</param>
/// <param name="Repository">The repository key of its project; null for a chat.</param>
public sealed record FleetAutomationContent(string Definition, string? Repository);

/// <summary>
/// Signs the versions of automations with the key of a controller device, and seals what they do for the machine that
/// runs them and the controller devices of its person (<c>doc/fleet/security-and-privacy.md</c>, what the server sees):
/// AES-256-GCM with a key of its own per version, bound to the automation, the number and the machine, wrapped to each
/// recipient as metadata keys are (<see cref="FleetKeyWrap"/>), so that only a controller device of the person can give
/// the key, and a server neither reads the content nor moves it to another version or machine.
/// </summary>
public static class FleetAutomationSigning
{
    /// <summary>The algorithm of a sealed definition.</summary>
    public const string SealAlgorithm = "a256gcm";

    private const string Domain = "codealta-automation-content/1";
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaxRecipients = 64;

    /// <summary>Signs a version with the key of this device, which it names as its signer.</summary>
    /// <param name="identity">The key of the controller device that signs.</param>
    /// <param name="version">The version; its signer and key are set to those of <paramref name="identity"/>.</param>
    /// <returns>The signed version.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The version does not hold.</exception>
    public static FleetSignedAutomationVersion Sign(DeviceIdentity identity, FleetAutomationVersion version)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(version);
        var signed = version with { SignerDeviceId = identity.DeviceId, SignerKey = Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo()) };
        if (FleetAutomationVersionDocuments.Check(signed) is { } reason) throw new ArgumentException($"The automation version does not hold: {reason}.", nameof(version));
        var text = FleetAutomationVersionDocuments.Serialize(signed);
        return new FleetSignedAutomationVersion(text, Base64Url.EncodeToString(identity.SignData(FleetAutomationVersionDocuments.GetSignedBytes(text))));
    }

    /// <summary>Signs a run request with the key of this device, which it names as its signer.</summary>
    /// <param name="identity">The key of the controller device that asks the run.</param>
    /// <param name="request">The request; its signer and key are set to those of <paramref name="identity"/>.</param>
    /// <returns>The signed request.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The request does not hold.</exception>
    public static FleetSignedAutomationRunRequest SignRun(DeviceIdentity identity, FleetAutomationRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(request);
        var named = request with { SignerDeviceId = identity.DeviceId, SignerKey = Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo()) };
        var text = FleetAutomationRunRequestDocuments.Serialize(named);
        if (!FleetAutomationRunRequestDocuments.TryRead(text, out _, out var reason)) throw new ArgumentException($"The run request does not hold: {reason}.", nameof(request));
        return new FleetSignedAutomationRunRequest(text, Base64Url.EncodeToString(identity.SignData(FleetAutomationRunRequestDocuments.GetSignedBytes(text))));
    }

    /// <summary>
    /// Seals what a version does for its machine and the controller devices of the signer's person, as the device list
    /// verified by the signer holds them.
    /// </summary>
    /// <param name="content">The definition and the repository.</param>
    /// <param name="automationId">The automation.</param>
    /// <param name="number">The number of the version.</param>
    /// <param name="machineId">The machine that runs it, which must be a device of the list.</param>
    /// <param name="list">The device list as the signer verified it.</param>
    /// <param name="signer">The key of the controller device that seals and signs.</param>
    /// <returns>The sealed text: <c>{ "alg", "kid", "iv", "ct", "keys": { device: wrapped } }</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The machine or the signer is not a device of the list, or the list names no organization or person.</exception>
    public static string Seal(FleetAutomationContent content, string automationId, long number, string machineId, FleetDeviceListState list, DeviceIdentity signer)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(automationId);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(signer);
        if (!list.Members.TryGetValue(signer.DeviceId, out var self) || self.Kind != FleetDeviceKinds.Controller || self.OwnerId is not { } person)
            throw new ArgumentException("Only a controller device of the list, with its person, seals an automation.", nameof(signer));
        if (!list.Members.TryGetValue(machineId, out var machine)) throw new ArgumentException("The machine is not a device of the list.", nameof(machineId));
        if (list.OrganizationId is not { } organization) throw new ArgumentException("The list names no organization yet.", nameof(list));
        var recipients = list.Members.Values.Where(member => member.DeviceId == machine.DeviceId || member.Kind == FleetDeviceKinds.Controller && member.OwnerId == person)
            .Where(member => !list.Removed.Contains(member.DeviceId)).Take(MaxRecipients).ToArray();
        var key = FleetMetadataKey.Generate(new FleetKeyGrant(organization, person, 1, Math.Max(1, list.Sequence)));
        var plain = FleetSealedJson.Write(writer =>
        {
            writer.WriteString("definition", content.Definition);
            if (content.Repository is not null) writer.WriteString("repository", content.Repository);
        });
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var sealedBytes = new byte[plain.Length + TagLength];
        using (var aes = new AesGcm(key.Material, TagLength))
        {
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), Associated(key.Id, automationId, number, machineId));
        }

        CryptographicOperations.ZeroMemory(plain);
        return Encoding.UTF8.GetString(FleetSealedJson.Write(writer =>
        {
            writer.WriteString("alg", SealAlgorithm);
            writer.WriteString("kid", key.Id);
            writer.WriteString("iv", Base64Url.EncodeToString(nonce));
            writer.WriteString("ct", Base64Url.EncodeToString(sealedBytes));
            writer.WriteStartObject("keys");
            foreach (var recipient in recipients) writer.WriteString(recipient.DeviceId, FleetKeyWrap.Wrap(key, recipient.Key, key.Grant!, signer));
            writer.WriteEndObject();
        }));
    }

    /// <summary>
    /// Opens what a version does, on a device it was sealed for: the copy of the key wrapped to this device must be given
    /// by a controller device of its person (<see cref="FleetKeyWrap.Unwrap(string, string, int, DeviceIdentity, FleetDeviceListState)"/>), and the content must be the one of this
    /// automation, number and machine.
    /// </summary>
    /// <param name="sealedText">The sealed text of the version.</param>
    /// <param name="automationId">The automation the version names.</param>
    /// <param name="number">Its number.</param>
    /// <param name="machineId">Its machine.</param>
    /// <param name="identity">The key of this device.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <param name="content">The content, when it opens.</param>
    /// <param name="reason">Why it does not open, or null.</param>
    /// <returns>Whether it opened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> or <paramref name="list"/> is null.</exception>
    public static bool TryOpen(string? sealedText, string automationId, long number, string machineId, DeviceIdentity identity, FleetDeviceListState list,
        [NotNullWhen(true)] out FleetAutomationContent? content, [NotNullWhen(false)] out string? reason)
        => TryOpen(sealedText, automationId, number, machineId, identity, list, null, out content, out reason);

    /// <summary>
    /// Seals what a version of an automation of a team does (<c>doc/fleet/orchestration.md</c>, scopes): for its machine and the
    /// controller devices of the team's maintainers, as the access document and the device list this device verified say, so
    /// that every maintainer reads and changes it, whichever person enrolled the machine. Each copy of the key is signed by
    /// this device, which a reader accepts only from a maintainer of that team.
    /// </summary>
    /// <param name="content">The definition and the repository.</param>
    /// <param name="automationId">The automation.</param>
    /// <param name="number">The number of the version.</param>
    /// <param name="machineId">The machine of the team that runs it.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="list">The device list as the signer verified it.</param>
    /// <param name="access">The access document as the signer verified it.</param>
    /// <param name="signer">The controller device of a maintainer of the team.</param>
    /// <returns>The sealed text: <c>{ "alg", "kid", "team", "iv", "ct", "keys": { device: wrapped } }</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The machine is not the team's, or the signer does not maintain it.</exception>
    public static string SealForTeam(FleetAutomationContent content, string automationId, long number, string machineId, string teamId, FleetDeviceListState list,
        FleetAccessState access, DeviceIdentity signer)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(automationId);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(signer);
        if (access.TeamOf(machineId) is not { } team || team.Id != teamId) throw new ArgumentException("The machine is not a machine of the team.", nameof(machineId));
        if (!list.Members.TryGetValue(signer.DeviceId, out var self) || self.Kind != FleetDeviceKinds.Controller || FleetAccessState.RoleIn(team, self.OwnerId) != FleetTeamRoles.Maintainer)
            throw new ArgumentException("Only a controller device of a maintainer of the team seals its automations.", nameof(signer));
        if (!list.Members.TryGetValue(machineId, out var machine)) throw new ArgumentException("The machine is not a device of the list.", nameof(machineId));
        var recipients = list.Members.Values.Where(member => member.DeviceId == machine.DeviceId
                || member.Kind == FleetDeviceKinds.Controller && member.OwnerId is { } owner && team.Maintainers.Contains(owner, StringComparer.Ordinal))
            .Where(member => !list.Removed.Contains(member.DeviceId)).Take(MaxRecipients).ToArray();
        return SealScoped(content, automationId, number, machineId, "team", teamId, recipients, list, signer, TeamKeys);
    }

    /// <summary>
    /// Seals what a version of an automation of the organization does: for its machine and the controller devices of the
    /// owners and the admins who sign, as the access document and the device list this device verified say.
    /// </summary>
    /// <param name="content">The definition and the repository.</param>
    /// <param name="automationId">The automation.</param>
    /// <param name="number">The number of the version.</param>
    /// <param name="machineId">The machine of the organization that runs it.</param>
    /// <param name="list">The device list as the signer verified it.</param>
    /// <param name="access">The access document as the signer verified it.</param>
    /// <param name="signer">The controller device of an owner or an admin who signs.</param>
    /// <returns>The sealed text: <c>{ "alg", "kid", "org", "iv", "ct", "keys": { device: wrapped } }</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The machine is not the organization's, or the signer is not an owner or an admin who signs.</exception>
    public static string SealForOrganization(FleetAutomationContent content, string automationId, long number, string machineId, FleetDeviceListState list,
        FleetAccessState access, DeviceIdentity signer)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(automationId);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(signer);
        if (!access.IsOrganizationMachine(machineId)) throw new ArgumentException("The machine is not a machine of the organization.", nameof(machineId));
        if (!list.Members.TryGetValue(signer.DeviceId, out var self) || self.Kind != FleetDeviceKinds.Controller || !access.IsSigner(self.OwnerId))
            throw new ArgumentException("Only a controller device of an owner or an admin who signs seals the automations of the organization.", nameof(signer));
        var recipients = list.Members.Values.Where(member => member.DeviceId == machineId || member.Kind == FleetDeviceKinds.Controller && access.IsSigner(member.OwnerId))
            .Where(member => !list.Removed.Contains(member.DeviceId)).Take(MaxRecipients).ToArray();
        return SealScoped(content, automationId, number, machineId, "organization", list.OrganizationId ?? string.Empty, recipients, list, signer, OrganizationKeys);
    }

    /// <summary>
    /// Opens what a version does, on a device it was sealed for. A personal version opens with a copy of the key given by a
    /// controller device of this device's person (the owner <paramref name="access"/> names once the machine was given to another
    /// person); a version of a team, with a copy given by a maintainer of that team as
    /// <paramref name="access"/> says. The content must be the one of this automation, number and machine.
    /// </summary>
    /// <param name="sealedText">The sealed text of the version.</param>
    /// <param name="automationId">The automation the version names.</param>
    /// <param name="number">Its number.</param>
    /// <param name="machineId">Its machine.</param>
    /// <param name="identity">The key of this device.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <param name="access">The access document as this device verified it; null opens personal versions only.</param>
    /// <param name="content">The content, when it opens.</param>
    /// <param name="reason">Why it does not open, or null.</param>
    /// <returns>Whether it opened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> or <paramref name="list"/> is null.</exception>
    public static bool TryOpen(string? sealedText, string automationId, long number, string machineId, DeviceIdentity identity, FleetDeviceListState list,
        FleetAccessState? access, [NotNullWhen(true)] out FleetAutomationContent? content, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(list);
        content = null;
        if (string.IsNullOrEmpty(sealedText) || sealedText.Length > FleetAutomationVersionDocuments.MaxSealedLength)
        {
            reason = "the version holds no sealed definition";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(sealedText);
            var root = document.RootElement;
            var keyId = FleetSealedJson.Text(root, "kid");
            var nonce = FleetSealedJson.Bytes(root, "iv");
            var sealedBytes = FleetSealedJson.Bytes(root, "ct");
            if (FleetSealedJson.Text(root, "alg") != SealAlgorithm || string.IsNullOrEmpty(keyId) || nonce is not { Length: NonceLength } || sealedBytes is not { Length: > TagLength })
            {
                reason = "the sealed definition uses a form this version does not read";
                return false;
            }

            if (!root.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Object || FleetSealedJson.Text(keys, identity.DeviceId) is not { } wrapped)
            {
                reason = "the definition was not sealed for this machine";
                return false;
            }

            FleetMetadataKey key;
            if (FleetSealedJson.Text(root, "team") is { } teamId)
            {
                // A copy of the key of a team's version is given by one of its maintainers, as the signed access document says.
                if (access?.Teams.GetValueOrDefault(teamId) is not { } team)
                {
                    reason = "an automation of a team opens with the access document that names the team";
                    return false;
                }

                key = FleetGrantKeys.Unwrap(wrapped, list.OrganizationId ?? string.Empty, automationId, ScopedSubject("team", teamId, number, machineId), identity, list,
                    signer => FleetAccessState.RoleIn(team, signer.OwnerId) == FleetTeamRoles.Maintainer, TeamKeys);
                if (key.Id != keyId) throw new InvalidDataException("The copy is the one of another key.");
            }
            else if (FleetSealedJson.Text(root, "organization") is { } organization)
            {
                // A copy of the key of a version of the organization is given by an owner or an admin who signs.
                if (access is null || organization != list.OrganizationId)
                {
                    reason = "an automation of the organization opens with the access document of the organization";
                    return false;
                }

                key = FleetGrantKeys.Unwrap(wrapped, organization, automationId, ScopedSubject("organization", organization, number, machineId), identity, list,
                    signer => access.IsSigner(signer.OwnerId), OrganizationKeys);
                if (key.Id != keyId) throw new InvalidDataException("The copy is the one of another key.");
            }
            else
            {
                // A personal version: on a machine given to another person, its new owner's devices give the key.
                key = FleetKeyWrap.Unwrap(wrapped, keyId, 1, null, identity, list, access);
            }

            var plain = new byte[sealedBytes.Length - TagLength];
            using (var aes = new AesGcm(key.Material, TagLength))
            {
                aes.Decrypt(nonce, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), plain, Associated(keyId, automationId, number, machineId));
            }

            string? definition, repository;
            // The document reads the bytes in place: they are cleared once the strings are read, not before.
            try
            {
                using var opened = JsonDocument.Parse(plain);
                (definition, repository) = (FleetSealedJson.Text(opened.RootElement, "definition"), FleetSealedJson.Text(opened.RootElement, "repository"));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }

            if (definition is not { Length: > 0 })
            {
                reason = "the sealed definition holds no automation";
                return false;
            }

            content = new FleetAutomationContent(definition, repository);
            reason = null;
            return true;
        }
        catch (InvalidDataException exception)
        {
            reason = "its key was not given by a controller device of the owner of this machine (" + exception.Message.TrimEnd('.') + ")";
            return false;
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException)
        {
            reason = "the sealed definition does not open for this automation, version and machine";
            return false;
        }
    }

    private static readonly FleetGrantKeys.Purpose TeamKeys = new("codealta-team-automation-key/1", "codealta-team-automation-key-sign/1");
    private static readonly FleetGrantKeys.Purpose OrganizationKeys = new("codealta-organization-automation-key/1", "codealta-organization-automation-key-sign/1");

    private static string ScopedSubject(string kind, string id, long number, string machineId)
        => string.Create(CultureInfo.InvariantCulture, $"{kind}:{id}#{number}#{machineId}");

    // Seals a version of a scope for the devices given, each copy of its key wrapped and signed by the signer under the domain of the scope.
    private static string SealScoped(FleetAutomationContent content, string automationId, long number, string machineId, string kind, string scopeId,
        IReadOnlyList<FleetDeviceListMember> recipients, FleetDeviceListState list, DeviceIdentity signer, FleetGrantKeys.Purpose purpose)
    {
        if (list.OrganizationId is not { } organization) throw new ArgumentException("The list names no organization yet.", nameof(list));
        var key = FleetMetadataKey.Generate();
        var plain = FleetSealedJson.Write(writer =>
        {
            writer.WriteString("definition", content.Definition);
            if (content.Repository is not null) writer.WriteString("repository", content.Repository);
        });
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var sealedBytes = new byte[plain.Length + TagLength];
        using (var aes = new AesGcm(key.Material, TagLength))
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), Associated(key.Id, automationId, number, machineId));
        CryptographicOperations.ZeroMemory(plain);
        var subject = ScopedSubject(kind, scopeId, number, machineId);
        return Encoding.UTF8.GetString(FleetSealedJson.Write(writer =>
        {
            writer.WriteString("alg", SealAlgorithm);
            writer.WriteString("kid", key.Id);
            writer.WriteString(kind, scopeId);
            writer.WriteString("iv", Base64Url.EncodeToString(nonce));
            writer.WriteString("ct", Base64Url.EncodeToString(sealedBytes));
            writer.WriteStartObject("keys");
            foreach (var recipient in recipients)
                writer.WriteString(recipient.DeviceId, FleetGrantKeys.Wrap(key, recipient.Key, organization, automationId, subject, Math.Max(1, list.Sequence), signer, purpose));
            writer.WriteEndObject();
        }));
    }

    private static byte[] Associated(string keyId, string automationId, long number, string machineId)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Domain}\n{SealAlgorithm}\n{keyId}\n{automationId}\n{number}\n{machineId}"));
}
