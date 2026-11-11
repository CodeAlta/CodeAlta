using System.Buffers.Text;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The private Dev Tunnel credentials of one device, protected by its local secret store, separate from shareable
/// server links. Each credential is bound to an exact HTTPS origin and is never followed through a redirect.
/// </summary>
public sealed class FleetTunnelAccessStore
{
    /// <summary>The header Dev Tunnels accepts without taking the application's Authorization header.</summary>
    public const string AuthorizationHeader = "X-Tunnel-Authorization";

    /// <summary>The largest enrollment invitation accepted, including a tunnel credential.</summary>
    public const int MaximumInvitationLength = 16384;

    private const string InvitationPrefix = "ca-tunnel:";
    private readonly LocalSecretStore _secrets;
    private readonly TimeProvider _time;

    /// <summary>Creates the credential store beside the device key.</summary>
    /// <param name="identity">The device whose private fleet folder owns the store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is null.</exception>
    public FleetTunnelAccessStore(DeviceIdentity identity) : this(DirectoryOf(identity), TimeProvider.System) { }

    /// <summary>Creates the credential store beside a device key with a clock of its own.</summary>
    /// <param name="identity">The device.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> or <paramref name="time"/> is null.</exception>
    public FleetTunnelAccessStore(DeviceIdentity identity, TimeProvider time) : this(DirectoryOf(identity), time) { }

