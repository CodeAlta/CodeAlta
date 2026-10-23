using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeAlta.Plugin.Mcp;
using NeoAstra;

namespace CodeAlta.Desktop;

/// <summary>
/// The views of MCP Apps the window shows: each is an HTML document of an MCP server, served on the <c>mcpapp</c> scheme
/// under a token that names it, on an origin of its session and server that is not the application's.
/// </summary>
/// <remarks>
/// A view is never on <c>app://codealta</c>, the origin the bridge of the window trusts: its frame reaches the page only
/// through <c>postMessage</c>, which the page checks. The document is served with the Content-Security-Policy its server
/// declared (<see cref="DesktopMcpAppPolicy"/>), and only to the frame of its token: nothing else of the scheme answers.
/// </remarks>
internal sealed partial class DesktopMcpAppViews : INeoResourceProvider
{
    /// <summary>The scheme of the views.</summary>
    internal const string Scheme = "mcpapp";

    /// <summary>The most views open at once; opening one more forgets the oldest, whose frame then gets no document.</summary>
    internal const int MaximumViews = 32;

    /// <summary>
    /// Gets whether the window shows views on this system: where no frame reaches its bridge. Since NeoAstra 1.9.0 the bridge
    /// answers the top-level page only on Windows, macOS and Linux (where it trusts the whole view, frames excluded).
    /// </summary>
    internal static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DesktopMcpAppView> _views = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary>Gets the number of open views.</summary>
    internal int Count { get { lock (_gate) return _views.Count; } }

    /// <summary>Gets the number of tokens the order of the views holds, open or closed.</summary>
    internal int QueuedTokens { get { lock (_gate) return _order.Count; } }

    /// <summary>Opens a view: its document is served under a new token from now on.</summary>
    /// <param name="sessionId">The session of the tool call.</param>
    /// <param name="server">The MCP server of the view.</param>
    /// <param name="tool">The tool of the call.</param>
    /// <param name="projectDirectory">The project of the session, or null.</param>
    /// <param name="view">The view as its server sent it.</param>
    /// <returns>The open view, with the address of its document.</returns>
    internal DesktopMcpAppView Open(string sessionId, string server, string tool, string? projectDirectory, McpAppView view)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentNullException.ThrowIfNull(view);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var opened = new DesktopMcpAppView(token, OriginOf(sessionId, server), sessionId, server, tool, projectDirectory,
            Encoding.UTF8.GetBytes(view.Html), DesktopMcpAppPolicy.Build(view.Meta));
        lock (_gate)
        {
            _views[token] = opened;
            _order.Enqueue(token);
            while (_views.Count > MaximumViews && _order.TryDequeue(out var oldest)) _views.Remove(oldest);
        }

        return opened;
    }

    /// <summary>Gets an open view by its token, or null.</summary>
    internal DesktopMcpAppView? Find(string? token)
    {
        if (token is null) return null;
        lock (_gate) return _views.GetValueOrDefault(token);
    }

    /// <summary>Closes a view: its document is no longer served, and its requests are refused.</summary>
    /// <returns>Whether the view was open.</returns>
    internal bool Close(string? token)
    {
        if (token is null) return false;
        lock (_gate)
        {
            if (!_views.Remove(token)) return false;
            // The order keeps the tokens of the open views only, so that it does not grow with each view opened and closed.
            if (_order.Count > 2 * _views.Count + 8)
            {
                var open = _order.Where(_views.ContainsKey).ToArray();
                _order.Clear();
                foreach (var kept in open) _order.Enqueue(kept);
            }

            return true;
        }
    }

    /// <inheritdoc />
    public NeoResourceResponse? GetResponse(NeoResourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Only the document of an open view, at its own address: `mcpapp://<origin>/view/<token>`.
        var match = ViewPath().Match(request.Uri.AbsolutePath);
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || !match.Success
            || Find(match.Groups[1].Value) is not { } view || !string.Equals(request.Uri.Host, view.Origin, StringComparison.OrdinalIgnoreCase))
        {
            return NeoResourceResponse.Empty(404, "Not Found");
        }

        return NeoResourceResponse.FromBytes(view.Document, "text/html; charset=utf-8", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Security-Policy"] = view.ContentSecurityPolicy,
            ["Cache-Control"] = "no-store",
            ["X-Content-Type-Options"] = "nosniff",
            ["Referrer-Policy"] = "no-referrer",
        });
    }

    // One origin per session and server: two views of a server in a session share their storage, no other view does.
    private static string OriginOf(string sessionId, string server)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + "\n" + server)))[..32];

    [GeneratedRegex("^/view/([0-9a-f]{32})$", RegexOptions.CultureInvariant)]
    private static partial Regex ViewPath();
}

/// <summary>An open view of an MCP App.</summary>
/// <param name="Token">The token that names it, in its address and in the requests of its frame.</param>
/// <param name="Origin">The host of its origin.</param>
/// <param name="SessionId">The session of its tool call, whose space of now each of its requests is checked in.</param>
/// <param name="Server">Its MCP server: the only one its requests reach.</param>
/// <param name="Tool">The tool of its call.</param>
/// <param name="ProjectDirectory">The project of the session, or null.</param>
/// <param name="Document">Its HTML document, in UTF-8.</param>
/// <param name="ContentSecurityPolicy">The policy its document is served with.</param>
internal sealed record DesktopMcpAppView(string Token, string Origin, string SessionId, string Server, string Tool, string? ProjectDirectory,
    byte[] Document, string ContentSecurityPolicy)
{
    /// <summary>Gets the address of its document.</summary>
    internal string FrameUrl => $"{DesktopMcpAppViews.Scheme}://{Origin}/view/{Token}";
}

/// <summary>
/// The Content-Security-Policy of the document of a view: the restrictive one of MCP Apps (2026-01-26), widened only by
/// the origins the view declares, each checked to be an origin and nothing else.
/// </summary>
internal static partial class DesktopMcpAppPolicy
{
    /// <summary>Builds the policy of a view from what it declares.</summary>
    internal static string Build(McpAppResourceMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var resources = Origins(meta.ResourceDomains);
        var connect = Origins(meta.ConnectDomains);
        var frames = Origins(meta.FrameDomains);
        var bases = Origins(meta.BaseUriDomains);
        return string.Join("; ",
            "default-src 'none'",
            $"script-src 'self' 'unsafe-inline'{resources}",
            $"style-src 'self' 'unsafe-inline'{resources}",
            $"img-src 'self' data:{resources}",
            $"font-src 'self'{resources}",
            $"media-src 'self' data:{resources}",
            connect.Length == 0 ? "connect-src 'none'" : $"connect-src{connect}",
            frames.Length == 0 ? "frame-src 'none'" : $"frame-src{frames}",
            "object-src 'none'",
            bases.Length == 0 ? "base-uri 'none'" : $"base-uri{bases}",
            "form-action 'none'");
    }

    // The declared values that are origins (`https://cdn.example.com`, `https://*.example.com`, `wss://host:443`), each
    // with a space before it; anything else, such as a keyword, a scheme alone or a value with a quote or `;`, is dropped.
    private static string Origins(IReadOnlyList<string> values)
    {
        var builder = new StringBuilder();
        foreach (var value in values.Distinct(StringComparer.OrdinalIgnoreCase).Take(32))
        {
            if (Origin().IsMatch(value)) builder.Append(' ').Append(value);
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"^(https?|wss?)://(\*\.)?[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*(:[0-9]{1,5})?(/[A-Za-z0-9._~%!$&()*+=:@/-]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Origin();
}
