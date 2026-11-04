using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodeAlta.Fleet.Server;

/// <summary>What the browser brought back to the loopback address.</summary>
/// <param name="Code">The code, or null when the provider refused.</param>
/// <param name="State">The state.</param>
/// <param name="Error">The error the provider gave (<c>access_denied</c> when the person cancelled), or null.</param>
public sealed record FleetLoopbackAnswer(string? Code, string? State, string? Error);

/// <summary>
/// The loopback redirect of a sign-in through the system browser (RFC 8252): a listener on <c>127.0.0.1</c> and a port
/// of the system, which answers the one request that brings the code back with a page that sends the person back to
/// CodeAlta. It reads nothing but the request line, needs no URL reservation, and closes once answered or disposed.
/// </summary>
public sealed class FleetLoopbackRedirect : IDisposable
{
    private const string CallbackPath = "/callback";
    private const int MaxRequestLine = 8192;

    private readonly TcpListener _listener;
    private int _disposed;

    private FleetLoopbackRedirect(TcpListener listener)
    {
        _listener = listener;
        RedirectUri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{CallbackPath}";
    }

    /// <summary>Gets the address the browser comes back to.</summary>
    public string RedirectUri { get; }

    /// <summary>Starts a listener on the loopback, on a port the system gives.</summary>
    /// <returns>The listener.</returns>
    /// <exception cref="SocketException">No port can be listened on.</exception>
    public static FleetLoopbackRedirect Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new FleetLoopbackRedirect(listener);
    }

    /// <summary>Waits for the browser to come back with the state of the sign-in; other requests (an icon) get a 404.</summary>
    /// <param name="state">The state the browser must bring back.</param>
    /// <param name="cancellationToken">Cancels the wait (the person gave up, the attempt expired).</param>
    /// <returns>What the browser brought back.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task<FleetLoopbackAnswer> WaitAsync(string state, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(state);
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
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

            var answer = Parse(line);
            if (answer is null || !string.Equals(answer.State, state, StringComparison.Ordinal))
            {
                await RespondAsync(stream, 404, "This is not the sign-in CodeAlta waits for.", reading.Token).ConfigureAwait(false);
                continue;
            }

            await RespondAsync(stream, 200, answer.Error is null ? "You are signed in. You can close this window and go back to CodeAlta."
                : "The sign-in did not complete. You can close this window and go back to CodeAlta.", reading.Token).ConfigureAwait(false);
            return answer;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _listener.Stop();
    }

    private static FleetLoopbackAnswer? Parse(string? line)
    {
        // GET /callback?code=...&state=... HTTP/1.1
        var parts = line?.Split(' ');
        if (parts is not { Length: 3 } || parts[0] != "GET" || !parts[1].StartsWith(CallbackPath, StringComparison.Ordinal)) return null;
        var query = parts[1].Length > CallbackPath.Length && parts[1][CallbackPath.Length] == '?' ? parts[1][(CallbackPath.Length + 1)..] : string.Empty;
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

        return state is null || code is null && error is null ? null : new FleetLoopbackAnswer(code, state, error);
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
