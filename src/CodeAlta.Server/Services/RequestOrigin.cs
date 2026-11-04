using System.Net;

namespace CodeAlta.Server.Services;

/// <summary>Where a request comes from, for the audit log, the rate limits and the loopback-only setup.</summary>
/// <param name="RemoteAddress">The address of the peer of the connection, or <see langword="null"/> when unknown.</param>
/// <param name="RequestId">The id of the request.</param>
public sealed record RequestOrigin(IPAddress? RemoteAddress, string RequestId)
{
    /// <summary>
    /// Gets the address of the caller: the one a trusted proxy forwards (<see cref="Hosting.TrustedProxies"/>), or the peer.
    /// </summary>
    public IPAddress? ClientAddress
    {
        get => field ?? RemoteAddress;
        init;
    }

    /// <summary>Gets the address of the caller as text, for the audit log.</summary>
    public string? SourceAddress => ClientAddress is null ? null : Normalize(ClientAddress).ToString();

    /// <summary>
    /// Gets whether the caller is this machine, by its loopback address. A proxy on the same machine counts as local unless it
    /// is named a trusted proxy, whose callers are then who they are: setup goes through it only while no owner exists, and
    /// the code is still needed.
    /// </summary>
    public bool IsLoopback => RemoteAddress is not null && IPAddress.IsLoopback(Normalize(RemoteAddress)) && ClientAddress is { } client && IPAddress.IsLoopback(Normalize(client));

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
