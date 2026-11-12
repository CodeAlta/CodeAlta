using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace CodeAlta.Sandbox.Network;

/// <summary>
/// The side of a sandbox that talks to the proxy of the organization (the manual proxy of CodeAlta): it sends a request head,
/// a <c>CONNECT</c> or a plain request, and answers the challenge of a proxy that asks who connects (<c>407</c>), with the
/// scheme the proxy offers and the credentials the proxy of CodeAlta gives for it: Negotiate and NTLM through
/// <see cref="NegotiateAuthentication"/> (the account of the process, or a user name and password), else Basic. Nothing is
/// sent before the proxy asks.
/// </summary>
internal static class UpstreamProxyClient
{
    /// <summary>The longest head of a request or an answer read.</summary>
    internal const int MaximumHead = 64 * 1024;

    // The longest body of a 407 read and dropped to reuse its connection; a longer one closes it.
    private const int MaximumDrainedBody = 256 * 1024;

    // The schemes answered, the strongest first. Negotiate and NTLM sign in a connection: their legs go on the same one.
    private static readonly string[] Schemes = ["Negotiate", "NTLM", "Basic"];

    /// <summary>Sends a request head to a proxy and answers its challenges.</summary>
    /// <param name="proxy">The address of the proxy, <c>http</c> or <c>https</c>.</param>
    /// <param name="credentials">What gives the credentials of a scheme for the proxy; null to answer no challenge.</param>
    /// <param name="headOf">Builds the head of the request, given a <c>Proxy-Authorization</c> line ending with CRLF, or an empty text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exchange: the connection and the answer the proxy gave last, whose body is still to read.</returns>
    /// <exception cref="IOException">The proxy closed the connection before it answered.</exception>
    internal static async Task<UpstreamExchange> SendAsync(Uri proxy, ICredentials? credentials, Func<string, byte[]> headOf, CancellationToken cancellationToken)
    {
        var exchange = await OpenAsync(proxy, cancellationToken).ConfigureAwait(false);
        try
        {
            await exchange.SendAsync(headOf(""), cancellationToken).ConfigureAwait(false);
            if (exchange.Status != 407 || credentials is null) return exchange;
            var offered = exchange.Challenges();
            foreach (var scheme in Schemes)
            {
                if (!offered.ContainsKey(scheme) || credentials.GetCredential(proxy, scheme) is not { } credential) continue;
                if (scheme == "Basic")
                {
                    // The account of the process has no password to send; a sandbox never signs in with it over Basic.
                    if (credential.UserName.Length == 0 || ReferenceEquals(credential, CredentialCache.DefaultNetworkCredentials)) continue;
                    var user = string.IsNullOrEmpty(credential.Domain) ? credential.UserName : credential.Domain + "\\" + credential.UserName;
                    exchange = await ReadyAsync(exchange, proxy, reopen: true, cancellationToken).ConfigureAwait(false) ?? throw new IOException("The proxy closed the connection.");
                    await exchange.SendAsync(headOf("Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + credential.Password)) + "\r\n"),
                        cancellationToken).ConfigureAwait(false);
                    return exchange;
                }

                NegotiateAuthentication authentication;
                try
                {
                    authentication = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
                    {
                        Package = scheme, Credential = credential, TargetName = "HTTP/" + proxy.IdnHost, RequiredProtectionLevel = ProtectionLevel.None,
                    });
                }
                catch (PlatformNotSupportedException)
                {
                    continue;
                }

                using var _ = authentication;
                byte[]? incoming = null;
                for (var leg = 0; leg < 4; leg++)
                {
                    var blob = authentication.GetOutgoingBlob(incoming, out var status);
                    // A scheme this computer cannot use (no Kerberos ticket, no NTLM): the next one is tried.
                    if (blob is null || status is not (NegotiateAuthenticationStatusCode.ContinueNeeded or NegotiateAuthenticationStatusCode.Completed)) break;
                    var ready = await ReadyAsync(exchange, proxy, reopen: leg == 0, cancellationToken).ConfigureAwait(false);
                    if (ready is null) return exchange;
                    exchange = ready;
                    await exchange.SendAsync(headOf($"Proxy-Authorization: {scheme} {Convert.ToBase64String(blob)}\r\n"), cancellationToken).ConfigureAwait(false);
                    if (exchange.Status != 407) return exchange;
                    if (!exchange.Challenges().TryGetValue(scheme, out var token) || string.IsNullOrEmpty(token)) return exchange;
                    try { incoming = Convert.FromBase64String(token); }
                    catch (FormatException) { return exchange; }
                }

                if (exchange.Status != 407) return exchange;
            }

            return exchange;
        }
        catch
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens a connection to a proxy, over TLS for an <c>https</c> one.</summary>
    /// <param name="proxy">The address of the proxy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exchange, with nothing sent.</returns>
    internal static async Task<UpstreamExchange> OpenAsync(Uri proxy, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(proxy.IdnHost, proxy.Port, cancellationToken).ConfigureAwait(false);
            Stream stream = new NetworkStream(socket, ownsSocket: true);
            if (proxy.Scheme == Uri.UriSchemeHttps)
            {
                var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                try { await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = proxy.IdnHost }, cancellationToken).ConfigureAwait(false); }
                catch
                {
                    await tls.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                stream = tls;
            }

            return new UpstreamExchange(stream);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Reads the head of a request or an answer, up to its blank line; what follows stays in the stream.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The head without its blank line, or null when the stream ended first or the head is too long.</returns>
    internal static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        while (buffer.Count < MaximumHead)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0) return null;
            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
                return Encoding.ASCII.GetString([.. buffer]).TrimEnd('\r', '\n');
        }

        return null;
    }

    /// <summary>Gets what a sandbox is told when the proxy did not open its tunnel: the status of the proxy and its challenge, without a body.</summary>
    /// <param name="answer">The head of the answer of the proxy.</param>
    /// <returns>The head to send, with its blank line.</returns>
    internal static byte[] RefusalOf(string answer)
    {
        var lines = answer.Split("\r\n").Where(static (line, index) => index == 0 || !(HeaderIs(line, "Content-Length") || HeaderIs(line, "Transfer-Encoding")
            || HeaderIs(line, "Connection") || HeaderIs(line, "Proxy-Connection")));
        return Encoding.ASCII.GetBytes(string.Join("\r\n", lines) + "\r\nX-CodeAlta-Sandbox: upstream\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    }

    /// <summary>Gets the value of a header of a head, or null.</summary>
    internal static string? HeaderOf(string head, string name)
        => head.Split("\r\n").Skip(1).Where(line => HeaderIs(line, name)).Select(static line => line[(line.IndexOf(':') + 1)..].Trim()).FirstOrDefault();

    private static bool HeaderIs(string line, string name)
        => line.Length > name.Length && line[name.Length] == ':' && line.StartsWith(name, StringComparison.OrdinalIgnoreCase);

    // The exchange on which the next leg goes: the same connection once the body of the 407 is read, or a new one when the
    // proxy closes it (null for a leg that must stay on its connection).
    private static async Task<UpstreamExchange?> ReadyAsync(UpstreamExchange exchange, Uri proxy, bool reopen, CancellationToken cancellationToken)
    {
        if (await exchange.TryDrainAsync(cancellationToken).ConfigureAwait(false)) return exchange;
        if (!reopen) return null;
        await exchange.DisposeAsync().ConfigureAwait(false);
        return await OpenAsync(proxy, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A connection to the proxy, and the answer it gave last.</summary>
    internal sealed class UpstreamExchange(Stream stream) : IAsyncDisposable
    {
        /// <summary>Gets the stream of the connection; it owns the connection.</summary>
        public Stream Stream { get; } = stream;

        /// <summary>Gets the head of the last answer, without its blank line.</summary>
        public string Answer { get; private set; } = "";

        /// <summary>Gets the status of the last answer.</summary>
        public int Status { get; private set; }

        /// <summary>Sends a request head and reads the head of the answer.</summary>
        /// <exception cref="IOException">The proxy closed the connection before it answered.</exception>
        public async Task SendAsync(byte[] head, CancellationToken cancellationToken)
        {
            await Stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
            Answer = await ReadHeadAsync(Stream, cancellationToken).ConfigureAwait(false) ?? throw new IOException("The proxy closed the connection before it answered.");
            var parts = Answer.Split(' ', 3);
            Status = parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status) ? status : 0;
        }

        /// <summary>Gets the challenges of the last answer, by scheme, with their token (empty for none).</summary>
        public Dictionary<string, string> Challenges()
        {
            var challenges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in Answer.Split("\r\n").Skip(1).Where(static line => HeaderIs(line, "Proxy-Authenticate")))
            {
                var value = line[(line.IndexOf(':') + 1)..].Trim();
                var space = value.IndexOf(' ');
                var scheme = space < 0 ? value : value[..space];
                challenges.TryAdd(scheme, space < 0 ? "" : value[(space + 1)..].Trim());
            }

            return challenges;
        }

        // Reads the body of the last answer to reuse the connection; false when the proxy closes it or the body has no length.
        public async Task<bool> TryDrainAsync(CancellationToken cancellationToken)
        {
            if (string.Equals(HeaderOf(Answer, "Connection"), "close", StringComparison.OrdinalIgnoreCase)
                || string.Equals(HeaderOf(Answer, "Proxy-Connection"), "close", StringComparison.OrdinalIgnoreCase)
                || HeaderOf(Answer, "Transfer-Encoding") is not null
                || !long.TryParse(HeaderOf(Answer, "Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > MaximumDrainedBody)
                return false;
            var buffer = new byte[4096];
            while (length > 0)
            {
                var read = await Stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), cancellationToken).ConfigureAwait(false);
                if (read == 0) return false;
                length -= read;
            }

            return true;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync() => Stream.DisposeAsync();
    }
}
