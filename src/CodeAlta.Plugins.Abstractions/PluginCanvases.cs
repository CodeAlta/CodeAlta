using System.Text.Json;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Says what a canvas is about, which decides what its instances are identified by.
/// </summary>
/// <remarks>
/// A canvas is also always in a space of the window (<see cref="PluginCanvasContext.SpaceId"/>): the space is where
/// the tab is, not a scope. Opening the same canvas in two spaces gives two instances of the same plugin state.
/// </remarks>
public enum PluginCanvasScope
{
    /// <summary>The canvas is about the application. An instance is identified by its key alone.</summary>
    Application,

    /// <summary>The canvas is about a project. An instance is identified by its project and its key.</summary>
    Project,

    /// <summary>The canvas is about a session. An instance is identified by its session and its key.</summary>
    Session,
}

/// <summary>Opens an instance of a canvas.</summary>
/// <param name="canvas">The instance. It stays valid until it is closed, which also cancels <see cref="PluginCanvasContext.Closed"/>.</param>
/// <param name="cancellationToken">A token cancelled when the instance is closed before it was shown.</param>
/// <returns>What the tab shows.</returns>
public delegate ValueTask<PluginCanvasView> PluginCanvasOpenHandler(PluginCanvasContext canvas, CancellationToken cancellationToken);

/// <summary>Writes the current HTML fragment of an instance. The host calls it when the instance opens and each time it is invalidated.</summary>
/// <param name="canvas">The instance.</param>
/// <param name="cancellationToken">A token cancelled when the instance is closed.</param>
/// <returns>The HTML fragment, as <see cref="PluginHtml"/> describes.</returns>
public delegate ValueTask<string> PluginCanvasRenderHandler(PluginCanvasContext canvas, CancellationToken cancellationToken);

/// <summary>Handles an action raised by an element of the HTML fragment of an open instance.</summary>
/// <param name="canvas">The instance that raised the action.</param>
/// <param name="action">The action and the current values of the named fields of the fragment.</param>
/// <param name="cancellationToken">A token cancelled when the instance is closed.</param>
/// <returns>What the tab does next.</returns>
public delegate ValueTask<PluginCanvasActionResult> PluginCanvasActionHandler(PluginCanvasContext canvas, PluginCanvasAction action, CancellationToken cancellationToken);

/// <summary>Describes what an instance shows now, as Markdown, for an agent that cannot look at the window.</summary>
/// <param name="canvas">The instance, open or not: <see cref="PluginCanvasContext.IsOpen"/> says which.</param>
/// <param name="cancellationToken">A token to cancel the description.</param>
/// <returns>The Markdown text, or <see langword="null"/> when the canvas has nothing to say.</returns>
public delegate ValueTask<string?> PluginCanvasDescribeHandler(PluginCanvasContext canvas, CancellationToken cancellationToken);

/// <summary>Tells the plugin that an instance was closed: the tab is gone, and so is whatever the instance held.</summary>
/// <param name="canvas">The instance. Its <see cref="PluginCanvasContext.Closed"/> token is cancelled already.</param>
/// <returns>A task that completes when the plugin has released what it kept for the instance.</returns>
public delegate ValueTask PluginCanvasClosedHandler(PluginCanvasContext canvas);

/// <summary>Runs a declared action of a canvas on behalf of an agent.</summary>
/// <param name="canvas">The instance the action is for, open or not: the state of a canvas is the plugin's.</param>
/// <param name="input">The JSON input, which the host checked against nothing: validate it. <see langword="null"/> when none was given.</param>
/// <param name="cancellationToken">A token to cancel the action.</param>
/// <returns>The JSON result, or <see langword="null"/> for none.</returns>
public delegate ValueTask<JsonElement?> PluginCanvasInvokeHandler(PluginCanvasContext canvas, JsonElement? input, CancellationToken cancellationToken);

/// <summary>
/// Declares a canvas: a tab that a plugin provides, which the plugin fills and keeps up to date.
/// </summary>
/// <remarks>
/// <para>
/// The plugin holds the state and the tab is a view of it. Nothing is shown until someone opens the canvas (a person,
/// a command, an agent, the plugin itself). An open tab is restored when the application restarts, and closing it
/// loses nothing: the next <see cref="Open"/> shows the state again.
/// </para>
/// <para>
/// Contributions are read once, when the plugin is activated, like the other contributions of a plugin.
/// </para>
/// </remarks>
public sealed record PluginCanvasContribution
{
    /// <summary>Gets the identifier of the canvas, unique among the canvases of the plugin: 1 to 64 letters, digits, <c>-</c>, <c>_</c> or <c>.</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the title shown on the tab of an instance until the plugin gives it another one.</summary>
    public required string Title { get; init; }

