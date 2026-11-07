using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodeAlta.Fleet.Server;

/// <summary>What the browser brought back to the loopback address.</summary>
/// <param name="Code">The code, or null when the provider refused.</param>
/// <param name="State">The state.</param>
/// <param name="Error">The error the provider gave (<c>access_denied</c> when the person cancelled), or null.</param>
public sealed record FleetLoopbackAnswer(string? Code, string? State, string? Error);

/// <summary>The name a loopback redirect gives the browser to come back to.</summary>
public enum FleetLoopbackHost
{
    /// <summary><c>http://127.0.0.1:&lt;port&gt;/callback</c>: the clients CodeAlta registers (Microsoft, Google).</summary>
    IPAddress,

    /// <summary>
    /// <c>http://localhost:&lt;port&gt;/callback</c>: a company's own client, since Entra ID's Web platform registers only
    /// <c>https://</c> or <c>http://localhost</c> addresses. A browser may resolve the name to IPv4 or IPv6: both are listened on.
    /// </summary>
    Localhost,
}

/// <summary>
/// The loopback redirect of a sign-in through the system browser (RFC 8252): a listener on the loopback and a port of the
/// system, which answers the one request that brings the code back with a page that sends the person back to CodeAlta.
/// It reads nothing but the request line, needs no URL reservation, and closes once answered or disposed.
/// </summary>
public sealed class FleetLoopbackRedirect : IDisposable
{
    private const string CallbackPath = "/callback";
    private const int MaxRequestLine = 8192;
    private const int PortAttempts = 8;

    private readonly TcpListener[] _listeners;
    private readonly string _path;
    private int _disposed;

    private FleetLoopbackRedirect(TcpListener[] listeners, string host)
    {
        _listeners = listeners;
        _path = CallbackPath;
        RedirectUri = $"http://{host}:{((IPEndPoint)listeners[0].LocalEndpoint).Port}{CallbackPath}";
    }

    private FleetLoopbackRedirect(TcpListener[] listeners, Uri redirect)
    {
        _listeners = listeners;
        _path = redirect.AbsolutePath;
        RedirectUri = redirect.GetLeftPart(UriPartial.Path);
    }

    /// <summary>
    /// Starts a listener on the exact loopback address a sign-in of another machine names (a node that a desktop signs in): its
    /// port and its path. <c>localhost</c> is listened on for IPv4 and IPv6, as the browser may resolve either.
    /// </summary>
    /// <param name="redirect">The address: <c>http</c>, a loopback host, a port of 1024 and up.</param>
    /// <returns>The listener.</returns>
    /// <exception cref="ArgumentException">The address is not an <c>http</c> loopback address with a port of 1024 and up.</exception>
    /// <exception cref="SocketException">The port is taken on this computer.</exception>
    public static FleetLoopbackRedirect StartAt(Uri redirect)
    {
        ArgumentNullException.ThrowIfNull(redirect);
        if (!redirect.IsAbsoluteUri || redirect.Scheme != Uri.UriSchemeHttp || !redirect.IsLoopback || redirect.IsDefaultPort || redirect.Port < 1024)
            throw new ArgumentException("A sign-in comes back to an http loopback address with a port of 1024 and up.", nameof(redirect));
        var addresses = redirect.HostNameType switch
        {
            UriHostNameType.IPv4 => [IPAddress.Loopback],
            UriHostNameType.IPv6 => [IPAddress.IPv6Loopback],
            _ => Socket.OSSupportsIPv6 ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : [IPAddress.Loopback],
        };
        var listeners = new List<TcpListener>(addresses.Length);
        try
        {
            foreach (var address in addresses)
            {
                var listener = new TcpListener(address, redirect.Port);
                listener.Start();
                listeners.Add(listener);
            }
        }
        catch
        {
            foreach (var listener in listeners) listener.Stop();
            throw;
        }

        return new FleetLoopbackRedirect([.. listeners], redirect);
    }

    /// <summary>
    /// Waits for the browser to come back with the state of the sign-in, as <see cref="WaitAsync(string, CancellationToken)"/> does, and gives the whole
    /// address it came back to (its path and its query), for a sign-in another machine completes.
    /// </summary>
    /// <param name="state">The state the browser must bring back.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the browser brought back, and the address with its query.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task<(FleetLoopbackAnswer Answer, Uri Callback)> WaitForCallbackAsync(string state, CancellationToken cancellationToken)
    {
        var (answer, target) = await WaitCoreAsync(state, cancellationToken).ConfigureAwait(false);
        return (answer, new Uri(new Uri(RedirectUri), target));
    }

    /// <summary>Gets the address the browser comes back to.</summary>
    public string RedirectUri { get; }

    /// <summary>Starts a listener on <c>127.0.0.1</c>, on a port the system gives.</summary>
    /// <returns>The listener.</returns>
    /// <exception cref="SocketException">No port can be listened on.</exception>
    public static FleetLoopbackRedirect Start() => Start(FleetLoopbackHost.IPAddress);

