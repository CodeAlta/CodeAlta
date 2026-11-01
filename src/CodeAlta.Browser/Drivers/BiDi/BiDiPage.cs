using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Browser.Agent;
using CodeAlta.Browser.Tools;
using WebDriverBiDi;
using WebDriverBiDi.BrowsingContext;
using WebDriverBiDi.Emulation;
using WebDriverBiDi.Input;
using WebDriverBiDi.Log;
using WebDriverBiDi.Network;
using WebDriverBiDi.Script;
using WebDriverBiDi.Session;
using BrowserModule = WebDriverBiDi.Browser;

namespace CodeAlta.Browser.Drivers.BiDi;

/// <summary>A tab of a session in a Firefox, driven over WebDriver BiDi; its frames are browsing contexts of their own.</summary>
internal sealed class BiDiPage : IBrowserPage
{
    // The agent is called with its options as JSON text, which the sandbox reads back.
    private const string CallFunction =
        "function (name, options) { const agent = globalThis.__codealtaBrowserAgent; return agent ? agent.call(name, JSON.parse(options)) : '" + BrowserAgent.Missing + "'; }";

    private const string Undefined = "\u0000codealta-undefined";

    // The key values of WebDriver for the keys that type no character.
    private static readonly FrozenDictionary<string, string> SpecialKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Cancel"] = "", ["Help"] = "", ["Backspace"] = "", ["Tab"] = "", ["Clear"] = "", ["Enter"] = "",
        ["Shift"] = "", ["Control"] = "", ["Alt"] = "", ["Pause"] = "", ["Escape"] = "", ["PageUp"] = "",
        ["PageDown"] = "", ["End"] = "", ["Home"] = "", ["ArrowLeft"] = "", ["ArrowUp"] = "", ["ArrowRight"] = "",
        ["ArrowDown"] = "", ["Insert"] = "", ["Delete"] = "", ["Meta"] = "", ["F1"] = "", ["F2"] = "",
        ["F3"] = "", ["F4"] = "", ["F5"] = "", ["F6"] = "", ["F7"] = "", ["F8"] = "", ["F9"] = "",
        ["F10"] = "", ["F11"] = "", ["F12"] = "",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly BiDiBrowserContext _owner;
    private readonly Lock _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _url = "about:blank";
    private string? _navigatingTo;
    private string _title = string.Empty;
    private long _started;
    private long _loaded;
    private PageDialog? _dialog;
    private Func<PageDialog, (bool Accept, string? Text)?>? _dialogPolicy;
    private string? _collector;
    private string? _intercept;
    private int _closed;

    internal BiDiPage(BiDiBrowserContext owner, string id)
    {
        _owner = owner;
        Id = id;
    }

    /// <summary>Gets the id of the top-level browsing context of the tab.</summary>
    public string Id { get; }

    private BiDiDriver Driver => _owner.Connection.Driver;

    /// <inheritdoc />
    public string Url => Volatile.Read(ref _url);

    /// <inheritdoc />
    public string Title => Volatile.Read(ref _title);

    /// <inheritdoc />
    public bool IsClosed => Volatile.Read(ref _closed) != 0 || _owner.IsClosed;

    /// <inheritdoc />
    public BrowserFrame MainFrame => new(Id);

    /// <inheritdoc />
    public PageLog Log { get; } = new();

    /// <inheritdoc />
    public PageDialog? Dialog => Volatile.Read(ref _dialog);

    /// <inheritdoc />
    public long Navigations => Interlocked.Read(ref _started);

    /// <summary>Subscribes the events of the tab, and keeps the bodies of its requests for get_network_request.</summary>
    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _owner.Connection.SubscribeAsync(Id).ConfigureAwait(false);
        try
        {
            var collector = new AddDataCollectorCommandParameters(10 * 1024 * 1024, DataType.Response, DataType.Request);
            collector.Contexts.Add(Id);
            _collector = (await Driver.Network.AddDataCollectorAsync(collector).ConfigureAwait(false)).CollectorId;
        }
        catch (WebDriverBiDiException)
        {
            // A Firefox without data collectors lists the requests without their bodies.
        }

        var options = _owner.Options;
        if (options.Gate is not null && (options.GateDocuments || options.BlockedPorts.Count != 0))
        {
            var intercept = new AddInterceptCommandParameters(InterceptPhase.BeforeRequestSent);
            intercept.Contexts.Add(Id);
            if (!options.GateDocuments)
            {
                foreach (var port in options.BlockedPorts)
                {
                    foreach (var host in new[] { "127.0.0.1", "localhost", "[::1]" }) intercept.UrlPatterns.Add(new UrlPatternPattern { HostName = host, Port = port.ToString(CultureInfo.InvariantCulture) });
                }
            }

            _intercept = (await Driver.Network.AddInterceptAsync(intercept).ConfigureAwait(false)).InterceptId;
        }

