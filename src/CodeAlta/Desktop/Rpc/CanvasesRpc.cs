using CodeAlta.Plugins.Abstractions;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The canvases of plugins for the page: the ones that plugins declare, the instances that tabs have open, the
/// actions of their fragments, and one channel on which plugins push what the tabs show.
/// </summary>
/// <remarks>
/// A canvas is a tab that a plugin provides. The plugin holds the state; the page asks for it each time a tab is
/// shown, draws the HTML fragment it gets with its own components after sanitizing it, and sends back the actions
/// of the fragment. A plugin never reaches the page, and the text of a plugin failure does not cross the bridge.
/// One channel carries every event of every instance, so the number of canvases never uses up the channels of the page.
/// </remarks>
[NeoRpcService("canvases", Version = 1)]
internal sealed class CanvasesService
{
    private readonly DesktopCanvases? _canvases;
    private readonly PluginIcons _icons = new();
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without plugins.</summary>
    internal CanvasesService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="canvases">The broker the host gave its plugins as their canvas service.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="canvases"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal CanvasesService(DesktopCanvases canvases, string epoch)
    {
        ArgumentNullException.ThrowIfNull(canvases);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _canvases = canvases;
        _epoch = epoch;
    }

    /// <summary>Lists the canvases that active plugins declare.</summary>
    [NeoRpcMethod("list")]
    public CanvasListResponse List(CanvasListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        try
        {
            return new("ok", [.. _canvases!.Declarations().Select(Item).Take(PluginUiService.MaximumContributions)]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new("read_failed", []); // A plugin failing in a property getter must not break the page.
        }
    }

    /// <summary>
    /// Opens an instance for a tab, or returns the one that is open: its title, its status and its fragment. The plugin's
    /// handler runs once for an identity.
    /// </summary>
    [NeoRpcMethod("open", TimeoutMilliseconds = 40_000)]
    public async Task<CanvasOpenResponse> OpenAsync(CanvasOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return Refused(refused);
        if (request.PluginKey is null || request.CanvasId is null) return Refused("invalid_request");
        var opening = await _canvases!.OpenAsync(new(request.PluginKey, request.CanvasId, request.SpaceId, request.ProjectId, request.SessionId, request.Key),
            request.Visible, cancellationToken).ConfigureAwait(false);
        var declaration = opening.Declaration;
        if (opening.State is not { } state)
        {
            return new(opening.Status, null, declaration?.Canvas.Title is { } title ? Line(title) : null, null, null, false, 0, declaration?.Package, declaration?.Canvas.Icon, IconData(declaration));
        }

        return new("ok", state.InstanceId, state.Title, state.StatusText, state.Html, state.Actions, state.Revision, declaration?.Package, declaration?.Canvas.Icon, IconData(declaration));
    }

    /// <summary>Says whether a tab shows an instance now: the plugin sees it as <c>IsVisible</c>.</summary>
    [NeoRpcMethod("visible")]
    public CanvasStatusResponse Visible(CanvasVisibleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (!DesktopCanvases.ValidLine(request.InstanceId, DesktopCanvases.MaximumIdUnits)) return new("invalid_request");
        return new(_canvases!.SetVisible(request.InstanceId!, request.Visible) ? "ok" : "unknown");
    }

    /// <summary>Closes an instance, because its tab was closed. The state of the canvas stays with its plugin.</summary>
    [NeoRpcMethod("close")]
    public async Task<CanvasStatusResponse> CloseAsync(CanvasCloseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (!DesktopCanvases.ValidLine(request.InstanceId, DesktopCanvases.MaximumIdUnits)) return new("invalid_request");
        return new(await _canvases!.CloseAsync(request.InstanceId!, notifyPage: false).ConfigureAwait(false) ? "ok" : "unknown");
    }

    /// <summary>Closes the instances of a space that was deleted: its tabs are forgotten.</summary>
    [NeoRpcMethod("closeSpace")]
    public async Task<CanvasStatusResponse> CloseSpaceAsync(CanvasCloseSpaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (!DesktopCanvases.ValidLine(request.SpaceId, DesktopCanvases.MaximumIdUnits)) return new("invalid_request");
        await _canvases!.CloseSpaceAsync(request.SpaceId!).ConfigureAwait(false);
        return new("ok");
    }

    /// <summary>Runs the action of an element of the fragment of an open instance.</summary>
    [NeoRpcMethod("action", TimeoutMilliseconds = 40_000)]
    public async Task<CanvasActionResponse> ActionAsync(CanvasActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, false);
        if (!DesktopCanvases.ValidLine(request.InstanceId, DesktopCanvases.MaximumIdUnits) || request.Action is not { Length: > 0 and <= MaximumNameUnits }
            || request.Value is { Length: > MaximumFieldUnits } || !ValidFields(request.Values)) return new("invalid_request", null, false);
        var (status, html, closed) = await _canvases!.ActionAsync(request.InstanceId!, request.Action, request.Value, request.Values, cancellationToken).ConfigureAwait(false);
        return new(status, html, closed);
    }

