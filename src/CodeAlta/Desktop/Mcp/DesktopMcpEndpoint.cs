using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Desktop.Mcp;

/// <summary>Where the MCP server of the application listens.</summary>
/// <param name="Host"><c>localhost</c> or an IP address.</param>
/// <param name="Port">The port; 0 for a free one.</param>
/// <param name="Chosen">
/// Whether the user named the port for this start. A port of the application's own choice that is taken is
/// replaced by a free one; one the user named is not.
/// </param>
internal sealed record DesktopMcpEndpoint(string Host, int Port, bool Chosen)
{
    /// <summary>The address the server listens on when none is named: this computer only.</summary>
    internal const string DefaultHost = "127.0.0.1";

    /// <summary>The port of the normal instance: ALTA on a telephone keypad.</summary>
    internal const int DefaultPort = 2582;

    /// <summary>The port of the developer instance, which runs beside the normal one.</summary>
    internal const int DeveloperPort = 2583;

    /// <summary>The path of the MCP endpoint.</summary>
    internal const string Path = "/mcp";

    /// <summary>
    /// The endpoint of a start: what its options name, or else the loopback address and the port of the
    /// instance. An instance on explicit roots is a test or automation, several of which run at once: it takes
    /// a free port.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    internal static DesktopMcpEndpoint For(DesktopLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var port = options.McpPort ?? (options.Owned is not { Home: null } ? 0 : options.Developer ? DeveloperPort : DefaultPort);
        return new DesktopMcpEndpoint(options.McpHost ?? DefaultHost, port, options.McpPort is not null);
    }

    /// <summary>The same address with another port.</summary>
    internal DesktopMcpEndpoint On(int port) => this with { Port = port };

    /// <summary>Whether only programs of this computer reach the address.</summary>
    internal bool IsLoopback => IsLoopbackHost(Host);

    /// <summary>The address of the endpoint as a client names it.</summary>
    internal string Url
    {
        get
        {
            var host = Host;
            if (IPAddress.TryParse(host, out var address))
            {
                // A server on every address is reached by the name of the computer.
                if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) host = Dns.GetHostName();
                else if (address.AddressFamily == AddressFamily.InterNetworkV6) host = "[" + address + "]";
            }

            return "http://" + host + ":" + Port.ToString(CultureInfo.InvariantCulture) + Path;
        }
    }

    /// <summary>Whether <paramref name="host"/> names this computer alone: <c>localhost</c> or a loopback address.</summary>
    internal static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var literal = host.Length > 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        return IPAddress.TryParse(literal, out var address) && IPAddress.IsLoopback(address);
    }
}

/// <summary>
/// Decides which requests the MCP server answers.
/// </summary>
/// <remarks>
/// <para>
/// A server on the loopback address is for the programs of this computer. A page in a browser is one of them,
/// and must not be: a request that names another host (a name an attacker resolves to this computer) or that
/// comes from the page of another site is refused.
/// </para>
/// <para>
/// A server other computers can reach asks every request for the access token.
/// </para>
/// </remarks>
internal static class DesktopMcpGuard
{
    /// <summary>The status of a request that is answered.</summary>
    internal const int Allowed = 200;

    /// <summary>Checks a request.</summary>
    /// <param name="host">The host of the request, without its port.</param>
    /// <param name="origin">The <c>Origin</c> header of the request, if it has one.</param>
    /// <param name="authorization">The <c>Authorization</c> header of the request, if it has one.</param>
    /// <param name="token">The access token the server asks for; null when it asks for none.</param>
    /// <returns><see cref="Allowed"/>, or the HTTP status the request is refused with: 401 or 403.</returns>
    internal static int Check(string? host, string? origin, string? authorization, string? token)
    {
        if (token is not null)
        {
            const string scheme = "Bearer ";
            if (authorization is null || !authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return 401;
            var given = Encoding.UTF8.GetBytes(authorization[scheme.Length..].Trim());
            return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(token)) ? Allowed : 401;
        }

        if (!DesktopMcpEndpoint.IsLoopbackHost(host)) return 403;
        if (string.IsNullOrEmpty(origin)) return Allowed;
        return Uri.TryCreate(origin, UriKind.Absolute, out var page) && page.Scheme is "http" or "https" && DesktopMcpEndpoint.IsLoopbackHost(page.Host)
            ? Allowed : 403;
    }

    /// <summary>
    /// Reads the access token of a server other computers can reach, and creates it the first time. It is kept
    /// from one run to the next, so that what a client was configured with stays valid.
    /// </summary>
    /// <param name="path">The file that holds the token.</param>
    /// <exception cref="IOException">The file cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be read or written.</exception>
    internal static string ReadOrCreateToken(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: >= 32 } existing && existing.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return existing;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, token);
        // The token is the user's alone, where the system has such permissions.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return token;
    }
}