        try
        {
            var tree = await Driver.BrowsingContext.GetTreeAsync(new GetTreeCommandParameters { RootBrowsingContextId = Id, MaxDepth = 0 }).ConfigureAwait(false);
            if (tree.ContextTree.FirstOrDefault() is { } info) Volatile.Write(ref _url, info.Url);
        }
        catch (WebDriverBiDiException)
        {
            // It closed meanwhile.
        }
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

    // -------------------------------------------------------------------------------------------------------------
    // Events, from the observers of the connection
    // -------------------------------------------------------------------------------------------------------------

    internal void OnNavigationStarted(string context, string url)
    {
        if (context != Id) return;
        Interlocked.Increment(ref _started);
        Log.StartNavigation();
        Volatile.Write(ref _navigatingTo, url);
        Volatile.Write(ref _url, url);
        Changed();
    }

    internal void OnLoad(string context, string url)
    {
        if (context != Id) return;
        Interlocked.Increment(ref _loaded);
        if (!string.IsNullOrEmpty(url)) Volatile.Write(ref _url, url);
        Changed();
    }

    internal void OnFragment(string context, string url)
    {
        if (context == Id) Volatile.Write(ref _url, url);
    }

    internal void OnPromptOpened(UserPromptOpenedEventArgs item)
    {
        var type = item.PromptType switch { UserPromptType.Alert => "alert", UserPromptType.Confirm => "confirm", UserPromptType.Prompt => "prompt", _ => "beforeunload" };
        var dialog = new PageDialog(type, item.Message, item.DefaultValue);
        Volatile.Write(ref _dialog, dialog);
        if (Volatile.Read(ref _dialogPolicy)?.Invoke(dialog) is { } answer) _ = HandleQuietlyAsync(answer.Accept, answer.Text);
        Changed();
    }

    internal void OnPromptClosed()
    {
        Volatile.Write(ref _dialog, null);
        Changed();
    }

    internal void OnLog(EntryAddedEventArgs item)
    {
        var top = item.StackTrace?.CallFrames.FirstOrDefault();
        var stack = FormatStack(item.StackTrace);
        var line = top is null ? 0 : (int)top.LineNumber + 1;
        var column = top is null ? 0 : (int)top.ColumnNumber + 1;
        if (item.Method is { } method && item.Arguments is { } values)
        {
            var arguments = values.Select(Describe).ToArray();
            var type = method switch { "warning" => "warn", _ => method };
            Log.AddConsole(type, arguments.Length != 0 ? string.Join(' ', arguments) : item.Text ?? string.Empty, arguments, top?.Url, line, column, stack);
        }
        else
        {
            var level = item.Level switch { LogLevel.Debug => "debug", LogLevel.Info => "info", LogLevel.Warn => "warn", _ => "error" };
            Log.AddConsole(level, item.Text ?? string.Empty, [], top?.Url, line, column, stack);
        }
    }

    internal void OnRequest(BeforeRequestSentEventArgs item)
    {
        var request = item.Request;
        Log.AddRequest(request.RequestId, request.Url, request.Method, ResourceType(request.Destination, request.InitiatorType), Headers(request.Headers), request.BodySize is > 0, null);
        if (item.IsBlocked) _ = DecideAsync(item);
    }

    internal void OnResponse(ResponseCompletedEventArgs item) => Log.Update(item.Request.RequestId, entry =>
    {
        entry.Status = (int)item.Response.Status;
        entry.ResponseHeaders = Headers(item.Response.Headers);
        entry.MimeType = item.Response.MimeType;
        entry.Finished = true;
    });

    internal void OnFetchError(FetchErrorEventArgs item) => Log.Update(item.Request.RequestId, entry =>
    {
        entry.Failure = item.ErrorText;
        entry.Finished = true;
    });

    private async Task DecideAsync(BeforeRequestSentEventArgs item)
    {
        try
        {
            string? refusal = null;
            if (_owner.Options.Gate is { } gate && Uri.TryCreate(item.Request.Url, UriKind.Absolute, out var url))
            {
                var isDocument = item.NavigationId is not null || item.Request.Destination is "document" or "iframe" or "frame";
                var kind = !isDocument ? BrowserRequestKind.Resource : item.BrowsingContextId == Id ? BrowserRequestKind.Page : BrowserRequestKind.Frame;
                refusal = await gate(url, kind, CancellationToken.None).ConfigureAwait(false);
            }

            if (refusal is null) await Driver.Network.ContinueRequestAsync(new ContinueRequestCommandParameters(item.Request.RequestId)).ConfigureAwait(false);
            else
            {
                Log.AddConsole("error", $"CodeAlta blocked {item.Request.Url}: {refusal}", [], item.Request.Url, 0, 0, null);
                await Driver.Network.FailRequestAsync(new FailRequestCommandParameters(item.Request.RequestId)).ConfigureAwait(false);
            }
        }
        catch (WebDriverBiDiException)
        {
            // The request ended meanwhile.
        }
    }