    /// <summary>Gets one sentence that says what the canvas shows and does. It is written for people and for agents.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the name of the icon of the tab: an icon of the application's icon library, in its kebab-case name (<c>list-checks</c>).</summary>
    public string? Icon { get; init; }

    /// <summary>Gets what the canvas is about.</summary>
    public PluginCanvasScope Scope { get; init; } = PluginCanvasScope.Application;

    /// <summary>Gets a JSON Schema, as text, of the input that <see cref="PluginCanvasContext.Input"/> accepts, or <see langword="null"/> for a canvas without input.</summary>
    public string? InputSchema { get; init; }

    /// <summary>Gets the handler that opens an instance and returns what its tab shows.</summary>
    public required PluginCanvasOpenHandler Open { get; init; }

    /// <summary>Gets the handler that describes what an instance shows, in Markdown, or <see langword="null"/> when it cannot.</summary>
    public PluginCanvasDescribeHandler? Describe { get; init; }

    /// <summary>Gets the handler that runs when an instance is closed, or <see langword="null"/>.</summary>
    public PluginCanvasClosedHandler? Closed { get; init; }

    /// <summary>Gets the actions that an agent can run on the canvas.</summary>
    public IReadOnlyList<PluginCanvasActionContribution> Actions { get; init; } = [];

    /// <summary>Gets the order among the canvases of the plugins; the lower comes first.</summary>
    public int Order { get; init; }
}

/// <summary>
/// Declares an action that an agent can run on a canvas, with the JSON Schema of its input.
/// </summary>
public sealed record PluginCanvasActionContribution
{
    /// <summary>Gets the name of the action: 1 to 64 letters, digits, <c>-</c>, <c>_</c> or <c>.</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Gets one sentence that says what the action does, written for an agent.</summary>
    public required string Description { get; init; }

    /// <summary>Gets a JSON Schema, as text, of the input of the action, or <see langword="null"/> for an action without input.</summary>
    public string? InputSchema { get; init; }

    /// <summary>Gets the handler that runs the action.</summary>
    public required PluginCanvasInvokeHandler Handler { get; init; }
}

/// <summary>
/// What the tab of an instance shows when it opens: an HTML fragment, and what handles its actions.
/// </summary>
/// <remarks>
/// The fragment is sanitized and drawn by the application as it draws the content of a plugin dialog, with the same
/// vocabulary (<see cref="PluginHtml"/>): <c>data-alta-command</c> runs a command of the plugin, and
/// <c>data-alta-action</c> calls the action handler with the values of the named fields. <see cref="Script"/> adds the
/// plugin's own JavaScript module, which draws the tab with the libraries of the application (see <see cref="PluginScript"/>);
/// the fragment is then the skeleton the module mounts on.
/// </remarks>
public sealed record PluginCanvasView
{
    /// <summary>Gets the HTML fragment shown first. Empty when <see cref="Renderer"/> writes it.</summary>
    public string Fragment { get; init; } = string.Empty;

    /// <summary>
    /// Gets the handler that writes the fragment. The host calls it when the instance opens and when
    /// <see cref="PluginCanvasContext.InvalidateAsync"/> or <see cref="IPluginCanvasService.InvalidateAsync"/> says the state changed.
    /// </summary>
    public PluginCanvasRenderHandler? Renderer { get; init; }

    /// <summary>Gets the handler of the actions raised by the fragment, or <see langword="null"/> when it raises none.</summary>
    public PluginCanvasActionHandler? OnAction { get; init; }

    /// <summary>Gets the JavaScript module that draws the tab, or <see langword="null"/> for a canvas that is its fragment alone.</summary>
    public PluginScript? Script { get; init; }

    /// <summary>
    /// Gets or sets the module of the tab as text, which is <see cref="Script"/> for a script given inline: a plugin of one file stays one file.
    /// Reading it gives the text of an inline script, or <see langword="null"/>.
    /// </summary>
    public string? ScriptSource
    {
        get => Script?.Source;
        init => Script = string.IsNullOrWhiteSpace(value) ? null : PluginScript.Inline(value);
    }

    /// <summary>Gets the title of the tab, or <see langword="null"/> for the title of the canvas.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the status text shown beside the title (for example <c>3 of 8 done</c>), or <see langword="null"/> for none.</summary>
    public string? Status { get; init; }

