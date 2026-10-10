using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// Names one instance of a canvas: what the plugin declared, and where it is shown.
/// </summary>
/// <param name="PluginKey">The runtime key of the plugin.</param>
/// <param name="CanvasId">The identifier of the canvas.</param>
/// <param name="SpaceId">The space of the tab, or null for a window without spaces.</param>
/// <param name="ProjectId">The project of an instance of a project canvas, or the one of the session of a session canvas.</param>
/// <param name="SessionId">The session of an instance of a session canvas.</param>
/// <param name="Key">The key that tells apart several instances in the same context.</param>
internal readonly record struct CanvasIdentity(string PluginKey, string CanvasId, string? SpaceId, string? ProjectId, string? SessionId, string? Key);

/// <summary>A canvas that an active plugin declares.</summary>
/// <param name="Plugin">The active plugin that declares it.</param>
/// <param name="Canvas">The declaration.</param>
/// <param name="Package">The id of the folder of the plugin package (<see cref="PluginFolder.Id"/>), or null for a built-in plugin.</param>
internal sealed record CanvasDeclaration(ActivePluginInstance Plugin, PluginCanvasContribution Canvas, string? Package)
{
    /// <summary>The runtime key of the plugin.</summary>
    public string PluginKey => Plugin.Descriptor.RuntimeKey;
}

/// <summary>What an instance shows now: the answer to opening it, and what a page reads when it comes back.</summary>
/// <param name="InstanceId">The identifier of the instance.</param>
/// <param name="Title">The title of the tab.</param>
/// <param name="StatusText">The status text beside the title, or null.</param>
/// <param name="Html">The HTML fragment.</param>
/// <param name="Actions">The fragment raises actions: its view has an action handler.</param>
/// <param name="Revision">Grows with every change of the content: a page ignores an event older than what it has.</param>
/// <param name="Script">The path of the module that draws the tab (<see cref="DesktopPluginModules.Prefix"/>), or null for a fragment alone.</param>
/// <param name="ScriptProblem">Why a script the plugin asked for cannot be served, or null.</param>
/// <param name="Input">The input the instance was opened with, as JSON, or null.</param>
internal sealed record CanvasInstanceState(string InstanceId, string Title, string? StatusText, string Html, bool Actions, int Revision,
    string? Script = null, string? ScriptProblem = null, string? Input = null);

/// <summary>An instance that is open, with the identity that names it.</summary>
/// <param name="InstanceId">The identifier of the instance.</param>
/// <param name="Identity">The plugin, the canvas, the space, the context and the key of the instance.</param>
/// <param name="Title">The title of its tab now.</param>
/// <param name="Visible">A tab shows it now.</param>
internal sealed record CanvasOpenInstance(string InstanceId, CanvasIdentity Identity, string Title, bool Visible);

/// <summary>How opening an instance ended: its state, or a refusal code.</summary>
/// <param name="Status"><c>ok</c>, <c>unavailable</c>, <c>invalid_request</c>, <c>unknown_canvas</c>, <c>plugin_stopped</c>, <c>failed</c> or <c>limit</c>.</param>
/// <param name="State">The state of the instance when it was opened.</param>
/// <param name="Declaration">The canvas as the plugin declares it, when it does.</param>
internal sealed record CanvasOpening(string Status, CanvasInstanceState? State, CanvasDeclaration? Declaration);

/// <summary>
/// The canvases of the plugins in the window: the ones the active plugins declare, the instances that tabs
/// of the window have open, and what flows between them. A plugin fills an instance and keeps its state; the
/// tab is a view of it, and the page asks the host for it each time it is shown.
/// </summary>
/// <remarks>
/// <para>
/// Plugins start with the host, before the window has a page, so the broker is created early and attached to
/// the plugin runtime once it exists (<see cref="Attach"/>). It is the service plugins get as
/// <see cref="IPluginServices.Canvases"/>: <see cref="ForPlugin"/> gives each plugin the view of its own canvases.
/// </para>
/// <para>
/// An instance is identified by its plugin, its canvas, its space, its project or session and its key: opening
/// the same identity twice is one instance. The page closes an instance when its tab is closed; a tab that is
/// only taken out of the page (another space is shown) stays open here and is told it is hidden. A new version
/// of a plugin opens its instances again; a plugin that stops leaves them waiting.
/// </para>
/// <para>
/// What the plugin sends to the page is coalesced: of several pushes to the same instance the last one is
/// delivered. Nothing a plugin throws crosses to the page.
/// </para>
/// </remarks>
internal sealed class DesktopCanvases : IPluginCanvasRuntimeService, ICanvasRpcCarrier, IDisposable
{
    /// <summary>The most instances that stay open; at the limit the oldest hidden one is closed to make room.</summary>
    internal const int MaximumInstances = 64;

    /// <summary>The most open requests kept for a page that does not watch yet.</summary>
    internal const int MaximumOpenBacklog = 16;

    /// <summary>The most events one page can have waiting: the page reads slowly or not at all.</summary>
    internal const int MaximumPendingEvents = 512;

    /// <summary>The most frames of connections of scripts that wait for the page; a session that has more waits for room.</summary>
    internal const int MaximumPendingRpcFrames = 128;

    /// <summary>The most characters of frames that wait for the page.</summary>
    internal const int MaximumPendingRpcChars = 16 * 1024 * 1024;

    /// <summary>The most frames of one connection that one event carries.</summary>
    internal const int MaximumRpcFramesPerEvent = 64;

    internal const int MaximumHtmlUnits = DesktopPluginUi.MaximumHtmlUnits;
    internal const int MaximumTitleUnits = 200;
    internal const int MaximumStatusUnits = 200;
    internal const int MaximumKeyUnits = 128;
    internal const int MaximumIdUnits = 256;
    internal const int MaximumInputUnits = 64 * 1024;
    internal const int MaximumDescriptionUnits = 64 * 1024;

    /// <summary>How long a handler of a plugin may take to open an instance, handle an action or describe it.</summary>
    internal static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(25);

    private readonly DesktopPluginUi? _ui;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _inputs = new(StringComparer.Ordinal);
    private readonly Queue<CanvasEvent> _openBacklog = new();
    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private PluginRuntimeManager? _runtime;
    private Func<string?> _shownSpace = static () => null;
    private CanvasOutbox? _outbox;
    private int _watchGeneration;
    private long _order;
    private bool _disposed;

    /// <summary>Creates the broker.</summary>
    /// <param name="ui">The window service of the plugins, whose current pane tells which project and session an operation is for; null for none.</param>
    /// <param name="modules">The server of scripts the window shares with the other content of plugins; null makes one.</param>
    internal DesktopCanvases(DesktopPluginUi? ui = null, DesktopPluginModules? modules = null)
    {
        _ui = ui;
        Modules = modules ?? new DesktopPluginModules();
        Modules.Attach(ActivePlugins);
    }

    /// <inheritdoc />
    public bool HasInteractiveUi => true;

    /// <summary>The server of the scripts that canvases and other HTML of plugins bring: it answers the page from the plugins that are active.</summary>
    internal DesktopPluginModules Modules { get; }

