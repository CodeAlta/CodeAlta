using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Update;

namespace CodeAlta.Fleet.Server;

/// <summary>The address of a CodeAlta Server, as people type it and as the clients use it.</summary>
public static class FleetServerAddress
{
    /// <summary>Reads the address of a server: <c>http://</c> or <c>https://</c>, a host, an optional port and path; nothing else.</summary>
    /// <param name="text">What the person typed, such as <c>https://fleet.example</c> or <c>http://127.0.0.1:2586</c>.</param>
    /// <param name="address">The address, its path ending with <c>/</c>.</param>
    /// <returns>Whether the text is the address of a server.</returns>
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? address)
    {
        address = null;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 2048) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) return false;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        address = new UriBuilder(uri.Scheme, uri.Host, uri.Port, path).Uri;
        return true;
    }

    /// <summary>The fragment that carries the pin of a server's own certificate in its address: <c>https://host:2586#pin=sha256:…</c>.</summary>
    public const string PinFragment = "#pin=";

    /// <summary>
    /// Reads the address of a server as a person or another machine gives it, with the pin of the server's own certificate
    /// when it has one (<c>https://box:2586#pin=sha256:…</c>, <see cref="FleetServerCertificates"/>).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="address">The address, without its pin.</param>
    /// <param name="certificatePin">The pin, or null for a server whose certificate validates as usual.</param>
    /// <returns>Whether the text is the address of a server, with a pin that reads when it has one.</returns>
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? address, out string? certificatePin)
    {
        certificatePin = null;
        var value = text?.Trim() ?? string.Empty;
        var at = value.IndexOf(PinFragment, StringComparison.Ordinal);
        if (at >= 0)
        {
            var pin = value[(at + PinFragment.Length)..];
            if (!FleetServerCertificates.IsPin(pin))
            {
                address = null;
                return false;
            }

            certificatePin = pin;
            value = value[..at];
        }

        if (TryParse(value, out address) && (certificatePin is null || address.Scheme == "https")) return true;
        address = null;
        certificatePin = null;
        return false;
    }

    /// <summary>Gets the text of an address with the pin of its certificate, as it is given to a person or another machine.</summary>
    /// <param name="address">The address.</param>
    /// <param name="certificatePin">The pin, or null.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    public static string Format(Uri address, string? certificatePin) => certificatePin is null ? Format(address) : Format(address) + PinFragment + certificatePin;

    /// <summary>Gets the text of an address as it is stored and shown: without the final <c>/</c> of an empty path.</summary>
    /// <param name="address">The address.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    public static string Format(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.AbsoluteUri.TrimEnd('/');
    }

    /// <summary>Gets the address of the control connection of a server: <c>ws(s)://&lt;server&gt;/fleet/v1</c>.</summary>
    /// <param name="address">The address of the server.</param>
    /// <returns>The WebSocket address.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    public static Uri ControlAddress(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var builder = new UriBuilder(address) { Scheme = address.Scheme == "https" ? "wss" : "ws" };
        builder.Path = builder.Path.TrimEnd('/') + FleetServerMethods.ControlPath;
        return builder.Uri;
    }
}

/// <summary>
/// The client of CodeAlta Server's REST API for one device: every call is signed with the device key
/// (<see cref="FleetSignedRequest"/>), so no bearer secret exists. It sets a server up, makes enrollment tokens, enrolls
/// this device, and approves and revokes devices with device-list entries this controller device signs.
/// </summary>
/// <remarks>The server signs nothing: an approval is an entry of the device list signed here, after the last one the server holds.</remarks>
public sealed partial class FleetServerClient : IDisposable
{
    private static readonly MediaTypeHeaderValue Json = new("application/json");

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly DeviceIdentity _identity;
    private readonly TimeProvider _time;
    private readonly HttpMessageHandler? _handler;

