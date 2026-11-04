using System.Net.Sockets;

namespace CodeAlta.Desktop.Fleet.MachineSetup;

/// <summary>What blocked a check that a machine answers from this computer.</summary>
/// <param name="Reason">What the connection met, in the words of the system ("No route to host"), for the person to see.</param>
/// <param name="BlockedHere">Whether this computer kept the application from the network, not the machine nor its firewall.</param>
internal sealed record ReachProblem(string Reason, bool BlockedHere = false);

/// <summary>Tells a connection that macOS kept from the local network.</summary>
internal static class LocalNetworkAccess
{
    /// <summary>
    /// Whether <paramref name="exception"/> is the refusal macOS gives an application that Privacy &amp; Security &gt;
    /// Local Network does not allow: "No route to host" at once, for an address of the local network that ssh and other
    /// programs of the computer reach. Only asked right after ssh reached the same machine, where a real lack of route
    /// is not the likely cause.
    /// </summary>
    /// <param name="exception">The failure of the connection.</param>
    /// <param name="isMacOS">Whether this computer runs macOS.</param>
    internal static bool IsBlockedHere(Exception? exception, bool isMacOS)
    {
        if (!isMacOS) return false;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.HostUnreachable }) return true;
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(inner => IsBlockedHere(inner, isMacOS))) return true;
        }

        return false;
    }

    /// <summary>The problem of a connection that failed with <paramref name="exception"/>.</summary>
    internal static ReachProblem ProblemOf(Exception exception, bool isMacOS)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var socket = exception as SocketException ?? exception.InnerException as SocketException;
        return new ReachProblem(socket?.Message ?? exception.InnerException?.Message ?? exception.Message, IsBlockedHere(exception, isMacOS));
    }
}
