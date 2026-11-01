using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Browser.Agent;
using CodeAlta.Browser.Cdp;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Browser.Drivers.Cdp;

/// <summary>A tab of a session in a Chromium browser, driven over its own flat session of the DevTools Protocol.</summary>
/// <remarks>
/// Events come in on the delivery loop of the session and only record what they say; commands are sent by the tools.
/// The frames of other sites run in their own processes and targets (out-of-process iframes): they are attached as
/// child sessions, and a frame is driven through the session that holds it.
/// </remarks>
internal sealed class CdpPage : IChromiumPage
{
    // The categories DevTools records a performance trace with.
    private const string TraceCategories =
        "-*,devtools.timeline,v8.execute,disabled-by-default-devtools.timeline,disabled-by-default-devtools.timeline.frame,toplevel,blink.console," +
        "blink.user_timing,latencyInfo,disabled-by-default-devtools.timeline.stack,disabled-by-default-v8.cpu_profiler,disabled-by-default-devtools.screenshot," +
        "loading,disabled-by-default-devtools.timeline.invalidationTracking,disabled-by-default-layout_shift.debug";

    private readonly CdpBrowserContext _owner;
    private readonly CdpSession _session;
    private readonly ConcurrentDictionary<string, int> _worlds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _mainWorlds = new(StringComparer.Ordinal);
    // The session that holds each frame: a frame of another site is in a target of its own.
    private readonly ConcurrentDictionary<string, CdpSession> _frameSessions = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Lock _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _mainFrameId;
    private string _url = "about:blank";
    private string _title = string.Empty;
    private long _started;
    private long _stopped;
    private PageDialog? _dialog;
    private Func<PageDialog, (bool Accept, string? Text)?>? _dialogPolicy;
    private InputDragData? _dragData;
    private TaskCompletionSource<int?>? _fileChooser;
    private int _closed;

    private CdpPage(CdpBrowserContext owner, CdpSession session, string targetId, string mainFrameId)
    {
        _owner = owner;
        _session = session;
        TargetId = targetId;
        _mainFrameId = mainFrameId;
        _frameSessions[mainFrameId] = session;
    }

    /// <summary>Gets the id of the target of the tab.</summary>
    public string TargetId { get; }

    /// <inheritdoc />
    public string Url => Volatile.Read(ref _url);

    /// <inheritdoc />
    public string Title => Volatile.Read(ref _title);

    /// <inheritdoc />
    public bool IsClosed => Volatile.Read(ref _closed) != 0 || _session.Completed.IsCompleted;

    /// <inheritdoc />
    public BrowserFrame MainFrame => new(Volatile.Read(ref _mainFrameId));

    /// <inheritdoc />
    public PageLog Log { get; } = new();

    /// <inheritdoc />
    public PageDialog? Dialog => Volatile.Read(ref _dialog);

    /// <inheritdoc />
    public long Navigations => Interlocked.Read(ref _started);

    /// <summary>Attaches to a target and readies its session: events first, then the domains that send them.</summary>
    public static async Task<CdpPage> AttachAsync(CdpBrowserContext owner, string targetId, CancellationToken cancellationToken)
    {
        var connection = owner.Connection;
        var attached = await connection.Browser.Target.AttachToTargetAsync(new TargetAttachToTargetParams { TargetId = targetId, Flatten = true }, cancellationToken).ConfigureAwait(false);
        var session = connection.Session(attached.SessionId);
        var frames = await session.Page.GetFrameTreeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var page = new CdpPage(owner, session, targetId, frames.FrameTree.Frame.Id) { _url = frames.FrameTree.Frame.Url };
        page.Subscribe(session, isMain: true);
        await page.EnableAsync(session, isMain: true, cancellationToken).ConfigureAwait(false);
        return page;
    }

    internal void UpdateInfo(TargetTargetInfo info)
    {
        Volatile.Write(ref _title, info.Title);
        if (!string.IsNullOrEmpty(info.Url)) Volatile.Write(ref _url, info.Url);
    }

    internal void MarkClosed()
    {
        Interlocked.Exchange(ref _closed, 1);
        Changed();
    }