    /// <summary>Creates a view of an HTML fragment.</summary>
    /// <param name="fragment">The HTML fragment.</param>
    /// <param name="onAction">The handler of the actions of the fragment, or <see langword="null"/>.</param>
    /// <returns>The view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fragment"/> is <see langword="null"/>.</exception>
    public static PluginCanvasView Html(string fragment, PluginCanvasActionHandler? onAction = null)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        return new PluginCanvasView { Fragment = fragment, OnAction = onAction };
    }

    /// <summary>Creates a view whose fragment is written by a handler, and written again when the state is invalidated.</summary>
    /// <param name="renderer">The handler that writes the fragment.</param>
    /// <param name="onAction">The handler of the actions of the fragment, or <see langword="null"/>.</param>
    /// <returns>The view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="renderer"/> is <see langword="null"/>.</exception>
    public static PluginCanvasView Rendered(PluginCanvasRenderHandler renderer, PluginCanvasActionHandler? onAction = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        return new PluginCanvasView { Renderer = renderer, OnAction = onAction };
    }
}

/// <summary>
/// Describes an action raised by an element of the HTML fragment of an open instance.
/// </summary>
public sealed record PluginCanvasAction
{
    /// <summary>Gets the action name: the value of the <c>data-alta-action</c> attribute of the element.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the value of the <c>data-alta-value</c> attribute of the element, when it has one.</summary>
    public string? Value { get; init; }

    /// <summary>Gets the current values of the named fields of the fragment.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Describes what a tab does after an action of its fragment.
/// </summary>
public sealed record PluginCanvasActionResult
{
    /// <summary>Gets a result that leaves the tab as it is.</summary>
    public static PluginCanvasActionResult KeepOpen { get; } = new();

    /// <summary>Gets the HTML fragment that replaces the content of the tab, or <see langword="null"/> to keep it.</summary>
    public string? Html { get; init; }

    /// <summary>Gets a value indicating whether the instance closes, and its tab with it.</summary>
    public bool Close { get; init; }

    /// <summary>Creates a result that replaces the content of the tab.</summary>
    /// <param name="html">The new HTML fragment.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is <see langword="null"/>.</exception>
    public static PluginCanvasActionResult Update(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return new PluginCanvasActionResult { Html = html };
    }

    /// <summary>Creates a result that closes the instance and its tab.</summary>
    /// <returns>The result.</returns>
    public static PluginCanvasActionResult CloseCanvas() => new() { Close = true };
}

/// <summary>
/// An instance of a canvas, as the plugin sees it: what it is about, where it is, and how to change what it shows.
/// </summary>
/// <remarks>
/// <para>
/// The context is given to <see cref="PluginCanvasContribution.Open"/> and stays valid until the instance is closed.
/// The same type is given, with <see cref="IsOpen"/> false, to the handlers that run without a tab (the description,
/// the actions of an agent): <c>UpdateAsync</c> and the like then do nothing.
/// </para>
/// <para>
/// An instance of an application canvas has no project and no session; of a project canvas, no session. A session canvas
/// has the project of its session, when it has one.
/// </para>
/// </remarks>
public abstract class PluginCanvasContext
{
    /// <summary>Gets the identifier of the instance: the same for the same canvas, space, project, session and key.</summary>
    public abstract string InstanceId { get; }

    /// <summary>Gets the identifier of the canvas, as <see cref="PluginCanvasContribution.Id"/> gives it.</summary>
    public abstract string CanvasId { get; }

    /// <summary>Gets the identifier of the space the tab is in, or <see langword="null"/> when the host has no spaces.</summary>
    public abstract string? SpaceId { get; }

    /// <summary>Gets the identifier of the project of the instance, or <see langword="null"/>.</summary>
    public abstract string? ProjectId { get; }

    /// <summary>Gets the identifier of the session of the instance, or <see langword="null"/>.</summary>
    public abstract string? SessionId { get; }

    /// <summary>Gets the key that tells apart several instances of a canvas in the same context (an issue number, a file), or <see langword="null"/>.</summary>
    public abstract string? Key { get; }

    /// <summary>Gets the input the instance was opened with, or <see langword="null"/>. It is not kept: a tab restored at a restart is opened without input.</summary>
    public abstract JsonElement? Input { get; }

    /// <summary>Gets a value indicating whether a tab shows the instance now. It is false while the tab is behind another one, and while its space is not shown.</summary>
    public abstract bool IsVisible { get; }

    /// <summary>Gets a value indicating whether the instance is open: a tab exists for it, and it has not been closed.</summary>
    public abstract bool IsOpen { get; }

