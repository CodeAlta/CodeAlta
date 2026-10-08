using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>The explicit bridge grant for opening message Markdown links in the system browser.</summary>
[NeoRpcService("markdownLinks", Version = 1)]
internal sealed class MarkdownLinksService
{
    private readonly string _epoch;
    private readonly Func<string, bool> _open;

    /// <summary>Creates a grant for one owned host; tests supply an inert opener.</summary>
    internal MarkdownLinksService(string epoch, Func<string, bool> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        ArgumentNullException.ThrowIfNull(open);
        (_epoch, _open) = (epoch, open);
    }

    /// <summary>Opens only bounded, credential-free absolute HTTP(S) addresses from the current host document.</summary>
    /// <exception cref="OperationCanceledException">The request was canceled before the browser was invoked.</exception>
    [NeoRpcMethod("open")]
    public Task<MarkdownLinkResponse> OpenAsync(MarkdownLinkRequest request, CancellationToken cancellationToken)
    {
        MarkdownLinkResponse Reply(string status) => new(status, _epoch);
        if (request is null || !Guid.TryParseExact(request.ExpectedHostEpoch, "D", out var epoch) || epoch == Guid.Empty
            || epoch.ToString("D") != request.ExpectedHostEpoch) return Task.FromResult(Reply("invalid_request"));
        if (request.ExpectedHostEpoch != _epoch) return Task.FromResult(Reply("stale_epoch"));
        if (WebAddress(request.Address) is not { } address) return Task.FromResult(Reply("invalid_request"));
        cancellationToken.ThrowIfCancellationRequested();
        try { return Task.FromResult(Reply(_open(address) ? "ok" : "failed")); }
        catch (Exception) { return Task.FromResult(Reply("failed")); } // Never send OS/browser diagnostics to the page.
    }

    private static string? WebAddress(string? address)
    {
        if (address is not { Length: > 0 and <= DesktopLinks.MaximumLength }
            || address.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\\' or '\ufeff')) return null;
        var prefix = address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8
            : address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : 0;
        if (prefix == 0) return null;
        var authorityEnd = address.AsSpan(prefix).IndexOfAny('/', '?', '#');
        var authority = authorityEnd < 0 ? address.AsSpan(prefix) : address.AsSpan(prefix, authorityEnd);
        if (authority.IsEmpty || authority.Contains('@')) return null;
        for (var i = 0; i < address.Length; i++)
        {
            if (address[i] == '%' && (i + 2 >= address.Length || !Uri.IsHexDigit(address[i + 1]) || !Uri.IsHexDigit(address[i + 2]))) return null;
        }

        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.AbsoluteUri.Length <= DesktopLinks.MaximumLength ? uri.AbsoluteUri : null;
    }
}

/// <summary>An explicitly activated link and the host epoch of its document.</summary>
internal sealed record MarkdownLinkRequest(string ExpectedHostEpoch, string Address);
/// <summary>A bounded result with no address, process details or private diagnostics.</summary>
internal sealed record MarkdownLinkResponse(string Status, string HostEpoch);
