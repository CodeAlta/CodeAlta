using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// A run of an automation a person asks by hand (<b>Run now</b>) of a machine reached through a server: a controller
/// device of that person signs it for one machine and one run id, and the machine runs it only once it verified the
/// signature against the device list it anchored, for an automation it holds from that server, within minutes of its
/// time and once: the server alone cannot make it run anything, nor replay a request.
/// </summary>
/// <param name="Version">The version of the format: 1.</param>
/// <param name="OrganizationId">The organization of the server it goes through.</param>
/// <param name="AutomationId">The automation.</param>
/// <param name="MachineId">The machine that runs it.</param>
/// <param name="RunId">The id the run takes on the machine; a request with a run id the machine has seen is not run again.</param>
/// <param name="SignerDeviceId">The controller device that signs it.</param>
/// <param name="SignerKey">The public key of the signer (the base64 of its DER SubjectPublicKeyInfo).</param>
/// <param name="Time">When it was signed; a machine refuses a request too far from its own time.</param>
public sealed record FleetAutomationRunRequest(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("org")] string OrganizationId,
    [property: JsonPropertyName("id")] string AutomationId,
    [property: JsonPropertyName("machine")] string MachineId,
    [property: JsonPropertyName("run")] string RunId,
    [property: JsonPropertyName("signer")] string SignerDeviceId,
    [property: JsonPropertyName("key")] string SignerKey,
    [property: JsonPropertyName("time")] DateTimeOffset Time)
{
    /// <summary>The action of a request that takes a run in progress over (<see cref="Action"/>).</summary>
    public const string TakeOverAction = "takeover";

    /// <summary>
    /// The action of a request that tries the automation (<see cref="Action"/>): a dry run, whose tools it calls by itself are
    /// replaced by fakes that record their calls on the run instead of making them.
    /// </summary>
    public const string TryAction = "try";

    /// <summary>
    /// Gets what the request asks of the run: null to run the automation now (the run takes <see cref="RunId"/>), or
    /// <see cref="TakeOverAction"/> to take the run in progress with <see cref="RunId"/> over: what it waits for waits for
    /// that person, and no approval stops it any more. The signature covers it: one request is never the other. Not
    /// written when null, so that a request to run reads as before.
    /// </summary>
    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Action { get; init; }

    /// <summary>The version of the format this library writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>A run request as it travels: the exact JSON text the signer signed, and the signature.</summary>
/// <param name="Payload">The JSON of a <see cref="FleetAutomationRunRequest"/>, byte for byte as signed.</param>
/// <param name="Signature">The ECDSA P-256 signature of <see cref="FleetAutomationRunRequestDocuments.GetSignedBytes"/>, r and s in base64url.</param>
public sealed record FleetSignedAutomationRunRequest(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sig")] string Signature);

/// <summary>Writes, reads, checks and verifies the signed run requests of automations.</summary>
public static class FleetAutomationRunRequestDocuments
{
    /// <summary>How far from the time of the machine a request may be signed, either way.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(10);

    private const string SignDomain = "codealta-automation-run/1\n";
    private const int MaxPayloadLength = 4 * 1024;
    private const int MaxIdLength = 128;

    /// <summary>Writes the JSON text of a request, the text a signer signs.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public static string Serialize(FleetAutomationRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.Serialize(request, FleetAutomationRunRequestJsonContext.Default.FleetAutomationRunRequest);
    }

    /// <summary>Gets the bytes a signer signs for a request.</summary>
    /// <param name="payload">The JSON text of the request.</param>
    /// <returns>The UTF-8 of a domain line and the text.</returns>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is null or empty.</exception>
    public static byte[] GetSignedBytes(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Encoding.UTF8.GetBytes(SignDomain + payload);
    }

    /// <summary>Reads the JSON text of a request and checks what it says, apart from its signature.</summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="request">The request read, or null.</param>
    /// <param name="reason">Why it does not hold, or null.</param>
    /// <returns>Whether it reads and holds.</returns>
    public static bool TryRead(string? payload, [NotNullWhen(true)] out FleetAutomationRunRequest? request, [NotNullWhen(false)] out string? reason)
    {
        request = null;
        reason = "the run request does not read";
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength) return false;
        FleetAutomationRunRequest? read;
        try { read = JsonSerializer.Deserialize(payload, FleetAutomationRunRequestJsonContext.Default.FleetAutomationRunRequest); }
        catch (JsonException) { return false; }
        if (read is null) return false;
        if (read.Version != FleetAutomationRunRequest.CurrentVersion)
        {
            reason = $"version {read.Version.ToString(CultureInfo.InvariantCulture)} of the run request is not known";
            return false;
        }

        if (!IsName(read.OrganizationId) || !IsName(read.AutomationId) || !IsName(read.MachineId) || !IsName(read.RunId) || !IsName(read.SignerDeviceId)
            || string.IsNullOrEmpty(read.SignerKey))
        {
            reason = "a run request names its organization, automation, machine, run and signer";
            return false;
        }

        if (read.Action is not (null or FleetAutomationRunRequest.TakeOverAction or FleetAutomationRunRequest.TryAction))
        {
            reason = "the action of the run request is not known";
            return false;
        }

        (request, reason) = (read, null);
        return true;
    }

    /// <summary>
    /// Verifies a request for a machine of an organization: it reads and holds, is for this machine and organization,
    /// was signed within <see cref="MaxSkew"/> of <paramref name="now"/>, by a controller device of the verified device
    /// list, of the same person as the machine, with the key the list holds for it.
    /// </summary>
    /// <param name="signed">The signed request.</param>
    /// <param name="devices">The device list as the machine verified and anchored it.</param>
    /// <param name="organizationId">The organization of the server the machine is linked to.</param>
    /// <param name="machineId">The device of the machine that verifies.</param>
    /// <param name="now">The time of the machine.</param>
    /// <param name="request">The request, when it verifies.</param>
    /// <param name="reason">Why it does not verify, or null.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
    public static bool TryVerify(FleetSignedAutomationRunRequest? signed, FleetDeviceListState devices, string? organizationId, string? machineId, DateTimeOffset now,
        [NotNullWhen(true)] out FleetAutomationRunRequest? request, [NotNullWhen(false)] out string? reason)
        => TryVerify(signed, devices, organizationId, machineId, now, null, out request, out reason);

    /// <summary>
    /// Verifies a request as <see cref="TryVerify(FleetSignedAutomationRunRequest?, FleetDeviceListState, string?, string?, DateTimeOffset, out FleetAutomationRunRequest?, out string?)"/>
    /// does, but also lets a controller device of another person than the owner of the machine sign it, when the machine
    /// says that person may make it: such as a member of the team of a team machine, or an approver of the automation that
    /// the signed access document names, taking a run over.
    /// </summary>
    /// <param name="signed">The signed request.</param>
    /// <param name="devices">The device list as the machine verified and anchored it.</param>
    /// <param name="organizationId">The organization of the server the machine is linked to.</param>
    /// <param name="machineId">The device of the machine that verifies.</param>
    /// <param name="now">The time of the machine.</param>
    /// <param name="mayAct">Whether the person of the signer, who is not the owner of the machine, may make the request read; null: nobody else.</param>
    /// <param name="request">The request, when it verifies.</param>
    /// <param name="reason">Why it does not verify, or null.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
    public static bool TryVerify(FleetSignedAutomationRunRequest? signed, FleetDeviceListState devices, string? organizationId, string? machineId, DateTimeOffset now,
        Func<FleetAutomationRunRequest, string, bool>? mayAct, [NotNullWhen(true)] out FleetAutomationRunRequest? request, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(devices);
        request = null;
        if (signed is null || string.IsNullOrEmpty(signed.Signature)) return Refuse("the run request does not read", out reason);
        if (!TryRead(signed.Payload, out var read, out reason)) return false;

        if (string.IsNullOrEmpty(machineId) || read.MachineId != machineId) return Refuse("the run request is for another machine", out reason);
        if (string.IsNullOrEmpty(organizationId) || read.OrganizationId != organizationId || devices.OrganizationId is { } listed && listed != organizationId)
            return Refuse("the run request is for another organization", out reason);
        if ((now - read.Time).Duration() > MaxSkew) return Refuse("the run request was signed too long ago, or in the future", out reason);
        if (!devices.Members.TryGetValue(read.SignerDeviceId, out var signer) || signer.Kind != FleetDeviceKinds.Controller || devices.Removed.Contains(read.SignerDeviceId))
            return Refuse("the run request is not signed by a controller device of the organization", out reason);
        if (!devices.Members.TryGetValue(machineId, out var self) || self.OwnerId is null
            || self.OwnerId != signer.OwnerId && (signer.OwnerId is not { } other || mayAct?.Invoke(read, other) != true))
            return Refuse("the run request is signed by a device of another person than the owner of this machine", out reason);
        if (read.SignerKey != signer.Key.Text || !signer.Key.Verify(GetSignedBytes(signed.Payload), signed.Signature))
            return Refuse("the signature does not verify with the key the device list holds for its signer", out reason);
        request = read;
        return true;
    }

    private static bool Refuse(string why, out string reason)
    {
        reason = why;
        return false;
    }

    private static bool IsName(string? text) => !string.IsNullOrEmpty(text) && text.Length <= MaxIdLength && !text.Any(char.IsControl);
}

/// <summary>The source-generated contracts of the run requests.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never, WriteIndented = false)]
[JsonSerializable(typeof(FleetAutomationRunRequest))]
[JsonSerializable(typeof(FleetSignedAutomationRunRequest))]
public sealed partial class FleetAutomationRunRequestJsonContext : JsonSerializerContext;