    /// <summary>Gets a token that is cancelled when the instance closes or the plugin stops.</summary>
    public abstract CancellationToken Closed { get; }

    /// <summary>Raised when <see cref="IsVisible"/> changes. The argument is the new value.</summary>
    public abstract event Action<bool>? VisibilityChanged;

    private IPluginCanvasRpc? _noRpc;

    /// <summary>
    /// Gets the registry of the calls that the script of the instance makes to the plugin, and the way to send it events. The handlers are
    /// registered in <see cref="PluginCanvasContribution.Open"/>, before it returns the view that names the script (see <see cref="IPluginCanvasRpc"/>).
    /// A context that has no tab (a description, the action of an agent) gives a registry that registers nothing.
    /// </summary>
    public virtual IPluginCanvasRpc Rpc => _noRpc ??= new NoopPluginCanvasRpc();

    /// <summary>Sets the title of the tab.</summary>
    /// <param name="title">The title. A blank title brings back the title of the canvas.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the host has the title. It does nothing once the instance is closed.</returns>
    public abstract ValueTask SetTitleAsync(string? title, CancellationToken cancellationToken = default);

    /// <summary>Sets the status text shown beside the title of the tab.</summary>
    /// <param name="status">The status text, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the host has the status. It does nothing once the instance is closed.</returns>
    public abstract ValueTask SetStatusAsync(string? status, CancellationToken cancellationToken = default);

    /// <summary>Pushes a new HTML fragment to the tab. Of several pushes sent while the tab is hidden, the last one is shown.</summary>
    /// <param name="html">The HTML fragment, as <see cref="PluginHtml"/> describes. It is cut at 256 KiB.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the host has the fragment. It does nothing once the instance is closed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is <see langword="null"/>.</exception>
    public abstract ValueTask UpdateAsync(string html, CancellationToken cancellationToken = default);

    /// <summary>Says that the state changed: the host writes the fragment again with the <see cref="PluginCanvasView.Renderer"/> of the instance.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the fragment is pushed. It does nothing for a view without renderer, and once the instance is closed.</returns>
    public abstract ValueTask InvalidateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What <see cref="IPluginCanvasService.OpenAsync"/> asks of the window.
/// </summary>
public sealed record PluginCanvasOpenOptions
{
    /// <summary>Gets the space to open the tab in, or <see langword="null"/> for the space the window shows.</summary>
    public string? SpaceId { get; init; }

    /// <summary>Gets the project of the instance, or <see langword="null"/> for the project of the operation that asks (the pane of a command).</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the session of the instance, or <see langword="null"/> for the session of the operation that asks.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the key that tells apart several instances in the same context, or <see langword="null"/>.</summary>
    public string? Key { get; init; }

    /// <summary>Gets the input for <see cref="PluginCanvasContext.Input"/>. It is used when the instance is created, and ignored when it exists.</summary>
    public JsonElement? Input { get; init; }

    /// <summary>Gets a value indicating whether the tab is brought to the front. The default is <see langword="true"/>.</summary>
    public bool Focus { get; init; } = true;
}

/// <summary>
/// The outcome of <see cref="IPluginCanvasService.OpenAsync"/>.
/// </summary>
/// <param name="Status">How the request ended.</param>
/// <param name="InstanceId">The identifier the instance has once the tab exists, or <see langword="null"/> when the request was refused.</param>
/// <param name="SpaceId">The space the tab is opened in, or <see langword="null"/> when the request was refused.</param>
/// <param name="Shown">Whether that space is the one the window shows: the person sees the tab at once.</param>
public sealed record PluginCanvasOpenResult(PluginCanvasOpenStatus Status, string? InstanceId, string? SpaceId, bool Shown)
{
    /// <summary>Gets a value indicating whether a window received the request.</summary>
    public bool Requested => Status == PluginCanvasOpenStatus.Requested;
}

/// <summary>How a request to open a canvas ended.</summary>
public enum PluginCanvasOpenStatus
{
    /// <summary>A window received the request and opens the tab.</summary>
    Requested,

    /// <summary>The host has no window to show the tab in.</summary>
    Unavailable,

    /// <summary>The plugin declares no canvas with that identifier.</summary>
    UnknownCanvas,

    /// <summary>The canvas needs a project or a session that the request does not give and the operation has none.</summary>
    MissingContext,