    private IReadOnlyList<PluginModuleOwner> ActivePlugins()
    {
        PluginRuntimeManager? runtime;
        lock (_gate) runtime = _runtime;
        return runtime is null ? [] : [.. runtime.ActivePlugins.Where(static plugin => plugin.Instance is not null).Select(DesktopPluginModules.OwnerOf)];
    }

    /// <summary>
    /// Connects the broker to the plugin runtime, which exists once the host started its plugins. It then follows
    /// the changes of the plugins.
    /// </summary>
    /// <param name="runtime">The plugin runtime of the host.</param>
    /// <param name="shownSpace">Says which space the window shows now, or null while it does not say.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The broker is attached already.</exception>
    internal void Attach(PluginRuntimeManager runtime, Func<string?> shownSpace)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(shownSpace);
        lock (_gate)
        {
            if (_runtime is not null) throw new InvalidOperationException("The canvases are attached already.");
            _runtime = runtime;
            _shownSpace = shownSpace;
        }

        runtime.Changed += OnPluginsChanged;
    }

    /// <summary>Counts the pages that began to watch: a test waits for it to grow before it expects the page to hear.</summary>
    internal int WatchGeneration
    {
        get { lock (_gate) return _watchGeneration; }
    }

    /// <summary>Gets the canvases that the active plugins declare, in the order of their contributions.</summary>
    internal IReadOnlyList<CanvasDeclaration> Declarations()
    {
        PluginRuntimeManager? runtime;
        lock (_gate) runtime = _runtime;
        if (runtime is null) return [];
        var active = runtime.ActivePlugins;
        var found = new List<CanvasDeclaration>();
        foreach (var registration in runtime.Registry.GetSnapshot())
        {
            if (registration.Handle.Point != PluginPoint.Canvas || registration.Contribution is not PluginCanvasContribution canvas || !ValidCanvasId(canvas.Id)) continue;
            var plugin = active.FirstOrDefault(candidate => candidate.Instance is not null && candidate.RuntimeContext.IsValid
                && string.Equals(candidate.Descriptor.RuntimeKey, registration.Handle.PluginRuntimeKey, StringComparison.Ordinal));
            if (plugin is null || found.Any(known => ReferenceEquals(known.Plugin, plugin) && string.Equals(known.Canvas.Id, canvas.Id, StringComparison.Ordinal))) continue;
            found.Add(new CanvasDeclaration(plugin, canvas, plugin.SourcePackage is { } package ? PluginFolder.Of(package, package.Root.ProjectId).Id : null));
        }

        return found;
    }

    /// <summary>Finds a canvas that an active plugin declares.</summary>
    internal CanvasDeclaration? Find(string pluginKey, string canvasId)
        => Declarations().FirstOrDefault(declaration => string.Equals(declaration.PluginKey, pluginKey, StringComparison.Ordinal)
            && string.Equals(declaration.Canvas.Id, canvasId, StringComparison.Ordinal));

    /// <summary>Whether a plugin is active, whatever the canvases it declares.</summary>
    internal bool IsActive(string pluginKey)
    {
        PluginRuntimeManager? runtime;
        lock (_gate) runtime = _runtime;
        return runtime?.ActivePlugins.Any(plugin => plugin.Instance is not null && plugin.RuntimeContext.IsValid
            && string.Equals(plugin.Descriptor.RuntimeKey, pluginKey, StringComparison.Ordinal)) ?? false;
    }

    /// <summary>The identifier of the instance that an identity names.</summary>
    internal static string InstanceId(CanvasIdentity identity)
    {
        var text = string.Join('\0', identity.PluginKey, identity.CanvasId, identity.SpaceId ?? string.Empty, identity.ProjectId ?? string.Empty,
            identity.SessionId ?? string.Empty, identity.Key ?? string.Empty);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 12));
    }

    /// <summary>
    /// Brings an identity to what its canvas is identified by: an application canvas has no project or session, a project
    /// canvas has no session. Null when the identity lacks what the canvas needs.
    /// </summary>
    internal static CanvasIdentity? Normalize(CanvasIdentity identity, PluginCanvasScope scope)
        => scope switch
        {
            PluginCanvasScope.Application => identity with { ProjectId = null, SessionId = null },
            PluginCanvasScope.Project => identity.ProjectId is null ? null : identity with { SessionId = null },
            _ => identity.SessionId is null ? null : identity,
        };

    /// <summary>Whether a text is a well-formed identifier of a canvas or of an action: 1 to 64 letters, digits, <c>-</c>, <c>_</c> or <c>.</c>.</summary>
    internal static bool ValidCanvasId(string? id)
        => id is { Length: > 0 and <= 64 } && id.All(static value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.');

    /// <summary>Whether a text can be an identifier of a plugin, a space, a project or a session, or a key: a line of at most <paramref name="maximum"/> units.</summary>
    internal static bool ValidLine(string? text, int maximum)
        => text is { Length: > 0 } && text.Length <= maximum && !text.Any(char.IsControl);

    /// <summary>
    /// Opens an instance, or returns the one that is open, and says whether a tab shows it. The plugin's open handler
    /// runs once for an identity, whatever the number of callers.
    /// </summary>
    /// <param name="identity">The instance.</param>
    /// <param name="visible">Whether a tab shows the instance now.</param>
    /// <param name="cancellationToken">Cancels the wait of the caller; an instance being opened is opened all the same.</param>
    /// <returns>How it ended.</returns>
    internal async ValueTask<CanvasOpening> OpenAsync(CanvasIdentity identity, bool visible, CancellationToken cancellationToken)
    {
        if (!ValidLine(identity.PluginKey, 512) || !ValidCanvasId(identity.CanvasId) || identity.SpaceId is not null && !ValidLine(identity.SpaceId, MaximumIdUnits)
            || identity.ProjectId is not null && !ValidLine(identity.ProjectId, MaximumIdUnits) || identity.SessionId is not null && !ValidLine(identity.SessionId, MaximumIdUnits)
            || identity.Key is not null && !ValidLine(identity.Key, MaximumKeyUnits)) return new("invalid_request", null, null);
        lock (_gate) if (_runtime is null || _disposed) return new("unavailable", null, null);
        var declaration = Find(identity.PluginKey, identity.CanvasId);
        if (declaration is null) return new(IsActive(identity.PluginKey) ? "unknown_canvas" : "plugin_stopped", null, null);
        if (Normalize(identity, declaration.Canvas.Scope) is not { } named) return new("invalid_request", null, declaration);
        var id = InstanceId(named);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Instance instance;
            lock (_gate)
            {
                if (_disposed) return new("unavailable", null, declaration);
                if (!_instances.TryGetValue(id, out instance!))
                {
                    if (_instances.Count >= MaximumInstances && !EvictOldestHiddenLocked()) return new("limit", null, declaration);
                    instance = new Instance(id, named, ++_order);
                    _instances.Add(id, instance);
                }
            }

            await instance.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (instance.Closed) continue;
                if (!instance.Initialized || !ReferenceEquals(instance.Plugin, declaration.Plugin) || instance.State != "ready")
                {
                    if (await InitializeAsync(instance, declaration).ConfigureAwait(false) is { } failure)
                    {
                        Remove(instance);
                        return new(failure, null, declaration);
                    }
                }

                SetVisible(instance, visible);
                return new("ok", Snapshot(instance), declaration);
            }
            finally
            {
                instance.Gate.Release();
            }
        }

        return new("failed", null, declaration);
    }

    /// <summary>Says whether a tab shows an instance: the plugin sees it in <see cref="PluginCanvasContext.IsVisible"/>.</summary>
    /// <returns>False when no such instance is open.</returns>
    internal bool SetVisible(string instanceId, bool visible)
    {
        Instance? instance;
        lock (_gate) _instances.TryGetValue(instanceId, out instance);
        if (instance is null || instance.Closed) return false;
        SetVisible(instance, visible);
        return true;
    }

    /// <summary>Closes an instance: its tab is closed. The state of the canvas stays with the plugin.</summary>
    /// <param name="instanceId">The identifier of the instance.</param>
    /// <param name="notifyPage">Whether the page is told, so that it closes the tab: the plugin closed the instance, not the page.</param>
    /// <returns>False when no such instance is open.</returns>
    internal async ValueTask<bool> CloseAsync(string instanceId, bool notifyPage)
    {
        Instance? instance;
        lock (_gate) _instances.TryGetValue(instanceId, out instance);
        return instance is not null && await CloseInstanceAsync(instance, notifyPage).ConfigureAwait(false);
    }

    /// <summary>Closes every instance of a space: the space was deleted and its tabs are forgotten.</summary>
    /// <param name="spaceId">The space.</param>
    /// <returns>The number of instances closed.</returns>
    internal async ValueTask<int> CloseSpaceAsync(string spaceId)
    {
        Instance[] closing;
        lock (_gate) closing = [.. _instances.Values.Where(instance => string.Equals(instance.Identity.SpaceId, spaceId, StringComparison.Ordinal))];
        var closed = 0;
        foreach (var instance in closing) if (await CloseInstanceAsync(instance, notifyPage: false).ConfigureAwait(false)) closed++;
        return closed;
    }

    /// <summary>Runs the action of an element of the fragment of an open instance.</summary>
    /// <returns>The status (<c>ok</c>, <c>unknown</c>, <c>unsupported</c>, <c>failed</c>), the new content when it changes, and whether the instance closed.</returns>
    internal async ValueTask<(string Status, string? Html, bool Closed)> ActionAsync(string instanceId, string action, string? value,
        IReadOnlyDictionary<string, string>? values, CancellationToken cancellationToken)
    {
        Instance? instance;
        HostContext? context;
        PluginCanvasActionHandler? handler;
        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out instance) || instance.Closed) return ("unknown", null, false);
            (context, handler) = (instance.Context, instance.View?.OnAction);
        }

        if (context is null || handler is null) return (instance.State == "ready" ? "unsupported" : "unknown", null, false);
        PluginCanvasActionResult result;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Closed);
            linked.CancelAfter(HandlerTimeout);
            result = await handler(context, new PluginCanvasAction { Name = action, Value = value, Values = values ?? new Dictionary<string, string>() }, linked.Token).ConfigureAwait(false)
                ?? PluginCanvasActionResult.KeepOpen;
        }
        catch (OperationCanceledException) { return ("unknown", null, false); }
        catch (Exception exception)
        {
            LogFailure(exception, instance, "action");
            return ("failed", null, false); // The plugin's own failure: its text stays out of the page.
        }

        if (result.Close)
        {
            await CloseInstanceAsync(instance, notifyPage: false).ConfigureAwait(false);
            return ("ok", null, true);
        }

        if (result.Html is null) return ("ok", null, false);
        var html = Cut(result.Html, MaximumHtmlUnits);
        // The answer carries the content, and the push of the same revision is dropped by the page: nothing is sent twice.
        return PushHtml(instance, context, html, answered: true) ? ("ok", html, false) : ("unknown", null, false);
    }

    /// <summary>Describes what an instance shows, in Markdown.</summary>
    /// <param name="identity">The instance; it need not be open.</param>
    /// <param name="cancellationToken">Cancels the description.</param>
    /// <returns>The status (<c>ok</c>, <c>unknown_canvas</c>, <c>plugin_stopped</c>, <c>invalid_request</c>, <c>failed</c>) and the text; the text is null when the canvas cannot describe itself.</returns>
    internal async ValueTask<(string Status, string? Markdown)> DescribeAsync(CanvasIdentity identity, CancellationToken cancellationToken)
    {
        var declaration = Find(identity.PluginKey, identity.CanvasId);
        if (declaration is null) return (IsActive(identity.PluginKey) ? "unknown_canvas" : "plugin_stopped", null);
        if (Normalize(identity, declaration.Canvas.Scope) is not { } named) return ("invalid_request", null);
        if (declaration.Canvas.Describe is not { } describe) return ("ok", null);
        var context = ContextOf(named, declaration);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Closed);
            linked.CancelAfter(HandlerTimeout);
            var text = await describe(context, linked.Token).ConfigureAwait(false);
            return ("ok", text is null ? null : Cut(text, MaximumDescriptionUnits));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            LogFailure(exception, context.Identity, "describe");
            return ("failed", null);
        }
    }

    /// <summary>Describes an open instance by its identifier.</summary>
    internal async ValueTask<(string Status, string? Markdown)> DescribeAsync(string instanceId, CancellationToken cancellationToken)
    {
        Instance? instance;
        lock (_gate) _instances.TryGetValue(instanceId, out instance);
        return instance is null ? ("unknown", null) : await DescribeAsync(instance.Identity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs an action that a canvas declares for agents. The instance need not be open.</summary>
    /// <param name="identity">The instance the action is for.</param>
    /// <param name="action">The name of the action.</param>
    /// <param name="input">The JSON input, or null.</param>
    /// <param name="cancellationToken">Cancels the action.</param>
    /// <returns>The status (<c>ok</c>, <c>unknown_canvas</c>, <c>unknown_action</c>, <c>plugin_stopped</c>, <c>invalid_request</c>, <c>failed</c>) and the JSON result, or null.</returns>
    internal async ValueTask<(string Status, JsonElement? Result)> InvokeAsync(CanvasIdentity identity, string action, JsonElement? input, CancellationToken cancellationToken)
    {
        var declaration = Find(identity.PluginKey, identity.CanvasId);
        if (declaration is null) return (IsActive(identity.PluginKey) ? "unknown_canvas" : "plugin_stopped", null);
        if (Normalize(identity, declaration.Canvas.Scope) is not { } named || input is { } given && given.GetRawText().Length > MaximumInputUnits) return ("invalid_request", null);
        if (declaration.Canvas.Actions.FirstOrDefault(candidate => string.Equals(candidate.Name, action, StringComparison.Ordinal)) is not { } declared) return ("unknown_action", null);
        var context = ContextOf(named, declaration);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Closed);
            linked.CancelAfter(HandlerTimeout);
            return ("ok", await declared.Handler(context, input, linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            LogFailure(exception, context.Identity, "invoke");
            return ("failed", null);
        }
    }

    /// <summary>Opens the connection of the script of an instance to its plugin: a session that the page's frames are given to.</summary>
    /// <param name="instanceId">The instance.</param>
    /// <returns><c>ok</c> with the identifier of the connection; <c>unknown</c> when the instance is not open; <c>unavailable</c> when it has no calls or no page watches.</returns>
    internal (string Status, string? Connection) RpcOpen(string instanceId)
    {
        HostContext? context;
        int generation;
        lock (_gate)
        {
            if (_disposed || _outbox is null) return ("unavailable", null);
            if (!_instances.TryGetValue(instanceId, out var instance) || instance.Closed || instance.State != "ready") return ("unknown", null);
            (context, generation) = (instance.Context, _watchGeneration);
        }

        if (context?.Endpoint is not { } endpoint) return ("unavailable", null);
        return endpoint.Connect(generation) is { } connection ? ("ok", connection) : ("unknown", null);
    }

    /// <summary>Gives the frames of a connection to its session.</summary>
    /// <returns><c>ok</c>, <c>closed</c> when the connection is not the open one of an open instance, or <c>payload_too_large</c>.</returns>
    internal string RpcSend(string instanceId, string connection, IReadOnlyList<string> frames)
        => EndpointOf(instanceId)?.Receive(connection, frames) ?? "closed";

    /// <summary>Ends a connection that the page closed.</summary>
    /// <returns>False when it is not the open one of an open instance.</returns>
    internal bool RpcClose(string instanceId, string connection) => EndpointOf(instanceId)?.Close(connection) ?? false;

    private CanvasRpcEndpoint? EndpointOf(string instanceId)
    {
        lock (_gate) return _instances.TryGetValue(instanceId, out var instance) && !instance.Closed ? instance.Context?.Endpoint : null;
    }

    // The page watched before a watch began or ended: what its connections were told will never be answered.
    private void DropRpcSessions(int upToGeneration)
    {
        CanvasRpcEndpoint[] endpoints;
        lock (_gate) endpoints = [.. _instances.Values.Select(static instance => instance.Context?.Endpoint).OfType<CanvasRpcEndpoint>()];
        foreach (var endpoint in endpoints) endpoint.DropSession(upToGeneration);
    }

    /// <inheritdoc />
    ValueTask ICanvasRpcCarrier.SendAsync(string instanceId, string connection, string frame, CancellationToken cancellationToken)
    {
        CanvasOutbox? outbox;
        lock (_gate) outbox = _outbox;
        return outbox is null ? ValueTask.FromException(new IOException("No page watches the canvases.")) : outbox.AddRpcAsync(instanceId, connection, frame, cancellationToken);
    }

    /// <inheritdoc />
    void ICanvasRpcCarrier.Closed(string instanceId, string connection, string reason)
    {
        CanvasOutbox? outbox;
        lock (_gate) outbox = _outbox;
        outbox?.AddRpcClosed(instanceId, connection, reason);
    }

    /// <summary>Whether a page watches now: a request for a tab reaches a window.</summary>
    internal bool HasPage
    {
        get { lock (_gate) return _outbox is not null && !_disposed; }
    }

    /// <summary>Lists the open instances with the identity of each, in the order they were opened.</summary>
    internal IReadOnlyList<CanvasOpenInstance> OpenInstances()
    {
        lock (_gate)
        {
            return [.. _instances.Values.Where(static instance => !instance.Closed).OrderBy(static instance => instance.Order)
                .Select(static instance => new CanvasOpenInstance(instance.Id, instance.Identity, instance.Title, instance.Visible))];
        }
    }

    /// <summary>Lists the open instances, in the order they were opened.</summary>
    /// <param name="pluginKey">Only the instances of this plugin; null for every plugin.</param>
    internal IReadOnlyList<PluginCanvasInstanceInfo> List(string? pluginKey)
    {
        lock (_gate)
        {
            return [.. _instances.Values.Where(instance => !instance.Closed && (pluginKey is null || string.Equals(instance.Identity.PluginKey, pluginKey, StringComparison.Ordinal)))
                .OrderBy(static instance => instance.Order)
                .Select(static instance => new PluginCanvasInstanceInfo(instance.Id, instance.Identity.CanvasId, instance.Identity.SpaceId, instance.Identity.ProjectId,
                    instance.Identity.SessionId, instance.Identity.Key, instance.Title, instance.Visible))];
        }
    }

    /// <summary>
    /// Gives the page every request from now on, after the open requests it missed. A page that watches replaces the one
    /// that watched before.
    /// </summary>
    /// <returns>The events, until the page goes away.</returns>
    internal async IAsyncEnumerable<CanvasEvent> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var outbox = new CanvasOutbox();
        CanvasOutbox? replaced;
        int generation;
        lock (_gate)
        {
            if (_disposed) yield break;
            replaced = _outbox;
            _outbox = outbox;
            generation = ++_watchGeneration;
            while (_openBacklog.TryDequeue(out var missed)) outbox.Add(missed);
        }

        replaced?.Complete();
        // The connections of the page that was replaced are gone with it: its frames are never answered.
        DropRpcSessions(generation - 1);
        try
        {
            await foreach (var value in outbox.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
        }
        finally
        {
            var current = false;
            lock (_gate)
            {
                if (ReferenceEquals(_outbox, outbox))
                {
                    _outbox = null;
                    current = true;
                }
            }

            outbox.Complete();
            if (current) DropRpcSessions(generation);
        }
    }

    /// <inheritdoc />
    public ValueTask<PluginCanvasOpenResult> OpenAsync(string canvasId, PluginCanvasOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canvasId);
        return new(new PluginCanvasOpenResult(PluginCanvasOpenStatus.UnknownCanvas, null, null, false)); // A plugin asks through its own service: see ForPlugin.
    }

    /// <inheritdoc />
    public async ValueTask<bool> CloseAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return await CloseAsync(instanceId, notifyPage: true).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask InvalidateAsync(string canvasId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canvasId);
        await InvalidateAsync(null, canvasId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginCanvasInstanceInfo> GetOpen() => List(null);

    /// <inheritdoc />
    public IPluginCanvasService ForPlugin(string pluginRuntimeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        return new PluginView(this, pluginRuntimeKey);
    }

    /// <summary>Closes every instance and ends what the page watches.</summary>
    public void Dispose()
    {
        Instance[] open;
        CanvasOutbox? outbox;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            open = [.. _instances.Values];
            _instances.Clear();
            _inputs.Clear();
            _openBacklog.Clear();
            outbox = _outbox;
            _outbox = null;
        }

        _lifetime.Cancel();
        if (_runtime is { } runtime) runtime.Changed -= OnPluginsChanged;
        foreach (var instance in open)
        {
            instance.Closed = true;
            instance.Context?.Retire();
        }

        outbox?.Complete();
    }

    /// <summary>Asks the page to open the tab of a canvas of a plugin, or to bring it to the front.</summary>
    private ValueTask<PluginCanvasOpenResult> RequestOpenAsync(string pluginKey, string canvasId, PluginCanvasOpenOptions? options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canvasId);
        cancellationToken.ThrowIfCancellationRequested();
        return new(RequestOpen(pluginKey, canvasId, options));
    }

    /// <summary>
    /// Asks the page for the tab of a canvas. A plugin's request takes what it leaves out from the pane or the operation it runs in;
    /// a request from outside a plugin (<c>alta canvas open</c>) names its context itself and takes nothing from them.
    /// </summary>
    /// <param name="pluginKey">The runtime key of the plugin.</param>
    /// <param name="canvasId">The canvas.</param>
    /// <param name="options">Where the tab is, what it is about, and its input.</param>
    /// <param name="fromPane">Whether the project and the session left out are those of the pane or of the operation that asks.</param>
    /// <returns>How it ended.</returns>
    internal PluginCanvasOpenResult RequestOpen(string pluginKey, string canvasId, PluginCanvasOpenOptions? options, bool fromPane = true)
    {
        string? shown;
        lock (_gate)
        {
            if (_disposed || _runtime is null) return new(PluginCanvasOpenStatus.Unavailable, null, null, false);
            shown = _shownSpace();
        }

        if (!ValidCanvasId(canvasId)) return new(PluginCanvasOpenStatus.Invalid, null, null, false);
        var declaration = Find(pluginKey, canvasId);
        if (declaration is null) return new(PluginCanvasOpenStatus.UnknownCanvas, null, null, false);
        var pane = fromPane ? _ui?.Scope : null;
        var operation = fromPane ? PluginOrchestrationBridge.CurrentToolOperation : null;
        var space = options?.SpaceId ?? shown;
        var identity = new CanvasIdentity(pluginKey, canvasId, space, options?.ProjectId ?? operation?.ProjectId ?? pane?.ProjectId,
            options?.SessionId ?? operation?.SessionId ?? pane?.SessionId, options?.Key);
        if (!ValidLine(identity.PluginKey, 512) || space is not null && !ValidLine(space, MaximumIdUnits) || identity.ProjectId is not null && !ValidLine(identity.ProjectId, MaximumIdUnits)
            || identity.SessionId is not null && !ValidLine(identity.SessionId, MaximumIdUnits) || identity.Key is not null && !ValidLine(identity.Key, MaximumKeyUnits)
            || options?.Input is { } input && input.GetRawText().Length > MaximumInputUnits) return new(PluginCanvasOpenStatus.Invalid, null, null, false);
        if (Normalize(identity, declaration.Canvas.Scope) is not { } named) return new(PluginCanvasOpenStatus.MissingContext, null, null, false);
        var id = InstanceId(named);
        var request = new CanvasEvent("open")
        {
            PluginKey = pluginKey, CanvasId = canvasId, SpaceId = named.SpaceId, ProjectId = named.ProjectId, SessionId = named.SessionId, Key = named.Key,
            Package = declaration.Package, Focus = options?.Focus ?? true, Title = Line(declaration.Canvas.Title, MaximumTitleUnits), Icon = declaration.Canvas.Icon,
            InstanceId = id,
        };
        lock (_gate)
        {
            // The input is for the instance that the tab creates: one that exists keeps what it was opened with.
            if (options?.Input is { } given && !_instances.ContainsKey(id))
            {
                if (_inputs.Count >= MaximumInstances) _inputs.Remove(_inputs.Keys.First());
                _inputs[id] = given.Clone();
            }

            if (_outbox is { } outbox) outbox.Add(request);
            else
            {
                if (_openBacklog.Count == MaximumOpenBacklog) _openBacklog.Dequeue();
                _openBacklog.Enqueue(request);
            }
        }

        return new(PluginCanvasOpenStatus.Requested, id, named.SpaceId, named.SpaceId is null || string.Equals(named.SpaceId, shown, StringComparison.Ordinal));
    }

    private async ValueTask InvalidateAsync(string? pluginKey, string canvasId)
    {
        Instance[] open;
        lock (_gate)
        {
            open = [.. _instances.Values.Where(instance => !instance.Closed && string.Equals(instance.Identity.CanvasId, canvasId, StringComparison.Ordinal)
                && (pluginKey is null || string.Equals(instance.Identity.PluginKey, pluginKey, StringComparison.Ordinal)))];
        }

        foreach (var instance in open)
        {
            HostContext? context;
            lock (_gate) context = instance.Context;
            if (context is not null) await context.InvalidateAsync().ConfigureAwait(false);
        }
    }

    // Writes an instance for a plugin: its open handler, then what its view shows. Null when it worked, the refusal otherwise.
    private async ValueTask<string?> InitializeAsync(Instance instance, CanvasDeclaration declaration)
    {
        JsonElement? input;
        lock (_gate)
        {
            if (_inputs.Remove(instance.Id, out var kept)) input = kept;
            else input = instance.Input;
        }

        var context = new HostContext(this, instance, input, declaration.Plugin.RuntimeContext.LifetimeCancellationToken, declaration.Canvas.Scope, declaration.Plugin.RuntimeContext.Services?.Logger);
        PluginCanvasView view;
        string html;
        string? script = null, scriptProblem = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.Closed, _lifetime.Token);
            linked.CancelAfter(HandlerTimeout);
            view = await declaration.Canvas.Open(context, linked.Token).ConfigureAwait(false) ?? throw new InvalidOperationException("The canvas opened no view.");
            html = view.Renderer is { } renderer ? await renderer(context, linked.Token).ConfigureAwait(false) : view.Fragment;
            if (view.Script is { HasEntry: true } wanted)
            {
                script = Modules.Publish(DesktopPluginModules.OwnerOf(declaration.Plugin), wanted);
                if (script is null) scriptProblem = "The script of the canvas could not be found.";
            }

            // What the plugin registered for its script to call is final once it has returned the view: the host of the calls is built from it.
            context.StartRpc(this, hasScript: view.Script is { HasEntry: true });
        }
        catch (Exception exception)
        {
            context.Retire();
            LogFailure(exception, instance.Identity, "open");
            return "failed";
        }

        HostContext? previous;
        bool shownBefore;
        lock (_gate)
        {
            if (instance.Closed || _disposed) { context.Retire(); return "failed"; }
            previous = instance.Context;
            shownBefore = instance.Initialized;
            instance.Plugin = declaration.Plugin;
            instance.Canvas = declaration.Canvas;
            instance.Context = context;
            instance.View = view;
            instance.Input = input;
            instance.Html = Cut(html ?? string.Empty, MaximumHtmlUnits);
            instance.Title = Line(string.IsNullOrWhiteSpace(view.Title) ? declaration.Canvas.Title : view.Title, MaximumTitleUnits);
            instance.StatusText = string.IsNullOrWhiteSpace(view.Status) ? null : Line(view.Status, MaximumStatusUnits);
            instance.Revision++;
            instance.Initialized = true;
            instance.State = "ready";
            instance.Script = script;
            instance.ScriptProblem = scriptProblem;
        }

        // A version that was replaced: what it held ends with it, and it is not asked to close.
        previous?.Retire();
        // The page that opened the instance has what it shows in its answer; the page of a tab that was waiting does not.
        if (shownBefore) PostUpdate(instance, full: true);
        return null;
    }

    private static CanvasInstanceState Snapshot(Instance instance)
        => new(instance.Id, instance.Title, instance.StatusText, instance.Html, instance.View?.OnAction is not null, instance.Revision, instance.Script, instance.ScriptProblem,
            instance.Input is { } input ? input.GetRawText() : null);

    private void SetVisible(Instance instance, bool visible)
    {
        HostContext? context;
        lock (_gate)
        {
            if (instance.Visible == visible) return;
            instance.Visible = visible;
            context = instance.Context;
        }

        context?.RaiseVisibility(visible);
    }

    // Pushes a fragment: false when the context is no longer the one of the instance (it was replaced or closed).
    private bool PushHtml(Instance instance, HostContext context, string html, bool answered)
    {
        lock (_gate)
        {
            if (instance.Closed || !ReferenceEquals(instance.Context, context)) return false;
            html = Cut(html, MaximumHtmlUnits);
            if (string.Equals(instance.Html, html, StringComparison.Ordinal)) return true;
            instance.Html = html;
            instance.Revision++;
            // The page that asked for this change has the content in its answer.
            if (!answered) PostUpdateLocked(instance, full: false, html);
        }

        return true;
    }

    private void PushTitle(Instance instance, HostContext context, string? title, string? status, bool setTitle, bool setStatus)
    {
        lock (_gate)
        {
            if (instance.Closed || !ReferenceEquals(instance.Context, context)) return;
            var changed = false;
            if (setTitle)
            {
                var next = Line(string.IsNullOrWhiteSpace(title) ? instance.Canvas?.Title ?? string.Empty : title, MaximumTitleUnits);
                if (!string.Equals(next, instance.Title, StringComparison.Ordinal)) { instance.Title = next; changed = true; }
            }

            if (setStatus)
            {
                var next = string.IsNullOrWhiteSpace(status) ? null : Line(status, MaximumStatusUnits);
                if (!string.Equals(next, instance.StatusText, StringComparison.Ordinal)) { instance.StatusText = next; changed = true; }
            }

            if (!changed) return;
            instance.Revision++;
            _outbox?.Add(new CanvasEvent("update") { InstanceId = instance.Id, Title = instance.Title, StatusText = instance.StatusText ?? string.Empty, Revision = instance.Revision });
        }
    }

    private void PostUpdate(Instance instance, bool full)
    {
        lock (_gate) PostUpdateLocked(instance, full, instance.Html);
    }

    private void PostUpdateLocked(Instance instance, bool full, string html)
    {
        if (_outbox is not { } outbox) return;
        outbox.Add(full
            ? new CanvasEvent("update")
            {
                InstanceId = instance.Id, Html = html, Title = instance.Title, StatusText = instance.StatusText ?? string.Empty, Actions = instance.View?.OnAction is not null,
                Revision = instance.Revision, State = "ready", Script = instance.Script ?? string.Empty, ScriptProblem = instance.ScriptProblem ?? string.Empty,
            }
            : new CanvasEvent("update") { InstanceId = instance.Id, Html = html, Revision = instance.Revision });
    }

    private async ValueTask<bool> CloseInstanceAsync(Instance instance, bool notifyPage)
    {
        await instance.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        HostContext? context;
        PluginCanvasContribution? canvas;
        try
        {
            lock (_gate)
            {
                if (instance.Closed) return false;
                instance.Closed = true;
                _instances.Remove(instance.Id);
                (context, canvas) = (instance.Context, instance.Canvas);
                if (notifyPage) _outbox?.Add(new CanvasEvent("closed") { InstanceId = instance.Id });
            }
        }
        finally
        {
            instance.Gate.Release();
        }

        context?.Retire();
        if (context is not null && canvas?.Closed is { } closed)
        {
            try { await closed(context).ConfigureAwait(false); }
            catch (Exception exception) { LogFailure(exception, instance.Identity, "close"); }
        }

        return true;
    }

    // An instance that is gone from the plugin: its tab waits, and the plugin's old handlers are let go of.
    private void MarkStopped(Instance instance, string state)
    {
        HostContext? context;
        lock (_gate)
        {
            if (instance.Closed || instance.State == state && instance.Context is null) return;
            context = instance.Context;
            instance.Context = null;
            instance.View = null;
            instance.Canvas = null;
            instance.Plugin = null;
            instance.State = state;
            _outbox?.Add(new CanvasEvent("state") { InstanceId = instance.Id, State = state });
        }

        context?.Retire();
    }

    private void OnPluginsChanged(object? sender, PluginRuntimeChangedEventArgs e)
        => _ = Task.Run(async () =>
        {
            try { await ReconcileAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Canvases").Error(exception, "The canvases could not follow a change of the plugins");
            }
        });

    // After plugins started, were replaced or stopped: each open instance moves to the new version, or waits.
    internal async Task ReconcileAsync()
    {
        await _reconcile.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            lock (_gate) _outbox?.Add(new CanvasEvent("plugins"));
            // What a plugin that is gone gave as text is let go.
            Modules.Prune();
            Instance[] open;
            lock (_gate) open = [.. _instances.Values.OrderBy(static instance => instance.Order)];
            foreach (var instance in open)
            {
                var declaration = Find(instance.Identity.PluginKey, instance.Identity.CanvasId);
                if (declaration is null)
                {
                    await instance.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try { MarkStopped(instance, IsActive(instance.Identity.PluginKey) ? "unknown_canvas" : "plugin_stopped"); }
                    finally { instance.Gate.Release(); }
                    continue;
                }

                if (ReferenceEquals(instance.Plugin, declaration.Plugin) && instance.State == "ready") continue;
                await instance.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (instance.Closed) continue;
                    if (await InitializeAsync(instance, declaration).ConfigureAwait(false) is not null) MarkStopped(instance, "failed");
                }
                finally
                {
                    instance.Gate.Release();
                }
            }
        }
        finally
        {
            _reconcile.Release();
        }
    }

    // Closes the oldest instance that no tab shows. The page asks for it again when its tab is shown.
    private bool EvictOldestHiddenLocked()
    {
        var oldest = _instances.Values.Where(static instance => instance.Initialized && !instance.Visible && !instance.Closed).OrderBy(static instance => instance.Order).FirstOrDefault();
        if (oldest is null) return false;
        oldest.Closed = true;
        _instances.Remove(oldest.Id);
        var context = oldest.Context;
        _ = Task.Run(() => context?.Retire());
        return true;
    }

    private void Remove(Instance instance)
    {
        lock (_gate)
        {
            instance.Closed = true;
            if (_instances.TryGetValue(instance.Id, out var known) && ReferenceEquals(known, instance)) _instances.Remove(instance.Id);
        }
    }

    // The context of an instance that need not be open: the one of the open instance, or a detached one.
    private HostContext ContextOf(CanvasIdentity identity, CanvasDeclaration declaration)
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(InstanceId(identity), out var instance) && !instance.Closed && instance.Context is { } open
                && ReferenceEquals(instance.Plugin, declaration.Plugin)) return open;
        }

        return new HostContext(this, null, identity, declaration.Plugin.RuntimeContext.LifetimeCancellationToken, declaration.Canvas.Scope);
    }

    private static void LogFailure(Exception exception, Instance instance, string what) => LogFailure(exception, instance.Identity, what);

    private static void LogFailure(Exception exception, CanvasIdentity identity, string what)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Canvases").Error(exception, $"The canvas '{identity.CanvasId}' of the plugin '{identity.PluginKey}' failed ({what})");
    }

    private static void LogFailure(Exception exception, HostContext context, string what) => LogFailure(exception, context.Identity, what);

    // One line, within a limit: a title or a status never carries a line break.
    private static string Line(string text, int maximum)
        => DesktopPluginUi.Cut(string.Concat(text.Select(static value => char.IsControl(value) ? ' ' : value)).Trim(), maximum);

    private static string Cut(string value, int maximum) => DesktopPluginUi.Cut(value, maximum);

    private sealed class Instance(string id, CanvasIdentity identity, long order)
    {
        public string Id { get; } = id;

        public CanvasIdentity Identity { get; } = identity;

        public long Order { get; } = order;

        /// <summary>Serializes the opening, the replacement and the closing of the instance.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool Initialized { get; set; }

        public bool Closed { get; set; }

        public bool Visible
        {
            get => Volatile.Read(ref VisibleFlag);
            set => Volatile.Write(ref VisibleFlag, value);
        }

        /// <summary>The field behind <see cref="Visible"/>, which a context reads without the lock of the broker.</summary>
        public bool VisibleFlag;

        public string State { get; set; } = "new";

        public ActivePluginInstance? Plugin { get; set; }

        public PluginCanvasContribution? Canvas { get; set; }

        public HostContext? Context { get; set; }

        public PluginCanvasView? View { get; set; }

        public JsonElement? Input { get; set; }

        public string Html { get; set; } = string.Empty;

        public string? Script { get; set; }

        public string? ScriptProblem { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? StatusText { get; set; }

        public int Revision { get; set; }
    }

    private sealed class HostContext : PluginCanvasContext
    {
        private readonly DesktopCanvases _owner;
        private readonly Instance? _instance;
        // Null for a context without an instance: it ends with the plugin and holds no registration on its token.
        private readonly CancellationTokenSource? _closed;
        // The token outlives its source, which is disposed when the context is retired.
        private readonly CancellationToken _token;

        // What the plugin registers for its script to call: only an instance that has a tab has one.
        private readonly PluginRpcRegistry? _rpc;

        public HostContext(DesktopCanvases owner, Instance? instance, CanvasIdentity identity, CancellationToken pluginLifetime, PluginCanvasScope scope, Logger? logger = null)
        {
            _owner = owner;
            _instance = instance;
            Identity = identity;
            Scope = scope;
            _closed = instance is null ? null : CancellationTokenSource.CreateLinkedTokenSource(pluginLifetime);
            _token = _closed?.Token ?? pluginLifetime;
            Input = null;
            _rpc = instance is null ? null : new PluginRpcRegistry(identity.PluginKey, identity.CanvasId, logger, _token);
        }

        public HostContext(DesktopCanvases owner, Instance instance, JsonElement? input, CancellationToken pluginLifetime, PluginCanvasScope scope, Logger? logger = null)
            : this(owner, instance, instance.Identity, pluginLifetime, scope, logger)
        {
            Input = input;
        }

        /// <summary>The host that serves the calls of the script of the tab, once the plugin returned its view; null when there is none.</summary>
        public CanvasRpcEndpoint? Endpoint { get; private set; }

        /// <summary>Ends the registration of the calls, and builds the host that serves them when the plugin registered any or its view has a script.</summary>
        public void StartRpc(ICanvasRpcCarrier carrier, bool hasScript)
        {
            if (_rpc is null) return;
            if (_rpc.Count > 0 || hasScript) Endpoint = new CanvasRpcEndpoint(InstanceId, _rpc, carrier);
            else _rpc.Seal();
        }

        public override IPluginCanvasRpc Rpc => _rpc ?? base.Rpc;

        public CanvasIdentity Identity { get; }

        public PluginCanvasScope Scope { get; }

        public override string InstanceId => DesktopCanvases.InstanceId(Identity);

        public override string CanvasId => Identity.CanvasId;

        public override string? SpaceId => Identity.SpaceId;

        public override string? ProjectId => Identity.ProjectId;

        public override string? SessionId => Identity.SessionId;

        public override string? Key => Identity.Key;

        public override JsonElement? Input { get; }

        public override bool IsVisible => _instance is { } instance && IsOpen && Volatile.Read(ref instance.VisibleFlag);

        public override bool IsOpen => _closed is not null && !_token.IsCancellationRequested;

        public override CancellationToken Closed => _token;

        public override event Action<bool>? VisibilityChanged;

        public void RaiseVisibility(bool visible)
        {
            try { VisibilityChanged?.Invoke(visible); }
            catch (Exception exception) { LogFailure(exception, this, "visibility"); }
        }

        /// <summary>Ends the instance for the plugin: its token is cancelled and what it pushes is dropped.</summary>
        public void Retire()
        {
            if (_closed is not { } closed) return;
            Endpoint?.Retire();
            try
            {
                closed.Cancel();
                closed.Dispose();
            }
            catch (Exception exception) { LogFailure(exception, this, "retire"); }
        }

        public override ValueTask SetTitleAsync(string? title, CancellationToken cancellationToken = default)
        {
            if (_instance is not null && IsOpen) _owner.PushTitle(_instance, this, title, null, setTitle: true, setStatus: false);
            return ValueTask.CompletedTask;
        }

        public override ValueTask SetStatusAsync(string? status, CancellationToken cancellationToken = default)
        {
            if (_instance is not null && IsOpen) _owner.PushTitle(_instance, this, null, status, setTitle: false, setStatus: true);
            return ValueTask.CompletedTask;
        }

        public override ValueTask UpdateAsync(string html, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(html);
            if (_instance is not null && IsOpen) _owner.PushHtml(_instance, this, html, answered: false);
            return ValueTask.CompletedTask;
        }

        public override async ValueTask InvalidateAsync(CancellationToken cancellationToken = default)
        {
            if (_instance is null || !IsOpen) return;
            PluginCanvasRenderHandler? renderer;
            lock (_owner._gate) renderer = ReferenceEquals(_instance.Context, this) ? _instance.View?.Renderer : null;
            if (renderer is null) return;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Closed);
                linked.CancelAfter(HandlerTimeout);
                var html = await renderer(this, linked.Token).ConfigureAwait(false);
                _owner.PushHtml(_instance, this, html ?? string.Empty, answered: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || !IsOpen) { }
            catch (Exception exception)
            {
                LogFailure(exception, this, "render"); // What was shown stays.
            }
        }
    }

    private sealed class PluginView(DesktopCanvases owner, string pluginKey) : IPluginCanvasService
    {
        public bool HasInteractiveUi => true;

        public ValueTask<PluginCanvasOpenResult> OpenAsync(string canvasId, PluginCanvasOpenOptions? options = null, CancellationToken cancellationToken = default)
            => owner.RequestOpenAsync(pluginKey, canvasId, options, cancellationToken);

        public async ValueTask<bool> CloseAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            // A plugin closes its own instances only.
            lock (owner._gate)
            {
                if (!owner._instances.TryGetValue(instanceId, out var instance) || !string.Equals(instance.Identity.PluginKey, pluginKey, StringComparison.Ordinal)) return false;
            }

            return await owner.CloseAsync(instanceId, notifyPage: true).ConfigureAwait(false);
        }

        public async ValueTask InvalidateAsync(string canvasId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(canvasId);
            await owner.InvalidateAsync(pluginKey, canvasId).ConfigureAwait(false);
        }

        public IReadOnlyList<PluginCanvasInstanceInfo> GetOpen() => owner.List(pluginKey);
    }
}

