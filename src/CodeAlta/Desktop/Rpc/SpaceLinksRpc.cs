using CodeAlta.Browser.Links;
using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Where the links of a space open, for the card of its page: the browser and the profile its setting names
/// (<see cref="SpaceLinks"/>), and the browsers of this computer with their profiles, the default one marked.
/// </summary>
[NeoRpcService("spaceLinks", Version = 1)]
internal sealed class SpaceLinksService
{
    private readonly SpaceCatalog? _catalog;
    private readonly Func<IReadOnlyList<LinkBrowser>>? _browsers;
    private readonly string? _epoch;

    /// <summary>Creates the service of a window whose host keeps no spaces: it answers <c>unavailable</c>.</summary>
    public SpaceLinksService()
    {
    }

    /// <summary>Creates the service of an owned host.</summary>
    /// <param name="catalog">The spaces.</param>
    /// <param name="browsers">Lists the browsers of this computer (<see cref="LinkBrowsers.Find"/>).</param>
    /// <param name="epoch">The host epoch.</param>
    internal SpaceLinksService(SpaceCatalog catalog, Func<IReadOnlyList<LinkBrowser>> browsers, string epoch)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(browsers);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_catalog, _browsers, _epoch) = (catalog, browsers, epoch);
    }

    /// <summary>Reads where the links of a space open, with the browsers of this computer.</summary>
    [NeoRpcMethod("get", TimeoutMilliseconds = 30_000)]
    public async Task<SpaceLinksResponse> GetAsync(SpaceLinksRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null, []);
        try
        {
            var space = await _catalog!.GetAsync(request.SpaceId ?? string.Empty, cancellationToken).ConfigureAwait(false);
            if (space is null) return new("not_found", null, null, []);
            // Reading the lists of profiles of the browsers touches the disk: off the thread of the call.
            var browsers = await Task.Run(_browsers!, cancellationToken).ConfigureAwait(false);
            return new("ok", null, Item(space.Settings.Links),
                [.. browsers.Select(static browser => new SpaceLinkBrowserItem(browser.Id, browser.Name, browser.SupportsProfiles, browser.Profiles, browser.IsDefault))]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("unreadable", null, null, []);
        }
    }

    /// <summary>
    /// Saves where the links of a space open: an empty browser is the default browser of the system, an empty profile the
    /// profile the browser opens. The other settings of the space stay as the file has them.
    /// </summary>
    [NeoRpcMethod("update", TimeoutMilliseconds = 30_000)]
    public async Task<SpaceLinksResponse> UpdateAsync(SpaceLinksUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null, []);
        var links = new SpaceLinks(Blank(request.Browser)?.ToLowerInvariant(), Blank(request.Profile));
        if (links.Problem() is { } problem) return new("invalid", problem, null, []);
        try
        {
            var change = await _catalog!.UpdateSettingsAsync(request.SpaceId ?? string.Empty,
                settings => settings with { Links = links.IsEmpty ? null : links }, cancellationToken).ConfigureAwait(false);
            var status = change.Status switch
            {
                SpaceChangeStatus.Ok => "ok",
                SpaceChangeStatus.NotFound => "not_found",
                SpaceChangeStatus.Conflict => "conflict",
                _ => "invalid",
            };
            return new(status, change.Succeeded ? null : change.Message, change.Space is { } space ? Item(space.Settings.Links) : null, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", null, null, []);
        }
    }

    private string? Refuse(string? expectedEpoch)
        => _catalog is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static SpaceLinksItem? Item(SpaceLinks? links) => links is null || links.IsEmpty ? null : new(links.Browser, links.Profile);

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>Asks where the links of a space open.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="SpaceId">The identifier of the space.</param>
internal sealed record SpaceLinksRequest(string ExpectedEpoch, string? SpaceId);

/// <summary>Saves where the links of a space open.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="SpaceId">The identifier of the space.</param>
/// <param name="Browser">The id of the browser; null or empty for the default browser of the system.</param>
/// <param name="Profile">The name of the profile; null or empty for the profile the browser opens.</param>
internal sealed record SpaceLinksUpdateRequest(string ExpectedEpoch, string? SpaceId, string? Browser, string? Profile);

/// <summary>
/// Where the links of a space open. Status is <c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>not_found</c>,
/// <c>invalid</c> (with the message of what is wrong), <c>conflict</c>, <c>unreadable</c> or <c>write_failed</c>.
/// </summary>
/// <param name="Status">How the call ended.</param>
/// <param name="Message">What was wrong, for a change that was not made.</param>
/// <param name="Links">The setting; null for the default browser in the profile it opens.</param>
/// <param name="Browsers">The browsers of this computer; only in the answer of <c>get</c>.</param>
internal sealed record SpaceLinksResponse(string Status, string? Message, SpaceLinksItem? Links, IReadOnlyList<SpaceLinkBrowserItem> Browsers);

/// <summary>The setting of a space: a value that is null is the default.</summary>
/// <param name="Browser">The id of the browser; null for the default browser of the system.</param>
/// <param name="Profile">The name of the profile; null for the profile the browser opens.</param>
internal sealed record SpaceLinksItem(string? Browser, string? Profile);

/// <summary>A browser of this computer that links can open in.</summary>
/// <param name="Id">The id the setting names it by.</param>
/// <param name="Name">Its name.</param>
/// <param name="SupportsProfiles">Whether a profile of it can be chosen.</param>
/// <param name="Profiles">The names of its profiles.</param>
/// <param name="IsDefault">Whether it is the default browser of the system.</param>
internal sealed record SpaceLinkBrowserItem(string Id, string Name, bool SupportsProfiles, IReadOnlyList<string> Profiles, bool IsDefault);
