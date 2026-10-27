using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The explicit bridge grant for the links of the Markdown that the window shows: an address of the web opens in
/// the system browser, and a link to a file of the disk opens the code editor (<see cref="DesktopFileLinks"/>). A
/// picture of the disk is read for the window to draw it, and its file can be shown in the file manager.
/// </summary>
[NeoRpcService("markdownLinks", Version = 1)]
internal sealed class MarkdownLinksService
{
    private readonly string _epoch;
    private readonly Func<string, bool> _open;
    private readonly DesktopFileLinks? _files;

    /// <summary>Creates a grant for one owned host; tests supply an inert opener.</summary>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="open">Hands an address of the web to the system browser; false when it could not.</param>
    /// <param name="files">Follows a link to a file, or null when only addresses of the web are opened.</param>
    internal MarkdownLinksService(string epoch, Func<string, bool> open, DesktopFileLinks? files = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        ArgumentNullException.ThrowIfNull(open);
        (_epoch, _open, _files) = (epoch, open, files);
    }

    /// <summary>
    /// Opens a bounded, credential-free absolute HTTP(S) address in the system browser, and any other target as
    /// a link to a file of the disk, from the current host document.
    /// </summary>
    /// <remarks>
    /// The answer is <c>ok</c>, <c>failed</c>, <c>invalid_request</c> or <c>stale_epoch</c>, and for a file also
    /// <c>not_found</c> or <c>binary</c>.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The request was canceled before anything was opened.</exception>
    [NeoRpcMethod("open")]
    public Task<MarkdownLinkResponse> OpenAsync(MarkdownLinkRequest request, NeoRpcContext context, CancellationToken cancellationToken)
        => OpenFromAsync(request, context.ViewLabel, cancellationToken);

    /// <summary>Follows a link as <see cref="OpenAsync(MarkdownLinkRequest, NeoRpcContext, CancellationToken)"/> does, from no window in particular.</summary>
    /// <exception cref="OperationCanceledException">The request was canceled before anything was opened.</exception>
    internal Task<MarkdownLinkResponse> OpenAsync(MarkdownLinkRequest request, CancellationToken cancellationToken)
        => OpenFromAsync(request, null, cancellationToken);

    // A code editor that no window shows opens in the window where the link was followed.
    private async Task<MarkdownLinkResponse> OpenFromAsync(MarkdownLinkRequest request, string? originLabel, CancellationToken cancellationToken)
    {
        MarkdownLinkResponse Reply(string status) => new(status, _epoch);
        if (Validate(request) is { } refused) return Reply(refused);
        if (WebAddress(request.Address) is { } address)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return Reply(_open(address) ? "ok" : "failed"); }
            catch (Exception) { return Reply("failed"); } // Never send OS/browser diagnostics to the page.
        }

        if (_files is null || !DesktopFileLink.TryParse(request.Address, out var link)) return Reply("invalid_request");
        cancellationToken.ThrowIfCancellationRequested();
        try { return Reply(await _files.OpenAsync(link, request.SessionId, request.ProjectId, request.Directory, originLabel, cancellationToken).ConfigureAwait(false)); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return Reply("failed"); } // Nor the paths of the disk.
    }

    /// <summary>
    /// Reads a picture of the disk that a link of the Markdown names, for the window to draw it in the text: a PNG,
    /// JPEG, GIF, WebP or BMP file, by its name and by its content, of at most
    /// <see cref="ProjectFilesService.MaximumImageBytes"/>.
    /// </summary>
    /// <remarks>
    /// The answer is <c>ok</c> with the picture and the full path of its file, or <c>not_found</c>, <c>not_image</c>,
    /// <c>too_large</c>, <c>failed</c>, <c>invalid_request</c> or <c>stale_epoch</c> with nothing else.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The request was canceled.</exception>
    [NeoRpcMethod("image", TimeoutMilliseconds = 60_000)]
    public async Task<MarkdownImageResponse> ImageAsync(MarkdownLinkRequest request, CancellationToken cancellationToken)
    {
        MarkdownImageResponse Reply(string status) => new(status, _epoch, null, null, null);
        if (Validate(request) is { } refused) return Reply(refused);
        if (_files is null || !DesktopFileLink.TryParse(request.Address, out var link)) return Reply("invalid_request");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var image = await _files.ReadImageAsync(link, request.SessionId, request.ProjectId, request.Directory, cancellationToken).ConfigureAwait(false);
            return image.Status == "ok" ? new("ok", _epoch, image.MediaType, Convert.ToBase64String(image.Bytes!), image.FullPath) : Reply(image.Status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return Reply("failed"); }
    }

    /// <summary>Shows the file that a link of the Markdown names in the file manager of the system.</summary>
    /// <remarks>The answer is <c>ok</c>, <c>not_found</c>, <c>failed</c>, <c>invalid_request</c> or <c>stale_epoch</c>.</remarks>
    /// <exception cref="OperationCanceledException">The request was canceled before anything was shown.</exception>
    [NeoRpcMethod("reveal")]
    public async Task<MarkdownLinkResponse> RevealAsync(MarkdownLinkRequest request, CancellationToken cancellationToken)
    {
        MarkdownLinkResponse Reply(string status) => new(status, _epoch);
        if (Validate(request) is { } refused) return Reply(refused);
        if (_files is null || !DesktopFileLink.TryParse(request.Address, out var link)) return Reply("invalid_request");
        cancellationToken.ThrowIfCancellationRequested();
        try { return Reply(await _files.RevealAsync(link, request.SessionId, request.ProjectId, request.Directory, cancellationToken).ConfigureAwait(false)); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return Reply("failed"); }
    }

    // Why a request is not answered, or null when it names the epoch of this host.
    private string? Validate(MarkdownLinkRequest? request)
    {
        if (request is null || !Guid.TryParseExact(request.ExpectedHostEpoch, "D", out var epoch) || epoch == Guid.Empty
            || epoch.ToString("D") != request.ExpectedHostEpoch) return "invalid_request";
        return request.ExpectedHostEpoch != _epoch ? "stale_epoch" : null;
    }

    private static string? WebAddress(string? address)
    {
        if (address is not { Length: > 0 and <= DesktopLinks.MaximumLength }
            || address.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\\' or '﻿')) return null;
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

/// <summary>An explicitly activated link, the host epoch of its document, and where a relative path starts from.</summary>
/// <param name="ExpectedHostEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="Address">The target of the link, as it was written.</param>
/// <param name="SessionId">The session whose message has the link: a relative path starts from the folder it works in.</param>
/// <param name="ProjectId">Without a session, the project or the folder whose document has the link.</param>
/// <param name="Directory">The folder of that document inside the project, with forward slashes; null for the folder of the project.</param>
internal sealed record MarkdownLinkRequest(string ExpectedHostEpoch, string Address, string? SessionId = null, string? ProjectId = null, string? Directory = null);
/// <summary>A bounded result with no address, process details or private diagnostics.</summary>
internal sealed record MarkdownLinkResponse(string Status, string HostEpoch);
/// <summary>A picture that a link names: its media type, its content in base64 and the full path of its file, only for <c>ok</c>.</summary>
internal sealed record MarkdownImageResponse(string Status, string HostEpoch, string? MediaType, string? Base64, string? FullPath);
