using System.Net;

namespace CodeAlta.Server.Services;

/// <summary>Where a request comes from, for the audit log and the loopback-only setup.</summary>
/// <param name="RemoteAddress">The address of the peer of the connection, or <see langword="null"/> when unknown.</param>
/// <param name="RequestId">The id of the request.</param>
public sealed record RequestOrigin(IPAddress? RemoteAddress, string RequestId)
{
    /// <summary>Gets the address as text, for the audit log.</summary>
    public string? SourceAddress => RemoteAddress is null ? null : Normalize(RemoteAddress).ToString();

    /// <summary>Gets whether the peer is this machine, by its loopback address. A proxy on the same machine counts as local: setup goes through it only while no owner exists, and the code is still needed.</summary>
    public bool IsLoopback => RemoteAddress is not null && IPAddress.IsLoopback(Normalize(RemoteAddress));

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