    /// <summary>Describes what an open instance shows, in Markdown.</summary>
    [NeoRpcMethod("describe", TimeoutMilliseconds = 40_000)]
    public async Task<CanvasDescribeResponse> DescribeAsync(CanvasDescribeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!DesktopCanvases.ValidLine(request.InstanceId, DesktopCanvases.MaximumIdUnits)) return new("invalid_request", null);
        var (status, markdown) = await _canvases!.DescribeAsync(request.InstanceId!, cancellationToken).ConfigureAwait(false);
        return new(status, markdown);
    }

    /// <summary>What plugins ask of the tabs and push to them, until the page goes away.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<CanvasEvent> Watch(CanvasWatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.CanvasEvent);
    }

    internal async IAsyncEnumerable<CanvasEvent> WatchAsync(CanvasWatchRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_canvases is null || !string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) yield break;
        await foreach (var value in _canvases.WatchAsync(cancellationToken).ConfigureAwait(false)) yield return value;
    }

    private const int MaximumNameUnits = 128;
    private const int MaximumFieldUnits = 64 * 1024;
    private const int MaximumFields = 128;

    private string? Refuse(string? epoch)
    {
        if (_canvases is null) return "unavailable";
        return string.Equals(epoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";
    }

    private static CanvasOpenResponse Refused(string status) => new(status, null, null, null, null, false, 0, null, null, null);

    // The icon of a canvas that names a file of its plugin: the clean SVG of that file, as the page draws it.
    private string? IconData(CanvasDeclaration? declaration)
        => declaration?.Canvas.Icon is { Length: > 0 and <= 64 } icon && PluginIcons.IsFile(icon) ? _icons.Read(declaration.Plugin.SourcePackage?.PackageDirectory, icon) : null;

    private CanvasItem Item(CanvasDeclaration declaration)
    {
        var canvas = declaration.Canvas;
        return new(declaration.PluginKey, Line(declaration.Plugin.Descriptor.DisplayName ?? declaration.PluginKey), declaration.Package, canvas.Id, Line(canvas.Title),
            string.IsNullOrWhiteSpace(canvas.Description) ? null : Line(canvas.Description), canvas.Icon is { Length: > 0 and <= 64 } icon ? icon : null,
            canvas.Scope.ToString(), canvas.InputSchema is not null, canvas.Actions.Count, canvas.Describe is not null, IconData(declaration));
    }

    private static bool ValidFields(IReadOnlyDictionary<string, string>? values)
        => values is null || values.Count <= MaximumFields && values.All(static pair => pair.Key is { Length: > 0 and <= MaximumNameUnits } && pair.Value is { Length: <= MaximumFieldUnits });

    private static string Line(string text)
        => DesktopPluginUi.Cut(string.Concat(text.Select(static value => char.IsControl(value) ? ' ' : value)).Trim(), 1024);
}