/// <summary>
/// The events waiting for one page. A new content of an instance replaces the one that waits for it, so a page that
/// reads slowly gets the last state of each instance and not every step; the events that cannot be merged are kept
/// in order up to a limit, and the oldest is dropped beyond it.
/// </summary>
internal sealed class CanvasOutbox
{
    private readonly Lock _lock = new();
    private readonly List<CanvasEvent> _events = [];
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    // The frames of the connections of scripts, in order: they are neither merged nor dropped, and a writer waits for room.
    private readonly Queue<RpcEntry> _rpc = new();
    private TaskCompletionSource _rpcRoom = NewRoom();
    private long _rpcChars;
    private bool _complete;

    public void Add(CanvasEvent value)
    {
        lock (_lock)
        {
            if (_complete) return;
            if (value.Kind == "update" && value.InstanceId is { } id)
            {
                var index = _events.FindIndex(candidate => candidate.Kind == "update" && string.Equals(candidate.InstanceId, id, StringComparison.Ordinal));
                if (index >= 0)
                {
                    _events[index] = Merge(_events[index], value);
                    _signal.Writer.TryWrite(true);
                    return;
                }
            }
            else if (value.Kind is "plugins" && _events.Any(static candidate => candidate.Kind == "plugins"))
            {
                return;
            }
            else if (value.Kind is "state" or "closed" && value.InstanceId is { } known)
            {
                // What waited for an instance that changed state or closed is moot.
                _events.RemoveAll(candidate => candidate.Kind is "update" or "state" && string.Equals(candidate.InstanceId, known, StringComparison.Ordinal));
            }

            if (_events.Count >= DesktopCanvases.MaximumPendingEvents)
            {
                var droppable = _events.FindIndex(static candidate => candidate.Kind != "open");
                _events.RemoveAt(droppable >= 0 ? droppable : 0);
            }

            _events.Add(value);
            _signal.Writer.TryWrite(true);
        }
    }

