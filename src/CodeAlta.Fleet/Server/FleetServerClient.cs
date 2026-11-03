using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

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
public sealed class FleetServerClient : IDisposable
{
    private static readonly MediaTypeHeaderValue Json = new("application/json");

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly DeviceIdentity _identity;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="FleetServerClient"/> class.</summary>
    /// <param name="serverAddress">The address of the server (<see cref="FleetServerAddress.TryParse"/>).</param>
    /// <param name="identity">The key of this device; the caller keeps and disposes it.</param>
    /// <param name="handler">The HTTP handler, such as the one of a test server; null for the usual one.</param>
    /// <param name="time">The clock of the signatures; null for the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serverAddress"/> or <paramref name="identity"/> is null.</exception>
    public FleetServerClient(Uri serverAddress, DeviceIdentity identity, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(serverAddress);
        ArgumentNullException.ThrowIfNull(identity);
        ServerAddress = serverAddress;
        _identity = identity;
        _time = time ?? TimeProvider.System;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _ownsHttp = true;
    }

    /// <summary>Gets the address of the server.</summary>
    public Uri ServerAddress { get; }

    /// <summary>Gets the id of this device.</summary>
    public string DeviceId => _identity.DeviceId;

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
    public Task<SetupResponse> SetUpAsync(string code, string ownerName, string deviceName, string? organizationName = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        var key = PublicKey;
        var entry = Sign(new FleetDeviceListEntryPayload(FleetDeviceListEntryPayload.CurrentVersion, null, 1, null, FleetDeviceListOperations.Add, DeviceId, key,
            FleetDeviceKinds.Controller, null, DeviceId, Now()));
        return SendAsync(HttpMethod.Post, "api/v1/setup", Body(new SetupRequest(code.Trim(), ownerName.Trim(), deviceName.Trim(), key, entry, organizationName?.Trim()),
            ServerApiJsonContext.Default.SetupRequest), ServerApiJsonContext.Default.SetupResponse, cancellationToken);
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
    /// signatures, sequence, previous hashes, signers that are controller devices. What it gives can be trusted whatever
    /// the server is; a list that does not verify is refused.
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
        var list = await GetDeviceListAsync(cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < list.Count; index++)
        {
            // The server's numbering of an entry agrees with the entry, or the list is not the one its signers signed.
            if (list[index].Sequence != index + 1 || list[index].Hash != FleetDeviceListEntries.Hash(list[index].Entry))
                throw new FleetDeviceListException(index + 1, "the server's sequence or hash of the entry does not match the entry");
        }

        return FleetDeviceListVerifier.VerifyOrThrow(list.Select(static item => item.Entry), organizationId);
    }

    /// <summary>Gets a device from the verified device list: the key to trust for it, whatever the server's tables say.</summary>
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
        var state = await ReadVerifiedDeviceListAsync(null, cancellationToken).ConfigureAwait(false);
        return state.Members.GetValueOrDefault(deviceId);
    }

    /// <summary>Lists the machines of the organization and their last inventory.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The machines.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<MachineDto>> ListMachinesAsync(CancellationToken cancellationToken = default)
        => (await SendAsync(HttpMethod.Get, "api/v1/machines", null, ServerApiJsonContext.Default.ListResponseMachineDto, cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>Lists the published sessions: their facts and their sealed metadata.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The sessions.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<SessionDto>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => (await SendAsync(HttpMethod.Get, "api/v1/sessions", null, ServerApiJsonContext.Default.ListResponseSessionDto, cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>
    /// Approves a pending device: this controller device signs the entry that adds it to the device list, after the last
    /// entry, giving it to this device's person. The list is read again once when another entry came first.
    /// </summary>
    /// <param name="deviceId">The pending device.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approved device.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused the approval: not a controller, not pending, or the list moved on twice.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify, or this device is not a controller device in it: nothing was signed.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<DeviceDto> ApproveAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return ChangeListAsync(deviceId, "approve", async (organization, self, token) =>
        {
            var device = await GetDeviceAsync(deviceId, token).ConfigureAwait(false);
            return (FleetDeviceListOperations.Add, device.PublicKey, device.Kind, self.OwnerId);
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
            // Nothing is signed after a list this device cannot verify: its signature would vouch for what the server added.
            var state = await ReadVerifiedDeviceListAsync(organization.Id, cancellationToken).ConfigureAwait(false);
            if (!state.IsController(DeviceId))
                throw new FleetDeviceListException(state.Sequence, "this device is not a controller device of the list, so it signs no entry");
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
