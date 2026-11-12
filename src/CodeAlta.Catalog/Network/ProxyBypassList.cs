using System.Globalization;
using System.Net;

namespace CodeAlta.Catalog.Network;

/// <summary>
/// The hosts a manual proxy does not serve, as <c>NO_PROXY</c> names them: <c>*</c> for every host, a name that also covers
/// its subdomains (<c>example.com</c>, <c>.example.com</c> and <c>*.example.com</c> say the same), an address, or a CIDR range
/// (<c>10.0.0.0/8</c>). A port after a name or an IPv4 address is ignored. This computer is never served by the proxy.
/// </summary>
/// <remarks>An instance is immutable.</remarks>
public sealed class ProxyBypassList
{
    /// <summary>The entries every list adds for the programs CodeAlta starts: this computer.</summary>
    public static IReadOnlyList<string> LoopbackEntries { get; } = ["localhost", "127.0.0.1", "::1"];

    private readonly bool _everything;
    private readonly string[] _domains;
    private readonly IPNetwork[] _networks;

    /// <summary>Initializes a list from its entries.</summary>
    /// <param name="entries">The entries; an entry that is not valid (<see cref="GetEntryError"/>) is ignored.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is null.</exception>
    public ProxyBypassList(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var domains = new List<string>();
        var networks = new List<IPNetwork>();
        foreach (var entry in entries)
        {
            switch (Parse(entry))
            {
                case { Everything: true }: _everything = true; break;
                case { Network: { } network }: networks.Add(network); break;
                case { Domain: { } domain }: domains.Add(domain); break;
            }
        }

        _domains = [.. domains.Distinct(StringComparer.OrdinalIgnoreCase)];
        _networks = [.. networks.Distinct()];
    }

    /// <summary>Gets whether a host is reached without the proxy.</summary>
    /// <param name="host">The name or address of the host, an IPv6 address with or without its brackets.</param>
    /// <returns>True for this computer, for every host when the list has <c>*</c>, and for the hosts of the list.</returns>
    public bool Matches(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.Trim().TrimStart('[').TrimEnd(']').TrimEnd('.');
        if (IsLoopback(name)) return true;
        if (_everything) return true;
        if (IPAddress.TryParse(name, out var address))
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            foreach (var network in _networks)
            {
                if (network.Contains(address)) return true;
            }

            // An address the list names as text, such as a zone-qualified IPv6 address.
            return _domains.Any(domain => string.Equals(domain, name, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var domain in _domains)
        {
            if (name.Equals(domain, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Length > domain.Length && name.EndsWith(domain, StringComparison.OrdinalIgnoreCase) && name[name.Length - domain.Length - 1] == '.') return true;
        }

        return false;
    }

    /// <summary>Gets whether a host is this computer: <c>localhost</c>, a name under <c>.localhost</c>, or a loopback address.</summary>
    /// <param name="host">The name or address, without brackets.</param>
    /// <returns>True for this computer.</returns>
    public static bool IsLoopback(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }

    /// <summary>
    /// Gets an entry as the programs CodeAlta starts read it in <c>NO_PROXY</c> (curl and git, Python, Node.js, Go): <c>*.example.com</c>
    /// is written <c>.example.com</c>, which they all read as its subdomains (curl also as the domain itself), the port and the brackets
    /// of an address are dropped (they match by host), and a CIDR range is written with its base address. An entry that is not valid
    /// gives nothing.
    /// </summary>
    /// <param name="entry">The entry, as the configuration writes it.</param>
    /// <returns>The entry for <c>NO_PROXY</c>, or null.</returns>
    public static string? ToEnvironmentEntry(string? entry)
    {
        switch (Parse(entry))
        {
            case null: return null;
            case { Everything: true }: return "*";
            case { Network: { } network }:
                var single = network.PrefixLength == (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
                return single ? network.BaseAddress.ToString() : network.ToString();
        }

        var text = entry!.Trim();
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon) text = text[..colon];
        text = text.TrimEnd('.');
        return text.StartsWith("*.", StringComparison.Ordinal) ? text[1..] : text;
    }

    /// <summary>Gets why an entry is not one a bypass list takes, or null when it is.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The error, or <see langword="null"/>.</returns>
    public static string? GetEntryError(string? entry)
        => Parse(entry) is null ? $"'{entry}' is not a host, a domain, an address or a CIDR range of the proxy bypass list." : null;

    private static Entry? Parse(string? entry)
    {
        var text = entry?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 255) return null;
        if (text == "*") return new Entry(true, null, null);
        if (text.Contains('/', StringComparison.Ordinal)) return TryParseRange(text.TrimStart('[').Replace("]", "", StringComparison.Ordinal)) is { } range ? new Entry(false, null, range) : null;
        var unbracketed = text.StartsWith('[') && text.IndexOf(']') is var close and > 0 ? text[1..close] : text;
        if (IPAddress.TryParse(unbracketed, out var address))
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return new Entry(false, null, new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
        }

        // name:port or a.b.c.d:port: the port is ignored, as the proxy is chosen by host.
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536)
            return Parse(text[..colon]);
        var domain = text.StartsWith("*.", StringComparison.Ordinal) ? text[2..] : text.TrimStart('.');
        domain = domain.TrimEnd('.');
        return domain.Length > 0 && domain.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_')
               && !domain.Contains("..", StringComparison.Ordinal)
            ? new Entry(false, domain, null)
            : null;
    }

    // A range as users write it: the bits of the address past the prefix are dropped (192.168.1.10/24 is 192.168.1.0/24).
    private static IPNetwork? TryParseRange(string text)
    {
        var slash = text.IndexOf('/');
        if (!IPAddress.TryParse(text.AsSpan(0, slash), out var address)
            || !int.TryParse(text.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (prefix > bytes.Length * 8) return null;
        for (var bit = prefix; bit < bytes.Length * 8; bit++) bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        return new IPNetwork(new IPAddress(bytes), prefix);
    }

    private sealed record Entry(bool Everything, string? Domain, IPNetwork? Network);
}
