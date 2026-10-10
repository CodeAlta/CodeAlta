using CodeAlta.Desktop.Rpc;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop;

/// <summary>
/// The pane a plugin operation runs for: the project and session of the composer where a command was
/// started, and its prompt draft at that moment.
/// </summary>
internal sealed class DesktopPluginScope
{
    /// <summary>Gets the project of the pane, or null for a global session.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the folder of the project, when it has one.</summary>
    public string? ProjectPath { get; init; }

    /// <summary>Gets the session of the pane, or null for a pane that has none yet.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the space the window showed when the operation started, or null when it has none.</summary>
    public string? SpaceId { get; init; }

    /// <summary>Gets a value indicating whether the session was running when the operation started.</summary>
    public bool SessionBusy { get; init; }

    /// <summary>Gets or sets the prompt draft of the pane.</summary>
    public string? DraftText { get; set; }
}

/// <summary>
/// What plugins ask of the window and how the window answers: notifications, dialogs, and prompts sent to
/// the session a command was started from.
/// </summary>
/// <remarks>
/// Plugins start with the host, before the window has a page. What is asked before a page watches is kept
/// and given to the page when it arrives; a dialog that is still open is given again to a page that
/// reloads. Nothing here touches the window: the page shows every request with its own components.
/// </remarks>
internal sealed class DesktopPluginUi : IPluginUiRuntimeService, IPluginSessionService, IPluginPromptService, IDisposable
{
    /// <summary>Largest number of notifications kept for a page that does not watch yet.</summary>
    internal const int MaximumBacklog = 32;

    internal const int MaximumTitleUnits = 200;
    internal const int MaximumMessageUnits = 8 * 1024;
    internal const int MaximumTextUnits = 256 * 1024;
    internal const int MaximumHtmlUnits = 256 * 1024;
    internal const int MaximumItems = 500;
    internal const int MaximumButtons = 6;

    /// <summary>How long a prompt sent by a plugin waits for the page to take it.</summary>
    internal static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(30);

    private readonly AsyncLocal<DesktopPluginScope?> _scope = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Queue<PluginUiEvent> _backlog = new();
    private Action<PluginUiEvent>? _watcher;
    private long _next;
    private bool _closed;

    /// <inheritdoc />
    public bool HasInteractiveUi => true;

    /// <summary>Gets or sets the server of the scripts of dialogs: set once, when the window has plugins.</summary>
    internal DesktopPluginModules? Modules { get; set; }

    /// <inheritdoc />
    public IPluginUiService ForPlugin(string pluginRuntimeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        return new PluginDialogs(this, pluginRuntimeKey);
    }

    /// <summary>Gets the pane of the plugin operation running on this flow, when there is one.</summary>
    internal DesktopPluginScope? Scope => _scope.Value;

    /// <summary>Makes <paramref name="scope"/> the pane of the plugin operation started on this flow.</summary>
    internal void Enter(DesktopPluginScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _scope.Value = scope;
    }

    /// <summary>
    /// Gives the page every request from now on, after the ones it missed and the dialogs still open.
    /// </summary>
    /// <returns>A registration to dispose when the page goes away.</returns>
    internal IDisposable Watch(Action<PluginUiEvent> watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        lock (_gate)
        {
            _watcher = watcher;
            while (_backlog.TryDequeue(out var missed)) watcher(missed);
            foreach (var pending in _pending.Values) watcher(pending.Event);
        }

        return new Registration(this, watcher);
    }

    /// <summary>Takes the page's answer to a dialog or a prompt.</summary>
    /// <returns>False when the request is unknown: answered already, cancelled, or from another start.</returns>
    internal bool Respond(PluginUiAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        Pending? pending;
        lock (_gate)
        {
            if (answer.RequestId is null || !_pending.Remove(answer.RequestId, out pending)) return false;
        }

        pending.Complete(answer);
        return true;
    }

