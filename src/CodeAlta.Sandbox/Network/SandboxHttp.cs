using System.Net.Http;
using System.Net.Sockets;

namespace CodeAlta.Sandbox.Network;

/// <summary>
/// The HTTP client of the requests CodeAlta makes for a sandboxed session (its <c>webget</c>): each connection it
/// opens follows the network of the sandbox, redirections included, since a redirection opens a connection too.
/// </summary>
public static class SandboxHttp
{
    /// <summary>Creates the client of a policy.</summary>
    /// <param name="policy">The policy of the session.</param>
    /// <param name="reservedPorts">Ports of this computer that are never reached: CodeAlta's own.</param>
    /// <returns>The client; with <see cref="SandboxNetwork.Off"/>, one that opens no connection.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static HttpClient Create(SandboxPolicy policy, IReadOnlySet<int> reservedPorts)
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
}