    private void Changed()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous.TrySetResult();
    }

    private Task NextChange()
    {
        lock (_gate) return _changed.Task;
    }

    private void Subscribe(CdpSession session, bool isMain)
    {
        lock (_subscriptions)
        {
            if (isMain)
            {
                _subscriptions.Add(session.Page.OnFrameStartedLoading(item =>
                {
                    if (item.FrameId == Volatile.Read(ref _mainFrameId)) { Interlocked.Increment(ref _started); Changed(); }
                }));
                _subscriptions.Add(session.Page.OnFrameStoppedLoading(item =>
                {
                    if (item.FrameId == Volatile.Read(ref _mainFrameId)) { Interlocked.Increment(ref _stopped); Changed(); }
                }));
                _subscriptions.Add(session.Page.OnNavigatedWithinDocument(item =>
                {
                    if (item.FrameId == Volatile.Read(ref _mainFrameId)) Volatile.Write(ref _url, item.Url);
                }));
                _subscriptions.Add(session.Page.OnJavascriptDialogOpening(item =>
                {
                    var dialog = new PageDialog(item.Type, item.Message, item.DefaultPrompt);
                    Volatile.Write(ref _dialog, dialog);
                    if (Volatile.Read(ref _dialogPolicy)?.Invoke(dialog) is { } answer)
                    {
                        _ = HandleQuietlyAsync(answer.Accept, answer.Text);
                    }

                    Changed();
                }));
                _subscriptions.Add(session.Page.OnJavascriptDialogClosed(_ =>
                {
                    Volatile.Write(ref _dialog, null);
                    Changed();
                }));
                _subscriptions.Add(session.Page.OnFileChooserOpened(item => Volatile.Read(ref _fileChooser)?.TrySetResult(item.BackendNodeId)));
                _subscriptions.Add(session.Input.OnDragIntercepted(item => Volatile.Write(ref _dragData, item.Data)));
            }

            _subscriptions.Add(session.Page.OnFrameNavigated(item =>
            {
                _worlds.TryRemove(item.Frame.Id, out _);
                if (item.Frame.ParentId is null && isMain)
                {
                    Volatile.Write(ref _mainFrameId, item.Frame.Id);
                    _frameSessions[item.Frame.Id] = session;
                    Volatile.Write(ref _url, item.Frame.Url);
                    if (item.Type == "Navigation") Log.StartNavigation();
                    Changed();
                }
            }));
            _subscriptions.Add(session.Runtime.OnExecutionContextCreated(item =>
            {
                var context = item.Context;
                if (context.AuxData is not { ValueKind: JsonValueKind.Object } aux || !aux.TryGetProperty("frameId", out var frameValue) || frameValue.GetString() is not { } frameId) return;
                _frameSessions.TryAdd(frameId, session);
                if (context.Name == BrowserAgent.WorldName) _worlds[frameId] = context.Id;
                else if (aux.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True) _mainWorlds[frameId] = context.Id;
            }));
            _subscriptions.Add(session.Runtime.OnExecutionContextDestroyed(item =>
            {
                foreach (var (frame, id) in _worlds) if (id == item.ExecutionContextId) _worlds.TryRemove(frame, out _);
                foreach (var (frame, id) in _mainWorlds) if (id == item.ExecutionContextId) _mainWorlds.TryRemove(frame, out _);
            }));
            _subscriptions.Add(session.Runtime.OnExecutionContextsCleared(cleared =>
            {
                foreach (var (frame, owner) in _frameSessions)
                {
                    if (owner != session) continue;
                    _worlds.TryRemove(frame, out _);
                    _mainWorlds.TryRemove(frame, out _);
                }
            }));
            _subscriptions.Add(session.Runtime.OnConsoleAPICalled(item =>
            {
                var arguments = item.Args.Select(Describe).ToArray();
                var top = item.StackTrace?.CallFrames.FirstOrDefault();
                Log.AddConsole(ConsoleType(item.Type), string.Join(' ', arguments), arguments, top?.Url, (top?.LineNumber ?? -1) + 1, (top?.ColumnNumber ?? -1) + 1, FormatStack(item.StackTrace));
            }));
            _subscriptions.Add(session.Runtime.OnExceptionThrown(item =>
            {
                var details = item.ExceptionDetails;
                var text = details.Exception?.Description ?? details.Text;
                Log.AddConsole("error", BrowserAgent.ErrorText(text, details.Text), [], details.Url, details.LineNumber + 1, details.ColumnNumber + 1, FormatStack(details.StackTrace));
            }));
            _subscriptions.Add(session.Log.OnEntryAdded(item =>
            {
                var entry = item.Entry;
                Log.AddConsole(entry.Level switch { "warning" => "warn", "verbose" => "debug", var level => level }, entry.Text, [], entry.Url, entry.LineNumber is { } line ? line + 1 : 0, 0, FormatStack(entry.StackTrace));
            }));
            _subscriptions.Add(session.Network.OnRequestWillBeSent(item =>
            {
                if (item.RedirectResponse is { } redirect)
                {
                    Log.Update(item.RequestId, entry => { entry.Status = redirect.Status; entry.ResponseHeaders = Headers(redirect.Headers); entry.Finished = true; });
                }

                Log.AddRequest(item.RequestId, item.Request.Url, item.Request.Method, (item.Type ?? "Other").ToLowerInvariant(), Headers(item.Request.Headers),
                    item.Request.HasPostData == true || item.Request.PostData is not null, item.Request.PostData);
            }));
            _subscriptions.Add(session.Network.OnResponseReceived(item => Log.Update(item.RequestId, entry =>
            {
                entry.Status = item.Response.Status;
                entry.ResponseHeaders = Headers(item.Response.Headers);
                entry.MimeType = item.Response.MimeType;
            })));
            _subscriptions.Add(session.Network.OnLoadingFinished(item => Log.Update(item.RequestId, static entry => entry.Finished = true)));
            _subscriptions.Add(session.Network.OnLoadingFailed(item => Log.Update(item.RequestId, entry =>
            {
                entry.Failure = item.Canceled == true ? "net::ERR_ABORTED" : item.ErrorText;
                entry.Finished = true;
            })));
            _subscriptions.Add(session.Fetch.OnRequestPaused(item => _ = DecideAsync(session, item)));
            _subscriptions.Add(session.Target.OnAttachedToTarget(item => _ = AttachFrameAsync(item)));
        }
    }

    private async Task EnableAsync(CdpSession session, bool isMain, CancellationToken cancellationToken)
    {
        await session.Page.EnableAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await session.Runtime.EnableAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await session.Network.EnableAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await session.Log.EnableAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await session.DOM.EnableAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (_owner.Options.Gate is not null && (_owner.Options.GateDocuments || _owner.Options.BlockedPorts.Count != 0))
        {
            var patterns = new List<FetchRequestPattern>();
            if (_owner.Options.GateDocuments) patterns.Add(new FetchRequestPattern { UrlPattern = "*", ResourceType = "Document", RequestStage = "Request" });
            foreach (var port in _owner.Options.BlockedPorts)
            {
                foreach (var host in new[] { "127.0.0.1", "localhost", "[::1]" }) patterns.Add(new FetchRequestPattern { UrlPattern = $"*://{host}:{port}/*", RequestStage = "Request" });
            }

            await session.Fetch.EnableAsync(new FetchEnableParams { Patterns = [.. patterns] }, cancellationToken).ConfigureAwait(false);
        }

        if (isMain)
        {
            // A tab in the background runs its timers and draws as one in the front: the agent works in every tab.
            await session.Emulation.SetFocusEmulationEnabledAsync(new EmulationSetFocusEmulationEnabledParams { Enabled = true }, cancellationToken).ConfigureAwait(false);
        }

        await session.Target.SetAutoAttachAsync(new TargetSetAutoAttachParams { AutoAttach = true, WaitForDebuggerOnStart = false, Flatten = true }, cancellationToken).ConfigureAwait(false);
    }

    private async Task AttachFrameAsync(TargetAttachedToTargetEvent item)
    {
        if (item.TargetInfo.Type != "iframe") return;
        try
        {
            var child = _session.Connection.Session(item.SessionId);
            _frameSessions[item.TargetInfo.TargetId] = child;
            Subscribe(child, isMain: false);
            await EnableAsync(child, isMain: false, CancellationToken.None).ConfigureAwait(false);
            if (item.WaitingForDebugger) await child.Runtime.RunIfWaitingForDebuggerAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (CdpException)
        {
            // The frame went away while it was attached.
        }
    }

    private async Task DecideAsync(CdpSession session, FetchRequestPausedEvent item)
    {
        try
        {
            string? refusal = null;
            if (_owner.Options.Gate is { } gate && Uri.TryCreate(item.Request.Url, UriKind.Absolute, out var url))
            {
                refusal = await gate(url, item.ResourceType == "Document", CancellationToken.None).ConfigureAwait(false);
            }

            if (refusal is null) await session.Fetch.ContinueRequestAsync(new FetchContinueRequestParams { RequestId = item.RequestId }).ConfigureAwait(false);
            else
            {
                Log.AddConsole("error", $"CodeAlta blocked {item.Request.Url}: {refusal}", [], item.Request.Url, 0, 0, null);
                await session.Fetch.FailRequestAsync(new FetchFailRequestParams { RequestId = item.RequestId, ErrorReason = "BlockedByClient" }).ConfigureAwait(false);
            }
        }
        catch (CdpException)
        {
            // The request ended meanwhile.
        }
    }

    private static string ConsoleType(string type) => type switch
    {
        "warning" => "warn",
        "startGroup" or "startGroupCollapsed" or "endGroup" or "table" or "trace" or "clear" or "assert" or "dir" or "dirxml" or "count" or "timeEnd" or "profile" or "profileEnd" => type,
        _ => type,
    };

    private static string Describe(RuntimeRemoteObject value)
    {
        if (value.Value is { } raw)
        {
            return raw.ValueKind == JsonValueKind.String ? raw.GetString()! : raw.GetRawText();
        }

        if (value.UnserializableValue is { } unserializable) return unserializable;
        if (value.Type == "undefined") return "undefined";
        if (value.Preview is { } preview && value.Subtype is not ("node" or "error"))
        {
            var properties = preview.Properties.Select(static property => $"{property.Name}: {property.Value ?? property.Type}");
            return value.Subtype == "array" ? $"[{string.Join(", ", preview.Properties.Select(static property => property.Value ?? property.Type))}{(preview.Overflow ? ", …" : string.Empty)}]"
                : $"{{{string.Join(", ", properties)}{(preview.Overflow ? ", …" : string.Empty)}}}";
        }

        return value.Description ?? value.Type;
    }

    private static string? FormatStack(RuntimeStackTrace? stack)
    {
        if (stack is null || stack.CallFrames.Length == 0) return null;
        var builder = new StringBuilder();
        foreach (var frame in stack.CallFrames.Take(50))
        {
            builder.Append("at ").Append(frame.FunctionName.Length == 0 ? "<anonymous>" : frame.FunctionName)
                .Append(" (").Append(frame.Url).Append(':').Append(frame.LineNumber + 1).Append(':').Append(frame.ColumnNumber + 1).Append(")\n");
        }

        return builder.ToString().TrimEnd('\n');
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> Headers(JsonElement? headers)
    {
        if (headers is not { ValueKind: JsonValueKind.Object } value) return [];
        return [.. value.EnumerateObject().Select(static property => new KeyValuePair<string, string>(property.Name, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText()))];
    }

    private CdpSession SessionOf(BrowserFrame frame)
        => _frameSessions.TryGetValue(frame.Key, out var session) ? session : _session;

    // -------------------------------------------------------------------------------------------------------------
    // Navigation
    // -------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task NavigateAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var before = Navigations;
        PageNavigateResult result;
        using (var limit = Limit(timeout, cancellationToken, out var token))
        {
            try
            {
                result = await _session.Page.NavigateAsync(new PageNavigateParams { Url = url }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BrowserToolException("timeout", $"Navigation timeout of {(int)timeout.TotalMilliseconds} ms exceeded");
            }
        }

        if (result.ErrorText is { Length: > 0 } error && error != "net::ERR_ABORTED") throw new BrowserToolException("navigation", $"{error} at {url}");
        if (result.LoaderId is null) return;
        await WaitForLoadAsync(before, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task TraverseAsync(int delta, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var history = await _session.Page.GetNavigationHistoryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var index = history.CurrentIndex + delta;
        if (index < 0 || index >= history.Entries.Length) throw new BrowserToolException("no-history", delta < 0 ? "Cannot navigate back, no previous page in history" : "Cannot navigate forward, no next page in history");
        var before = Navigations;
        await _session.Page.NavigateToHistoryEntryAsync(new PageNavigateToHistoryEntryParams { EntryId = history.Entries[index].Id }, cancellationToken).ConfigureAwait(false);
        await WaitForNavigationAsync(before, TimeSpan.FromSeconds(1), timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReloadAsync(bool ignoreCache, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var before = Navigations;
        await _session.Page.ReloadAsync(new PageReloadParams { IgnoreCache = ignoreCache }, cancellationToken).ConfigureAwait(false);
        await WaitForNavigationAsync(before, TimeSpan.FromSeconds(2), timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> WaitForNavigationAsync(long navigationsBefore, TimeSpan startWindow, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await WaitUntilAsync(() => Navigations > navigationsBefore || IsClosed, startWindow, cancellationToken).ConfigureAwait(false)) return false;
        await WaitForLoadAsync(navigationsBefore, timeout, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task WaitForLoadAsync(long navigationsBefore, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // A document is loaded when the main frame stopped loading as often as it started: a download or a
        // navigation that failed stops too.
        var loaded = await WaitUntilAsync(
            () => IsClosed || Dialog is not null || (Navigations > navigationsBefore && Interlocked.Read(ref _stopped) >= Navigations), timeout, cancellationToken).ConfigureAwait(false);
        if (!loaded) throw new BrowserToolException("timeout", $"Navigation timeout of {(int)timeout.TotalMilliseconds} ms exceeded");
        await RefreshInfoAsync(cancellationToken).ConfigureAwait(false);
    }

    // The title of a document is told a moment after its load: read it now, so that the answer of the tool has it.
    private async Task RefreshInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await _owner.Connection.Browser.Target.GetTargetInfoAsync(new TargetGetTargetInfoParams { TargetId = TargetId }, cancellationToken).ConfigureAwait(false);
            UpdateInfo(info.TargetInfo);
        }
        catch (CdpException)
        {
            // The tab closed.
        }
    }

    private async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var next = NextChange();
            if (condition()) return true;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;
            await Task.WhenAny(next, Task.Delay(remaining, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static CancellationTokenSource Limit(TimeSpan timeout, CancellationToken cancellationToken, out CancellationToken token)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        token = source.Token;
        return source;
    }

    // -------------------------------------------------------------------------------------------------------------
    // Script
    // -------------------------------------------------------------------------------------------------------------

    private async Task<int> WorldAsync(CdpSession session, string frameId, CancellationToken cancellationToken)
    {
        if (_worlds.TryGetValue(frameId, out var id)) return id;
        var world = await session.Page.CreateIsolatedWorldAsync(new PageCreateIsolatedWorldParams { FrameId = frameId, WorldName = BrowserAgent.WorldName, GrantUniveralAccess = false }, cancellationToken).ConfigureAwait(false);
        _worlds[frameId] = world.ExecutionContextId;
        return world.ExecutionContextId;
    }

    private static bool IsContextGone(CdpException exception)
        => exception.Message.Contains("context", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("Cannot find", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<JsonElement> CallAgentAsync(BrowserFrame frame, string operation, string optionsJson, CancellationToken cancellationToken)
    {
        var session = SessionOf(frame);
        using var options = JsonDocument.Parse(optionsJson);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var world = await WorldAsync(session, frame.Key, cancellationToken).ConfigureAwait(false);
                var result = await session.Runtime.CallFunctionOnAsync(new RuntimeCallFunctionOnParams
                {
                    FunctionDeclaration = BrowserAgent.CallFunction,
                    ExecutionContextId = world,
                    Arguments = [new RuntimeCallArgument { Value = JsonString(operation) }, new RuntimeCallArgument { Value = options.RootElement }],
                    ReturnByValue = true,
                    AwaitPromise = true,
                }, cancellationToken).ConfigureAwait(false);
                if (result.ExceptionDetails is { } details) throw new BrowserToolException("script-error", BrowserAgent.ErrorText(details.Exception?.Description, details.Text));
                var answer = BrowserAgent.ReadAnswer(result.Result.Value?.GetString());
                if (answer is { } value) return value;
                if (attempt > 2) throw new BrowserToolException("no-agent", "The page did not take CodeAlta's agent.");
                await session.Runtime.EvaluateAsync(new RuntimeEvaluateParams { Expression = BrowserAgent.Source, ContextId = world, Silent = true }, cancellationToken).ConfigureAwait(false);
            }
            catch (CdpException exception) when (attempt < 2 && IsContextGone(exception))
            {
                // The document changed under the call: its world is gone, and the next one is made for the new document.
                _worlds.TryRemove(frame.Key, out _);
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (CdpException exception)
            {
                throw new BrowserToolException("browser", exception.Message, exception);
            }
        }
    }

    private static JsonElement JsonString(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, BrowserJsonContext.Default.String));
        return document.RootElement.Clone();
    }

    private async Task<(CdpSession Session, string ObjectId)> ElementAsync(BrowserFrame frame, string uid, string function, CancellationToken cancellationToken)
    {
        var session = SessionOf(frame);
        var world = await WorldAsync(session, frame.Key, cancellationToken).ConfigureAwait(false);
        var result = await session.Runtime.CallFunctionOnAsync(new RuntimeCallFunctionOnParams
        {
            FunctionDeclaration = function,
            ExecutionContextId = world,
            Arguments = [new RuntimeCallArgument { Value = JsonString(uid) }],
        }, cancellationToken).ConfigureAwait(false);
        if (result.ExceptionDetails is { } details) throw new BrowserToolException("stale", BrowserAgent.ErrorText(details.Exception?.Description, details.Text));
        if (result.Result.ObjectId is not { } objectId) throw new BrowserToolException("no-element", $"The element with uid {uid} has no element of that kind.");
        return (session, objectId);
    }

    /// <inheritdoc />
    public async Task<BrowserFrame?> ChildFrameAsync(BrowserFrame parent, string uid, CancellationToken cancellationToken)
    {
        try
        {
            var (session, objectId) = await ElementAsync(parent, uid, BrowserAgent.ElementFunction, cancellationToken).ConfigureAwait(false);
            var node = await session.DOM.DescribeNodeAsync(new DOMDescribeNodeParams { ObjectId = objectId }, cancellationToken).ConfigureAwait(false);
            await session.Runtime.ReleaseObjectAsync(new RuntimeReleaseObjectParams { ObjectId = objectId }, cancellationToken).ConfigureAwait(false);
            if (node.Node.FrameId is not { } frameId) return null;
            // A frame of this process is driven through the session of its parent; a target of its own was registered when attached.
            _frameSessions.TryAdd(frameId, session);
            return new BrowserFrame(frameId);
        }
        catch (Exception exception) when (exception is CdpException or BrowserToolException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> EvaluateAsync(string source, bool isFunction, IReadOnlyList<(BrowserFrame Frame, string Uid)> arguments, CancellationToken cancellationToken)
    {
        var frame = arguments.Count != 0 ? arguments[0].Frame : MainFrame;
        if (arguments.Any(argument => argument.Frame != frame)) throw new BrowserToolException("invalid-arguments", "Elements from different frames can't be evaluated together.");
        var session = SessionOf(frame);
        if (!_mainWorlds.TryGetValue(frame.Key, out var context))
        {
            // The main world is created with the document; ask for it once more when the event was missed.
            await WaitUntilAsync(() => _mainWorlds.ContainsKey(frame.Key), TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            if (!_mainWorlds.TryGetValue(frame.Key, out context)) throw new BrowserToolException("no-context", "The page has no document to run the script in yet.");
        }

        try
        {
            if (!isFunction)
            {
                var evaluated = await session.Runtime.EvaluateAsync(new RuntimeEvaluateParams { Expression = source, ContextId = context, AwaitPromise = true, ReturnByValue = true, UserGesture = true }, cancellationToken).ConfigureAwait(false);
                if (evaluated.ExceptionDetails is { } failure) throw new BrowserToolException("script-error", BrowserAgent.ErrorText(failure.Exception?.Description, failure.Text));
                return evaluated.Result.Type == "undefined" ? null : evaluated.Result.Value is { } raw ? raw.GetRawText() : "null";
            }

            var handles = new List<RuntimeCallArgument>();
            foreach (var (argumentFrame, uid) in arguments)
            {
                var (_, isolated) = await ElementAsync(argumentFrame, uid, BrowserAgent.ElementFunction, cancellationToken).ConfigureAwait(false);
                var node = await session.DOM.DescribeNodeAsync(new DOMDescribeNodeParams { ObjectId = isolated }, cancellationToken).ConfigureAwait(false);
                var resolved = await session.DOM.ResolveNodeAsync(new DOMResolveNodeParams { BackendNodeId = node.Node.BackendNodeId, ExecutionContextId = context }, cancellationToken).ConfigureAwait(false);
                handles.Add(new RuntimeCallArgument { ObjectId = resolved.Object.ObjectId });
            }

            var result = await session.Runtime.CallFunctionOnAsync(new RuntimeCallFunctionOnParams
            {
                FunctionDeclaration = BrowserAgent.WrapFunction(source),
                ExecutionContextId = context,
                Arguments = [.. handles],
                AwaitPromise = true,
                ReturnByValue = true,
                UserGesture = true,
            }, cancellationToken).ConfigureAwait(false);
            if (result.ExceptionDetails is { } details) throw new BrowserToolException("script-error", BrowserAgent.ErrorText(details.Exception?.Description, details.Text));
            return ReadWrapped(result.Result.Value);
        }
        catch (CdpException exception)
        {
            throw new BrowserToolException("script-error", exception.Message, exception);
        }
    }

    internal static string? ReadWrapped(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } wrapped) return null;
        return wrapped.TryGetProperty("json", out var json) && json.ValueKind == JsonValueKind.String ? json.GetString() : null;
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AddInitScriptAsync(string source, CancellationToken cancellationToken)
    {
        var added = await _session.Page.AddScriptToEvaluateOnNewDocumentAsync(new PageAddScriptToEvaluateOnNewDocumentParams { Source = source }, cancellationToken).ConfigureAwait(false);
        return new InitScript(_session, added.Identifier);
    }

    private sealed class InitScript(CdpSession session, string identifier) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await session.Page.RemoveScriptToEvaluateOnNewDocumentAsync(new PageRemoveScriptToEvaluateOnNewDocumentParams { Identifier = identifier }).ConfigureAwait(false); }
            catch (CdpException) { /* the tab is gone */ }
        }
    }

    /// <inheritdoc />
    public async Task SetFilesAsync(BrowserFrame frame, string uid, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        try
        {
            var (session, objectId) = await ElementAsync(frame, uid, BrowserAgent.FileInputFunction, cancellationToken).ConfigureAwait(false);
            await session.DOM.SetFileInputFilesAsync(new DOMSetFileInputFilesParams { Files = [.. paths], ObjectId = objectId }, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (BrowserToolException exception) when (exception.Code == "no-element")
        {
            // A button that opens the file chooser of a hidden input: click it, and take the input the chooser is for.
        }

        var chooser = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _fileChooser, chooser);
        await _session.Page.SetInterceptFileChooserDialogAsync(new PageSetInterceptFileChooserDialogParams { Enabled = true }, cancellationToken).ConfigureAwait(false);
        try
        {
            // The tool layer resolves points across frames; a file input is clicked where the agent of its frame says.
            var point = await CallAgentAsync(frame, "prepareClick", $$"""{"uid":{{Json(uid)}}}""", cancellationToken).ConfigureAwait(false);
            await ClickAtAsync(point.GetProperty("x").GetDouble(), point.GetProperty("y").GetDouble(), 1, cancellationToken).ConfigureAwait(false);
            if (await Task.WhenAny(chooser.Task, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)).ConfigureAwait(false) != chooser.Task || chooser.Task.Result is not { } backendNodeId)
            {
                throw new BrowserToolException("no-file-input", $"The element with uid {uid} is not a file input and opened no file chooser.");
            }

            await _session.DOM.SetFileInputFilesAsync(new DOMSetFileInputFilesParams { Files = [.. paths], BackendNodeId = backendNodeId }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _fileChooser, null);
            try { await _session.Page.SetInterceptFileChooserDialogAsync(new PageSetInterceptFileChooserDialogParams { Enabled = false }, CancellationToken.None).ConfigureAwait(false); }
            catch (CdpException) { /* the tab is gone */ }
        }
    }

    internal static string Json(string value) => JsonSerializer.Serialize(value, BrowserJsonContext.Default.String);

    // -------------------------------------------------------------------------------------------------------------
    // Input
    // -------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task MouseMoveAsync(BrowserPoint point, CancellationToken cancellationToken) => MoveAsync(point.X, point.Y, cancellationToken);

    private Task MoveAsync(double x, double y, CancellationToken cancellationToken)
        => _session.Input.DispatchMouseEventAsync(new InputDispatchMouseEventParams { Type = "mouseMoved", X = x, Y = y, Button = "none" }, cancellationToken);

    /// <inheritdoc />
    public Task ClickAsync(BrowserPoint point, int clickCount, CancellationToken cancellationToken) => ClickAtAsync(point.X, point.Y, clickCount, cancellationToken);

    private async Task ClickAtAsync(double x, double y, int clickCount, CancellationToken cancellationToken)
    {
        await MoveAsync(x, y, cancellationToken).ConfigureAwait(false);
        for (var count = 1; count <= clickCount; count++)
        {
            await _session.Input.DispatchMouseEventAsync(new InputDispatchMouseEventParams { Type = "mousePressed", X = x, Y = y, Button = "left", Buttons = 1, ClickCount = count }, cancellationToken).ConfigureAwait(false);
            await _session.Input.DispatchMouseEventAsync(new InputDispatchMouseEventParams { Type = "mouseReleased", X = x, Y = y, Button = "left", Buttons = 0, ClickCount = count }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DragAsync(BrowserPoint from, BrowserPoint to, CancellationToken cancellationToken)
    {
        var (fromX, fromY, toX, toY) = (from.X, from.Y, to.X, to.Y);
        // A native drag and drop of the page is taken over, as Puppeteer does: the browser gives the drag data, which
        // is dropped where asked; a drag made of pointer events only is moved along.
        Volatile.Write(ref _dragData, null);
        await _session.Input.SetInterceptDragsAsync(new InputSetInterceptDragsParams { Enabled = true }, cancellationToken).ConfigureAwait(false);
        try
        {
            await MoveAsync(fromX, fromY, cancellationToken).ConfigureAwait(false);
            await _session.Input.DispatchMouseEventAsync(new InputDispatchMouseEventParams { Type = "mousePressed", X = fromX, Y = fromY, Button = "left", Buttons = 1, ClickCount = 1 }, cancellationToken).ConfigureAwait(false);
            const int steps = 5;
            for (var step = 1; step <= steps; step++)
            {
                var x = fromX + (toX - fromX) * step / steps;
                var y = fromY + (toY - fromY) * step / steps;
                await _session.Input.DispatchMouseEventAsync(new InputDispatchMouseEventParams { Type = "mouseMoved", X = x, Y = y, Button = "left", Buttons = 1 }, cancellationToken).ConfigureAwait(false);
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }

            await _session.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _dragData) is { } data)
            {
                foreach (var type in new[] { "dragEnter", "dragOver", "drop" })
                {
                    await _session.Input.DispatchDragEventAsync(new InputDispatchDragEventParams { Type = type, X = toX, Y = toY, Data = data }, cancellationToken).ConfigureAwait(false);
                }
            }

            await _session.Input.DispatchMouseEventAsync(new InputDispatchMouseEventParams { Type = "mouseReleased", X = toX, Y = toY, Button = "left", Buttons = 0, ClickCount = 1 }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await _session.Input.SetInterceptDragsAsync(new InputSetInterceptDragsParams { Enabled = false }, CancellationToken.None).ConfigureAwait(false); }
            catch (CdpException) { /* the tab is gone */ }
        }
    }

    /// <inheritdoc />
    public Task KeyAsync(BrowserKey key, bool down, int modifiers, CancellationToken cancellationToken)
        => _session.Input.DispatchKeyEventAsync(new InputDispatchKeyEventParams
        {
            Type = down ? key.Text is null ? "rawKeyDown" : "keyDown" : "keyUp",
            Key = key.Key,
            Code = key.Code,
            WindowsVirtualKeyCode = key.KeyCode,
            NativeVirtualKeyCode = key.KeyCode,
            Location = key.Location,
            IsKeypad = key.Location == 3,
            Text = down && (modifiers & ~8) == 0 ? key.Text : null,
            UnmodifiedText = down && (modifiers & ~8) == 0 ? key.Text : null,
            Modifiers = modifiers,
        }, cancellationToken);

    /// <inheritdoc />
    public Task InsertTextAsync(string text, CancellationToken cancellationToken)
        => _session.Input.InsertTextAsync(new InputInsertTextParams { Text = text }, cancellationToken);

    // -------------------------------------------------------------------------------------------------------------
    // Capture, dialogs, network, emulation
    // -------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<byte[]> ScreenshotAsync(ScreenshotRequest request, CancellationToken cancellationToken)
    {
        PageViewport? clip = null;
        if (request.FullPage)
        {
            var metrics = await _session.Page.GetLayoutMetricsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            clip = new PageViewport { X = 0, Y = 0, Width = Math.Ceiling(metrics.CssContentSize.Width), Height = Math.Ceiling(metrics.CssContentSize.Height), Scale = 1 };
        }
        else if (request.Clip is { } box)
        {
            clip = new PageViewport { X = box.X, Y = box.Y, Width = Math.Max(1, box.Width), Height = Math.Max(1, box.Height), Scale = 1 };
        }

        var shot = await _session.Page.CaptureScreenshotAsync(new PageCaptureScreenshotParams
        {
            Format = request.Format,
            Quality = request.Format == "jpeg" ? request.Quality : null,
            Clip = clip,
            CaptureBeyondViewport = clip is not null,
            OptimizeForSpeed = true,
        }, cancellationToken).ConfigureAwait(false);
        return Convert.FromBase64String(shot.Data);
    }

    /// <inheritdoc />
    public async Task HandleDialogAsync(bool accept, string? promptText, CancellationToken cancellationToken)
    {
        if (Dialog is null) throw new BrowserToolException("no-dialog", "No open dialog found");
        await _session.Page.HandleJavaScriptDialogAsync(new PageHandleJavaScriptDialogParams { Accept = accept, PromptText = promptText }, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _dialog, null);
        Changed();
    }

    private async Task HandleQuietlyAsync(bool accept, string? text)
    {
        try { await HandleDialogAsync(accept, text, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is CdpException or BrowserToolException) { /* answered meanwhile */ }
    }

    /// <inheritdoc />
    public IDisposable AnswerDialogs(Func<PageDialog, (bool Accept, string? Text)?> policy)
    {
        Volatile.Write(ref _dialogPolicy, policy);
        return new Release(() => Volatile.Write(ref _dialogPolicy, null));
    }

    private sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <inheritdoc />
    public async Task<NetworkBodies> ReadBodiesAsync(NetworkEntry entry, CancellationToken cancellationToken)
    {
        var request = entry.PostData;
        if (request is null && entry.HasPostData)
        {
            try { request = (await _session.Network.GetRequestPostDataAsync(new NetworkGetRequestPostDataParams { RequestId = entry.RequestId }, cancellationToken).ConfigureAwait(false)).PostData; }
            catch (CdpException) { request = null; }
        }

        string? response = null;
        if (entry.Finished && entry.Failure is null)
        {
            try
            {
                var body = await _session.Network.GetResponseBodyAsync(new NetworkGetResponseBodyParams { RequestId = entry.RequestId }, cancellationToken).ConfigureAwait(false);
                response = !body.Base64Encoded ? body.Body : IsTextual(entry.MimeType) ? Encoding.UTF8.GetString(Convert.FromBase64String(body.Body)) : "<binary data>";
            }
            catch (Exception exception) when (exception is CdpException or FormatException)
            {
                response = null;
            }
        }

        return new NetworkBodies(request, response);
    }

    internal static bool IsTextual(string? mimeType)
        => mimeType is not null && (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mimeType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mimeType.Contains("javascript", StringComparison.OrdinalIgnoreCase) || mimeType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mimeType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<(int Width, int Height)?> ResizeAsync(int width, int height, CancellationToken cancellationToken)
    {
        try
        {
            var window = await _owner.Connection.Browser.Browser.GetWindowForTargetAsync(new BrowserGetWindowForTargetParams { TargetId = TargetId }, cancellationToken).ConfigureAwait(false);
            if (window.Bounds.WindowState is { } state && state != "normal")
            {
                await _owner.Connection.Browser.Browser.SetWindowBoundsAsync(new BrowserSetWindowBoundsParams { WindowId = window.WindowId, Bounds = new BrowserBounds { WindowState = "normal" } }, cancellationToken).ConfigureAwait(false);
                window = await _owner.Connection.Browser.Browser.GetWindowForTargetAsync(new BrowserGetWindowForTargetParams { TargetId = TargetId }, cancellationToken).ConfigureAwait(false);
            }

            var (innerWidth, innerHeight) = await ViewportAsync(cancellationToken).ConfigureAwait(false);
            var frameWidth = (window.Bounds.Width ?? innerWidth) - innerWidth;
            var frameHeight = (window.Bounds.Height ?? innerHeight) - innerHeight;
            await _owner.Connection.Browser.Browser.SetWindowBoundsAsync(new BrowserSetWindowBoundsParams
            {
                WindowId = window.WindowId,
                Bounds = new BrowserBounds { Width = width + frameWidth, Height = height + frameHeight },
            }, cancellationToken).ConfigureAwait(false);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            return await ViewportAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CdpException)
        {
            // A browser without windows (headless shell): the viewport is emulated instead.
            await _session.Emulation.SetDeviceMetricsOverrideAsync(new EmulationSetDeviceMetricsOverrideParams { Width = width, Height = height, DeviceScaleFactor = 0, Mobile = false }, cancellationToken).ConfigureAwait(false);
            return (width, height);
        }
    }

    private async Task<(int Width, int Height)> ViewportAsync(CancellationToken cancellationToken)
    {
        var info = await CallAgentAsync(MainFrame, "info", "{}", cancellationToken).ConfigureAwait(false);
        var viewport = info.GetProperty("viewport");
        return ((int)viewport.GetProperty("width").GetDouble(), (int)viewport.GetProperty("height").GetDouble());
    }

    /// <inheritdoc />
    public async Task EmulateAsync(EmulationSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Viewport is { } viewport)
        {
            await _session.Emulation.SetDeviceMetricsOverrideAsync(new EmulationSetDeviceMetricsOverrideParams
            {
                Width = viewport.Width, Height = viewport.Height, DeviceScaleFactor = viewport.DeviceScaleFactor, Mobile = viewport.Mobile,
                ScreenOrientation = viewport.Mobile || viewport.Landscape
                    ? new EmulationScreenOrientation { Type = viewport.Landscape ? "landscapePrimary" : "portraitPrimary", Angle = viewport.Landscape ? 90 : 0 }
                    : null,
            }, cancellationToken).ConfigureAwait(false);
            await _session.Emulation.SetTouchEmulationEnabledAsync(new EmulationSetTouchEmulationEnabledParams { Enabled = viewport.Touch }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _session.Emulation.ClearDeviceMetricsOverrideAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await _session.Emulation.SetTouchEmulationEnabledAsync(new EmulationSetTouchEmulationEnabledParams { Enabled = false }, cancellationToken).ConfigureAwait(false);
        }

        var features = new List<EmulationMediaFeature>();
        if (settings.ColorScheme is { } scheme) features.Add(new EmulationMediaFeature { Name = "prefers-color-scheme", Value = scheme });
        if (settings.ForcedColors is { } forced) features.Add(new EmulationMediaFeature { Name = "forced-colors", Value = forced });
        if (settings.PrefersContrast is { } contrast) features.Add(new EmulationMediaFeature { Name = "prefers-contrast", Value = contrast });
        if (settings.PrefersReducedMotion is { } motion) features.Add(new EmulationMediaFeature { Name = "prefers-reduced-motion", Value = motion });
        await _session.Emulation.SetEmulatedMediaAsync(new EmulationSetEmulatedMediaParams { Features = [.. features] }, cancellationToken).ConfigureAwait(false);

        await _session.Emulation.SetUserAgentOverrideAsync(new EmulationSetUserAgentOverrideParams { UserAgent = settings.UserAgent ?? _owner.DefaultUserAgent }, cancellationToken).ConfigureAwait(false);

        if (settings.Geolocation is { } location)
        {
            await _session.Emulation.SetGeolocationOverrideAsync(new EmulationSetGeolocationOverrideParams { Latitude = location.Latitude, Longitude = location.Longitude, Accuracy = 1 }, cancellationToken).ConfigureAwait(false);
        }
        else await _session.Emulation.ClearGeolocationOverrideAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var (offline, latency, download, upload) = NetworkPreset(settings.NetworkConditions);
        await _session.Network.EmulateNetworkConditionsAsync(new NetworkEmulateNetworkConditionsParams { Offline = offline, Latency = latency, DownloadThroughput = download, UploadThroughput = upload }, cancellationToken).ConfigureAwait(false);
        await _session.Emulation.SetCPUThrottlingRateAsync(new EmulationSetCPUThrottlingRateParams { Rate = settings.CpuThrottlingRate }, cancellationToken).ConfigureAwait(false);
        if (settings.ExtraHeaders is { } headers)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(headers, BrowserJsonContext.Default.DictionaryStringString));
            await _session.Network.SetExtraHTTPHeadersAsync(new NetworkSetExtraHTTPHeadersParams { Headers = document.RootElement.Clone() }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The throughput (bytes per second) and latency (ms) of the presets of DevTools, as Puppeteer defines them.</summary>
    internal static (bool Offline, double Latency, double Download, double Upload) NetworkPreset(string? name) => name switch
    {
        "Offline" => (true, 0, 0, 0),
        "Slow 3G" => (false, 400 * 5, 500.0 * 1000 / 8 * 0.8, 500.0 * 1000 / 8 * 0.8),
        "Fast 3G" or "Slow 4G" => (false, 150 * 3.75, 1.6 * 1000 * 1000 / 8 * 0.9, 750.0 * 1000 / 8 * 0.9),
        "Fast 4G" => (false, 60 * 2.75, 9.0 * 1000 * 1000 / 8 * 0.9, 1.5 * 1000 * 1000 / 8 * 0.9),
        _ => (false, 0, -1, -1),
    };

    /// <inheritdoc />
    public Task BringToFrontAsync(CancellationToken cancellationToken) => _session.Page.BringToFrontAsync(cancellationToken: cancellationToken);

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        try { await _owner.Connection.Browser.Target.CloseTargetAsync(new TargetCloseTargetParams { TargetId = TargetId }, cancellationToken).ConfigureAwait(false); }
        catch (CdpException) { /* closed meanwhile */ }
        MarkClosed();
    }

    // -------------------------------------------------------------------------------------------------------------
    // Chromium extras
    // -------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task StartTraceAsync(CancellationToken cancellationToken)
        => _session.Tracing.StartAsync(new TracingStartParams { Categories = TraceCategories, TransferMode = "ReturnAsStream", StreamFormat = "json" }, cancellationToken);

    /// <inheritdoc />
    public async Task StopTraceAsync(Stream destination, CancellationToken cancellationToken)
    {
        var complete = new TaskCompletionSource<TracingTracingCompleteEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _session.Tracing.OnTracingComplete(item => complete.TrySetResult(item));
        await _session.Tracing.EndAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var done = await complete.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        if (done.Stream is not { } handle) throw new BrowserToolException("trace", "The browser gave no trace.");
        try
        {
            while (true)
            {
                var chunk = await _session.IO.ReadAsync(new IOReadParams { Handle = handle, Size = 1 << 20 }, cancellationToken).ConfigureAwait(false);
                var bytes = chunk.Base64Encoded == true ? Convert.FromBase64String(chunk.Data) : Encoding.UTF8.GetBytes(chunk.Data);
                await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (chunk.Eof) break;
            }
        }
        finally
        {
            try { await _session.IO.CloseAsync(new IOCloseParams { Handle = handle }, CancellationToken.None).ConfigureAwait(false); }
            catch (CdpException) { /* the stream ended with the tab */ }
        }
    }

    /// <inheritdoc />
    public async Task TakeHeapSnapshotAsync(Stream destination, CancellationToken cancellationToken)
    {
        var chunks = new List<string>();
        using var subscription = _session.HeapProfiler.OnAddHeapSnapshotChunk(item => { lock (chunks) chunks.Add(item.Chunk); });
        await _session.HeapProfiler.EnableAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await _session.HeapProfiler.TakeHeapSnapshotAsync(new HeapProfilerTakeHeapSnapshotParams { ReportProgress = false }, cancellationToken).ConfigureAwait(false);
            // The chunks come before the answer, and wait for their handler behind it.
            await _session.FlushAsync(cancellationToken).ConfigureAwait(false);
            string[] parts;
            lock (chunks) parts = [.. chunks];
            foreach (var part in parts) await destination.WriteAsync(Encoding.UTF8.GetBytes(part), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await _session.HeapProfiler.DisableAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false); }
            catch (CdpException) { /* the tab is gone */ }
        }
    }

    internal void DisposeSubscriptions()
    {
        lock (_subscriptions)
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }

    internal static string FormatNumber(double value) => value.ToString(CultureInfo.InvariantCulture);
}