    /// <summary>Starts a listener on the loopback, on a port the system gives, for the name of <paramref name="host"/>.</summary>
    /// <param name="host">The name the browser comes back to.</param>
    /// <returns>The listener.</returns>
    /// <exception cref="SocketException">No port can be listened on.</exception>
    public static FleetLoopbackRedirect Start(FleetLoopbackHost host)
    {
        if (host != FleetLoopbackHost.Localhost)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new FleetLoopbackRedirect([listener], "127.0.0.1");
        }

        // localhost: the same port on 127.0.0.1 and [::1] (where the system has IPv6), so the browser finds it whichever it resolves.
        for (var attempt = 0; ; attempt++)
        {
            var v4 = new TcpListener(IPAddress.Loopback, 0);
            v4.Start();
            if (!Socket.OSSupportsIPv6) return new FleetLoopbackRedirect([v4], "localhost");
            var v6 = new TcpListener(IPAddress.IPv6Loopback, ((IPEndPoint)v4.LocalEndpoint).Port);
            try
            {
                v6.Start();
                return new FleetLoopbackRedirect([v4, v6], "localhost");
            }
            catch (SocketException) when (attempt < PortAttempts)
            {
                // That port is taken on IPv6: another one.
                v4.Stop();
            }
        }
    }

    /// <summary>Waits for the browser to come back with the state of the sign-in; other requests (an icon) get a 404.</summary>
    /// <param name="state">The state the browser must bring back.</param>
    /// <param name="cancellationToken">Cancels the wait (the person gave up, the attempt expired).</param>
    /// <returns>What the browser brought back.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task<FleetLoopbackAnswer> WaitAsync(string state, CancellationToken cancellationToken)
        => (await WaitCoreAsync(state, cancellationToken).ConfigureAwait(false)).Answer;

    private async Task<(FleetLoopbackAnswer Answer, string Target)> WaitCoreAsync(string state, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(state);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var accepting = _listeners.Select(listener => listener.AcceptTcpClientAsync(stop.Token).AsTask()).ToArray();
        try
        {
            return await WaitAsync(state, accepting, stop.Token, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The listeners that did not get the request stop waiting; a connection one accepted meanwhile is closed.
            await stop.CancelAsync().ConfigureAwait(false);
            foreach (var pending in accepting)
            {
                try { (await pending.ConfigureAwait(false)).Dispose(); }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException) { }
            }
        }
    }

    private async Task<(FleetLoopbackAnswer Answer, string Target)> WaitAsync(string state, Task<TcpClient>[] accepting, CancellationToken acceptToken, CancellationToken cancellationToken)
    {
        while (true)
        {
            var accepted = await Task.WhenAny(accepting).ConfigureAwait(false);
            var index = Array.IndexOf(accepting, accepted);
            using var client = await accepted.ConfigureAwait(false);
            // That listener waits for the next request while this one is read.
            accepting[index] = _listeners[index].AcceptTcpClientAsync(acceptToken).AsTask();
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            reading.CancelAfter(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            string? line;
            try
            {
                line = await ReadRequestLineAsync(stream, reading.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            var (answer, target) = Parse(line, _path);
            if (answer is null || !string.Equals(answer.State, state, StringComparison.Ordinal))
            {
                await RespondAsync(stream, 404, "This is not the sign-in CodeAlta waits for.", reading.Token).ConfigureAwait(false);
                continue;
            }

            await RespondAsync(stream, 200, answer.Error is null ? "You are signed in. You can close this window and go back to CodeAlta."
                : "The sign-in did not complete. You can close this window and go back to CodeAlta.", reading.Token).ConfigureAwait(false);
            return (answer, target!);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var listener in _listeners) listener.Stop();
    }

    private static (FleetLoopbackAnswer? Answer, string? Target) Parse(string? line, string path)
    {
        // GET /callback?code=...&state=... HTTP/1.1
        var parts = line?.Split(' ');
        if (parts is not { Length: 3 } || parts[0] != "GET" || !parts[1].StartsWith(path, StringComparison.Ordinal)
            || parts[1].Length > path.Length && parts[1][path.Length] != '?') return (null, null);
        var query = parts[1].Length > path.Length ? parts[1][(path.Length + 1)..] : string.Empty;
        string? code = null, state = null, error = null;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;
            var value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            switch (pair[..separator])
            {
                case "code": code = value; break;
                case "state": state = value; break;
                case "error": error = value; break;
            }
        }

        return state is null || code is null && error is null ? (null, null) : (new FleetLoopbackAnswer(code, state, error), parts[1]);
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestLine];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            var end = Array.IndexOf(buffer, (byte)'\n', 0, length);
            if (end >= 0) return Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\r');
        }

        return null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>CodeAlta</title></head>"
            + $"<body style=\"font-family:system-ui,sans-serif;display:grid;place-items:center;min-height:90vh\"><p>{WebUtility.HtmlEncode(message)}</p></body></html>");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n"
            + "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            // The browser went: what it brought is all that counts.
        }
    }
}
