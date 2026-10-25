using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodeAlta.Sandbox.Network;

/// <summary>
/// The proxy of a sandbox with the <see cref="SandboxNetwork.Hosts"/> network: the container reaches this loopback
/// port and nothing else, and the proxy lets through only the hosts of the profile.
/// </summary>
/// <remarks>
/// It takes <c>CONNECT</c> (HTTPS and any TLS) and plain HTTP requests. It resolves the names itself and refuses an
/// address of this computer or of its networks (loopback, private, link-local, cloud metadata), except the local ports
/// of the profile, so that a name that points at this computer does not lead the sandbox to its services.
/// </remarks>
public sealed class SandboxNetworkProxy : IAsyncDisposable
{
    private const int MaximumHeader = 64 * 1024;
    private const int MaximumRefusals = 16;

    private readonly TcpListener _listener;
    private readonly IReadOnlyList<string> _hosts;
    private readonly IReadOnlySet<int> _localPorts;
    private readonly IReadOnlySet<int> _reservedPorts;
    private readonly CancellationTokenSource _closing = new();
    private readonly ConcurrentQueue<string> _refusals = new();
    private readonly Task _accepting;

    private SandboxNetworkProxy(TcpListener listener, IReadOnlyList<string> hosts, IReadOnlySet<int> localPorts, IReadOnlySet<int> reservedPorts)
    {
        _listener = listener;
        _hosts = hosts;
        _localPorts = localPorts;
        _reservedPorts = reservedPorts;
        _accepting = Task.Run(AcceptAsync);
    }

    /// <summary>Gets the address of the proxy, such as <c>http://127.0.0.1:50123</c>.</summary>
    public string Endpoint => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Gets the last hosts the proxy refused, the newest last.</summary>
    public IReadOnlyList<string> RecentRefusals => [.. _refusals];

    /// <summary>Starts a proxy on a free loopback port.</summary>
    /// <param name="hosts">The host patterns of the profile (<see cref="SandboxHostPattern"/>).</param>
    /// <param name="localPorts">The ports of this computer the sandbox may reach.</param>
    /// <param name="reservedPorts">Ports of this computer that are never reached, whatever the profile: CodeAlta's own.</param>
    /// <returns>The proxy.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="SocketException">No port could be listened on.</exception>
    public static SandboxNetworkProxy Start(IEnumerable<string> hosts, IEnumerable<int> localPorts, IEnumerable<int> reservedPorts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(localPorts);
        ArgumentNullException.ThrowIfNull(reservedPorts);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var reserved = reservedPorts.ToHashSet();
        return new SandboxNetworkProxy(listener, [.. hosts], localPorts.Where(port => !reserved.Contains(port)).ToHashSet(), reserved);
    }

    /// <summary>Says whether the sandbox may reach a host and port, as the proxy decides it (also for the <c>webget</c> of CodeAlta).</summary>
    /// <param name="host">The host name or address.</param>
    /// <param name="port">The port.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The address to connect to, or null when the host is refused.</returns>
    public Task<IPAddress?> ResolveAllowedAsync(string host, int port, CancellationToken cancellationToken)
        => ResolveAllowedAsync(host, port, _hosts, _localPorts, _reservedPorts, cancellationToken);

    /// <summary>
    /// Says whether a sandbox may reach a host and port: a host of its patterns (none for <paramref name="hosts"/> null,
    /// which lets every host of the internet through), never an address of this computer or of its networks but the
    /// local ports of the profile.
    /// </summary>
    /// <param name="host">The host name or address.</param>
    /// <param name="port">The port.</param>
    /// <param name="hosts">The host patterns, or null for every host of the internet.</param>
    /// <param name="localPorts">The ports of this computer the sandbox may reach.</param>
    /// <param name="reservedPorts">Ports of this computer that are never reached.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The address to connect to, or null when the host is refused.</returns>
    public static async Task<IPAddress?> ResolveAllowedAsync(string host, int port, IReadOnlyList<string>? hosts, IReadOnlySet<int> localPorts,
        IReadOnlySet<int> reservedPorts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(localPorts);
        ArgumentNullException.ThrowIfNull(reservedPorts);
        var name = host.Trim().TrimStart('[').TrimEnd(']').TrimEnd('.');
        if (IsLocalName(name) || IPAddress.TryParse(name, out var literal) && IsLocalAddress(literal))
            return localPorts.Contains(port) && !reservedPorts.Contains(port) ? IPAddress.Loopback : null;
        if (hosts is not null && !SandboxHostPattern.Matches(name, hosts)) return null;
        if (hosts is null && IPAddress.TryParse(name, out var address)) return address;
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return null;
        }

