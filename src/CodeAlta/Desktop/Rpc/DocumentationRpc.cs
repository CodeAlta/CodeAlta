using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Catalog.Documentation;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The user guide that ships with the application, for the Documentation tab of the window: its navigation, its
/// pages, its pictures, a search in its text, and a question about it for an agent.
/// </summary>
/// <remarks>
/// <para>
/// The page names a page of the guide by its path below the folder of the guide, and a picture by its file name.
/// <see cref="ShippedDocumentation"/> answers only for what it listed in that folder: the page is given no way to
/// read another file, and no path of the disk crosses the bridge in either direction.
/// </para>
/// <para>
/// A question creates a chat and sends it one prompt. It is asked only when the user asks it: nothing here runs a
/// model when a page is shown.
/// </para>
/// </remarks>
[NeoRpcService("documentation", Version = 1)]
internal sealed class DocumentationService
{
    /// <summary>The most entries of the navigation, and the most pages, that one answer lists.</summary>
    internal const int MaximumEntries = 512;

    private readonly ShippedDocumentation? _documentation;
    private readonly DocumentationAsker? _asker;
    private readonly DesktopDocumentationView? _view;
    private readonly Action? _asked;
    private readonly string? _epoch;
    private int _asking;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal DocumentationService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="documentation">The guide.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="view">Passes the requests to show a page to the window; null when nothing asks.</param>
    /// <param name="asker">Asks an agent a question; null when the host has no session to create.</param>
    /// <param name="asked">Called when a question created a chat, so that the window reads its sessions again.</param>
    /// <exception cref="ArgumentNullException"><paramref name="documentation"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal DocumentationService(ShippedDocumentation documentation, string epoch, DesktopDocumentationView? view = null, DocumentationAsker? asker = null, Action? asked = null)
    {
        ArgumentNullException.ThrowIfNull(documentation);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_documentation, _epoch, _view, _asker, _asked) = (documentation, epoch, view, asker, asked);
    }

    /// <summary>Returns the navigation of the guide and the list of its pages.</summary>
    [NeoRpcMethod("menu")]
    public DocumentationMenuResponse Menu(DocumentationMenuRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, [], [], false);
        var items = new List<DocumentationMenuItem>();
        void Add(IReadOnlyList<ShippedDocumentationMenuItem> entries, int depth, string? parent)
        {
            foreach (var entry in entries)
            {
                if (items.Count >= MaximumEntries) return;
                items.Add(new(entry.Path, entry.Title, entry.Icon, depth, parent));
                Add(entry.Children, depth + 1, entry.Path);
            }
        }

        Add(_documentation!.GetMenu(), 0, null);
        return new("ok", _documentation.Home, items, [.. _documentation.ListPages().Take(MaximumEntries).Select(static page => new DocumentationPageInfo(page.Path, page.Title))], _asker is not null);
    }

    /// <summary>Returns a page of the guide: its texts of Markdown and its pictures, in their order.</summary>
    /// <remarks>The answer is <c>ok</c>, <c>not_found</c> for a path that names no page of the guide, <c>unavailable</c> or <c>stale_epoch</c>.</remarks>
    [NeoRpcMethod("page")]
    public DocumentationPageResponse Page(DocumentationPageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null, []);
        if (_documentation!.ReadPage(request.Path) is not { } page) return new("not_found", null, null, []);
        return new("ok", page.Path, page.Title, [.. page.Blocks.Select(static block => block.Figure is { } figure
            ? new DocumentationBlock("figure", null, figure.Image, figure.Svg, figure.Alt, figure.Caption, figure.Width, figure.Height)
            : new DocumentationBlock("markdown", block.Markdown, null, null, null, null))]);
    }

    /// <summary>Returns a picture of the guide as base64.</summary>
    /// <remarks>The answer is <c>ok</c>, <c>not_found</c> for a name that is no picture of the guide, <c>unavailable</c> or <c>stale_epoch</c>.</remarks>
    [NeoRpcMethod("image")]
    public DocumentationImageResponse Image(DocumentationImageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        return _documentation!.ReadImage(request.Name) is { } image
            ? new("ok", image.MediaType, Convert.ToBase64String(image.Content.Span))
            : new("not_found", null, null);
    }

    /// <summary>Finds a text in the pages of the guide.</summary>
    [NeoRpcMethod("search")]
    public DocumentationSearchResponse Search(DocumentationSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        return new("ok", [.. _documentation!.Search(request.Text).Select(static hit => new DocumentationSearchHit(hit.Path, hit.Title, hit.Heading, hit.Text))]);
    }

    /// <summary>Asks an agent a question about the guide, or about one of its pages, in a new chat.</summary>
    /// <remarks>
    /// The answer is <c>ok</c> with the chat, <c>no_provider</c>, <c>busy</c> (the host takes no more command, or a
    /// question is being asked), <c>closing</c>, <c>not_sent</c> with the chat when its prompt was refused,
    /// <c>invalid_request</c>, <c>failed</c>, <c>unavailable</c> or <c>stale_epoch</c>.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The request was canceled before the chat was created.</exception>
    [NeoRpcMethod("ask", TimeoutMilliseconds = 120_000)]
    public async Task<DocumentationAskResponse> AskAsync(DocumentationAskRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (_asker is null) return new("unavailable", null, null);
        if (!DocumentationAsker.IsQuestion(request.Question)) return new("invalid_request", null, null);
        // The page the question is about is one of the guide, named as the guide writes it; anything else is no page.
        string? page = null;
        if (request.Page is not null && (page = _documentation!.ResolvePage(request.Page)) is null) return new("invalid_request", null, null);
        if (Interlocked.CompareExchange(ref _asking, 1, 0) != 0) return new("busy", null, null);
        try
        {
            var result = await _asker.AskAsync(request.Question!, page, cancellationToken).ConfigureAwait(false);
            if (result.SessionId is not null) _asked?.Invoke();
            return new(result.Status, result.SessionId, result.Problem);
        }
        finally
        {
            Volatile.Write(ref _asking, 0);
        }
    }

    /// <summary>The requests to show a page (<c>alta documentation open</c>, a link of a message) for this page, until the page goes away.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<DocumentationShowEvent> Watch(DocumentationWatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.DocumentationShowEvent);
    }

    internal async IAsyncEnumerable<DocumentationShowEvent> WatchAsync(DocumentationWatchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_view is null || !string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) yield break;
        // A page that does not read keeps the newest requests.
        var requests = Channel.CreateBounded<DocumentationShowEvent>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        using var registration = _view.Watch(value => requests.Writer.TryWrite(value));
        await foreach (var value in requests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
    }

    private string? Refuse(string? expectedEpoch)
        => _documentation is null || _epoch is null || !_documentation.Available ? "unavailable"
            : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";
}

