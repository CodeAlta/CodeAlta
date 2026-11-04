using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The hand-over of an automation given to a person (<c>doc/fleet/access-control.md</c>, ownership over time): a computer of
/// its owner seals what it does with a key made for the transfer and wraps that key to each controller device of the person
/// who takes it, signed by itself; only those devices open it, and only when the copy holds for this transfer and automation
/// and was signed by a controller device of its former owner. The new owner's computer then gives it versions of its own.
/// </summary>
public static class FleetTransferHandover
{
    private const string ContentDomain = "codealta-handover-content/1";
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly FleetGrantKeys.Purpose Handovers = new("codealta-handover-key/1", "codealta-handover-key-sign/1");

    /// <summary>Seals what an automation does for the devices of the person who takes it.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="handoverId">The id of the hand-over, which this device makes before the offer and the copies name.</param>
    /// <param name="automationId">The automation.</param>
    /// <param name="content">What it does.</param>
    /// <param name="recipients">The controller devices of who takes it, from the device list this device verified.</param>
    /// <param name="listSequence">The entry of the device list this device read.</param>
    /// <param name="signer">The controller device of its owner.</param>
    /// <returns>The sealed content, and the copy of its key for each device.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">An id is empty or no device receives the key.</exception>
    public static (string Sealed, IReadOnlyList<(string DeviceId, string Wrapped)> Wraps) Seal(string organizationId, string handoverId, string automationId,
        FleetAutomationContent content, IReadOnlyList<FleetDevicePublicKey> recipients, long listSequence, DeviceIdentity signer)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(handoverId);
        ArgumentException.ThrowIfNullOrEmpty(automationId);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(signer);
        if (recipients.Count == 0) throw new ArgumentException("A hand-over gives its key to at least one device.", nameof(recipients));
        var key = FleetMetadataKey.Generate();
        var plain = FleetSealedJson.Write(writer =>
        {
            writer.WriteString("definition", content.Definition);
            if (content.Repository is { } repository) writer.WriteString("repository", repository);
        });
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var cipher = new byte[plain.Length + TagLength];
        using (var aes = new AesGcm(key.Material, TagLength))
            aes.Encrypt(nonce, plain, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), Associated(key.Id, organizationId, handoverId, automationId));
        CryptographicOperations.ZeroMemory(plain);
        var sealedText = Encoding.UTF8.GetString(FleetSealedJson.Write(writer =>
        {
            writer.WriteString("alg", FleetMetadataKey.SealAlgorithm);
            writer.WriteString("kid", key.Id);
            writer.WriteString("hid", handoverId);
            writer.WriteString("iv", Base64Url.EncodeToString(nonce));
            writer.WriteString("ct", Base64Url.EncodeToString(cipher));
        }));
        var wraps = new List<(string, string)>(recipients.Count);
        foreach (var recipient in recipients)
            wraps.Add((recipient.DeviceId, FleetGrantKeys.Wrap(key, recipient, organizationId, handoverId, Subject(automationId), listSequence, signer, Handovers)));
        return (sealedText, wraps);
    }

    /// <summary>Opens what a hand-over gives this device, once its copy holds against the device list this device verified.</summary>
    /// <param name="sealedText">The sealed content.</param>
    /// <param name="wrapped">This device's copy of the key.</param>
    /// <param name="organizationId">The organization.</param>
    /// <param name="automationId">The automation.</param>
    /// <param name="fromPersonId">Its former owner, whose controller device signed the copy.</param>
    /// <param name="identity">The key of this device.</param>
    /// <param name="list">The device list as this device verified it.</param>
    /// <returns>What the automation does.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidDataException">The copy is not for this device, transfer or automation, is not signed by its former owner, or does not open.</exception>
    public static FleetAutomationContent Open(string sealedText, string wrapped, string organizationId, string automationId, string fromPersonId,
        DeviceIdentity identity, FleetDeviceListState list)
    {
        ArgumentNullException.ThrowIfNull(sealedText);
        ArgumentNullException.ThrowIfNull(wrapped);
        ArgumentNullException.ThrowIfNull(fromPersonId);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(list);
        try
        {
            using var document = JsonDocument.Parse(sealedText);
            var root = document.RootElement;
            // The copy names the hand-over it was made for: one taken from another hand-over does not open this one.
            var handoverId = FleetSealedJson.Text(root, "hid") ?? throw new InvalidDataException("The hand-over has no id.");
            var key = FleetGrantKeys.Unwrap(wrapped, organizationId, handoverId, Subject(automationId), identity, list, signer => signer.OwnerId == fromPersonId, Handovers);
            if (FleetSealedJson.Text(root, "alg") != FleetMetadataKey.SealAlgorithm || FleetSealedJson.Text(root, "kid") != key.Id)
                throw new InvalidDataException("The hand-over is not sealed with the key of its copy.");
            var nonce = Base64Url.DecodeFromChars(FleetSealedJson.Text(root, "iv") ?? string.Empty);
            var cipher = Base64Url.DecodeFromChars(FleetSealedJson.Text(root, "ct") ?? string.Empty);
            if (nonce.Length != NonceLength || cipher.Length < TagLength) throw new InvalidDataException("The hand-over does not read.");
            var plain = new byte[cipher.Length - TagLength];
            using (var aes = new AesGcm(key.Material, TagLength))
                aes.Decrypt(nonce, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), plain, Associated(key.Id, organizationId, handoverId, automationId));
            using var content = JsonDocument.Parse(plain);
            var definition = FleetSealedJson.Text(content.RootElement, "definition") ?? throw new InvalidDataException("The hand-over holds no automation.");
            return new FleetAutomationContent(definition, FleetSealedJson.Text(content.RootElement, "repository"));
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException)
        {
            throw new InvalidDataException("The hand-over does not open with its key.", exception);
        }
    }

    private static string Subject(string automationId) => "automation:" + automationId;

    private static byte[] Associated(string keyId, string organizationId, string handoverId, string automationId)
        => Encoding.UTF8.GetBytes($"{ContentDomain}\n{FleetMetadataKey.SealAlgorithm}\n{keyId}\n{organizationId}\n{handoverId}\n{automationId}");
}