    /// <summary>Creates the store in a private fleet folder, using a clock of its own.</summary>
    /// <param name="fleetRoot">The private fleet folder.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentException"><paramref name="fleetRoot"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is null.</exception>
    public FleetTunnelAccessStore(string fleetRoot, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        ArgumentNullException.ThrowIfNull(time);
        var root = Path.Combine(fleetRoot, "tunnel-access");
        // Like the device key: no interactive keychain unlock is needed by a headless node.
        ISecretProtector protector = OperatingSystem.IsWindows() ? new DpapiSecretProtector()
            : new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(root, "secrets", "master-key")));
        _secrets = new LocalSecretStore(root, protector);
        _time = time;
    }

    /// <summary>Gets whether an address is an HTTPS Dev Tunnel origin, never a lookalike domain.</summary>
    /// <param name="address">The address.</param>
    /// <returns>Whether it is a Dev Tunnel address.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    public static bool IsTunnel(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsAbsoluteUri && address.Scheme == Uri.UriSchemeHttps
            && address.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase) && address.UserInfo.Length == 0;
    }

    /// <summary>Gets whether a text is a bounded enrollment code or private tunnel invitation.</summary>
    /// <param name="token">The code.</param>
    /// <returns>Whether its size and prefix are accepted.</returns>
    public static bool IsEnrollmentCode(string? token) => token is { Length: > 0 and <= MaximumInvitationLength }
        && (token.Length <= 256 || token.StartsWith(InvitationPrefix, StringComparison.Ordinal));

    /// <summary>Gets whether a code carries private tunnel access, including for recovery of an existing link.</summary>
    /// <param name="token">The code.</param>
    /// <returns>Whether it has the bounded private-invitation prefix.</returns>
    public static bool IsInvitation(string? token) => token is not null && IsEnrollmentCode(token) && token.StartsWith(InvitationPrefix, StringComparison.Ordinal);

    /// <summary>Reads a credential, including one that expired, so that recovery can distinguish it from a missing one.</summary>
    /// <param name="address">The exact origin.</param>
    /// <returns>The credential, or null.</returns>
    /// <exception cref="InvalidDataException">The stored credential is damaged.</exception>
    /// <exception cref="SecretStoreUnavailableException">The local protection cannot be opened.</exception>
    public FleetTunnelAccessDto? Read(Uri address)
    {
        if (!IsTunnel(address) || !_secrets.TryGetSecret(Name(address), out var value)) return null;
        try
        {
            var access = JsonSerializer.Deserialize(value, ServerApiJsonContext.Default.FleetTunnelAccessDto);
            if (access is null || !Valid(address, access)) throw new InvalidDataException("The private Dev Tunnel credential does not read.");
            return access;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The private Dev Tunnel credential does not read.");
        }
    }

    /// <summary>Keeps a connect credential protected at rest, never in the server URL or public link file.</summary>
    /// <param name="address">The exact HTTPS origin.</param>
    /// <param name="access">The credential.</param>
    /// <exception cref="ArgumentException">The credential is invalid, expired, or belongs to another origin.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="access"/> is null.</exception>
    /// <exception cref="SecretStoreUnavailableException">The credential cannot be protected.</exception>
    public void Save(Uri address, FleetTunnelAccessDto access)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!Valid(address, access) || access.ExpiresAt <= _time.GetUtcNow())
            throw new ArgumentException("The private Dev Tunnel credential is invalid, expired, or for another server.", nameof(access));
        _secrets.SetSecret(Name(address), JsonSerializer.Serialize(access, ServerApiJsonContext.Default.FleetTunnelAccessDto), "Fleet transport");
    }

    /// <summary>Imports the transport part of an invitation and returns its actual single-use enrollment code.</summary>
    /// <param name="address">The server the person chose.</param>
    /// <param name="invitation">The enrollment code, with or without tunnel access.</param>
    /// <returns>The actual enrollment code.</returns>
    /// <exception cref="ArgumentException">The invitation is damaged, expired or for another server.</exception>
    public string ImportInvitation(Uri address, string invitation)
    {
        if (!IsEnrollmentCode(invitation)) throw new ArgumentException("The enrollment invitation is not valid.", nameof(invitation));
        if (!invitation.StartsWith(InvitationPrefix, StringComparison.Ordinal)) return invitation;
        try
        {
            var bytes = Base64Url.DecodeFromChars(invitation.AsSpan(InvitationPrefix.Length));
            var value = JsonSerializer.Deserialize(bytes, ServerApiJsonContext.Default.FleetTunnelInvitationDto);
            if (value?.EnrollmentToken is not { Length: > 0 and <= 256 }) throw new ArgumentException("The enrollment invitation is not valid.", nameof(invitation));
            Save(address, value.Access);
            return value.EnrollmentToken;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The enrollment invitation is not valid.", nameof(invitation));
        }
    }

    /// <summary>Wraps an enrollment code with connect-only access. Give the whole result privately to the new device.</summary>
    /// <param name="enrollmentToken">The single-use enrollment code.</param>
    /// <param name="access">The transport credential.</param>
    /// <returns>The secret invitation.</returns>
    /// <exception cref="ArgumentException">The enrollment code is empty or too long.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="access"/> is null.</exception>
    public static string Invitation(string enrollmentToken, FleetTunnelAccessDto access)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentToken);
        ArgumentNullException.ThrowIfNull(access);
        if (enrollmentToken.Length > 256) throw new ArgumentException("The enrollment code is too long.", nameof(enrollmentToken));
        var result = InvitationPrefix + Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new FleetTunnelInvitationDto(enrollmentToken, access),
            ServerApiJsonContext.Default.FleetTunnelInvitationDto));
        if (result.Length > MaximumInvitationLength) throw new ArgumentException("The private tunnel invitation is too long.", nameof(access));
        return result;
    }

    /// <summary>Adds the current token to a request to its exact origin; application authorization is left alone.</summary>
    /// <param name="address">The server origin.</param>
    /// <param name="request">The request.</param>
    /// <exception cref="HttpRequestException">The credential expired; import a new invitation from an approved computer.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> or <paramref name="request"/> is null.</exception>
    public void Authorize(Uri address, HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsTunnel(address) && request.RequestUri is { IsAbsoluteUri: true } target && SameOrigin(address, target) && Header(address) is { } value)
            request.Headers.Add(AuthorizationHeader, value);
    }

    internal string? Header(Uri address)
    {
        if (Read(address) is not { } access) return null;
        if (access.ExpiresAt <= _time.GetUtcNow())
            throw new HttpRequestException("The private Dev Tunnel connect token expired. Import a fresh invitation from an approved computer; anonymous access stays disabled.");
        return "tunnel " + access.Token;
    }

    // ClientWebSocket's default HTTP handler follows redirects. A tunnel credential must not travel to a sign-in or
    // another origin. The explicit invoker disables them, just as the REST client does.
    internal Func<HttpMessageInvoker>? HandshakeInvoker { get; init; }

    internal async Task<ClientWebSocket> ConnectAsync(Uri server, Uri socketAddress, string? certificatePin, Action<ClientWebSocketOptions>? configure, CancellationToken cancellationToken)
    {
        if (socketAddress.Scheme != "wss" || socketAddress.Port != server.Port || !string.Equals(socketAddress.Host, server.Host, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A private tunnel credential cannot be sent to another origin.", nameof(socketAddress));
        var socket = new ClientWebSocket();
        using var handler = certificatePin is null ? new SocketsHttpHandler() : FleetServerCertificates.CreateHandler(certificatePin);
        handler.AllowAutoRedirect = false;
        using var invoker = HandshakeInvoker?.Invoke() ?? new HttpMessageInvoker(handler, disposeHandler: false);
        try
        {
            configure?.Invoke(socket.Options);
            if (Header(server) is { } header) socket.Options.SetRequestHeader(AuthorizationHeader, header);
            await socket.ConnectAsync(socketAddress, invoker, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool Valid(Uri address, FleetTunnelAccessDto access) => IsTunnel(address)
        && Uri.TryCreate(access.Address, UriKind.Absolute, out var actual) && IsTunnel(actual) && SameOrigin(address, actual)
        && actual.AbsolutePath == "/" && actual.Query.Length == 0 && actual.Fragment.Length == 0
        && address.AbsolutePath == "/" && address.Query.Length == 0 && address.Fragment.Length == 0
        && access.Token is { Length: > 0 and <= 8192 } && access.Token.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '+' or '/' or '=');

    private static bool SameOrigin(Uri left, Uri right) => left.Scheme == right.Scheme && left.Port == right.Port
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase);

    private static string Name(Uri address) => "DEV_TUNNEL_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.GetLeftPart(UriPartial.Authority).ToLowerInvariant())));

    private static string DirectoryOf(DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        // DeviceIdentity always keeps an absolute key path.
        return Path.GetDirectoryName(identity.KeyPath) ?? throw new InvalidOperationException("The device key has no fleet folder.");
    }
}