    /// <summary>The request is not valid: an identifier or a key is not well formed, or the input is too large.</summary>
    Invalid,
}

/// <summary>
/// An instance of a canvas that is open, as <see cref="IPluginCanvasService.GetOpen"/> lists it.
/// </summary>
/// <param name="InstanceId">The identifier of the instance.</param>
/// <param name="CanvasId">The identifier of its canvas.</param>
/// <param name="SpaceId">The space of its tab.</param>
/// <param name="ProjectId">Its project, or <see langword="null"/>.</param>
/// <param name="SessionId">Its session, or <see langword="null"/>.</param>
/// <param name="Key">Its key, or <see langword="null"/>.</param>
/// <param name="Title">The title of its tab.</param>
/// <param name="IsVisible">Whether a tab shows it now.</param>
public sealed record PluginCanvasInstanceInfo(string InstanceId, string CanvasId, string? SpaceId, string? ProjectId, string? SessionId, string? Key, string Title, bool IsVisible);

/// <summary>
/// Opens, closes and lists the canvases of the calling plugin.
/// </summary>
/// <remarks>
/// The service a plugin gets knows which plugin asks: it only sees the canvases that plugin declares. A host without
/// a window gives <see cref="NoopPluginCanvasService"/>.
/// </remarks>
public interface IPluginCanvasService
{
    /// <summary>Gets a value indicating whether a window can show canvases.</summary>
    bool HasInteractiveUi { get; }

    /// <summary>Asks the window to open the tab of a canvas, or to bring it to the front when it is open.</summary>
    /// <param name="canvasId">The identifier of a canvas of the calling plugin.</param>
    /// <param name="options">What the instance is about, or <see langword="null"/> for the context of the operation that asks.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>How the request ended. The tab is opened by the window after the request, not before it returns.</returns>
    /// <exception cref="ArgumentException"><paramref name="canvasId"/> is null, empty or whitespace.</exception>
    ValueTask<PluginCanvasOpenResult> OpenAsync(string canvasId, PluginCanvasOpenOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Closes an instance and its tab. The state of the canvas stays with the plugin.</summary>
    /// <param name="instanceId">The identifier of an instance of the calling plugin.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns><see langword="true"/> when the instance was open and is closed.</returns>
    /// <exception cref="ArgumentException"><paramref name="instanceId"/> is null, empty or whitespace.</exception>
    ValueTask<bool> CloseAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>Writes again the fragment of every open instance of a canvas that has a <see cref="PluginCanvasView.Renderer"/>.</summary>
    /// <param name="canvasId">The identifier of a canvas of the calling plugin.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>A task that completes when the fragments are pushed.</returns>
    /// <exception cref="ArgumentException"><paramref name="canvasId"/> is null, empty or whitespace.</exception>
    ValueTask InvalidateAsync(string canvasId, CancellationToken cancellationToken = default);

    /// <summary>Lists the open instances of the canvases of the calling plugin.</summary>
    /// <returns>The instances, in the order they were opened.</returns>
    IReadOnlyList<PluginCanvasInstanceInfo> GetOpen();
}

/// <summary>
/// The canvas service of a host that has no window: nothing can be shown.
/// </summary>
public sealed class NoopPluginCanvasService : IPluginCanvasService
{
    /// <summary>Gets the shared instance. It holds no state.</summary>
    public static NoopPluginCanvasService Instance { get; } = new();

    private NoopPluginCanvasService()
    {
    }

    /// <inheritdoc />
    public bool HasInteractiveUi => false;

    /// <inheritdoc />
    public ValueTask<PluginCanvasOpenResult> OpenAsync(string canvasId, PluginCanvasOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canvasId);
        return new ValueTask<PluginCanvasOpenResult>(new PluginCanvasOpenResult(PluginCanvasOpenStatus.Unavailable, null, null, false));
    }

    /// <inheritdoc />
    public ValueTask<bool> CloseAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return new ValueTask<bool>(false);
    }

    /// <inheritdoc />
    public ValueTask InvalidateAsync(string canvasId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canvasId);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginCanvasInstanceInfo> GetOpen() => [];
}

/// <summary>
/// The canvas service of a host that knows which plugin asks. The runtime asks the host for the service of each
/// plugin it starts; a host that does not implement this interface gives every plugin the same service.
/// </summary>
public interface IPluginCanvasRuntimeService : IPluginCanvasService
{
    /// <summary>Gets the service of one plugin.</summary>
    /// <param name="pluginRuntimeKey">The runtime key of the plugin.</param>
    /// <returns>A service that only sees the canvases of that plugin.</returns>
    /// <exception cref="ArgumentException"><paramref name="pluginRuntimeKey"/> is null, empty or whitespace.</exception>
    IPluginCanvasService ForPlugin(string pluginRuntimeKey);
}