/// <summary>Asks for the navigation of the guide.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
internal sealed record DocumentationMenuRequest(string ExpectedEpoch);

/// <summary>One entry of the navigation, in the order of the menu.</summary>
/// <param name="Path">The page of the entry.</param>
/// <param name="Title">The title of the entry.</param>
/// <param name="Icon">The name of the icon the menu gives the entry; null for none.</param>
/// <param name="Depth">0 for an entry of the menu of the guide, 1 for an entry of the menu of a folder.</param>
/// <param name="Parent">The page of the entry the folder belongs to; null at depth 0.</param>
internal sealed record DocumentationMenuItem(string Path, string Title, string? Icon, int Depth, string? Parent);

/// <summary>A page of the guide and its title.</summary>
internal sealed record DocumentationPageInfo(string Path, string Title);

/// <summary>The navigation of the guide, every page it has, and whether a question can be asked.</summary>
/// <param name="Status"><c>ok</c>, <c>unavailable</c> or <c>stale_epoch</c>.</param>
/// <param name="Home">The page the guide opens with.</param>
/// <param name="Items">The entries of the navigation.</param>
/// <param name="Pages">Every page, whether the navigation names it or not: a link can name any of them.</param>
/// <param name="CanAsk">Whether the host can ask an agent a question.</param>
internal sealed record DocumentationMenuResponse(string Status, string? Home, IReadOnlyList<DocumentationMenuItem> Items, IReadOnlyList<DocumentationPageInfo> Pages, bool CanAsk);

/// <summary>Asks for a page.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="Path">The path of the page below the folder of the guide, with forward slashes.</param>
internal sealed record DocumentationPageRequest(string ExpectedEpoch, string? Path);

/// <summary>One part of a page.</summary>
/// <param name="Kind"><c>markdown</c> or <c>figure</c>.</param>
/// <param name="Markdown">The Markdown of a text.</param>
/// <param name="Image">The file name of the picture of a figure, to ask <c>image</c> for; null for a drawing.</param>
/// <param name="Svg">The SVG of a drawing, which the page shows as an image and never as markup.</param>
/// <param name="Alt">What the picture shows.</param>
/// <param name="Caption">The caption of the figure, as inline HTML for the Markdown boundary.</param>
/// <param name="Width">The width of the picture in pixels, when its file says it: the page keeps its place before the picture is read.</param>
/// <param name="Height">The height of the picture in pixels, when its file says it.</param>
internal sealed record DocumentationBlock(string Kind, string? Markdown, string? Image, string? Svg, string? Alt, string? Caption, int? Width = null, int? Height = null);

/// <summary>A page of the guide.</summary>
internal sealed record DocumentationPageResponse(string Status, string? Path, string? Title, IReadOnlyList<DocumentationBlock> Blocks);

/// <summary>Asks for a picture.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="Name">The file name of the picture, as a figure gave it.</param>
internal sealed record DocumentationImageRequest(string ExpectedEpoch, string? Name);

/// <summary>A picture as base64, with its media type.</summary>
internal sealed record DocumentationImageResponse(string Status, string? MediaType, string? Base64);

/// <summary>Asks for the places of a text.</summary>
internal sealed record DocumentationSearchRequest(string ExpectedEpoch, string? Text);

/// <summary>A place where the text was found.</summary>
/// <param name="Path">The page.</param>
/// <param name="Title">The title of the page.</param>
/// <param name="Heading">The heading the place is under; null for the start of the page.</param>
/// <param name="Text">The line that has the text, without its markup.</param>
internal sealed record DocumentationSearchHit(string Path, string Title, string? Heading, string Text);

/// <summary>The places of a text.</summary>
internal sealed record DocumentationSearchResponse(string Status, IReadOnlyList<DocumentationSearchHit> Hits);

/// <summary>A question for an agent.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="Question">The question, as the user wrote it.</param>
/// <param name="Page">The page the question is about; null for the whole guide.</param>
internal sealed record DocumentationAskRequest(string ExpectedEpoch, string? Question, string? Page);

/// <summary>What asking gave: the chat to show, or why there is none.</summary>
internal sealed record DocumentationAskResponse(string Status, string? SessionId, string? Problem);

/// <summary>Asks for the requests to show a page.</summary>
internal sealed record DocumentationWatchRequest(string ExpectedEpoch);

/// <summary>A request to show the guide.</summary>
/// <param name="Page">The page to show; null for the page the tab shows already.</param>
/// <param name="Anchor">The heading of the page to go to; null for its top.</param>
internal sealed record DocumentationShowEvent(string? Page, string? Anchor);
