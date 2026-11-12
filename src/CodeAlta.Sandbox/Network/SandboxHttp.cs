using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace CodeAlta.Sandbox.Network;

/// <summary>
/// The HTTP client of the requests CodeAlta makes for a sandboxed session (its <c>webget</c>): each connection it
/// opens follows the network of the sandbox, redirections included, since a redirection opens a connection too, and goes
/// through the upstream proxy it is given (the manual proxy of CodeAlta), as the proxy of the sandbox does.
/// </summary>
public static class SandboxHttp
{
    /// <summary>Creates the client of a policy, which reaches the hosts directly.</summary>
    /// <param name="policy">The policy of the session.</param>
    /// <param name="reservedPorts">Ports of this computer that are never reached: CodeAlta's own.</param>
    /// <returns>The client; with <see cref="SandboxNetwork.Off"/>, one that opens no connection.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static HttpClient Create(SandboxPolicy policy, IReadOnlySet<int> reservedPorts) => Create(policy, reservedPorts, upstream: null);

    /// <summary>Creates the client of a policy.</summary>
    /// <param name="policy">The policy of the session.</param>
    /// <param name="reservedPorts">Ports of this computer that are never reached: CodeAlta's own.</param>
    /// <param name="upstream">
    /// The proxy asked, for each connection, how to reach a host the sandbox may reach: a host it does not bypass is reached by a
    /// tunnel (<c>CONNECT</c>) to the address checked, signed in when the proxy asks; null to reach every host directly.
    /// </param>
    /// <returns>The client; with <see cref="SandboxNetwork.Off"/>, one that opens no connection.</returns>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="upstream"/> is null.</exception>
    public static HttpClient Create(SandboxPolicy policy, IReadOnlySet<int> reservedPorts, IWebProxy? upstream)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(reservedPorts);
        IReadOnlyList<string>? hosts = policy.Network == SandboxNetwork.Hosts ? policy.Hosts : null;
        var localPorts = policy.LocalPorts.ToHashSet();
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (policy.Network == SandboxNetwork.Off)
                    throw new HttpRequestException($"The sandbox of this session ({policy.ProfileName}) has no network.");
                var address = await SandboxNetworkProxy.ResolveAllowedAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, hosts, localPorts, reservedPorts, cancellationToken).ConfigureAwait(false)
                    ?? throw new HttpRequestException($"The sandbox of this session ({policy.ProfileName}) does not reach {context.DnsEndPoint.Host}.");
                if (UpstreamOf(upstream, context) is { } proxy) return await TunnelAsync(upstream!, proxy, address, context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    // The proxy that reaches the host of a connection, or null to reach it directly.
    private static Uri? UpstreamOf(IWebProxy? upstream, SocketsHttpConnectionContext context)
    {
        if (upstream is null) return null;
        var scheme = context.InitialRequestMessage.RequestUri?.Scheme == Uri.UriSchemeHttp ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;
        var host = context.DnsEndPoint.Host.Contains(':', StringComparison.Ordinal) && !context.DnsEndPoint.Host.StartsWith('[') ? $"[{context.DnsEndPoint.Host}]" : context.DnsEndPoint.Host;
        var target = new Uri($"{scheme}://{host}:{context.DnsEndPoint.Port.ToString(CultureInfo.InvariantCulture)}/");
        var proxy = upstream.IsBypassed(target) ? null : upstream.GetProxy(target);
        return proxy is null || proxy == target || proxy.Scheme is not ("http" or "https") ? null : proxy;
    }

    // A tunnel to the address checked, through the upstream proxy: the handler speaks TLS (with the name of the host) or
    // HTTP over it, as over a connection of its own.
    private static async ValueTask<Stream> TunnelAsync(IWebProxy upstream, Uri proxy, IPAddress address, DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        var text = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        var authority = text + ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
        var exchange = await UpstreamProxyClient.SendAsync(proxy, upstream.Credentials,
            auth => Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n{auth}\r\n"), cancellationToken).ConfigureAwait(false);
        if (exchange.Status == 200) return exchange.Stream;
        await exchange.DisposeAsync().ConfigureAwait(false);
        var status = exchange.Answer.Split("\r\n")[0];
        throw new HttpRequestException($"The proxy {proxy.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)} did not open a connection to {endpoint.Host}:{endpoint.Port}: {status}.");
    }
}
