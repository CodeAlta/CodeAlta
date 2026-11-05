using System.Net;
using System.Net.Sockets;

namespace CodeAlta.Fleet.Channels;

/// <summary>A stream a <see cref="FleetDirectListener"/> accepted, with where it comes from.</summary>
/// <param name="Stream">The stream of the connection; the handler owns it.</param>
/// <param name="RemoteEndPoint">The address and port of the other end.</param>
/// <param name="LocalEndPoint">The address and port of this end.</param>
public sealed record FleetAcceptedStream(Stream Stream, IPEndPoint RemoteEndPoint, IPEndPoint LocalEndPoint)
{
    /// <summary>
    /// Gets whether the other end is on this machine: a loopback address, or the address of this end (a connection of this
    /// machine to its own address on the network).
    /// </summary>
    public bool IsFromThisMachine
    {
        get
        {
            var remote = Normalize(RemoteEndPoint.Address);
            return IPAddress.IsLoopback(remote) || remote.Equals(Normalize(LocalEndPoint.Address));
        }
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>
/// The TCP listener of direct session channels: it accepts connections and gives each stream to a handler, a bounded
/// number at a time. What runs on the stream (<see cref="FleetChannelStack.AcceptAsync"/>) is the handler's.
/// </summary>
public sealed class FleetDirectListener : IAsyncDisposable
{
    /// <summary>How many connections are handled at once, by default; the others wait in the backlog of the socket.</summary>
    public const int DefaultMaxConnections = 32;

    private readonly TcpListener _listener;
    private readonly Func<FleetAcceptedStream, CancellationToken, Task> _handler;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _running = [];
    private Task? _acceptLoop;

    private FleetDirectListener(TcpListener listener, Func<FleetAcceptedStream, CancellationToken, Task> handler, int maxConnections)
    {
        _listener = listener;
        _handler = handler;
        _slots = new SemaphoreSlim(maxConnections, maxConnections);
    }

    /// <summary>Gets the address and port the listener is bound to (the actual port when 0 was asked).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Binds a listener and starts accepting.</summary>
    /// <param name="endPoint">The address and port to bind; port 0 picks a free port.</param>
    /// <param name="handler">Handles each accepted stream; cancelled when the listener stops. An exception it throws is its own business: the connection is closed.</param>
    /// <param name="maxConnections">How many connections are handled at once.</param>
    /// <returns>The running listener.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxConnections"/> is not positive.</exception>
    /// <exception cref="SocketException">The address cannot be bound (in use, not an address of this machine).</exception>
    public static FleetDirectListener Start(IPEndPoint endPoint, Func<FleetAcceptedStream, CancellationToken, Task> handler, int maxConnections = DefaultMaxConnections)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConnections);
        var listener = new TcpListener(endPoint);
        if (endPoint.Address.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
        // Another process may not take the port beside this one.
        if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
        listener.Start();
        var direct = new FleetDirectListener(listener, handler, maxConnections);
        direct._acceptLoop = Task.Run(direct.AcceptLoopAsync);
        return direct;
    }

    private async Task AcceptLoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _slots.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException || token.IsCancellationRequested)
            {
                _slots.Release();
                return;
            }
            catch (SocketException)
            {
                // A connection reset before it was accepted: the next one is accepted.
                _slots.Release();
                continue;
            }

            socket.NoDelay = true;
            var accepted = new FleetAcceptedStream(new NetworkStream(socket, ownsSocket: true), (IPEndPoint)socket.RemoteEndPoint!, (IPEndPoint)socket.LocalEndPoint!);
            var work = HandleAsync(accepted, token);
            lock (_gate)
            {
                _running.Add(work);
            }

            _ = work.ContinueWith(Forget, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(FleetAcceptedStream accepted, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await _handler(accepted, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The handler reports its own failures; the connection ends with it.
            await accepted.Stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    private static void Forget(Task work, object? state)
    {
        var listener = (FleetDirectListener)state!;
        lock (listener._gate)
        {
            listener._running.Remove(work);
        }
    }

    /// <summary>Stops accepting and waits for the handlers, which are cancelled.</summary>
    /// <returns>A task that completes once the listener and its handlers ended.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_acceptLoop is not null) await _acceptLoop.ConfigureAwait(false);
        Task[] running;
        lock (_gate)
        {
            running = [.. _running];
            _running.Clear();
        }

        await Task.WhenAll(running).ConfigureAwait(false);
        _stop.Dispose();
    }
}

/// <summary>Opens the TCP stream of a direct channel to one of the addresses of a node.</summary>
public static class FleetDirectConnector
{
    /// <summary>Connects to the first address of a node that answers, in their order.</summary>
    /// <param name="addresses">The addresses of the node, as <c>host:port</c> (an IPv6 address in brackets).</param>
    /// <param name="timeoutPerAddress">How long each address is given.</param>
    /// <param name="cancellationToken">Cancels the attempts.</param>
    /// <returns>The stream of the first connection that opened, and the address it went to.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="addresses"/> is null.</exception>
    /// <exception cref="IOException">
    /// No address answered; the message names the failure of each address, and the inner exception holds them (an
    /// <see cref="AggregateException"/> when there are several).
    /// </exception>
    public static async Task<(Stream Stream, string Address)> ConnectAsync(IReadOnlyList<string> addresses, TimeSpan timeoutPerAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        // Each address fails in its own way (the loopback of this computer refuses, the network of the machine has no
        // route...): the last one alone can hide the one that tells what to do.
        var failures = new List<(string Address, Exception Failure)>();
        foreach (var address in addresses)
        {
            if (!TrySplit(address, out var host, out var port))
            {
                failures.Add((address, new FormatException($"'{address}' is not an address of the form host:port.")));
                continue;
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutPerAddress);
            try
            {
                await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                return (new NetworkStream(socket, ownsSocket: true), address);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                failures.Add((address, exception is OperationCanceledException ? new TimeoutException($"No answer within {timeoutPerAddress.TotalSeconds:0} s.") : exception));
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        if (failures.Count == 0) throw new IOException("The machine has no address to connect to.");
        var inner = failures.Count == 1 ? failures[0].Failure : new AggregateException(failures.Select(static failure => failure.Failure));
        throw new IOException("No address of the machine answered: " + string.Join("; ", failures.Select(static failure => $"{failure.Address} {failure.Failure.Message}")), inner);
    }

    /// <summary>Splits an address of the form <c>host:port</c> or <c>[ipv6]:port</c>.</summary>
    /// <param name="address">The address.</param>
    /// <param name="host">The host, without brackets.</param>
    /// <param name="port">The port.</param>
    /// <returns>Whether the address has that form.</returns>
    public static bool TrySplit(string? address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrEmpty(address)) return false;
        var separator = address.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(address.AsSpan(separator + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65535) return false;
        host = address[..separator];
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        return host.Length > 0;
    }

    /// <summary>Formats an end point as an address of a pairing payload: <c>host:port</c>, an IPv6 address in brackets.</summary>
    /// <param name="address">The address.</param>
    /// <param name="port">The port.</param>
    /// <returns>The address.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    public static string Format(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address.ToString().Split('%')[0]}]:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : $"{address}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }
}