/// <summary>Asks for the canvases that plugins declare.</summary>
internal sealed record CanvasListRequest(string? ExpectedEpoch);

/// <summary><c>ok</c> with the canvases in the order of the plugins' contributions, or a refusal code with none.</summary>
internal sealed record CanvasListResponse(string Status, CanvasItem[] Canvases);

/// <summary>One canvas that a plugin declares.</summary>
/// <param name="PluginKey">The runtime key of the plugin: with the id, it names the canvas.</param>
/// <param name="Plugin">The display name of the plugin.</param>
/// <param name="Package">The id of the folder of the plugin package, as the code editor and Settings name it; null for a built-in plugin.</param>
/// <param name="Id">The identifier of the canvas, unique within its plugin.</param>
/// <param name="Title">The title of its tabs.</param>
/// <param name="Description">What it shows and does, or null.</param>
/// <param name="Icon">The name of the icon of its tabs, or null.</param>
/// <param name="Scope"><c>Application</c>, <c>Project</c> or <c>Session</c>.</param>
/// <param name="Input">The canvas accepts an input.</param>
/// <param name="Actions">The number of actions it declares for agents.</param>
/// <param name="Describes">It can describe what it shows in Markdown.</param>
/// <param name="IconData">When the icon names a file of the plugin package, that file as a clean SVG data URL; null otherwise.</param>
internal sealed record CanvasItem(string PluginKey, string Plugin, string? Package, string Id, string Title, string? Description, string? Icon, string Scope,
    bool Input, int Actions, bool Describes, string? IconData = null);

/// <summary>Opens the instance a tab shows.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="PluginKey">The runtime key of the plugin.</param>
/// <param name="CanvasId">The canvas.</param>
/// <param name="SpaceId">The space of the tab.</param>
/// <param name="ProjectId">The project of a project canvas, or of the session of a session canvas.</param>
/// <param name="SessionId">The session of a session canvas.</param>
/// <param name="Key">The key that tells apart several instances in the same context.</param>
/// <param name="Visible">The tab is shown now.</param>
internal sealed record CanvasOpenRequest(string? ExpectedEpoch, string? PluginKey, string? CanvasId, string? SpaceId, string? ProjectId, string? SessionId, string? Key, bool Visible);

/// <summary>
/// <c>ok</c> with the instance and what it shows, or a refusal code: <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid_request</c>,
/// <c>plugin_stopped</c> (the plugin is not running), <c>unknown_canvas</c> (the plugin does not declare it), <c>failed</c>
/// (the plugin could not open it) or <c>limit</c>.
/// </summary>
/// <param name="Status">The status code.</param>
/// <param name="InstanceId">The identifier of the instance, which every later call and event names.</param>
/// <param name="Title">The title of the tab; the title of the canvas for a refusal that knows it.</param>
/// <param name="StatusText">The status text beside the title, or null.</param>
/// <param name="Html">The HTML fragment.</param>
/// <param name="Actions">The fragment raises actions: <c>action</c> reaches an action handler.</param>
/// <param name="Revision">Grows with every change of the content: an event with a revision that is not newer is dropped.</param>
/// <param name="Package">The id of the folder of the plugin package, or null for a built-in plugin.</param>
/// <param name="Icon">The name of the icon of the canvas, or null.</param>
/// <param name="IconData">When the icon names a file of the plugin package, that file as a clean SVG data URL; null otherwise.</param>
internal sealed record CanvasOpenResponse(string Status, string? InstanceId, string? Title, string? StatusText, string? Html, bool Actions, int Revision,
    string? Package, string? Icon, string? IconData = null);

/// <summary>Says whether a tab shows an instance.</summary>
internal sealed record CanvasVisibleRequest(string? ExpectedEpoch, string? InstanceId, bool Visible);

/// <summary>Closes an instance.</summary>
internal sealed record CanvasCloseRequest(string? ExpectedEpoch, string? InstanceId);