    // The resource types of Chrome DevTools from the destination and the initiator of the Fetch standard.
    internal static string ResourceType(string destination, string? initiator) => destination switch
    {
        "document" or "iframe" or "frame" => "document",
        "style" => "stylesheet",
        "script" or "worker" or "sharedworker" or "serviceworker" or "audioworklet" or "paintworklet" => "script",
        "image" => "image",
        "font" => "font",
        "audio" or "video" => "media",
        "track" => "texttrack",
        "manifest" => "manifest",
        _ => initiator switch { "fetch" => "fetch", "xmlhttprequest" => "xhr", "beacon" or "ping" => "ping", "css" => "stylesheet", "img" or "image" => "image", _ => "other" },
    };

    private static IReadOnlyList<KeyValuePair<string, string>> Headers(IList<ReadOnlyHeader> headers)
        => [.. headers.Select(static header => new KeyValuePair<string, string>(header.Name, header.Value.Value))];

    private static string Describe(RemoteValue value) => value switch
    {
        StringRemoteValue text => text.Value,
        NumberRemoteValue number => number.Value.ToString(CultureInfo.InvariantCulture),
        BooleanRemoteValue boolean => boolean.Value ? "true" : "false",
        NullRemoteValue => "null",
        UndefinedRemoteValue => "undefined",
        _ => value.Type.ToString().ToLowerInvariant(),
    };

    private static string? FormatStack(WebDriverBiDi.Script.StackTrace? stack)
    {
        if (stack is null || stack.CallFrames.Count == 0) return null;
        var builder = new StringBuilder();
        foreach (var frame in stack.CallFrames.Take(50))
        {
            builder.Append("at ").Append(frame.FunctionName.Length == 0 ? "<anonymous>" : frame.FunctionName)
                .Append(" (").Append(frame.Url).Append(':').Append(frame.LineNumber + 1).Append(':').Append(frame.ColumnNumber + 1).Append(")\n");
        }

        return builder.ToString().TrimEnd('\n');
    }

    // -------------------------------------------------------------------------------------------------------------
    // Navigation
    // -------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task NavigateAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var before = Navigations;
        try
        {
            await Driver.BrowsingContext.NavigateAsync(new NavigateCommandParameters(Id, url) { Wait = ReadinessState.None }).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new BrowserToolException("timeout", $"Navigation timeout of {(int)timeout.TotalMilliseconds} ms exceeded");
        }
        catch (WebDriverBiDiException exception)
        {
            throw new BrowserToolException("navigation", exception.Message, exception);
        }