    /// <summary>Initializes a new instance of the <see cref="FleetServerClient"/> class.</summary>
    /// <param name="serverAddress">The address of the server (<see cref="FleetServerAddress.TryParse(string?, out Uri?)"/>).</param>
    /// <param name="identity">The key of this device; the caller keeps and disposes it.</param>
    /// <param name="handler">The HTTP handler, such as the one of a test server; null for the usual one.</param>
    /// <param name="time">The clock of the signatures; null for the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serverAddress"/> or <paramref name="identity"/> is null.</exception>
    public FleetServerClient(Uri serverAddress, DeviceIdentity identity, HttpMessageHandler? handler = null, TimeProvider? time = null)
        : this(serverAddress, identity, handler, time, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="FleetServerClient"/> class for a server that may pin its own certificate.</summary>
    /// <param name="serverAddress">The address of the server (<see cref="FleetServerAddress.TryParse(string?, out Uri?)"/>).</param>
    /// <param name="identity">The key of this device; the caller keeps and disposes it.</param>
    /// <param name="handler">The HTTP handler, such as the one of a test server; null for the usual one, or the pinned one.</param>
    /// <param name="time">The clock of the signatures; null for the system clock.</param>
    /// <param name="certificatePin">The pin of the server's own certificate (<see cref="FleetServerCertificates"/>); null to validate it as usual.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serverAddress"/> or <paramref name="identity"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="certificatePin"/> is not a pin.</exception>
    public FleetServerClient(Uri serverAddress, DeviceIdentity identity, HttpMessageHandler? handler, TimeProvider? time, string? certificatePin)
    {
        ArgumentNullException.ThrowIfNull(serverAddress);
        ArgumentNullException.ThrowIfNull(identity);
        ServerAddress = serverAddress;
        _identity = identity;
        _time = time ?? TimeProvider.System;
        _handler = handler;
        CertificatePin = certificatePin;
        // A pinned server is reached through a handler of this client that trusts its key only (a given handler, of a test, wins).
        _http = handler is not null ? new HttpClient(handler, disposeHandler: false)
            : certificatePin is not null ? new HttpClient(FleetServerCertificates.CreateHandler(certificatePin), disposeHandler: true)
            : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(30);
        _ownsHttp = true;
    }

    /// <summary>Gets the pin of the server's own certificate, or null.</summary>
    public string? CertificatePin { get; }

    /// <summary>Gets the address of the server.</summary>
    public Uri ServerAddress { get; }

    /// <summary>Gets the id of this device.</summary>
    public string DeviceId => _identity.DeviceId;

    /// <summary>
    /// Gets the anchored verifier of the device list of this server for this device (its pin and checkpoint in the
    /// folder of the link), which every entry this client signs follows; null verifies the list from the first entry
    /// the server gives now, for a device that holds no link to this server yet.
    /// </summary>
    /// <exception cref="ArgumentException">The verifier is for another server or another device.</exception>
    public FleetOrganizationDevices? DeviceList
    {
        get;
        init
        {
            if (value is not null && (value.ServerAddress != ServerAddress || value.Identity.DeviceId != _identity.DeviceId))
                throw new ArgumentException("The device list is anchored for another server or another device.", nameof(value));
            field = value;
        }
    }

    /// <summary>Gets the public key of this device, as the server records it: the base64 of its SubjectPublicKeyInfo.</summary>
    public string PublicKey => Convert.ToBase64String(_identity.ExportSubjectPublicKeyInfo());

    /// <summary>Reads the health of the server; the call needs no device.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The health, with whether the server has an owner.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<ServerHealthDto> GetHealthAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/server/health", null, ServerApiJsonContext.Default.ServerHealthDto, cancellationToken);

    /// <summary>
    /// Sets a server up with its one-time code: this device becomes the first controller device of a new organization,
    /// and its person the owner. The device list starts with an entry this device signs for itself.
    /// </summary>
    /// <param name="code">The setup code (<c>altaserver setup-code</c>).</param>
    /// <param name="ownerName">The name of the owner.</param>
    /// <param name="deviceName">The name of this device.</param>
    /// <param name="organizationName">The name of the organization; null for the owner's name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The organization, the owner and this device.</returns>
    /// <exception cref="ArgumentException">A name or the code is empty.</exception>
    /// <exception cref="FleetServerException">The server refused the setup: wrong or expired code, not from its machine, already set up.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<SetupResponse> SetUpAsync(string code, string ownerName, string deviceName, string? organizationName = null, CancellationToken cancellationToken = default)
        => (await SetUpCoreAsync(code, ownerName, deviceName, organizationName, cancellationToken).ConfigureAwait(false)).Response;

    /// <summary>
    /// Sets a server up with its one-time code (<see cref="SetUpAsync(string, string, string, string?, CancellationToken)"/>), then pins
    /// the device list of the link to the first entry this device signed and to the organization the setup made
    /// (<see cref="FleetOrganizationDevices.Anchor"/>) before it returns: no list the server gives later, the first one
    /// this device reads included, can start with another entry or name another organization.
    /// </summary>
    /// <param name="code">The setup code (<c>altaserver setup-code</c>).</param>
    /// <param name="ownerName">The name of the owner.</param>
    /// <param name="deviceName">The name of this device.</param>
    /// <param name="organizationName">The name of the organization; null for the owner's name.</param>
    /// <param name="pinPath">The file of the pin in the folder of the link (<see cref="FleetOrganizationDevices.PinFileName"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The organization, the owner and this device.</returns>
    /// <exception cref="ArgumentException">A name, the code or the path is empty.</exception>
    /// <exception cref="FleetServerException">The server refused the setup, or answered another device than this one.</exception>
    /// <exception cref="FleetDeviceListException">The link pinned another first entry already.</exception>
    /// <exception cref="IOException">The pin cannot be written.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<SetupResponse> SetUpAsync(string code, string ownerName, string deviceName, string? organizationName, string pinPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pinPath);
        var (entry, response) = await SetUpCoreAsync(code, ownerName, deviceName, organizationName, cancellationToken).ConfigureAwait(false);
        if (response.Device.Id != DeviceId || string.IsNullOrEmpty(response.Organization.Id))
            throw new FleetServerException(HttpStatusCode.OK, FleetServerException.HttpErrorCode, null, "The server answered the setup of another device, or no organization.");
        // The root is the entry signed here, never one the server gives: its first list is checked against it like every later one.
        FleetOrganizationDevices.Anchor(pinPath, FleetDeviceListEntries.Hash(entry), response.Organization.Id);
        return response;
    }

    private async Task<(FleetSignedDeviceListEntry Entry, SetupResponse Response)> SetUpCoreAsync(string code, string ownerName, string deviceName, string? organizationName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        var key = PublicKey;
        var entry = Sign(new FleetDeviceListEntryPayload(FleetDeviceListEntryPayload.CurrentVersion, null, 1, null, FleetDeviceListOperations.Add, DeviceId, key,
            FleetDeviceKinds.Controller, null, DeviceId, Now()));
        var response = await SendAsync(HttpMethod.Post, "api/v1/setup", Body(new SetupRequest(code.Trim(), ownerName.Trim(), deviceName.Trim(), key, entry, organizationName?.Trim()),
            ServerApiJsonContext.Default.SetupRequest), ServerApiJsonContext.Default.SetupResponse, cancellationToken).ConfigureAwait(false);
        return (entry, response);
    }