/// <summary>Closes the instances of a space.</summary>
internal sealed record CanvasCloseSpaceRequest(string? ExpectedEpoch, string? SpaceId);

/// <summary>A status code alone: <c>ok</c>, <c>unknown</c> or a refusal.</summary>
internal sealed record CanvasStatusResponse(string Status);

/// <summary>The action of an element of a fragment, with the values of its named fields.</summary>
internal sealed record CanvasActionRequest(string? ExpectedEpoch, string? InstanceId, string? Action, string? Value, Dictionary<string, string>? Values);

/// <param name="Status"><c>ok</c>, <c>unknown</c>, <c>unsupported</c>, <c>failed</c> or a refusal.</param>
/// <param name="Html">The new content of the tab, or null to keep it.</param>
/// <param name="Closed">The instance closed: so does its tab.</param>
internal sealed record CanvasActionResponse(string Status, string? Html, bool Closed);

/// <summary>Asks what an open instance shows.</summary>
internal sealed record CanvasDescribeRequest(string? ExpectedEpoch, string? InstanceId);

/// <summary><c>ok</c> with the Markdown, or null when the canvas does not describe itself; or a refusal code.</summary>
internal sealed record CanvasDescribeResponse(string Status, string? Markdown);

/// <summary>Asks for the events of the canvases.</summary>
internal sealed record CanvasWatchRequest(string? ExpectedEpoch);

/// <summary>
/// One event for the page.
/// </summary>
/// <param name="Kind">
/// <c>open</c> (a plugin asks for a tab: its canvas, its space and context, and whether to bring it to the front),
/// <c>update</c> (an instance shows another fragment, title or status: only what changed is set),
/// <c>state</c> (the plugin of an instance stopped, or its canvas is gone: <c>plugin_stopped</c>, <c>unknown_canvas</c>, <c>failed</c>),
/// <c>closed</c> (the plugin closed an instance: close its tab) or
/// <c>plugins</c> (plugins were started, replaced or stopped: read the canvases again).
/// </param>
internal sealed record CanvasEvent(string Kind)
{
    /// <summary>The instance an <c>update</c>, <c>state</c> or <c>closed</c> is about; for an <c>open</c>, the one the tab will have.</summary>
    public string? InstanceId { get; init; }

    /// <summary>For <c>open</c>: the runtime key of the plugin.</summary>
    public string? PluginKey { get; init; }

    /// <summary>For <c>open</c>: the canvas.</summary>
    public string? CanvasId { get; init; }

    /// <summary>For <c>open</c>: the space to open the tab in.</summary>
    public string? SpaceId { get; init; }

    /// <summary>For <c>open</c>: the project of the instance.</summary>
    public string? ProjectId { get; init; }

    /// <summary>For <c>open</c>: the session of the instance.</summary>
    public string? SessionId { get; init; }

    /// <summary>For <c>open</c>: the key of the instance.</summary>
    public string? Key { get; init; }

    /// <summary>For <c>open</c>: the id of the folder of the plugin package, or null.</summary>
    public string? Package { get; init; }

    /// <summary>For <c>open</c>: bring the tab to the front.</summary>
    public bool Focus { get; init; }

    /// <summary>The title of the tab.</summary>
    public string? Title { get; init; }

    /// <summary>For <c>open</c>: the name of the icon of the canvas.</summary>
    public string? Icon { get; init; }

    /// <summary>The status text beside the title; empty for none, null for no change.</summary>
    public string? StatusText { get; init; }

    /// <summary>The HTML fragment, or null for no change.</summary>
    public string? Html { get; init; }

    /// <summary>The revision of the content, or null for an event that says nothing of it.</summary>
    public int? Revision { get; init; }

    /// <summary>Whether the fragment raises actions, or null for no change.</summary>
    public bool? Actions { get; init; }

    /// <summary>For <c>update</c> and <c>state</c>: <c>ready</c>, <c>plugin_stopped</c>, <c>unknown_canvas</c> or <c>failed</c>; null for no change.</summary>
    public string? State { get; init; }
}