    /// <summary>Runs the action handler of an open dialog.</summary>
    /// <returns>The status (<c>ok</c>, <c>unknown</c>, <c>unsupported</c> or <c>failed</c>), the new content when it changes, and whether the dialog closed.</returns>
    internal async ValueTask<(string Status, string? Html, bool Closed)> ActionAsync(string? requestId, string? action, string? value,
        IReadOnlyDictionary<string, string>? values, CancellationToken cancellationToken)
    {
        Pending? pending;
        lock (_gate)
        {
            if (requestId is null || !_pending.TryGetValue(requestId, out pending)) return ("unknown", null, false);
        }

        if (pending.OnAction is null || string.IsNullOrWhiteSpace(action)) return ("unsupported", null, false);
        var fields = values ?? new Dictionary<string, string>();
        PluginDialogActionResult result;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pending.Cancellation.Token);
            result = await pending.OnAction(new PluginDialogAction { Name = action, Value = value, Values = fields }, linked.Token).ConfigureAwait(false)
                ?? PluginDialogActionResult.KeepOpen;
        }
        catch (OperationCanceledException) { return ("unknown", null, false); }
        catch (Exception) { return ("failed", null, false); } // The plugin's own failure: its text stays out of the page.

        if (result.Close)
        {
            lock (_gate) _pending.Remove(pending.Event.RequestId!);
            pending.Complete(new PluginUiAnswer(pending.Event.RequestId, result.ButtonName, false, null, null, new Dictionary<string, string>(fields)));
            return ("ok", null, true);
        }

        return ("ok", result.Html is null ? null : Cut(result.Html, MaximumHtmlUnits), false);
    }

    /// <inheritdoc />
    public ValueTask NotifyAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        cancellationToken.ThrowIfCancellationRequested();
        Post(new PluginUiEvent("notify") { Message = Cut(message, MaximumMessageUnits) });
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Tells the page that what plugins show may have changed (a command or a dialog action of a plugin ended):
    /// it reads their status items and their content again. A page that is not there reads them when it arrives.
    /// </summary>
    internal void Refresh()
    {
        lock (_gate)
        {
            if (!_closed && _watcher is { } watcher) watcher(new PluginUiEvent("refresh"));
        }
    }

    /// <inheritdoc />
    public void InvalidateButtons()
    {
        // A page that is not there reads the buttons when it arrives.
        lock (_gate)
        {
            if (!_closed && _watcher is { } watcher) watcher(new PluginUiEvent("buttons"));
        }
    }

    /// <inheritdoc />
    public void InvalidateLandingCards()
    {
        // A landing page that is not there reads the cards when it is shown.
        lock (_gate)
        {
            if (!_closed && _watcher is { } watcher) watcher(new PluginUiEvent("landing"));
        }
    }

    /// <summary>Shows a notification with the tone of a problem.</summary>
    internal void NotifyProblem(string message)
        => Post(new PluginUiEvent("notify") { Message = Cut(message, MaximumMessageUnits), Tone = "warning" });

    /// <inheritdoc />
    public async ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(message);
        var answer = await AskAsync(new PluginUiEvent("ask")
        {
            // Without buttons the page shows its own Yes and No, in the language of the window.
            Dialog = "confirm", Title = Cut(title, MaximumTitleUnits), Message = Cut(message, MaximumMessageUnits),
        }, null, cancellationToken).ConfigureAwait(false);
        return answer is { Cancelled: false, Button: "yes" };
    }

    /// <inheritdoc />
    public async ValueTask<string?> InputAsync(string title, string? initialText = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var answer = await AskAsync(new PluginUiEvent("ask")
        {
            Dialog = "input", Title = Cut(title, MaximumTitleUnits), Text = Cut(initialText ?? string.Empty, MaximumTextUnits),
        }, null, cancellationToken).ConfigureAwait(false);
        return answer is { Cancelled: false } ? answer.Text ?? string.Empty : null;
    }

    /// <inheritdoc />
    public async ValueTask<string?> EditTextAsync(string title, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(text);
        var answer = await AskAsync(new PluginUiEvent("ask")
        {
            Dialog = "edit", Title = Cut(title, MaximumTitleUnits), Text = Cut(text, MaximumTextUnits),
        }, null, cancellationToken).ConfigureAwait(false);
        return answer is { Cancelled: false } ? answer.Text ?? string.Empty : null;
    }

    /// <inheritdoc />
    public async ValueTask<T?> SelectAsync<T>(string title, IReadOnlyList<PluginSelectItem<T>> items, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return default;
        var shown = items.Take(MaximumItems).ToArray();
        var answer = await AskAsync(new PluginUiEvent("ask")
        {
            Dialog = "select", Title = Cut(title, MaximumTitleUnits),
            Items = [.. shown.Select(static item => new PluginUiChoice(Cut(item.Label, MaximumTitleUnits),
                item.Description is null ? null : Cut(item.Description, MaximumTitleUnits), item.IsSelected))],
        }, null, cancellationToken).ConfigureAwait(false);
        return answer is { Cancelled: false, SelectedIndex: { } index } && index >= 0 && index < shown.Length ? shown[index].Value : default;
    }

    /// <inheritdoc />
    public async ValueTask ShowDialogAsync(PluginDialogRequest request, CancellationToken cancellationToken = default)
        => _ = await ShowDialogForResultAsync(request, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<PluginDialogResponse?> ShowDialogForResultAsync(PluginDialogRequest request, CancellationToken cancellationToken = default)
        => ShowDialogForResultAsync(request, null, cancellationToken);

    // The plugin that asks, when the host knows it: the module of the script of the dialog is served for that plugin.
    private async ValueTask<PluginDialogResponse?> ShowDialogForResultAsync(PluginDialogRequest request, string? pluginKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        string? script = null, scriptProblem = null;
        if (request.Kind == PluginDialogKind.Custom && request.Html is not null && request.Script is { HasEntry: true } wanted)
        {
            script = pluginKey is null ? null : Modules?.PublishFor(pluginKey, wanted);
            if (script is null) scriptProblem = "The script of the dialog could not be found.";
        }

        var answer = await AskAsync(new PluginUiEvent("ask")
        {
            Dialog = request.Kind switch
            {
                PluginDialogKind.Confirmation => "confirm",
                PluginDialogKind.Input => "input",
                PluginDialogKind.TextEditor => "edit",
                PluginDialogKind.Selection => "select",
                PluginDialogKind.Custom when request.Html is not null => "html",
                _ => "message",
            },
            Title = Cut(request.Title, MaximumTitleUnits),
            Message = request.Message is null ? null : Cut(request.Message, MaximumMessageUnits),
            Text = request.InitialText is null ? null : Cut(request.InitialText, MaximumTextUnits),
            Html = request.Kind == PluginDialogKind.Custom && request.Html is not null ? Cut(request.Html, MaximumHtmlUnits) : null,
            Items = request.Kind != PluginDialogKind.Selection ? null
                : [.. request.SelectionItems.Take(MaximumItems).Select(static label => new PluginUiChoice(Cut(label ?? string.Empty, MaximumTitleUnits), null, false))],
            Buttons = request.Buttons.Count == 0 ? null
                : [.. request.Buttons.Take(MaximumButtons).Select(static button => new PluginUiButton(
                    Cut(button.Name, MaximumTitleUnits), Cut(button.Label, MaximumTitleUnits), button.IsDefault, button.IsCancel))],
            Actions = request.OnAction is not null,
            Script = script, ScriptProblem = scriptProblem,
        }, request.OnAction, cancellationToken).ConfigureAwait(false);
        if (answer is null) return null;
        return new PluginDialogResponse
        {
            ButtonName = answer.Button, Cancelled = answer.Cancelled, Text = answer.Text, SelectedIndex = answer.SelectedIndex,
            Values = answer.Values ?? new Dictionary<string, string>(),
        };
    }

    /// <inheritdoc />
    public string? SelectedSessionId => _scope.Value?.SessionId;

    /// <inheritdoc />
    public bool IsSelectedSessionBusy => _scope.Value?.SessionBusy ?? false;

    /// <inheritdoc />
    public async ValueTask SendPromptAsync(string text, CancellationToken cancellationToken = default)
        => _ = await PromptAsync("send", text, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask EnqueuePromptAsync(string text, CancellationToken cancellationToken = default)
        => _ = await PromptAsync("enqueue", text, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<bool> TrySteerAsync(string text, CancellationToken cancellationToken = default)
        => PromptAsync("steer", text, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> RequestCompactionAsync(CancellationToken cancellationToken = default)
        => PromptAsync("compact", null, cancellationToken);

    /// <summary>
    /// Asks the page to send, queue or steer a prompt, or to compact, in the session of the operation's pane
    /// (the focused one when the operation has no pane). The page uses its own Send, so the prompt is
    /// shown, recorded and refused exactly like one the user typed.
    /// </summary>
    internal async ValueTask<bool> PromptAsync(string mode, string? text, CancellationToken cancellationToken)
    {
        if (mode != "compact") ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var scope = _scope.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PromptTimeout);
        try
        {
            var answer = await AskAsync(new PluginUiEvent("prompt")
            {
                Mode = mode, Text = text is null ? null : Cut(text, MaximumTextUnits), SessionId = scope?.SessionId, ProjectId = scope?.ProjectId,
            }, null, timeout.Token).ConfigureAwait(false);
            return answer is { Cancelled: false };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // The page did not take it in time.
        }
    }

    /// <inheritdoc />
    public string? DraftText => _scope.Value?.DraftText;

    /// <inheritdoc />
    public ValueTask SetDraftTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        var scope = _scope.Value;
        if (scope is not null) scope.DraftText = text;
        Post(new PluginUiEvent("draft") { Text = Cut(text, MaximumTextUnits), SessionId = scope?.SessionId, ProjectId = scope?.ProjectId });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask AddAttachmentAsync(PluginPromptAttachment attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PluginPromptAttachment>> GetAttachmentsAsync(CancellationToken cancellationToken = default)
        => new(Array.Empty<PluginPromptAttachment>());

    /// <summary>Ends every open dialog as cancelled and refuses what comes after.</summary>
    public void Dispose()
    {
        Pending[] pending;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            pending = [.. _pending.Values];
            _pending.Clear();
            _backlog.Clear();
            _watcher = null;
        }

        foreach (var entry in pending) entry.Complete(null);
    }

    private void Post(PluginUiEvent value)
    {
        lock (_gate)
        {
            if (_closed) return;
            if (_watcher is { } watcher) { watcher(value); return; }
            // A page that is not there yet gets the newest notifications when it arrives.
            if (_backlog.Count == MaximumBacklog) _backlog.Dequeue();
            _backlog.Enqueue(value);
        }
    }

    private async ValueTask<PluginUiAnswer?> AskAsync(PluginUiEvent value, PluginDialogActionHandler? onAction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Pending pending;
        lock (_gate)
        {
            if (_closed) return null;
            var id = "ui-" + Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
            pending = new Pending(value with { RequestId = id }, onAction);
            _pending.Add(id, pending);
            _watcher?.Invoke(pending.Event);
        }

        await using var registration = cancellationToken.Register(() =>
        {
            bool removed;
            lock (_gate) removed = _pending.Remove(pending.Event.RequestId!);
            if (!removed) return;
            // The page closes what it shows for a request that no longer waits.
            Post(new PluginUiEvent("close") { RequestId = pending.Event.RequestId });
            pending.Cancel(cancellationToken);
        }).ConfigureAwait(false);
        return await pending.Answer.ConfigureAwait(false);
    }

    private void Unwatch(Action<PluginUiEvent> watcher)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_watcher, watcher)) _watcher = null;
        }
    }

    // Cuts between characters, never inside a surrogate pair.
    internal static string Cut(string value, int maximum)
        => value.Length <= maximum ? value : value[..(char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum)];

    private sealed class Pending(PluginUiEvent value, PluginDialogActionHandler? onAction)
    {
        private readonly TaskCompletionSource<PluginUiAnswer?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PluginUiEvent Event { get; } = value;

        public PluginDialogActionHandler? OnAction { get; } = onAction;

        /// <summary>Cancelled when the request ends, so a running action handler stops.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        public Task<PluginUiAnswer?> Answer => _answer.Task;

        public void Complete(PluginUiAnswer? answer)
        {
            _answer.TrySetResult(answer);
            Cancellation.Cancel();
        }

        public void Cancel(CancellationToken cancellationToken)
        {
            _answer.TrySetCanceled(cancellationToken);
            Cancellation.Cancel();
        }
    }

    // The dialogs of one plugin: all else the window does for it is the window's, and a dialog with a script is served for this plugin.
    private sealed class PluginDialogs(DesktopPluginUi owner, string pluginKey) : IPluginUiService
    {
        public bool HasInteractiveUi => owner.HasInteractiveUi;

        public ValueTask NotifyAsync(string message, CancellationToken cancellationToken = default) => owner.NotifyAsync(message, cancellationToken);

        public ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) => owner.ConfirmAsync(title, message, cancellationToken);

        public ValueTask<string?> InputAsync(string title, string? initialText = null, CancellationToken cancellationToken = default) => owner.InputAsync(title, initialText, cancellationToken);

        public ValueTask<string?> EditTextAsync(string title, string text, CancellationToken cancellationToken = default) => owner.EditTextAsync(title, text, cancellationToken);

        public ValueTask<T?> SelectAsync<T>(string title, IReadOnlyList<PluginSelectItem<T>> items, CancellationToken cancellationToken = default)
            => owner.SelectAsync(title, items, cancellationToken);

        public async ValueTask ShowDialogAsync(PluginDialogRequest request, CancellationToken cancellationToken = default)
            => _ = await owner.ShowDialogForResultAsync(request, pluginKey, cancellationToken).ConfigureAwait(false);

        public ValueTask<PluginDialogResponse?> ShowDialogForResultAsync(PluginDialogRequest request, CancellationToken cancellationToken = default)
            => owner.ShowDialogForResultAsync(request, pluginKey, cancellationToken);

        public void InvalidateButtons() => owner.InvalidateButtons();

        public void InvalidateLandingCards() => owner.InvalidateLandingCards();
    }

    private sealed class Registration(DesktopPluginUi owner, Action<PluginUiEvent> watcher) : IDisposable
    {
        public void Dispose() => owner.Unwatch(watcher);
    }
}