        // A name of the profile that points at this computer or its networks reaches none of them.
        return addresses.Length > 0 && addresses.All(static address => !IsLocalAddress(address)) ? addresses[0] : null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _accepting.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _closing.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_closing.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_closing.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            var header = await ReadHeaderAsync(stream, _closing.Token).ConfigureAwait(false);
            if (header is null) return;
            var lines = header.Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 3)
            {
                await RefuseAsync(stream, "a request the proxy does not read").ConfigureAwait(false);
                return;
            }

            string host;
            int port;
            byte[]? forward = null;
            if (first[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                if (!TrySplitHostPort(first[1], 443, out host, out port))
                {
                    await RefuseAsync(stream, first[1]).ConfigureAwait(false);
                    return;
                }
            }
            else if (Uri.TryCreate(first[1], UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
            {
                host = uri.Host;
                port = uri.Port;
                // The request goes on with the form an origin server reads, and its other lines as they came.
                forward = Encoding.ASCII.GetBytes($"{first[0]} {uri.PathAndQuery} {first[2]}\r\n" + string.Concat(lines.Skip(1).Select(static line => line + "\r\n")) + "\r\n");
            }
            else
            {
                await RefuseAsync(stream, first[1]).ConfigureAwait(false);
                return;
            }

            var address = await ResolveAllowedAsync(host, port, _closing.Token).ConfigureAwait(false);
            if (address is null)
            {
                Refused($"{host}:{port}");
                await RefuseAsync(stream, $"{host}:{port}").ConfigureAwait(false);
                return;
            }

            using var upstream = new TcpClient(address.AddressFamily);
            await upstream.ConnectAsync(address, port, _closing.Token).ConfigureAwait(false);
            var target = upstream.GetStream();
            if (forward is null) await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), _closing.Token).ConfigureAwait(false);
            else await target.WriteAsync(forward, _closing.Token).ConfigureAwait(false);
            await Task.WhenAny(stream.CopyToAsync(target, _closing.Token), target.CopyToAsync(stream, _closing.Token)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The connection ends with either side.
        }
    }

    private void Refused(string target)
    {
        _refusals.Enqueue(target);
        while (_refusals.Count > MaximumRefusals && _refusals.TryDequeue(out _))
        {
        }
    }

    private async Task RefuseAsync(NetworkStream stream, string target)
    {
        var body = Encoding.UTF8.GetBytes($"The sandbox of this session does not reach {target}: it is not a host of its profile.\n");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 403 Forbidden\r\nContent-Type: text/plain; charset=utf-8\r\nX-CodeAlta-Sandbox: refused\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, _closing.Token).ConfigureAwait(false);
        await stream.WriteAsync(body, _closing.Token).ConfigureAwait(false);
    }

    // The request line and the headers, up to the blank line; what follows stays in the stream.
    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        while (buffer.Count < MaximumHeader)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0) return null;
            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
                return Encoding.ASCII.GetString([.. buffer]).TrimEnd('\r', '\n');
        }

        return null;
    }

    private static bool TrySplitHostPort(string value, int defaultPort, out string host, out int port)
    {
        host = value;
        port = defaultPort;
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || value.EndsWith(']')) return value.Length > 0;
        host = value[..colon];
        return int.TryParse(value[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) && port is > 0 and < 65536;
    }

    private static bool IsLocalName(string name)
        => name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets whether an address is one of this computer or of its networks, which no host of a profile reaches.</summary>
    /// <param name="address">The address.</param>
    /// <returns>True for loopback, private, link-local, carrier-grade, unique-local and unspecified addresses.</returns>
    public static bool IsLocalAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast;
        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127 || bytes[0] >= 224
               || bytes[0] == 169 && bytes[1] == 254
               || bytes[0] == 172 && bytes[1] is >= 16 and < 32
               || bytes[0] == 192 && bytes[1] == 168
               || bytes[0] == 100 && bytes[1] is >= 64 and < 128;
    }
}
