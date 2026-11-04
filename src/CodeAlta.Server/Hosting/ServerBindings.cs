using System.Net;

namespace CodeAlta.Server.Hosting;

/// <summary>
/// The addresses a server listens on (<c>doc/fleet/security-and-privacy.md</c>, channels): plain HTTP on the loopback
/// only, where Tailscale Serve, Caddy or the TLS of a platform is put in front of it; beyond the loopback, an
/// <c>https://</c> address with a certificate, or plain HTTP only when its owner forces it on a network they trust.
/// </summary>
public static class ServerBindings
{
    /// <summary>The option of <c>altaserver run</c> and <c>install</c> that allows plain HTTP beyond the loopback.</summary>
    public const string AllowInsecureHttpOption = "--allow-insecure-http";

    /// <summary>Checks the addresses to listen on.</summary>
    /// <param name="urls">The addresses.</param>
    /// <param name="allowInsecureHttp">Whether plain HTTP beyond the loopback is forced.</param>
    /// <exception cref="ArgumentException">An address does not read, or listens in plain HTTP beyond the loopback without being forced.</exception>
    public static void Require(IEnumerable<string> urls, bool allowInsecureHttp)
    {
        ArgumentNullException.ThrowIfNull(urls);
        foreach (var url in urls)
        {
            // Kestrel's wildcards (http://*:2586, http://+:2586) are every interface.
            var readable = url.Replace("://*", "://0.0.0.0", StringComparison.Ordinal).Replace("://+", "://0.0.0.0", StringComparison.Ordinal);
            if (!Uri.TryCreate(readable, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException($"'{url}' is not an http:// or https:// address.", nameof(urls));
            if (uri.Scheme == "http" && !IsLoopback(uri) && !allowInsecureHttp)
            {
                throw new ArgumentException(
                    $"'{url}' would serve plain HTTP to other machines: anyone on the network path could read the setup code and the server's answers, and change them. " +
                    "Keep the server on the loopback (http://127.0.0.1) behind Tailscale Serve, Caddy or the TLS of your platform, or give it a certificate and an https:// address. " +
                    $"On a network you trust, {AllowInsecureHttpOption} forces it.", nameof(urls));
            }
        }
    }

    /// <summary>Gets whether an address is of this machine only.</summary>
    /// <param name="uri">The address.</param>
    /// <returns>Whether its host is a loopback address or <c>localhost</c>.</returns>
    public static bool IsLoopback(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }
}