    public void Complete()
    {
        TaskCompletionSource room;
        lock (_lock)
        {
            _complete = true;
            room = _rpcRoom;
        }

        room.TrySetResult();
        _signal.Writer.TryComplete();
    }

    /// <summary>
    /// Queues a frame of a connection for the page. It waits while the page has too many frames waiting, which holds back the session
    /// that produced it, and so the stream behind it.
    /// </summary>
    /// <exception cref="IOException">The page stopped watching.</exception>
    public async ValueTask AddRpcAsync(string instanceId, string connection, string frame, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task room;
            lock (_lock)
            {
                if (_complete) throw new IOException("The page stopped watching the canvases.");
                if (_rpc.Count < DesktopCanvases.MaximumPendingRpcFrames && (_rpc.Count == 0 || _rpcChars + frame.Length <= DesktopCanvases.MaximumPendingRpcChars))
                {
                    _rpc.Enqueue(new RpcEntry(instanceId, connection, frame, null));
                    _rpcChars += frame.Length;
                    _signal.Writer.TryWrite(true);
                    return;
                }

                room = _rpcRoom.Task;
            }

            await room.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Queues the end of a connection after the frames queued for it.</summary>
    public void AddRpcClosed(string instanceId, string connection, string reason)
    {
        lock (_lock)
        {
            if (_complete) return;
            _rpc.Enqueue(new RpcEntry(instanceId, connection, null, reason));
            _signal.Writer.TryWrite(true);
        }
    }

    private static TaskCompletionSource NewRoom() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The next event for the page, with the frames of one connection that wait together in one event.
    private CanvasEvent? TakeRpcLocked()
    {
        if (_rpc.Count == 0) return null;
        var first = _rpc.Dequeue();
        CanvasEvent value;
        if (first.Frame is null)
        {
            value = new CanvasEvent("rpcClosed") { InstanceId = first.InstanceId, Connection = first.Connection, Reason = first.Reason };
        }
        else
        {
            var frames = new List<string> { first.Frame };
            _rpcChars -= first.Frame.Length;
            var chars = first.Frame.Length;
            while (_rpc.TryPeek(out var next) && next.Frame is not null && frames.Count < DesktopCanvases.MaximumRpcFramesPerEvent
                && string.Equals(next.InstanceId, first.InstanceId, StringComparison.Ordinal) && string.Equals(next.Connection, first.Connection, StringComparison.Ordinal)
                && chars + next.Frame.Length <= DesktopCanvases.MaximumPendingRpcChars / 8)
            {
                _rpc.Dequeue();
                frames.Add(next.Frame);
                _rpcChars -= next.Frame.Length;
                chars += next.Frame.Length;
            }

            value = new CanvasEvent("rpc") { InstanceId = first.InstanceId, Connection = first.Connection, Frames = [.. frames] };
        }

        var room = _rpcRoom;
        _rpcRoom = NewRoom();
        room.TrySetResult();
        return value;
    }

    private sealed record RpcEntry(string InstanceId, string Connection, string? Frame, string? Reason);

    public async IAsyncEnumerable<CanvasEvent> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            CanvasEvent? next = null;
            lock (_lock)
            {
                if (_events.Count > 0) { next = _events[0]; _events.RemoveAt(0); }
                else next = TakeRpcLocked();
            }

            if (next is not null) { yield return next; continue; }
            try
            {
                if (!await _signal.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) yield break;
                _signal.Reader.TryRead(out _);
            }
            catch (OperationCanceledException) { yield break; }
        }
    }

    // What the second event says wins; what it does not say stays.
    private static CanvasEvent Merge(CanvasEvent older, CanvasEvent newer)
        => older with
        {
            Html = newer.Html ?? older.Html, Title = newer.Title ?? older.Title, StatusText = newer.StatusText ?? older.StatusText,
            Actions = newer.Actions ?? older.Actions, Revision = newer.Revision ?? older.Revision, State = newer.State ?? older.State,
        };
}