    /// <summary>Makes an enrollment token for a machine; only a controller device may.</summary>
    /// <param name="lifetime">How long it works (10 minutes to 24 hours); null for the server's default.</param>
    /// <param name="label">What it is for; null for nothing.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token, whose value is shown once.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<EnrollmentTokenResponse> CreateEnrollmentTokenAsync(TimeSpan? lifetime = null, string? label = null, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, "api/v1/enrollment-tokens",
            Body(new EnrollmentTokenRequest(lifetime is { } value ? (int)value.TotalSeconds : null, string.IsNullOrWhiteSpace(label) ? null : label.Trim()),
                ServerApiJsonContext.Default.EnrollmentTokenRequest),
            ServerApiJsonContext.Default.EnrollmentTokenResponse, cancellationToken);

    /// <summary>Registers this device as an execution node with an enrollment token: it is pending until a controller device approves it.</summary>
    /// <param name="token">The enrollment token.</param>
    /// <param name="deviceName">The name of this device.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The pending device.</returns>
    /// <exception cref="ArgumentException">The token or the name is empty.</exception>
    /// <exception cref="FleetServerException">The token is unknown, used or expired, or this device is registered already.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<DeviceDto> EnrollAsync(string token, string deviceName, CancellationToken cancellationToken = default)
        => EnrollAsync(token, deviceName, FleetDeviceKinds.Node, cancellationToken);

    /// <summary>
    /// Registers this device with an enrollment token, as a controller device (a person's desktop, which may approve
    /// others once a controller device of the same person approved it) or as an execution node; pending until then.
    /// </summary>
    /// <param name="token">The enrollment token.</param>
    /// <param name="deviceName">The name of this device.</param>
    /// <param name="kind">Its class (<see cref="FleetDeviceKinds"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The pending device.</returns>
    /// <exception cref="ArgumentException">The token or the name is empty, or the kind is unknown.</exception>
    /// <exception cref="FleetServerException">The token is unknown, used or expired, or this device is registered already.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<DeviceDto> EnrollAsync(string token, string deviceName, string kind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        if (!FleetDeviceKinds.IsKnown(kind)) throw new ArgumentException($"'{kind}' is not a class of device.", nameof(kind));
        var enrolled = await SendAsync(HttpMethod.Post, "api/v1/enroll",
            Body(new EnrollRequest(token.Trim(), PublicKey, deviceName.Trim(), kind), ServerApiJsonContext.Default.EnrollRequest),
            ServerApiJsonContext.Default.EnrollResponse, cancellationToken).ConfigureAwait(false);
        return enrolled.Device;
    }

    /// <summary>Reads the organization of the server and its people.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The organization.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<OrganizationDto> GetOrganizationAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/organization", null, ServerApiJsonContext.Default.OrganizationDto, cancellationToken);

    /// <summary>Reads a device.</summary>
    /// <param name="deviceId">The device; this device when null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The device.</returns>
    /// <exception cref="FleetServerException">The server refused the call, or knows no such device.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<DeviceDto> GetDeviceAsync(string? deviceId = null, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/devices/" + Uri.EscapeDataString(deviceId ?? DeviceId), null, ServerApiJsonContext.Default.DeviceDto, cancellationToken);

    /// <summary>Lists the devices of the organization: pending, approved and revoked.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The devices.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<DeviceDto>> ListDevicesAsync(CancellationToken cancellationToken = default)
        => (await SendAsync(HttpMethod.Get, "api/v1/devices", null, ServerApiJsonContext.Default.ListResponseDeviceDto, cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>Reads the signed device list, from its first entry.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries, in order.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<DeviceListEntryDto>> GetDeviceListAsync(CancellationToken cancellationToken = default)
        => (await SendAsync(HttpMethod.Get, "api/v1/device-list", null, ServerApiJsonContext.Default.ListResponseDeviceListEntryDto, cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>
    /// Reads the signed device list and replays it with the rules every device applies (<see cref="FleetDeviceListVerifier"/>):
    /// signatures, sequence, previous hashes, signers that are controller devices. A list that does not verify is
    /// refused; one that verifies is not compared with what this device verified before: what a device trusts, signs
    /// after or wraps keys to comes from <see cref="FleetOrganizationDevices"/>, which anchors it.
    /// </summary>
    /// <param name="organizationId">The organization the list must name; null for the one the server says.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The list after its last entry: its members and their keys.</returns>
    /// <exception cref="FleetDeviceListException">The list does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetDeviceListState> ReadVerifiedDeviceListAsync(string? organizationId = null, CancellationToken cancellationToken = default)
    {
        organizationId ??= (await GetOrganizationAsync(cancellationToken).ConfigureAwait(false)).Id;
        return VerifyDeviceList(await GetDeviceListAsync(cancellationToken).ConfigureAwait(false), organizationId);
    }

    /// <summary>Replays the entries of a device list as the server gave them.</summary>
    /// <param name="list">The entries, with the server's sequence and hash of each.</param>
    /// <param name="organizationId">The organization the list must name.</param>
    /// <returns>The list after its last entry.</returns>
    /// <exception cref="FleetDeviceListException">The list does not verify.</exception>
    internal static FleetDeviceListState VerifyDeviceList(IReadOnlyList<DeviceListEntryDto> list, string? organizationId)
    {
        for (var index = 0; index < list.Count; index++)
        {
            // The server's numbering of an entry agrees with the entry, or the list is not the one its signers signed.
            if (list[index].Sequence != index + 1 || list[index].Hash != FleetDeviceListEntries.Hash(list[index].Entry))
                throw new FleetDeviceListException(index + 1, "the server's sequence or hash of the entry does not match the entry");
        }

        // A list a newer version extended stays readable: what these rules do not understand adds no member (doc/fleet/protocol.md, versions).
        return FleetDeviceListVerifier.VerifyOrThrow(list.Select(static item => item.Entry), organizationId, FleetDeviceListMode.Tolerant);
    }

    // The list as the anchor of this server verifies it: the one every entry signed here follows.
    private Task<FleetDeviceListState> ReadAnchoredDeviceListAsync(CancellationToken cancellationToken)
        => (DeviceList ?? new FleetOrganizationDevices(ServerAddress, _identity, null, _handler) { CertificatePin = CertificatePin }).RefreshAsync(cancellationToken);

    /// <summary>Gets a device from the anchored device list (<see cref="DeviceList"/>): the key to trust for it, whatever the server's tables say.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Its member record, or null when it is not in the list.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is empty.</exception>
    /// <exception cref="FleetDeviceListException">The list does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetDeviceListMember?> GetTrustedMemberAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var state = await ReadAnchoredDeviceListAsync(cancellationToken).ConfigureAwait(false);
        return state.Members.GetValueOrDefault(deviceId);
    }

    /// <summary>Lists the metadata keys of this device's person, each with the copy wrapped to this device when it has one.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The keys.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<MetadataKeyDto>> ListMetadataKeysAsync(CancellationToken cancellationToken = default)
        => (await SendAsync(HttpMethod.Get, "api/v1/metadata-keys", null, ServerApiJsonContext.Default.ListResponseMetadataKeyDto, cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>Records the metadata key this controller device made for its person, wrapped to that person's devices, this one included.</summary>
    /// <param name="keyId">The id of the key.</param>
    /// <param name="epoch">Its epoch.</param>
    /// <param name="wraps">Its copies, one per device.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The key.</returns>
    /// <exception cref="FleetServerException">The server refused the key: not a controller, a wrong wrap, or a key exists (<c>metadata_key.exists</c>).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<MetadataKeyDto> CreateMetadataKeyAsync(string keyId, int epoch, IReadOnlyList<MetadataKeyWrapDto> wraps, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, "api/v1/metadata-keys", Body(new CreateMetadataKeyRequest(keyId, epoch, wraps), ServerApiJsonContext.Default.CreateMetadataKeyRequest),
            ServerApiJsonContext.Default.MetadataKeyDto, cancellationToken);

    /// <summary>Records the key this controller device made for a space of its person, wrapped to that person's devices, this one included.</summary>
    /// <param name="keyId">The id of the key.</param>
    /// <param name="epoch">Its epoch in the space.</param>
    /// <param name="space">The opaque id of the space.</param>
    /// <param name="sealedDescriptor">Which space it is on the devices, sealed with the key (<see cref="FleetMetadataKey.SealSpace"/>).</param>
    /// <param name="wraps">Its copies, one per device.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The key.</returns>
    /// <exception cref="FleetServerException">The server refused the key: not a controller, a wrong wrap, or a key exists at that epoch (<c>metadata_key.exists</c>).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<MetadataKeyDto> CreateSpaceKeyAsync(string keyId, int epoch, string space, string sealedDescriptor, IReadOnlyList<MetadataKeyWrapDto> wraps,
        CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, "api/v1/metadata-keys",
            Body(new CreateMetadataKeyRequest(keyId, epoch, wraps, space, sealedDescriptor), ServerApiJsonContext.Default.CreateMetadataKeyRequest),
            ServerApiJsonContext.Default.MetadataKeyDto, cancellationToken);

    /// <summary>Gives copies of a metadata key this controller device holds to devices of its person that have none.</summary>
    /// <param name="keyId">The key.</param>
    /// <param name="wraps">The copies.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The key.</returns>
    /// <exception cref="FleetServerException">The server refused the copies.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<MetadataKeyDto> AddMetadataKeyWrapsAsync(string keyId, IReadOnlyList<MetadataKeyWrapDto> wraps, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, $"api/v1/metadata-keys/{Uri.EscapeDataString(keyId)}/wraps",
            Body(new AddMetadataKeyWrapsRequest(wraps), ServerApiJsonContext.Default.AddMetadataKeyWrapsRequest), ServerApiJsonContext.Default.MetadataKeyDto, cancellationToken);

    /// <summary>Lists the machines of the organization and their last inventory.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The machines.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<MachineDto>> ListMachinesAsync(CancellationToken cancellationToken = default)
        => (await SendAsync(HttpMethod.Get, "api/v1/machines", null, ServerApiJsonContext.Default.ListResponseMachineDto, cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>
    /// Sums the statistics facts the machines of this device's person published (<c>POST /api/v1/statistics/query</c>): time, runs,
    /// tokens, costs and tool calls, by machine, project key, provider, model, day or month.
    /// </summary>
    /// <param name="request">The query.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The sums.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused the query (<c>statistics.invalid</c>).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<StatisticsQueryResponse> QueryStatisticsAsync(StatisticsQueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, FleetStatisticsMethods.QueryPath.TrimStart('/'), Body(request, FleetStatisticsJsonContext.Default.StatisticsQueryRequest),
            FleetStatisticsJsonContext.Default.StatisticsQueryResponse, cancellationToken);
    }

    /// <summary>Lists the published sessions: their facts and their sealed metadata.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The sessions.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<SessionDto>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => (await SendAsync(HttpMethod.Get, "api/v1/sessions", null, ServerApiJsonContext.Default.ListResponseSessionDto, cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>
    /// Approves a pending device as the class the caller approves: this controller device signs the entry that adds it to
    /// the device list as that class, after the last entry, giving it to this device's person. The list is read again once
    /// when another entry came first.
    /// </summary>
    /// <param name="deviceId">The pending device.</param>
    /// <param name="kind">The class approved (<see cref="FleetDeviceKinds.Controller"/> or <see cref="FleetDeviceKinds.Node"/>): a device the server lists as another is refused.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approved device.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is empty, or <paramref name="kind"/> is not a class of device.</exception>
    /// <exception cref="FleetServerException">The server refused the approval: not a controller, not pending, or the list moved on twice.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify, this device is not a controller device in it, or the server lists the device as another class: nothing was signed.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<DeviceDto> ApproveAsync(string deviceId, string kind, CancellationToken cancellationToken = default)
        => ApproveCoreAsync(deviceId, kind, null, null, cancellationToken);

    /// <summary>
    /// Approves a pending device once the person confirmed its class and that it shows the words of its key: this controller
    /// device signs the entry that adds that key as that class only, so that a server can neither show the words of one key
    /// and have another one signed, nor have a machine approved as a controller.
    /// </summary>
    /// <param name="deviceId">The pending device.</param>
    /// <param name="kind">The class the person approves (<see cref="FleetDeviceKinds.Controller"/> or <see cref="FleetDeviceKinds.Node"/>).</param>
    /// <param name="confirmedWords">The words the person compared (<see cref="DeviceFingerprint.ToShortWords()"/>); null when none were compared.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approved device.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is empty, or <paramref name="kind"/> is not a class of device.</exception>
    /// <exception cref="FleetServerException">The server refused the approval: not a controller, not pending, or the list moved on twice.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify, this device is not a controller device in it, the server lists the device as another class, or its key is not the one whose words were confirmed: nothing was signed.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<DeviceDto> ApproveAsync(string deviceId, string kind, string? confirmedWords, CancellationToken cancellationToken = default)
        => ApproveCoreAsync(deviceId, kind, confirmedWords, null, cancellationToken);

    /// <summary>
    /// Approves a pending device whose whole key this device knows already (the machine it paired with, over the channel
    /// pinned to that key), as the class approved: nothing a server or the machine answers chooses the key or the class signed.
    /// </summary>
    /// <param name="deviceId">The pending device, which must be the device of <paramref name="key"/>.</param>
    /// <param name="kind">The class approved (<see cref="FleetDeviceKinds.Controller"/> or <see cref="FleetDeviceKinds.Node"/>).</param>
    /// <param name="key">The fingerprint of the key this device trusts for it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approved device.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is empty, <paramref name="kind"/> is not a class of device, or <paramref name="key"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused the approval: not a controller, not pending, or the list moved on twice.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify, this device is not a controller device in it, the server lists the device as another class, or with another key: nothing was signed.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<DeviceDto> ApproveAsync(string deviceId, string kind, DeviceFingerprint key, CancellationToken cancellationToken = default)
    {
        if (key.IsEmpty) throw new ArgumentException("The key of the device to approve is needed.", nameof(key));
        return ApproveCoreAsync(deviceId, kind, null, key, cancellationToken);
    }

    private Task<DeviceDto> ApproveCoreAsync(string deviceId, string kind, string? confirmedWords, DeviceFingerprint? expected, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!FleetDeviceKinds.IsKnown(kind)) throw new ArgumentException($"'{kind}' is not a class of device to approve.", nameof(kind));
        return ChangeListAsync(deviceId, "approve", async (organization, self, token) =>
        {
            var device = await GetDeviceAsync(deviceId, token).ConfigureAwait(false);
            // The key signed is the one the device id comes from, and the one whose words the person saw on both screens, or
            // the one this device knows whole.
            if (!FleetDevicePublicKey.TryParse(device.PublicKey, out var key) || key.DeviceId != deviceId
                || expected is { } known && DeviceFingerprint.Parse(key.Fingerprint) != known
                || confirmedWords is not null && !string.Equals(DeviceFingerprint.Parse(key.Fingerprint).ToShortWords(), confirmedWords.Trim(), StringComparison.Ordinal))
            {
                throw new FleetDeviceListException(0, "the key of the device is not the one whose words were confirmed");
            }

            // The class signed is the one approved, never the one the server says: a machine is never signed in as a controller.
            if (!string.Equals(device.Kind, kind, StringComparison.Ordinal))
                throw new FleetDeviceListException(0, $"the server lists the device as a {device.Kind}, not as the {kind} approved");
            // A person's first controller device, which the owner signs in (a person company sign-in created), is theirs; the
            // device list this device verifies refuses it for anyone but the owner, and for a person who holds a device.
            var owner = kind == FleetDeviceKinds.Controller && device.OwnerId is { } person && person != self.OwnerId ? person : self.OwnerId;
            return (FleetDeviceListOperations.Add, device.PublicKey, kind, owner);
        }, cancellationToken);
    }

    /// <summary>
    /// Revokes a device: an approved one leaves the device list by an entry this controller device signs; a pending one
    /// is refused without an entry. The server closes its connection.
    /// </summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The revoked device.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused the revocation.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify, or this device is not a controller device in it: nothing was signed.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<DeviceDto> RevokeAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var device = await GetDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device.Status == "pending")
        {
            return await SendAsync(HttpMethod.Post, $"api/v1/devices/{Uri.EscapeDataString(deviceId)}/revoke",
                Body(new DeviceListChangeRequest(null), ServerApiJsonContext.Default.DeviceListChangeRequest), ServerApiJsonContext.Default.DeviceDto, cancellationToken).ConfigureAwait(false);
        }

        return await ChangeListAsync(deviceId, "revoke",
            static (_, _, _) => Task.FromResult<(string, string?, string?, string?)>((FleetDeviceListOperations.Remove, null, null, null)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the telemetry setting of the fleet, with the headers only the server holds; an owner's controller device only.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The setting as the server holds it.</returns>
    /// <exception cref="FleetServerException">The server refused the call (<c>forbidden</c> for another device).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<TelemetrySettingsDto> GetTelemetryAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/telemetry", null, ServerApiJsonContext.Default.TelemetrySettingsDto, cancellationToken);

    /// <summary>
    /// Sets the telemetry of the fleet: this controller device signs a setting numbered after the one the server holds,
    /// which the server applies and pushes to the machines; they check this signature against their device list. The
    /// setting is read again once when another one was stored meanwhile.
    /// </summary>
    /// <param name="change">What the setting says.</param>
    /// <param name="serverHeaders">The headers of the collector only the server holds, for the server route; null for none.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The setting as the server holds it now.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is null.</exception>
    /// <exception cref="ArgumentException">The setting does not hold.</exception>
    /// <exception cref="FleetDeviceListException">This device is not a controller device of the device list it verified: nothing was signed.</exception>
    /// <exception cref="FleetServerException">The server refused the setting.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<TelemetrySettingsDto> SetTelemetryAsync(FleetTelemetryChange change, IReadOnlyDictionary<string, string>? serverHeaders,
        CancellationToken cancellationToken = default)
        => SetTelemetryAsync(change, serverHeaders, null, cancellationToken);

    /// <summary>
    /// Sets the telemetry of the fleet, keeping by their names headers of the server route the server holds: their values
    /// never come back to a device.
    /// </summary>
    /// <param name="change">What the setting says.</param>
    /// <param name="serverHeaders">The headers of the collector only the server holds, for the server route; null for none.</param>
    /// <param name="keepServerHeaders">The names of the headers of the server route whose held values are kept; null for none.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The setting as the server holds it now.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is null.</exception>
    /// <exception cref="ArgumentException">The setting does not hold.</exception>
    /// <exception cref="FleetDeviceListException">This device is not a controller device of the device list it verified: nothing was signed.</exception>
    /// <exception cref="FleetServerException">The server refused the setting.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<TelemetrySettingsDto> SetTelemetryAsync(FleetTelemetryChange change, IReadOnlyDictionary<string, string>? serverHeaders,
        IReadOnlyList<string>? keepServerHeaders, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var organization = await GetOrganizationAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadAnchoredDeviceListAsync(cancellationToken).ConfigureAwait(false);
        if (!state.IsController(DeviceId)) throw new FleetDeviceListException(state.Sequence, "this device is not a controller device of the list, so it signs no setting");
        for (var attempt = 0; ; attempt++)
        {
            var held = await GetTelemetryAsync(cancellationToken).ConfigureAwait(false);
            var sequence = held.Settings is { } signed && FleetTelemetrySettingsDocuments.TryParse(signed.Payload, out var current) ? current.Sequence + 1 : 1;
            var settings = new FleetTelemetrySettings(FleetTelemetrySettings.CurrentVersion, organization.Id, sequence, change.Enabled, change.Endpoint, change.Protocol,
                change.Route, change.Signals, change.Headers is { Count: > 0 } headers ? headers : null, change.Environment, DeviceId, Now());
            if (!FleetTelemetrySettingsDocuments.TryValidate(settings, out var reason)) throw new ArgumentException($"The telemetry setting does not hold: {reason}.", nameof(change));
            var text = FleetTelemetrySettingsDocuments.Serialize(settings);
            var request = new SetTelemetrySettingsRequest(new FleetSignedTelemetrySettings(text, Base64Url.EncodeToString(_identity.SignData(FleetTelemetrySettingsDocuments.GetSignedBytes(text)))),
                serverHeaders is { Count: > 0 } ? serverHeaders : null, keepServerHeaders is { Count: > 0 } ? keepServerHeaders : null);
            try
            {
                return await SendAsync(HttpMethod.Put, "api/v1/telemetry", Body(request, ServerApiJsonContext.Default.SetTelemetrySettingsRequest),
                    ServerApiJsonContext.Default.TelemetrySettingsDto, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetServerException exception) when (exception.Code == "telemetry.conflict" && attempt == 0)
            {
                // Another owner set it first: signed again after the new one.
            }
        }
    }

    /// <summary>Reads the updates of the fleet: the server's versions, the update policy and the recent campaigns; a controller device only.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The updates.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<UpdatesDto> GetUpdatesAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/updates", null, ServerApiJsonContext.Default.UpdatesDto, cancellationToken);

    /// <summary>
    /// Starts an update campaign: this controller device reads the feed (the latest version every product lists, unless
    /// one is given, and the SHA-512 of the runtime package of each runtime), signs the manifest, numbered after the
    /// last campaign, and sends it. The server asks the nodes, then updates itself; each machine checks this signature
    /// and its package against the hash signed for its runtime.
    /// </summary>
    /// <param name="change">What the campaign installs, from where, when and on which machines.</param>
    /// <param name="feedHttp">The HTTP client of the feed; null for one of its own.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The campaign as the server holds it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is null.</exception>
    /// <exception cref="UpdateException">The feed is not valid, did not answer, or lacks the version.</exception>
    /// <exception cref="FleetDeviceListException">This device is not a controller device of the device list it verified: nothing was signed.</exception>
    /// <exception cref="FleetServerException">The server refused the campaign.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<UpdateCampaignDto> StartUpdateAsync(FleetUpdateCampaignChange change, HttpClient? feedHttp = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var organization = await GetOrganizationAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadAnchoredDeviceListAsync(cancellationToken).ConfigureAwait(false);
        if (!state.IsController(DeviceId)) throw new FleetDeviceListException(state.Sequence, "this device is not a controller device of the list, so it signs no update");
        using var ownHttp = feedHttp is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(2) } : null;
        var feed = UpdateFeeds.Open(change.Feed ?? UpdateFeeds.NuGetOrg, feedHttp ?? ownHttp!);
        UpdateVersion version;
        if (change.Version is { } asked)
        {
            version = UpdateVersion.TryParse(asked, out var parsed) ? parsed : throw new UpdateException(UpdateErrorCodes.NotFound, $"'{asked}' is not a version.");
        }
        else
        {
            // As the self-update of each machine: a fleet whose server runs a prerelease follows the prereleases.
            var running = (await GetUpdatesAsync(cancellationToken).ConfigureAwait(false)).Server.Running;
            var prerelease = change.Prerelease || UpdateVersion.TryParse(running, out var current) && current.IsPrerelease;
            version = (await UpdateReleases.ListAsync(feed, UpdateReleases.FleetProducts, prerelease, cancellationToken).ConfigureAwait(false)).FirstOrDefault()
                ?? throw new UpdateException(UpdateErrorCodes.NotFound, $"{feed.Source} lists no version of both CodeAlta.Node and CodeAlta.Server.");
        }

        var hashes = await UpdateReleases.ReadHashesAsync(feed, UpdateReleases.FleetProducts, version, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var updates = await GetUpdatesAsync(cancellationToken).ConfigureAwait(false);
            var manifest = new FleetUpdateManifest(FleetUpdateManifest.CurrentVersion, organization.Id, Guid.NewGuid().ToString("N"), updates.NextSequence, version.ToString(),
                feed.Source, change.Rollback, change.NotBefore?.ToUniversalTime(), change.TimeZone, change.Machines is { Count: > 0 } machines ? machines : null, hashes, DeviceId,
                Now());
            if (FleetUpdateDocuments.Check(manifest) is { } reason) throw new ArgumentException($"The manifest does not hold: {reason}.", nameof(change));
            var text = FleetUpdateDocuments.Serialize(manifest);
            var request = new CreateUpdateCampaignRequest(new FleetSignedUpdateDocument(text,
                Base64Url.EncodeToString(_identity.SignData(FleetUpdateDocuments.GetManifestSignedBytes(text)))));
            try
            {
                return await SendAsync(HttpMethod.Post, "api/v1/updates/campaigns", Body(request, ServerApiJsonContext.Default.CreateUpdateCampaignRequest),
                    ServerApiJsonContext.Default.UpdateCampaignDto, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetServerException exception) when (exception.Code == "update.conflict" && attempt == 0 && exception.Message.Contains("numbered", StringComparison.Ordinal))
            {
                // Another owner started one first: signed again after it.
            }
        }
    }

    /// <summary>Cancels an update campaign before the server's step; an owner's controller device only.</summary>
    /// <param name="campaignId">The campaign.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The campaign.</returns>
    /// <exception cref="FleetServerException">The server refused (<c>update.conflict</c> once the server updates).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<UpdateCampaignDto> CancelUpdateAsync(string campaignId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        return SendAsync(HttpMethod.Post, $"api/v1/updates/campaigns/{Uri.EscapeDataString(campaignId)}/cancel", [], ServerApiJsonContext.Default.UpdateCampaignDto,
            cancellationToken);
    }

    /// <summary>Sets the update policy of the fleet: this controller device signs it, numbered after the one held.</summary>
    /// <param name="change">The policy.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The policy as the server holds it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is null.</exception>
    /// <exception cref="ArgumentException">The policy does not hold.</exception>
    /// <exception cref="FleetDeviceListException">This device is not a controller device of the device list it verified: nothing was signed.</exception>
    /// <exception cref="FleetServerException">The server refused the policy.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<UpdatePolicyDto> SetUpdatePolicyAsync(FleetUpdatePolicyChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var organization = await GetOrganizationAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadAnchoredDeviceListAsync(cancellationToken).ConfigureAwait(false);
        if (!state.IsController(DeviceId)) throw new FleetDeviceListException(state.Sequence, "this device is not a controller device of the list, so it signs no policy");
        for (var attempt = 0; ; attempt++)
        {
            var held = (await GetUpdatesAsync(cancellationToken).ConfigureAwait(false)).Policy;
            var sequence = held.Policy is { } signed && FleetUpdateDocuments.TryParsePolicy(signed.Payload, out var current, out _) ? current.Sequence + 1 : 1;
            var policy = new FleetUpdatePolicy(FleetUpdatePolicy.CurrentVersion, organization.Id, sequence, change.Mode, change.PinnedVersion, change.Feed, change.Window,
                change.TimeZone, DeviceId, Now());
            if (FleetUpdateDocuments.Check(policy) is { } reason) throw new ArgumentException($"The update policy does not hold: {reason}.", nameof(change));
            var text = FleetUpdateDocuments.Serialize(policy);
            var request = new SetUpdatePolicyRequest(new FleetSignedUpdateDocument(text, Base64Url.EncodeToString(_identity.SignData(FleetUpdateDocuments.GetPolicySignedBytes(text)))));
            try
            {
                return await SendAsync(HttpMethod.Put, "api/v1/updates/policy", Body(request, ServerApiJsonContext.Default.SetUpdatePolicyRequest),
                    ServerApiJsonContext.Default.UpdatePolicyDto, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetServerException exception) when (exception.Code == "update.conflict" && attempt == 0)
            {
                // Another owner set it first: signed again after the new one.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // Signs the entry that follows the last one of the verified list and posts it; read again once when another entry was appended meanwhile.
    private async Task<DeviceDto> ChangeListAsync(string deviceId, string action,
        Func<OrganizationDto, DeviceDto, CancellationToken, Task<(string Operation, string? Key, string? Kind, string? Owner)>> describe, CancellationToken cancellationToken)
    {
        var organization = await GetOrganizationAsync(cancellationToken).ConfigureAwait(false);
        var self = await GetDeviceAsync(null, cancellationToken).ConfigureAwait(false);
        var (operation, key, kind, owner) = await describe(organization, self, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            // Nothing is signed after a list this device cannot verify, or that does not extend the one it verified before
            // (another root, a removal taken away): its signature would vouch for what the server added.
            var state = await ReadAnchoredDeviceListAsync(cancellationToken).ConfigureAwait(false);
            if (state.OrganizationId is { } named && !string.Equals(named, organization.Id, StringComparison.Ordinal))
                throw new FleetDeviceListException(state.Sequence, "the server names another organization than its device list");
            if (!state.IsController(DeviceId))
                throw new FleetDeviceListException(state.Sequence, "this device is not a controller device of the list, so it signs no entry");
            // A device of another person is signed only by a device the list already gives to its own person: an entry of
            // a device not yet bound would bind it to that other person.
            if (owner is not null && owner != self.OwnerId && state.Members[DeviceId].OwnerId != self.OwnerId)
                throw new FleetDeviceListException(state.Sequence, "this device signs a device of another person only once the list gives it to its own person");
            var entry = Sign(new FleetDeviceListEntryPayload(FleetDeviceListEntryPayload.CurrentVersion, organization.Id, state.Sequence + 1, state.LastHash, operation, deviceId,
                key, kind, owner, DeviceId, Now()));
            if (!FleetDeviceListVerifier.TryAppend(state, entry, organization.Id, out _, out var reason))
                throw new FleetDeviceListException(state.Sequence + 1, $"the entry this device would sign does not hold ({reason})");
            try
            {
                return await SendAsync(HttpMethod.Post, $"api/v1/devices/{Uri.EscapeDataString(deviceId)}/{action}",
                    Body(new DeviceListChangeRequest(entry), ServerApiJsonContext.Default.DeviceListChangeRequest), ServerApiJsonContext.Default.DeviceDto, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FleetServerException exception) when (exception.Code == "device_list.conflict" && attempt == 0)
            {
                // Another controller appended first: the entry is signed again after the new last one.
            }
        }
    }

    private FleetSignedDeviceListEntry Sign(FleetDeviceListEntryPayload payload)
    {
        var text = FleetDeviceListEntries.SerializePayload(payload);
        return new FleetSignedDeviceListEntry(text, Base64Url.EncodeToString(_identity.SignData(FleetDeviceListEntries.GetSignedBytes(text))));
    }

    // To the second, as entries and times are compared by people, never for ordering.
    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeSeconds(_time.GetUtcNow().ToUnixTimeSeconds());

    private static byte[] Body<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.SerializeToUtf8Bytes(value, type);

    private async Task<TResult> SendAsync<TResult>(HttpMethod method, string relative, byte[]? body, JsonTypeInfo<TResult> resultType, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(ServerAddress, relative));
        var bytes = body ?? [];
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = Json;
        }

        var time = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var nonce = FleetSignedRequest.CreateNonce();
        var signed = FleetSignedRequest.GetSignedBytes(method.Method, request.RequestUri!.PathAndQuery, time, nonce, FleetSignedRequest.HashBody(bytes), DeviceId);
        request.Headers.Add(FleetSignedRequest.DeviceHeader, DeviceId);
        request.Headers.Add(FleetSignedRequest.TimestampHeader, time.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(FleetSignedRequest.NonceHeader, nonce);
        request.Headers.Add(FleetSignedRequest.SignatureHeader, Base64Url.EncodeToString(_identity.SignData(signed)));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Refusal(response.StatusCode, text);
        try
        {
            return JsonSerializer.Deserialize(text, resultType)
                ?? throw new FleetServerException(response.StatusCode, FleetServerException.HttpErrorCode, null, "The server answered nothing.");
        }
        catch (JsonException exception)
        {
            throw new FleetServerException(response.StatusCode, FleetServerException.HttpErrorCode, null, $"The answer of the server does not read: {exception.Message}");
        }
    }

    private static FleetServerException Refusal(HttpStatusCode status, byte[] body)
    {
        ApiProblemDto? problem = null;
        try { problem = body.Length == 0 ? null : JsonSerializer.Deserialize(body, ServerApiJsonContext.Default.ApiProblemDto); }
        catch (JsonException) { /* Not problem details: a proxy's page, say. */ }
        return new FleetServerException(status, problem?.Code ?? FleetServerException.HttpErrorCode, problem?.Hint,
            problem?.Detail ?? $"The server answered {(int)status} {status}.");
    }
}
