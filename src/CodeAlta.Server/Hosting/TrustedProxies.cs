using System.Net;
using Microsoft.AspNetCore.Http;

namespace CodeAlta.Server.Hosting;

/// <summary>
/// The reverse proxies in front of the server that its owner trusts (<c>--trusted-proxy</c>): Tailscale Serve, Caddy, a load
/// balancer. Only a request whose peer is one of them has its <c>X-Forwarded-For</c> read, from its right end, past the
/// trusted proxies of the chain: what a caller wrote before the first trusted proxy added its address is never believed.
/// The address found is what the rate limits count and the audit log records; without a trusted proxy the peer is.
/// </summary>
public sealed class TrustedProxies
{
    /// <summary>The header a proxy adds the address of its caller to.</summary>
    public const string ForwardedForHeader = "X-Forwarded-For";

    private const int MaxForwardedAddresses = 32;

    private readonly IPNetwork[] _networks;

    /// <summary>Initializes a new instance of the <see cref="TrustedProxies"/> class.</summary>
    /// <param name="proxies">Addresses or networks (<c>127.0.0.1</c>, <c>::1</c>, <c>10.0.0.0/8</c>).</param>
    /// <exception cref="ArgumentException">An entry is neither an address nor a network.</exception>
    public TrustedProxies(IEnumerable<string> proxies)
    {
        ArgumentNullException.ThrowIfNull(proxies);
        _networks = [.. proxies.Select(static text => text.Trim()).Where(static text => text.Length > 0).Select(Parse)];
    }

    /// <summary>Gets whether any proxy is trusted.</summary>
    public bool Any => _networks.Length > 0;

    /// <summary>Gets the address of the caller of a request.</summary>
    /// <param name="peer">The peer of the connection.</param>
    /// <param name="headers">The headers of the request.</param>
    /// <returns>The address its trusted proxies forward, or <paramref name="peer"/>.</returns>
    public IPAddress? ClientOf(IPAddress? peer, IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (peer is null || !IsTrusted(peer) || !headers.TryGetValue(ForwardedForHeader, out var values)) return peer;
        var hops = values.SelectMany(static value => (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray();
        // From the proxy next to the server back toward the caller: the first address no trusted proxy has is the caller's.
        for (var index = hops.Length - 1; index >= 0 && index >= hops.Length - MaxForwardedAddresses; index--)
        {
            if (!IPAddress.TryParse(hops[index].Trim('[', ']'), out var hop)) return peer;
            if (!IsTrusted(hop)) return Normalize(hop);
        }

        return peer;
    }

    /// <summary>The header a proxy names the scheme its caller used in: <c>https</c> when it ended TLS.</summary>
    public const string ForwardedProtoHeader = "X-Forwarded-Proto";

    /// <summary>
    /// Gets the scheme the caller of a request used: the one a trusted proxy forwards (<c>X-Forwarded-Proto</c>, as
    /// Tailscale Serve and Caddy send it once they ended TLS), or the one of the request. A caller that is not a trusted
    /// proxy names no scheme.
    /// </summary>
    /// <param name="peer">The peer of the connection.</param>
    /// <param name="headers">The headers of the request.</param>
    /// <param name="scheme">The scheme of the request.</param>
    /// <returns><c>http</c> or <c>https</c>.</returns>
    public string SchemeOf(IPAddress? peer, IHeaderDictionary headers, string scheme)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (peer is null || !IsTrusted(peer) || !headers.TryGetValue(ForwardedProtoHeader, out var values)) return scheme;
        // The proxy next to the server writes the scheme it was reached with; a chain lists the first one's on the left.
        var first = values.SelectMany(static value => (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault();
        return first?.ToLowerInvariant() is "https" or "http" ? first.ToLowerInvariant() : scheme;
    }

    /// <summary>Gets whether an address is a trusted proxy.</summary>
    /// <param name="address">The address.</param>
    /// <returns>Whether one of the networks holds it.</returns>
    public bool IsTrusted(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var normalized = Normalize(address);
        return _networks.Any(network => network.Contains(normalized));
    }

    private static IPNetwork Parse(string text)
    {
        if (IPNetwork.TryParse(text, out var network)) return network;
        if (IPAddress.TryParse(text, out var address)) return new IPNetwork(Normalize(address), Normalize(address).GetAddressBytes().Length * 8);
        throw new ArgumentException($"'{text}' is not an address or a network (127.0.0.1, ::1, 10.0.0.0/8) of a trusted proxy.", nameof(text));
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