        await WaitForNavigationAsync(before, TimeSpan.FromSeconds(2), timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task TraverseAsync(int delta, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var before = Navigations;
        try
        {
            await Driver.BrowsingContext.TraverseHistoryAsync(new TraverseHistoryCommandParameters(Id, delta)).ConfigureAwait(false);
        }
        catch (WebDriverBiDiException)
        {
            throw new BrowserToolException("no-history", delta < 0 ? "Cannot navigate back, no previous page in history" : "Cannot navigate forward, no next page in history");
        }

        await WaitForNavigationAsync(before, TimeSpan.FromSeconds(1), timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReloadAsync(bool ignoreCache, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var before = Navigations;
        try
        {
            await Driver.BrowsingContext.ReloadAsync(new ReloadCommandParameters(Id) { IgnoreCache = ignoreCache ? true : null, Wait = ReadinessState.None }).ConfigureAwait(false);
        }
        catch (WebDriverBiDiException) when (ignoreCache)
        {
            // A Firefox that does not reload without its cache yet: the cache of the tab is bypassed for the reload.
            var bypass = new SetCacheBehaviorCommandParameters(CacheBehavior.Bypass);
            bypass.Contexts.Add(Id);
            await Driver.Network.SetCacheBehaviorAsync(bypass).ConfigureAwait(false);
            try
            {
                await Driver.BrowsingContext.ReloadAsync(new ReloadCommandParameters(Id) { Wait = ReadinessState.None }).ConfigureAwait(false);
                await WaitForNavigationAsync(before, TimeSpan.FromSeconds(2), timeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                var restore = new SetCacheBehaviorCommandParameters(CacheBehavior.Default);
                restore.Contexts.Add(Id);
                await Driver.Network.SetCacheBehaviorAsync(restore).ConfigureAwait(false);
            }

            return;
        }

        await WaitForNavigationAsync(before, TimeSpan.FromSeconds(2), timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> WaitForNavigationAsync(long navigationsBefore, TimeSpan startWindow, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await WaitUntilAsync(() => Navigations > navigationsBefore || IsClosed, startWindow, cancellationToken).ConfigureAwait(false)) return false;
        var deadline = DateTime.UtcNow + timeout;
        while (!(IsClosed || Dialog is not null || Interlocked.Read(ref _loaded) >= Navigations))
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new BrowserToolException("timeout", $"Navigation timeout of {(int)timeout.TotalMilliseconds} ms exceeded");
            if (await WaitUntilAsync(() => IsClosed || Dialog is not null || Interlocked.Read(ref _loaded) >= Navigations, remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false)) break;
            // A page that the history restores from the back-forward cache sends no load event: it is loaded once its
            // document is complete at the address the navigation went to.
            if (await IsRestoredAsync(cancellationToken).ConfigureAwait(false)) break;
        }
        await RefreshTitleAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> IsRestoredAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await CallAgentAsync(MainFrame, "info", "{}", cancellationToken).ConfigureAwait(false);
            return info.GetProperty("readyState").GetString() == "complete" && info.GetProperty("url").GetString() == Volatile.Read(ref _navigatingTo);
        }
        catch (BrowserToolException)
        {
            return false;
        }
    }

    private async Task RefreshTitleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await CallAgentAsync(MainFrame, "info", "{}", cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _title, info.GetProperty("title").GetString() ?? string.Empty);
            Volatile.Write(ref _url, info.GetProperty("url").GetString() ?? Url);
        }
        catch (BrowserToolException)
        {
            // The document is changing again.
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

    // -------------------------------------------------------------------------------------------------------------
    // Script
    // -------------------------------------------------------------------------------------------------------------

    private static ContextTarget Sandbox(BrowserFrame frame) => new(frame.Key) { Sandbox = BrowserAgent.WorldName };

    /// <inheritdoc />
    public async Task<JsonElement> CallAgentAsync(BrowserFrame frame, string operation, string optionsJson, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            EvaluateResult result;
            try
            {
                var call = new CallFunctionCommandParameters(CallFunction, Sandbox(frame), awaitPromise: true);
                call.Arguments.Add(LocalValue.String(operation));
                call.Arguments.Add(LocalValue.String(optionsJson));
                result = await Driver.Script.CallFunctionAsync(call).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (WebDriverBiDiException exception) when (attempt < 2)
            {
                // The document changed under the call: the next attempt runs in the new one.
                _ = exception;
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (WebDriverBiDiException exception)
            {
                throw new BrowserToolException("browser", exception.Message, exception);
            }

            if (result is EvaluateResultException failure) throw new BrowserToolException("script-error", ExceptionText(failure.ExceptionDetails));
            var text = result is EvaluateResultSuccess { Result: StringRemoteValue value } ? value.Value : null;
            if (BrowserAgent.ReadAnswer(text) is { } answer) return answer;
            if (attempt > 2) throw new BrowserToolException("no-agent", "The page did not take CodeAlta's agent.");
            await Driver.Script.EvaluateAsync(new EvaluateCommandParameters(BrowserAgent.Source, Sandbox(frame), awaitPromise: false)).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ExceptionText(ExceptionDetails details)
    {
        var message = details.Exception is ObjectReferenceRemoteValue or NodeRemoteValue ? details.Text : Describe(details.Exception);
        return BrowserAgent.ErrorText(string.IsNullOrEmpty(details.Text) ? message : details.Text, message);
    }

    private async Task<RemoteValue> CallInSandboxAsync(BrowserFrame frame, string function, string uid, CancellationToken cancellationToken)
    {
        var call = new CallFunctionCommandParameters(function, Sandbox(frame), awaitPromise: false);
        call.Arguments.Add(LocalValue.String(uid));
        var result = await Driver.Script.CallFunctionAsync(call).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result is EvaluateResultException failure) throw new BrowserToolException("stale", ExceptionText(failure.ExceptionDetails));
        return ((EvaluateResultSuccess)result).Result;
    }

    private async Task<SharedReference> ElementAsync(BrowserFrame frame, string uid, string function, CancellationToken cancellationToken)
    {
        if (await CallInSandboxAsync(frame, function, uid, cancellationToken).ConfigureAwait(false) is NodeRemoteValue { SharedId: { } shared }) return new SharedReference(shared);
        throw new BrowserToolException("no-element", $"The element with uid {uid} has no element of that kind.");
    }

    /// <inheritdoc />
    public async Task<BrowserFrame?> ChildFrameAsync(BrowserFrame parent, string uid, CancellationToken cancellationToken)
    {
        try
        {
            return await CallInSandboxAsync(parent, BrowserAgent.FrameWindowFunction, uid, cancellationToken).ConfigureAwait(false) is WindowProxyRemoteValue window
                ? new BrowserFrame(window.Value.BrowsingContextId)
                : null;
        }
        catch (Exception exception) when (exception is WebDriverBiDiException or BrowserToolException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> EvaluateAsync(string source, bool isFunction, IReadOnlyList<(BrowserFrame Frame, string Uid)> arguments, CancellationToken cancellationToken)
    {
        var frame = arguments.Count != 0 ? arguments[0].Frame : MainFrame;
        if (arguments.Any(argument => argument.Frame != frame)) throw new BrowserToolException("invalid-arguments", "Elements from different frames can't be evaluated together.");
        try
        {
            EvaluateResult result;
            if (isFunction)
            {
                var wrapped = "async function (...args) { const fn = (\n" + source + "\n); const value = await fn(...args); " +
                    "if (value === undefined) return '" + Undefined + "'; const json = JSON.stringify(value); return json === undefined ? '" + Undefined + "' : json; }";
                var call = new CallFunctionCommandParameters(wrapped, new ContextTarget(frame.Key), awaitPromise: true) { UserActivation = true };
                foreach (var (argumentFrame, uid) in arguments) call.Arguments.Add(await ElementAsync(argumentFrame, uid, BrowserAgent.ElementFunction, cancellationToken).ConfigureAwait(false));
                result = await Driver.Script.CallFunctionAsync(call).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (result is EvaluateResultException failure) throw new BrowserToolException("script-error", ExceptionText(failure.ExceptionDetails));
                var text = ((EvaluateResultSuccess)result).Result is StringRemoteValue value ? value.Value : Undefined;
                return text == Undefined ? null : text;
            }

            result = await Driver.Script.EvaluateAsync(new EvaluateCommandParameters(source, new ContextTarget(frame.Key), awaitPromise: true) { ResultOwnership = ResultOwnership.Root, UserActivation = true })
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result is EvaluateResultException error) throw new BrowserToolException("script-error", ExceptionText(error.ExceptionDetails));
            return await ToJsonAsync(frame, ((EvaluateResultSuccess)result).Result, cancellationToken).ConfigureAwait(false);
        }
        catch (WebDriverBiDiException exception)
        {
            throw new BrowserToolException("script-error", exception.Message, exception);
        }
    }

    // A value of a script as JSON.stringify writes it: a primitive here, an object by the page.
    private async Task<string?> ToJsonAsync(BrowserFrame frame, RemoteValue value, CancellationToken cancellationToken)
    {
        switch (value)
        {
            case UndefinedRemoteValue: return null;
            case NullRemoteValue: return "null";
            case StringRemoteValue text: return JsonSerializer.Serialize(text.Value, BrowserJsonContext.Default.String);
            case NumberRemoteValue number: return double.IsFinite(number.Value) ? number.Value.ToString("R", CultureInfo.InvariantCulture) : "null";
            case BooleanRemoteValue boolean: return boolean.Value ? "true" : "false";
            case ObjectReferenceRemoteValue { Handle: { } handle }:
                var call = new CallFunctionCommandParameters("function () { const json = JSON.stringify(this); return json === undefined ? '" + Undefined + "' : json; }", new ContextTarget(frame.Key), awaitPromise: false)
                {
                    ThisObject = new RemoteObjectReference(handle),
                };
                var result = await Driver.Script.CallFunctionAsync(call).WaitAsync(cancellationToken).ConfigureAwait(false);
                var json = result is EvaluateResultSuccess { Result: StringRemoteValue stringified } ? stringified.Value : Undefined;
                try { await Driver.Script.DisownAsync(new DisownCommandParameters(new ContextTarget(frame.Key), handle)).ConfigureAwait(false); }
                catch (WebDriverBiDiException) { /* the document is gone */ }
                return json == Undefined ? null : json;
            default:
                return null;
        }
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AddInitScriptAsync(string source, CancellationToken cancellationToken)
    {
        var preload = new AddPreloadScriptCommandParameters("() => {\n" + source + "\n}");
        preload.Contexts.Add(Id);
        var added = await Driver.Script.AddPreloadScriptAsync(preload).WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Preload(Driver, added.PreloadScriptId);
    }

    private sealed class Preload(BiDiDriver driver, string id) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await driver.Script.RemovePreloadScriptAsync(new RemovePreloadScriptCommandParameters(id)).ConfigureAwait(false); }
            catch (WebDriverBiDiException) { /* the tab is gone */ }
        }
    }

    /// <inheritdoc />
    public async Task SetFilesAsync(BrowserFrame frame, string uid, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var element = await ElementAsync(frame, uid, BrowserAgent.FileInputFunction, cancellationToken).ConfigureAwait(false);
        var parameters = new SetFilesCommandParameters(frame.Key, element);
        parameters.Files.AddRange(paths);
        await Driver.Input.SetFilesAsync(parameters).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------------------------------------------
    // Input
    // -------------------------------------------------------------------------------------------------------------

    private static PointerSourceActions Mouse() => new("mouse") { Parameters = new PointerParameters { PointerType = PointerType.Mouse } };

    private static PointerMoveAction MoveTo(double x, double y) => new() { X = Math.Round(x), Y = Math.Round(y), Origin = Origin.Viewport };

    private Task PerformAsync(SourceActions actions, CancellationToken cancellationToken, string? context = null)
    {
        var parameters = new PerformActionsCommandParameters(context ?? Id);
        parameters.Actions.Add(actions);
        return Driver.Input.PerformActionsAsync(parameters).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task MouseMoveAsync(BrowserPoint point, CancellationToken cancellationToken)
    {
        var mouse = Mouse();
        mouse.Actions.Add(MoveTo(point.LocalX, point.LocalY));
        return PerformAsync(mouse, cancellationToken, point.Frame.Key);
    }

    // Firefox gives the input of a frame of another process to that frame only when the actions name its context,
    // with coordinates in its viewport.
    /// <inheritdoc />
    public Task ClickAsync(BrowserPoint point, int clickCount, CancellationToken cancellationToken)
    {
        var mouse = Mouse();
        mouse.Actions.Add(MoveTo(point.LocalX, point.LocalY));
        for (var count = 0; count < clickCount; count++)
        {
            mouse.Actions.Add(new PointerDownAction(0));
            mouse.Actions.Add(new PointerUpAction(0));
        }

        return PerformAsync(mouse, cancellationToken, point.Frame.Key);
    }

    /// <inheritdoc />
    public Task DragAsync(BrowserPoint from, BrowserPoint to, CancellationToken cancellationToken)
    {
        // Within one frame, in its coordinates; across frames, in the ones of the page.
        var sameFrame = from.Frame == to.Frame;
        var (fromX, fromY, toX, toY) = sameFrame ? (from.LocalX, from.LocalY, to.LocalX, to.LocalY) : (from.X, from.Y, to.X, to.Y);
        var mouse = Mouse();
        mouse.Actions.Add(MoveTo(fromX, fromY));
        mouse.Actions.Add(new PointerDownAction(0));
        for (var step = 1; step <= 5; step++) mouse.Actions.Add(MoveTo(fromX + (toX - fromX) * step / 5, fromY + (toY - fromY) * step / 5));
        mouse.Actions.Add(new PointerUpAction(0));
        return PerformAsync(mouse, cancellationToken, sameFrame ? from.Frame.Key : null);
    }

    /// <inheritdoc />
    public Task KeyAsync(BrowserKey key, bool down, int modifiers, CancellationToken cancellationToken)
    {
        var value = SpecialKeys.TryGetValue(key.Key, out var special) ? special : key.Key;
        var keyboard = new KeySourceActions("keyboard");
        keyboard.Actions.Add(down ? new KeyDownAction(value) : new KeyUpAction(value));
        return PerformAsync(keyboard, cancellationToken);
    }

    /// <inheritdoc />
    public Task InsertTextAsync(string text, CancellationToken cancellationToken)
    {
        // WebDriver types a text as the keys of its characters; any character is a key value of its own.
        var keyboard = new KeySourceActions("keyboard");
        var characters = StringInfo.GetTextElementEnumerator(text);
        while (characters.MoveNext())
        {
            var character = (string)characters.Current;
            var value = character == "\n" ? SpecialKeys["Enter"] : character;
            keyboard.Actions.Add(new KeyDownAction(value));
            keyboard.Actions.Add(new KeyUpAction(value));
        }

        return PerformAsync(keyboard, cancellationToken);
    }

    // -------------------------------------------------------------------------------------------------------------
    // Capture, dialogs, network, emulation
    // -------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<byte[]> ScreenshotAsync(ScreenshotRequest request, CancellationToken cancellationToken)
    {
        var parameters = new CaptureScreenshotCommandParameters(Id)
        {
            Format = new ImageFormat { Type = $"image/{request.Format}", Quality = request.Format == "png" || request.Quality is null ? null : request.Quality / 100.0 },
            Origin = request.FullPage || request.Clip is not null ? ScreenshotOrigin.Document : ScreenshotOrigin.Viewport,
        };
        if (request.Clip is { } box) parameters.Clip = new BoxClipRectangle { X = box.X, Y = box.Y, Width = Math.Max(1, box.Width), Height = Math.Max(1, box.Height) };
        var shot = await Driver.BrowsingContext.CaptureScreenshotAsync(parameters).WaitAsync(cancellationToken).ConfigureAwait(false);
        return Convert.FromBase64String(shot.Data);
    }

    /// <inheritdoc />
    public async Task HandleDialogAsync(bool accept, string? promptText, CancellationToken cancellationToken)
    {
        if (Dialog is null) throw new BrowserToolException("no-dialog", "No open dialog found");
        await Driver.BrowsingContext.HandleUserPromptAsync(new HandleUserPromptCommandParameters(Id) { Accept = accept, UserText = promptText }).WaitAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _dialog, null);
        Changed();
    }

    private async Task HandleQuietlyAsync(bool accept, string? text)
    {
        try { await HandleDialogAsync(accept, text, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is WebDriverBiDiException or BrowserToolException) { /* answered meanwhile */ }
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
        if (_collector is null) return new NetworkBodies(null, null);
        async Task<string?> ReadAsync(DataType type)
        {
            try
            {
                var data = await Driver.Network.GetDataAsync(new GetDataCommandParameters(entry.RequestId, type) { CollectorId = _collector }).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (data.Bytes.Type == BytesValueType.String) return data.Bytes.Value;
                return type == DataType.Request || Cdp.CdpPage.IsTextual(entry.MimeType) ? Encoding.UTF8.GetString(Convert.FromBase64String(data.Bytes.Value)) : "<binary data>";
            }
            catch (Exception exception) when (exception is WebDriverBiDiException or FormatException)
            {
                return null;
            }
        }

        return new NetworkBodies(entry.HasPostData ? await ReadAsync(DataType.Request).ConfigureAwait(false) : null, entry.Finished && entry.Failure is null ? await ReadAsync(DataType.Response).ConfigureAwait(false) : null);
    }

    /// <inheritdoc />
    public async Task<(int Width, int Height)?> ResizeAsync(int width, int height, CancellationToken cancellationToken)
    {
        try
        {
            var tree = await Driver.BrowsingContext.GetTreeAsync(new GetTreeCommandParameters { RootBrowsingContextId = Id, MaxDepth = 0 }).WaitAsync(cancellationToken).ConfigureAwait(false);
            var windowId = tree.ContextTree.First().ClientWindowId;
            var windows = await Driver.Browser.GetClientWindowsAsync(new BrowserModule.GetClientWindowsCommandParameters()).WaitAsync(cancellationToken).ConfigureAwait(false);
            var window = windows.ClientWindows.First(item => item.ClientWindowId == windowId);
            var (innerWidth, innerHeight) = await ViewportAsync(cancellationToken).ConfigureAwait(false);
            await Driver.Browser.SetClientWindowStateAsync(new BrowserModule.SetClientWindowStateCommandParameters(windowId)
            {
                State = BrowserModule.ClientWindowState.Normal,
                Width = (ulong)Math.Max(1, width + (long)window.Width - innerWidth),
                Height = (ulong)Math.Max(1, height + (long)window.Height - innerHeight),
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            return await ViewportAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebDriverBiDiException or InvalidOperationException)
        {
            // A browser that cannot size its windows, as when it is headless: the viewport takes the size.
            await Driver.BrowsingContext.SetViewportAsync(new SetViewportCommandParameters { BrowsingContextId = Id, Viewport = new Viewport { Width = (ulong)width, Height = (ulong)height } }).WaitAsync(cancellationToken).ConfigureAwait(false);
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
        if (settings.CpuThrottlingRate > 1) throw new BrowserToolException("not-supported", "Firefox cannot slow down the CPU: only Chromium browsers can.");
        if (settings.NetworkConditions is { } network && network != "Offline") throw new BrowserToolException("not-supported", "Firefox cannot throttle the network: only Chromium browsers can. Offline is available.");
        var viewport = new SetViewportCommandParameters { BrowsingContextId = Id };
        if (settings.Viewport is { } size)
        {
            viewport.Viewport = new Viewport { Width = (ulong)size.Width, Height = (ulong)size.Height };
            viewport.DevicePixelRatio = size.DeviceScaleFactor;
        }

        await Driver.BrowsingContext.SetViewportAsync(viewport).WaitAsync(cancellationToken).ConfigureAwait(false);
        await TryAsync(() =>
        {
            var touch = new SetTouchOverrideCommandParameters { MaxTouchPoints = settings.Viewport is { Touch: true } ? 5 : null };
            touch.Contexts.Add(Id);
            return Driver.Emulation.SetTouchOverrideAsync(touch);
        }, settings.Viewport is { Touch: true } ? "touch" : null).ConfigureAwait(false);

        await TryAsync(() =>
        {
            var agent = new SetUserAgentOverrideCommandParameters { UserAgent = settings.UserAgent };
            agent.Contexts.Add(Id);
            return Driver.Emulation.SetUserAgentOverrideAsync(agent);
        }, settings.UserAgent is null ? null : "the user agent").ConfigureAwait(false);

        await TryAsync(() =>
        {
            var location = new SetGeolocationOverrideCoordinatesCommandParameters
            {
                Coordinates = settings.Geolocation is { } point ? new GeolocationCoordinates(point.Latitude, point.Longitude) { Accuracy = 1 } : null,
            };
            location.Contexts.Add(Id);
            return Driver.Emulation.SetGeolocationOverrideAsync(location);
        }, settings.Geolocation is null ? null : "the geolocation").ConfigureAwait(false);

        await TryAsync(() =>
        {
            var conditions = new SetNetworkConditionsCommandParameters { NetworkConditions = settings.NetworkConditions == "Offline" ? new NetworkConditionsOffline() : null };
            conditions.Contexts.Add(Id);
            return Driver.Emulation.SetNetworkConditionsAsync(conditions);
        }, settings.NetworkConditions is null ? null : "the network conditions").ConfigureAwait(false);

        var features = settings.ColorScheme is not null || settings.ForcedColors is not null || settings.PrefersContrast is not null || settings.PrefersReducedMotion is not null;
        await TryAsync(() =>
        {
            var media = new SetMediaFeaturesOverrideCommandParameters { Features = features ? MediaFeaturesOf(settings) : null };
            media.Contexts.Add(Id);
            return Driver.Emulation.SetMediaFeaturesOverrideAsync(media);
        }, features ? "the media features" : null).ConfigureAwait(false);

        if (settings.ExtraHeaders is { } headers)
        {
            var extra = new SetExtraHeadersCommandParameters();
            extra.Contexts.Add(Id);
            foreach (var (name, value) in headers) extra.Headers.Add(new Header(name, value));
            await TryAsync(() => Driver.Network.SetExtraHeadersAsync(extra), headers.Count == 0 ? null : "extra headers").ConfigureAwait(false);
        }
    }

    // Sets an emulation: one that Firefox does not have fails the call only when it was asked for, not when it is reset.
    private static async Task TryAsync(Func<Task> command, string? asked)
    {
        try
        {
            await command().ConfigureAwait(false);
        }
        catch (WebDriverBiDiException exception) when (asked is null)
        {
            _ = exception;
        }
        catch (WebDriverBiDiException exception)
        {
            throw new BrowserToolException("not-supported", $"Firefox cannot emulate {asked}: {exception.Message}", exception);
        }
    }

    private static MediaFeatures MediaFeaturesOf(EmulationSettings settings)
    {
        var features = new MediaFeatures();
        if (settings.ColorScheme is { } scheme) features.PrefersColorScheme = scheme == "dark" ? PrefersColorSchemeMediaFeatureValue.Dark : PrefersColorSchemeMediaFeatureValue.Light;
        if (settings.ForcedColors is { } forced) features.ForcedColors = forced == "active" ? ForcedColorsMediaFeatureValue.Active : ForcedColorsMediaFeatureValue.None;
        if (settings.PrefersReducedMotion is { } motion) features.PrefersReducedMotion = motion == "reduce" ? PrefersReducedMotionMediaFeatureValue.Reduce : PrefersReducedMotionMediaFeatureValue.NoPreference;
        if (settings.PrefersContrast is { } contrast)
        {
            features.PrefersContrast = contrast switch
            {
                "more" => PrefersContrastMediaFeatureValue.More,
                "less" => PrefersContrastMediaFeatureValue.Less,
                "custom" => PrefersContrastMediaFeatureValue.Custom,
                _ => PrefersContrastMediaFeatureValue.NoPreference,
            };
        }

        return features;
    }

    /// <inheritdoc />
    public Task BringToFrontAsync(CancellationToken cancellationToken)
        => Driver.BrowsingContext.ActivateAsync(new ActivateCommandParameters(Id)).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_intercept is { } intercept) await Driver.Network.RemoveInterceptAsync(new RemoveInterceptCommandParameters(intercept)).ConfigureAwait(false);
            if (_collector is { } collector) await Driver.Network.RemoveDataCollectorAsync(new RemoveDataCollectorCommandParameters(collector)).ConfigureAwait(false);
            await Driver.BrowsingContext.CloseAsync(new WebDriverBiDi.BrowsingContext.CloseCommandParameters(Id)).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (WebDriverBiDiException)
        {
            // Closed meanwhile.
        }

        MarkClosed();
    }
}
