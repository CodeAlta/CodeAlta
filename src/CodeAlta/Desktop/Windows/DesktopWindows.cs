using NeoAstra;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Windows;

/// <summary>What a secondary window is made of, given once the main window's view and the RPC host exist.</summary>
/// <param name="Environment">The browser environment the views of the windows share.</param>
/// <param name="Chrome">The chromeless look of the windows and the desktop services (icon).</param>
/// <param name="Rpc">The RPC host the pages of every window are bound to.</param>
/// <param name="Appearance">The theme of the page as it was reported last.</param>
/// <param name="Zoom">The zoom of the views, 1 for 100 percent.</param>
/// <param name="ConfigureView">Gives a new view of a window what the main view has: its navigation, its permissions, its files.</param>
internal sealed record DesktopWindowServices(NeoEnvironment Environment, DesktopWindowChrome Chrome, NeoRpcHost Rpc,
    Func<DesktopAppearance> Appearance, Func<double> Zoom, Action<NeoWindow, global::NeoAstra.NeoAstra> ConfigureView);

/// <summary>A request that reaches the page of one window.</summary>
/// <param name="Kind">
/// <c>changed</c> (the windows, their names or their tabs changed: the page reads them again), <c>open</c> (open the tab
/// that <paramref name="Payload"/> describes, at the point <paramref name="X"/>, <paramref name="Y"/> when a drag dropped
/// it), <c>focus</c> (bring the tab <paramref name="Tab"/> to the front), <c>move</c> (move the tab to the window
/// <paramref name="Target"/>, <c>new</c> for a new one, and answer <paramref name="RequestId"/>), <c>close</c> (close the
/// tabs of the window, then the window), <c>drag-over</c> (a tab dragged from another window is over this one at
/// <paramref name="X"/>, <paramref name="Y"/>; without a point it left).
/// </param>
internal sealed record WindowsEvent(string Kind, string? Tab = null, string? Payload = null, string? RequestId = null, string? Target = null,
    int? X = null, int? Y = null, bool Refused = false);

/// <summary>A window as the page, Settings and <c>alta window</c> see it.</summary>
internal sealed record DesktopWindowInfo
{
    public required string Id { get; init; }
    public required bool Main { get; init; }
    public required string Name { get; init; }
    public string? Icon { get; init; }
    public string? Color { get; init; }
    public bool Saved { get; init; }
    public bool Open { get; init; }
    public string? SpaceId { get; init; }
    public string? DisplayId { get; init; }
    public string? DisplayName { get; init; }
    public bool DisplayPrimary { get; init; }
    public string? PinnedDisplayId { get; init; }
    public string? PinnedDisplayName { get; init; }
    public bool PinnedConnected { get; init; }
    public string State { get; init; } = "normal";
    public bool Focused { get; init; }
    public bool Visible { get; init; }
    public NeoRect? Bounds { get; init; }
    public bool? OnCurrentDesktop { get; init; }
    public string? VirtualDesktopId { get; init; }
    public IReadOnlyList<string> Tabs { get; init; } = [];
    public string? ActiveTab { get; init; }
}

/// <summary>What became of a move of a tab to another window.</summary>
/// <param name="Status"><c>ok</c>, <c>not_found</c> (no such window), <c>other_space</c> (the window shows another space), <c>unavailable</c> (no window can be opened now).</param>
/// <param name="WindowId">The window the tab went to, with <c>ok</c>.</param>
internal sealed record DesktopTabMove(string Status, string? WindowId);

/// <summary>
/// The windows of the application: the main one, the control center that has the activity rail, the projects and
/// Settings, and the secondary ones, chromeless as it is, that hold tabs moved or torn out of it. It knows which
/// window holds which tab, so that a tab is in one window at a time and a request (a file to open, a diff to show)
/// goes to the window that has it; it opens, places and closes the secondary windows, and keeps them in
/// <c>windows.json</c> for the next start.
/// </summary>
/// <remarks>
/// Every change of the windows and of the tabs they hold happens on the thread of the windows, which is the single
/// writer of what this keeps; the methods marshal to it.
/// </remarks>
internal sealed class DesktopWindows : IAsyncDisposable
{
    /// <summary>The id of the main window.</summary>
    internal const string MainId = "main";

    /// <summary>The label of the main window and of its view.</summary>
    internal const string MainLabel = "main";

    /// <summary>What the labels of the secondary windows and of their views start with.</summary>
    internal const string LabelPrefix = "window-";

    private static readonly TimeSpan MoveWait = TimeSpan.FromSeconds(10);

    private readonly NeoApplication _application;
    private readonly string _dataRoot;
    private readonly bool _developer;
    private readonly Lock _gate = new();
    private readonly DesktopWindowsFile _file;
    // The windows that were open at the last exit and open again at this start, as the file had them.
    private readonly DesktopWindowRecord[] _restoring;
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<DesktopTabMove>> _moves = new(StringComparer.Ordinal);
    private readonly List<Task> _disposals = [];
    private Task _writing = Task.CompletedTask;
    private DesktopWindowServices? _services;
    private string _focused = MainId;
    private bool _exiting;
    private bool _disposed;
    private string? _dragOver;

    /// <summary>Creates the windows of an application, with what <c>windows.json</c> of the data root kept of them.</summary>
    /// <param name="application">The application.</param>
    /// <param name="dataRoot">The desktop data root.</param>
    /// <param name="developer">Whether this is the developer instance, which its titles say.</param>
    internal DesktopWindows(NeoApplication application, string dataRoot, bool developer)
        : this(application, dataRoot, developer, DesktopWindowsFile.Load(dataRoot))
    {
    }

    internal DesktopWindows(NeoApplication application, string dataRoot, bool developer, DesktopWindowsFile file)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(file);
        (_application, _dataRoot, _developer, _file) = (application, dataRoot, developer, file);
        _restoring = [.. RestoredAtStart(_file).Select(static record => record.Clone())];
        // The tabs of the windows that come back are theirs before any page says what it holds.
        foreach (var record in _restoring)
        {
            foreach (var tab in record.Tabs) _owners.TryAdd(tab, record.Id);
        }

        // A window is open from the moment it opens again: one that does not, such as a window left with no tab,
        // is kept closed, and the exit does not write it as open once more.
        foreach (var record in _file.Windows) record.Open = false;
    }

    /// <summary>The windows of a file that open again at a start: those that were open at the exit, with tabs.</summary>
    internal static IEnumerable<DesktopWindowRecord> RestoredAtStart(DesktopWindowsFile file)
        => file.RestoreAtStartup ? file.Windows.Where(static record => record.Open && record.Tabs.Count > 0) : [];

    /// <summary>Raised on the thread of the windows when the windows, their names or the tabs they hold changed.</summary>
    internal event Action? Changed;

    /// <summary>Gets whether the secondary windows that were open come back at the next start.</summary>
    internal bool RestoreAtStartup { get { lock (_gate) return _file.RestoreAtStartup; } }

    /// <summary>Gets the display the main window goes to whenever it is connected; null for where it was.</summary>
    internal string? MainPinnedDisplayId { get { lock (_gate) return (Find(MainId)?.Record ?? _file.Main)?.PinnedDisplayId; } }

    /// <summary>The main window, once it exists.</summary>
    internal NeoWindow? MainWindow { get { lock (_gate) return Find(MainId)?.Window; } }

    /// <summary>The label of the view of the window that was focused last: <c>main</c> before any other was.</summary>
    internal string FocusedLabel { get { lock (_gate) return LabelOf(_focused); } }

    /// <summary>The window that was focused last, the main one when none is open any more.</summary>
    internal NeoWindow? FocusedWindow { get { lock (_gate) return (Find(_focused) ?? Find(MainId))?.Window; } }

    /// <summary>The label of the view of a window.</summary>
    internal static string LabelOf(string windowId) => windowId == MainId ? MainLabel : LabelPrefix + windowId;

    /// <summary>The id of the window of a view label; null for a label that is no window's.</summary>
    internal static string? IdOf(string? label)
        => label == MainLabel ? MainId
            : label is not null && label.StartsWith(LabelPrefix, StringComparison.Ordinal) && DesktopWindowRecord.IsValidId(label[LabelPrefix.Length..]) ? label[LabelPrefix.Length..] : null;

    /// <summary>The sessions whose tabs the secondary windows hold, which no page of the main window claims.</summary>
    internal IReadOnlyList<string> SecondarySessions()
    {
        lock (_gate)
        {
            return [.. _owners.Where(static owner => owner.Value != MainId && owner.Key.StartsWith("session:", StringComparison.Ordinal))
                .Select(static owner => owner.Key["session:".Length..]).Where(static id => id.Length > 0)];
        }
    }

    /// <summary>Registers the main window and its view; the page of the main window then has its window.</summary>
    internal void AttachMain(NeoWindow window, global::NeoAstra.NeoAstra view)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(view);
        Entry entry;
        lock (_gate)
        {
            if (Find(MainId) is not null) throw new InvalidOperationException("The main window is attached already.");
            var record = _file.Main?.Clone() ?? new DesktopWindowRecord { Id = MainId };
            record.Id = MainId;
            entry = new(MainId, MainLabel, record) { Window = window, View = view, Opened = true };
            _entries.Insert(0, entry);
        }

        Follow(entry);
    }

    /// <summary>Gives the windows what a secondary one is made of: from now on, windows can be opened.</summary>
    internal void Attach(DesktopWindowServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        lock (_gate) _services = services;
    }

    /// <summary>Opens the secondary windows that were open when the application last ended, where they were.</summary>
    internal async Task RestoreAsync()
    {
        DesktopWindowRecord[] records;
        lock (_gate)
        {
            if (_services is null || _exiting) records = [];
            else records = [.. _restoring.Select(static record => record.Clone())];
        }

        foreach (var record in records)
        {
            try { await OnWindowThreadAsync(() => OpenEntryAsync(record, at: null, activate: false, queued: [])).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Warn($"The window {record.Name ?? record.Id} could not be opened again: {exception.Message}");
            }
        }
    }

    /// <summary>The windows: the main one first, then the open secondary ones, then the saved ones that are closed.</summary>
    internal Task<IReadOnlyList<DesktopWindowInfo>> ListAsync() => OnWindowThreadAsync(() => Task.FromResult(List()));

    /// <summary>The displays there are now, the primary one first, in the units of the windows.</summary>
    internal Task<IReadOnlyList<NeoDisplay>> DisplaysAsync() => OnWindowThreadAsync(() => Task.FromResult(DesktopWindowState.Displays(_application)));

    /// <summary>The window that holds a tab; null when none does.</summary>
    internal string? OwnerOf(string tab)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tab);
        lock (_gate) return _owners.TryGetValue(tab, out var owner) && Find(owner) is { Window: not null } ? owner : null;
    }

    /// <summary>The window that holds the tab of a session; null when none does.</summary>
    internal string? WindowOfSession(string? sessionId)
        => string.IsNullOrWhiteSpace(sessionId) ? null : OwnerOf("session:" + sessionId.Trim());

    /// <summary>
    /// The window a request about a tab goes to, which is brought to the front: the window that holds the tab, else the
    /// window the request came from, else the main one.
    /// </summary>
    /// <param name="tab">The tab the request opens or shows; null for a request about no tab.</param>
    /// <param name="originLabel">The label of the window the request came from, when it is known.</param>
    /// <param name="callerSessionId">The session whose agent asked, when it was one: the window of its tab.</param>
    /// <returns>The label of the view of the window.</returns>
    internal string Route(string? tab, string? originLabel, string? callerSessionId)
    {
        string id;
        lock (_gate)
        {
            id = (tab is not null && _owners.TryGetValue(tab, out var owner) && Find(owner) is { Window: not null } ? owner : null)
                ?? (IdOf(originLabel) is { } origin && Find(origin) is { Window: not null } ? origin : null)
                ?? (callerSessionId is { Length: > 0 } && _owners.TryGetValue("session:" + callerSessionId.Trim(), out var session) && Find(session) is { Window: not null } ? session : null)
                ?? MainId;
        }

        if (id != MainId) Post(() => { if (Find(id)?.Window is { IsClosed: false } window) Activate(window); });
        return LabelOf(id);
    }

    /// <summary>What the page of a window starts with: its window, and what it keeps of its tabs.</summary>
    /// <param name="label">The label of the view of the page.</param>
    /// <returns>The window, and the state of its page; null for a label that is no window's.</returns>
    internal (DesktopWindowInfo Window, string? PageState)? Current(string label)
    {
        lock (_gate)
        {
            if (IdOf(label) is not { } id || Find(id) is not { } entry) return null;
            return (Info(entry, null), entry.IsMain ? null : entry.Record.PageState);
        }
    }

    /// <summary>
    /// The windows as they are kept, without asking the windows themselves where they are: what a page needs to know at
    /// once of the tabs the other windows hold, so that it does not restore them too.
    /// </summary>
    internal IReadOnlyList<DesktopWindowInfo> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<DesktopWindowInfo>();
            foreach (var entry in _entries.Where(static entry => entry.Window is not null)) list.Add(Info(entry, null));
            // The windows that come back hold their tabs before their pages exist.
            foreach (var record in _file.Windows.Where(record => Find(record.Id) is null && (record.Saved || record.Open)))
                list.Add(InfoOf(record) with { Open = record.Open && _file.RestoreAtStartup });
            return list;
        }
    }

    /// <summary>
    /// Lets the page of a window receive what is asked of it until the returned registration is disposed. The requests
    /// made before the page watched (the tab a new window opens with) come first.
    /// </summary>
    internal IDisposable? Watch(string label, Action<WindowsEvent> watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        WindowsEvent[] queued;
        Entry? entry;
        lock (_gate)
        {
            entry = IdOf(label) is { } id ? Find(id) : null;
            if (entry is null) return null;
            entry.Watchers.Add(watcher);
            queued = [.. entry.Queued];
            entry.Queued.Clear();
        }

        foreach (var value in queued) watcher(value);
        return new Registration(this, entry, watcher);
    }

    /// <summary>Records the tabs the page of a window holds, and what it keeps of them.</summary>
    /// <param name="label">The label of the view of the page.</param>
    /// <param name="tabs">The tabs, as <c>session:&lt;id&gt;</c>, <c>editor:&lt;project&gt;</c> and so on.</param>
    /// <param name="active">The tab in front; null for none.</param>
    /// <param name="pageState">What the page keeps of its tabs and panes, for a secondary window; null to leave it.</param>
    /// <returns>False for a label that is no window's.</returns>
    internal bool Report(string label, IReadOnlyList<string> tabs, string? active, string? pageState)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        bool changed, empty;
        Entry? entry;
        lock (_gate)
        {
            entry = IdOf(label) is { } id ? Find(id) : null;
            if (entry is null || _disposed) return false;
            var held = tabs.Where(static tab => tab is { Length: > 0 and <= 1024 }).Distinct(StringComparer.Ordinal).Take(DesktopWindowRecord.MaximumTabs).ToArray();
            changed = !held.SequenceEqual(entry.Record.Tabs, StringComparer.Ordinal) || active != entry.ActiveTab;
            foreach (var tab in entry.Record.Tabs) if (_owners.TryGetValue(tab, out var owner) && owner == entry.Id) _owners.Remove(tab);
            // A tab is in one window: the window that says it opened it last holds it.
            foreach (var tab in held) _owners[tab] = entry.Id;
            entry.Record.Tabs = [.. held];
            entry.ActiveTab = active;
            if (!entry.IsMain && pageState is { Length: <= DesktopWindowRecord.MaximumPageState }) entry.Record.PageState = pageState;
            if (held.Length > 0) entry.HadTabs = true;
            // A secondary window is never empty: the last tab that leaves it closes it.
            empty = !entry.IsMain && entry.HadTabs && held.Length == 0;
        }

        if (!entry.IsMain) Persist();
        if (changed) RaiseChanged();
        if (empty) Post(() => CloseNow(entry));
        return true;
    }

    /// <summary>
    /// Moves a tab of a window to another one, or to a new one: the window that receives it opens it with what the
    /// page of the window it leaves gave (<paramref name="payload"/>). The page that asked closes the tab once this
    /// answers <c>ok</c>.
    /// </summary>
    /// <param name="sourceLabel">The label of the view of the window the tab leaves.</param>
    /// <param name="tab">The tab.</param>
    /// <param name="payload">What the page that receives it opens, which only the pages read.</param>
    /// <param name="spaceBound">Whether the tab belongs to the space of its window, and goes only to a window of that space.</param>
    /// <param name="target">The window the tab goes to; null for a new one.</param>
    /// <param name="screenPoint">For a new window, where the top-left corner of the window goes; null for a default place.</param>
    /// <param name="dropPoint">For a window that receives a drop, where in it the tab was dropped.</param>
    internal Task<DesktopTabMove> MoveTabAsync(string sourceLabel, string tab, string payload, bool spaceBound, string? target, NeoPoint? screenPoint, (double X, double Y)? dropPoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tab);
        ArgumentNullException.ThrowIfNull(payload);
        return OnWindowThreadAsync(async () =>
        {
            Entry? source, destination;
            DesktopWindowRecord? closed = null;
            lock (_gate)
            {
                if (_disposed || _exiting) return new DesktopTabMove("unavailable", null);
                source = IdOf(sourceLabel) is { } from ? Find(from) : null;
                destination = target is null ? null : Find(target);
                // A saved window that is closed opens again for the tab.
                if (target is not null && destination is null) closed = _file.Windows.FirstOrDefault(record => record.Id == target && record.Saved)?.Clone();
                if (target is not null && destination is null && closed is null) return new DesktopTabMove("not_found", null);
                var space = source?.SpaceId;
                if (spaceBound && destination is not null && destination.Id != source?.Id && !SameSpace(destination.SpaceId, space)) return new DesktopTabMove("other_space", null);
                if (spaceBound && closed is not null && !SameSpace(closed.SpaceId, space)) return new DesktopTabMove("other_space", null);
                if ((destination is null || closed is not null) && _services is null) return new DesktopTabMove("unavailable", null);
            }

            var open = new WindowsEvent("open", tab, payload, X: Whole(dropPoint?.X), Y: Whole(dropPoint?.Y));
            if (destination is not null)
            {
                Deliver(destination, open);
                lock (_gate) _owners[tab] = destination.Id;
                if (destination.Window is { IsClosed: false } window) Activate(window);
                RaiseChanged();
                return new DesktopTabMove("ok", destination.Id);
            }

            var record = closed ?? new DesktopWindowRecord { Id = NewId(), SpaceId = source?.SpaceId };
            if (closed is null) record.SpaceId = source?.SpaceId;
            var entry = await OpenEntryAsync(record, screenPoint, activate: true, queued: [open]).ConfigureAwait(true);
            if (entry is null) return new DesktopTabMove("unavailable", null);
            lock (_gate) _owners[tab] = entry.Id;
            RaiseChanged();
            return new DesktopTabMove("ok", entry.Id);
        });
    }

    /// <summary>
    /// Asks the page of the window that holds a tab to move it (what <c>alta window tab move</c> does): only that page
    /// knows what the tab holds. Waits for the move, a few seconds at most.
    /// </summary>
    /// <param name="tab">The tab.</param>
    /// <param name="target">The window it goes to; null for a new one.</param>
    /// <returns>What became of the move; <c>not_found</c> when no window holds the tab, <c>timeout</c> when its page did not move it.</returns>
    internal async Task<DesktopTabMove> RequestMoveAsync(string tab, string? target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tab);
        var requestId = Guid.NewGuid().ToString("N");
        var done = new TaskCompletionSource<DesktopTabMove>(TaskCreationOptions.RunContinuationsAsynchronously);
        Entry? owner;
        lock (_gate)
        {
            owner = _owners.TryGetValue(tab, out var id) ? Find(id) : null;
            if (owner is null || owner.Window is null) return new("not_found", null);
            if (target is not null && target == owner.Id) return new("ok", owner.Id);
            if (target is not null && Find(target) is null && !_file.Windows.Any(record => record.Id == target && record.Saved)) return new("not_found", null);
            _moves[requestId] = done;
        }

        try
        {
            if (!Deliver(owner, new WindowsEvent("move", tab, RequestId: requestId, Target: target ?? "new"), queue: false)) return new("unavailable", null);
            return await done.Task.WaitAsync(MoveWait).ConfigureAwait(false);
        }
        catch (TimeoutException) { return new("timeout", null); }
        finally { lock (_gate) _moves.Remove(requestId); }
    }

    /// <summary>Gives the answer of a page to a move that was asked of it.</summary>
    internal void CompleteMove(string requestId, DesktopTabMove result)
    {
        TaskCompletionSource<DesktopTabMove>? done;
        lock (_gate) _moves.TryGetValue(requestId, out done);
        done?.TrySetResult(result);
    }

    /// <summary>Brings the window that holds a tab to the front, on that tab.</summary>
    /// <returns>The window, or null when no window holds the tab.</returns>
    internal string? FocusTab(string tab)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tab);
        Entry? owner;
        lock (_gate) owner = _owners.TryGetValue(tab, out var id) ? Find(id) : null;
        if (owner?.Window is null) return null;
        Deliver(owner, new WindowsEvent("focus", tab), queue: false);
        Post(() => { if (owner.Window is { IsClosed: false } window) Activate(window); });
        return owner.Id;
    }

    /// <summary>Opens a new secondary window, empty until a tab is moved into it, or a saved one that is closed.</summary>
    /// <param name="request">What the window is.</param>
    /// <returns>The window, or null when no window can be opened now.</returns>
    internal Task<DesktopWindowInfo?> OpenAsync(DesktopWindowOpening request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OnWindowThreadAsync(async () =>
        {
            DesktopWindowRecord record;
            lock (_gate)
            {
                if (_services is null || _exiting || _disposed) return null;
                if (request.SavedId is { } id)
                {
                    if (Find(id) is { Window: { IsClosed: false } running } open) { Activate(running); return Info(open, null); }
                    if (_file.Windows.FirstOrDefault(value => value.Id == id && value.Saved) is not { } saved) return null;
                    record = saved.Clone();
                }
                else
                {
                    record = new DesktopWindowRecord
                    {
                        Id = NewId(), Name = request.Name, Icon = request.Icon, Color = request.Color, SpaceId = request.SpaceId,
                        Saved = request.Name is not null, PinnedDisplayId = request.DisplayId,
                    }.Normalized();
                }
            }

            var entry = await OpenEntryAsync(record, request.ScreenPoint, activate: true, queued: [.. request.Tabs.Select(static payload => new WindowsEvent("open", null, payload))]).ConfigureAwait(true);
            if (entry is null) return null;
            if (request.DisplayId is { } display && entry.Window is { } window) MoveTo(window, display);
            lock (_gate) return Info(entry, null);
        });
    }

    /// <summary>Asks a secondary window to close: its page closes its tabs, asking about unsaved edits, then the window.</summary>
    /// <returns><c>ok</c>, <c>not_found</c>, or <c>main</c> for the main window, which this does not close.</returns>
    internal string RequestClose(string windowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        if (windowId == MainId) return "main";
        Entry? entry;
        lock (_gate) entry = Find(windowId);
        if (entry?.Window is null) return "not_found";
        if (!Deliver(entry, new WindowsEvent("close"), queue: false)) Post(() => CloseNow(entry));
        return "ok";
    }

    /// <summary>The page of a secondary window closed its tabs: the window closes.</summary>
    internal bool CloseFromPage(string label)
    {
        Entry? entry;
        lock (_gate) entry = IdOf(label) is { } id && id != MainId ? Find(id) : null;
        if (entry is null) return false;
        Post(() => CloseNow(entry));
        return true;
    }

    /// <summary>Brings a window to the front.</summary>
    internal bool Focus(string windowId)
    {
        Entry? entry;
        lock (_gate) entry = Find(windowId);
        if (entry?.Window is null) return false;
        Post(() => { if (entry.Window is { IsClosed: false } window) Activate(window); });
        return true;
    }

    /// <summary>Changes the name, the icon, the color, whether a window is saved, and the display it is pinned to.</summary>
    /// <returns>The window as it is now; null when there is no such window.</returns>
    internal Task<DesktopWindowInfo?> SetIdentityAsync(string windowId, DesktopWindowIdentityChange change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentNullException.ThrowIfNull(change);
        return OnWindowThreadAsync(() =>
        {
            Entry? entry;
            DesktopWindowRecord? record;
            lock (_gate)
            {
                entry = Find(windowId);
                record = entry?.Record ?? _file.Windows.FirstOrDefault(value => value.Id == windowId && value.Saved);
                if (record is null) return Task.FromResult<DesktopWindowInfo?>(null);
                if (change.Name is not null) record.Name = change.Name.Length == 0 ? null : change.Name;
                if (change.Icon is not null) record.Icon = change.Icon.Length == 0 ? null : change.Icon;
                if (change.Color is not null) record.Color = change.Color.Length == 0 ? null : change.Color;
                record.Normalized();
                // A window the user names is kept; one whose name is taken away is no longer, unless it is said so.
                if (change.Saved is { } saved) record.Saved = saved;
                else if (change.Name is not null && record.Id != MainId) record.Saved = record.Name is not null;
                if (change.PinnedDisplayId is not null)
                {
                    record.PinnedDisplayId = change.PinnedDisplayId.Length == 0 ? null : change.PinnedDisplayId;
                    record.PinnedDisplayName = record.PinnedDisplayId is null ? null
                        : DesktopWindowState.Displays(_application).FirstOrDefault(display => display.Id == record.PinnedDisplayId)?.Name ?? record.PinnedDisplayName;
                }
                if (entry is null) Upsert(record);
            }

            if (entry?.Window is { IsClosed: false } window)
            {
                window.Title = TitleOf(entry.Record);
                if (change.PinnedDisplayId is { Length: > 0 } pinned) MoveTo(window, pinned);
            }
            Persist();
            RaiseChanged();
            lock (_gate) return Task.FromResult<DesktopWindowInfo?>(entry is not null ? Info(entry, null) : InfoOf(record));
        });
    }

    /// <summary>Forgets a saved window that is closed, with its placement.</summary>
    /// <returns><c>ok</c>, <c>not_found</c>, <c>open</c> for a window that is open, or <c>main</c>.</returns>
    internal string Forget(string windowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        if (windowId == MainId) return "main";
        lock (_gate)
        {
            if (Find(windowId) is not null) return "open";
            if (_file.Windows.RemoveAll(record => record.Id == windowId) == 0) return "not_found";
        }

        DeletePlacement(windowId);
        Persist();
        RaiseChanged();
        return "ok";
    }

    /// <summary>Changes whether the secondary windows that were open come back at the next start.</summary>
    internal void SetRestoreAtStartup(bool value)
    {
        lock (_gate)
        {
            if (_file.RestoreAtStartup == value) return;
            _file.RestoreAtStartup = value;
        }

        Persist();
        RaiseChanged();
    }

    /// <summary>Places a window: on a display, in a state, at bounds in the work area of its display, on the current virtual desktop.</summary>
    /// <returns><c>ok</c>, <c>not_found</c>, <c>display_not_found</c>, <c>not_supported</c> (the platform cannot place windows) or <c>desktop_failed</c>.</returns>
    internal Task<string> PlaceAsync(string windowId, DesktopWindowPlacing placing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentNullException.ThrowIfNull(placing);
        return OnWindowThreadAsync(() =>
        {
            Entry? entry;
            lock (_gate) entry = Find(windowId);
            if (entry?.Window is not { IsClosed: false } window) return Task.FromResult("not_found");
            var displays = DesktopWindowState.Displays(_application);
            NeoDisplay? display = null;
            if (placing.DisplayId is { } id && (display = displays.FirstOrDefault(value => value.Id == id)) is null) return Task.FromResult("display_not_found");
            try
            {
                if (display is not null || placing.Bounds is not null)
                {
                    if (window.State != NeoWindowState.Normal) window.State = NeoWindowState.Normal;
                    var target = display ?? window.Display ?? displays.FirstOrDefault(static value => value.IsPrimary);
                    if (target is null) return Task.FromResult("not_supported");
                    if (placing.Bounds is { } bounds) window.MoveToDisplay(target, Fit(bounds, target.WorkArea));
                    else window.MoveToDisplay(target);
                }
                if (placing.State is { } state) window.State = state;
                if (placing.CurrentDesktop && window.IsOnCurrentVirtualDesktop == false && MainWindowOf()?.VirtualDesktopId is { } desktop
                    && window.MoveToVirtualDesktop(desktop) != NeoVirtualDesktopMoveResult.Moved) return Task.FromResult("desktop_failed");
                // A window placed comes to the front, unless it was asked to be minimized: activating it would restore it.
                if (placing.State is not NeoWindowState.Minimized) Activate(window);
            }
            catch (NotSupportedException) { return Task.FromResult("not_supported"); }
            return Task.FromResult("ok");
        });
    }

    /// <summary>The space each window shows changed: a secondary window keeps the one it was created in.</summary>
    internal void SetShownSpace(string label, string spaceId)
    {
        lock (_gate)
        {
            if (IdOf(label) is not { } id || Find(id) is not { } entry) return;
            entry.ShownSpace = spaceId;
            if (!entry.IsMain && entry.Record.SpaceId is null) entry.Record.SpaceId = spaceId;
        }
    }

    /// <summary>The space a window shows; null when its page has not said it yet.</summary>
    internal string? ShownSpaceOf(string label)
    {
        lock (_gate) return IdOf(label) is { } id && Find(id) is { } entry ? entry.SpaceId : null;
    }

    /// <summary>Applies a zoom to the views of every window.</summary>
    internal void ApplyZoom(double factor) => Post(() =>
    {
        foreach (var view in Views()) view.ZoomFactor = factor;
    });

    /// <summary>Gives every window the title bar and the background of the page's theme.</summary>
    internal void ApplyAppearance(DesktopAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        Post(() =>
        {
            foreach (var window in OpenWindows())
            {
                window.TitleBar = DesktopWindowChrome.TitleBar(appearance);
                window.BackgroundColor = appearance.Background;
            }
        });
    }

    /// <summary>Shows the secondary windows again, or hides them with the main one.</summary>
    internal void SetSecondaryVisible(bool visible) => Post(() =>
    {
        Entry[] entries;
        lock (_gate) entries = [.. _entries.Where(static entry => !entry.IsMain && entry.Window is { IsClosed: false })];
        foreach (var entry in entries)
        {
            if (visible)
            {
                entry.Window!.Show();
                if (entry.Window.State == NeoWindowState.Minimized) entry.Window.Restore();
            }
            else entry.Window!.Hide();
        }
    });

    /// <summary>
    /// Keeps every open window for the next start, then closes the secondary ones without asking their pages: the
    /// application exits. Call it once, when the host has closed.
    /// </summary>
    internal async Task SaveAndCloseForExitAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            _exiting = true;
            foreach (var entry in _entries.Where(static entry => !entry.IsMain && entry.Window is not null))
            {
                entry.Record.Open = true;
                Upsert(entry.Record);
            }
            entries = [.. _entries.Where(static entry => !entry.IsMain)];
        }

        Persist();
        try { await _writing.ConfigureAwait(false); } catch (Exception) { /* Logged where it failed. */ }
        await OnWindowThreadAsync(() =>
        {
            foreach (var entry in entries) CloseNow(entry);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        Task[] disposals;
        lock (_gate) disposals = [.. _disposals];
        try { await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception) { /* A view that does not end in time is left to the shutdown of the environment. */ }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { await _writing.ConfigureAwait(false); } catch (Exception) { /* Logged where it failed. */ }
    }

    // ---- The secondary windows ----

    private async Task<Entry?> OpenEntryAsync(DesktopWindowRecord record, NeoPoint? at, bool activate, IReadOnlyList<WindowsEvent> queued)
    {
        DesktopWindowServices services;
        Entry entry;
        lock (_gate)
        {
            if (_services is null || _exiting || _disposed) return null;
            services = _services;
            if (string.IsNullOrWhiteSpace(record.Name) && !record.Saved) record.Name = null;
            record.Open = true;
            entry = new(record.Id, LabelOf(record.Id), record);
            entry.Queued.AddRange(queued);
            _entries.Add(entry);
            Upsert(record);
        }

        try
        {
            var appearance = services.Appearance();
            var options = DesktopWindowChrome.WindowOptions(_developer, appearance);
            options.Label = entry.Label;
            options.Title = TitleOf(record);
            var window = _application.CreateWindow(options);
            entry.Window = window;
            window.CloseRequested += request =>
            {
                OnCloseRequested(entry, request);
                return ValueTask.CompletedTask; // Never await inside the native deadline.
            };
            window.Closed += (_, _) => OnClosed(entry);
            await services.Chrome.ApplyWindowIconAsync(window).ConfigureAwait(true);
            var view = await services.Environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), DesktopWindowChrome.ViewOptions(entry.Label)).ConfigureAwait(true);
            entry.View = view;
            services.ConfigureView(window, view);
            view.ZoomFactor = services.Zoom();
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => shown.TrySetResult();
            entry.State = await DesktopWindowState.StartAsync(_application, window, _dataRoot, DesktopWindowState.KeyOf(record.Id), record.PinnedDisplayId, null).ConfigureAwait(true);
            if (at is { } point) PlaceAt(window, point);
            else if (entry.State is null || !File.Exists(Path.Combine(_dataRoot, DesktopWindowState.KeyOf(record.Id) + ".json"))) Cascade(window);
            Follow(entry);
            var cloaked = DesktopWindowReveal.ShowCloaked(window);
            entry.Binding = NeoRpcViewBinding.Bind(services.Rpc, view);
            await view.NavigateAsync(DesktopApplication.ApplicationDocument).ConfigureAwait(true);
            await Task.WhenAny(shown.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(true);
            if (cloaked)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(80)).ConfigureAwait(true);
                DesktopWindowReveal.Reveal(window);
            }
            // The virtual desktop it was on, while that desktop is there; the current one otherwise.
            if (record.VirtualDesktopId is { } desktop && window.VirtualDesktopId != desktop) _ = window.MoveToVirtualDesktop(desktop);
            entry.Opened = true;
            if (activate) Activate(window);
            RaiseChanged();
            return entry;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn($"A window could not be opened: {exception.Message}");
            CloseNow(entry);
            return null;
        }
    }

    private void OnCloseRequested(Entry entry, NeoWindowCloseRequest request)
    {
        bool allowed;
        lock (_gate) allowed = entry.CloseAllowed || _exiting || _disposed;
        if (allowed || !request.CanCancel || request.Reason is NeoWindowCloseReason.SessionEnd or NeoWindowCloseReason.System or NeoWindowCloseReason.Owner) return;
        request.Cancel();
        // The page closes its tabs, with its questions about unsaved edits, and then its window; without a page, it closes at once.
        if (!Deliver(entry, new WindowsEvent("close"), queue: false)) Post(() => CloseNow(entry));
    }

    private void CloseNow(Entry entry)
    {
        lock (_gate) entry.CloseAllowed = true;
        if (entry.Window is { IsClosed: false } window) window.Close();
        else OnClosed(entry);
    }

    private void OnClosed(Entry entry)
    {
        bool forget;
        lock (_gate)
        {
            if (entry.Closed) return;
            entry.Closed = true;
            _entries.Remove(entry);
            foreach (var tab in entry.Record.Tabs) if (_owners.TryGetValue(tab, out var owner) && owner == entry.Id) _owners.Remove(tab);
            if (_focused == entry.Id) _focused = MainId;
            try { entry.Record.VirtualDesktopId = entry.Window?.VirtualDesktopId ?? entry.Record.VirtualDesktopId; }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or NeoAstraException) { /* Closed already. */ }
            // A window that is closed while the application runs is kept only when it is saved; at the exit every open one is kept.
            forget = !_exiting && !entry.Record.Saved;
            if (!_exiting)
            {
                entry.Record.Open = false;
                if (forget) _file.Windows.RemoveAll(record => record.Id == entry.Id);
                else Upsert(entry.Record);
            }
            foreach (var watcher in entry.Watchers.ToArray()) entry.Watchers.Remove(watcher);
            _disposals.Add(DisposeEntryAsync(entry, forget));
        }

        Persist();
        RaiseChanged();
    }

    private async Task DisposeEntryAsync(Entry entry, bool forget)
    {
        // The placement is written, then the page and its view end, then the window.
        try { if (entry.State is not null) await entry.State.DisposeAsync().ConfigureAwait(true); } catch (Exception) { }
        try { if (entry.Binding is not null) await entry.Binding.DisposeAsync().ConfigureAwait(true); } catch (Exception) { }
        try { if (entry.View is not null) await entry.View.DisposeAsync().ConfigureAwait(true); } catch (Exception) { }
        try { if (entry.Window is not null) await entry.Window.DisposeAsync().ConfigureAwait(true); } catch (Exception) { }
        if (forget) DeletePlacement(entry.Id);
    }

    private void DeletePlacement(string windowId)
    {
        try { File.Delete(Path.Combine(_dataRoot, DesktopWindowState.KeyOf(windowId) + ".json")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* An orphan placement is harmless. */ }
    }

    // A new window that was never placed is put a little below and to the right of the window in front, so that it does not hide it exactly.
    private void Cascade(NeoWindow window)
    {
        try
        {
            if (FocusedWindow is not { IsClosed: false } front || ReferenceEquals(front, window)) return;
            var bounds = front.State == NeoWindowState.Normal ? front.Bounds : window.Bounds;
            PlaceAt(window, new(bounds.X + 48, bounds.Y + 48), new NeoSize(Math.Max(640, bounds.Width * 3 / 4), Math.Max(480, bounds.Height * 3 / 4)));
        }
        catch (Exception exception) when (exception is InvalidOperationException or NeoAstraException or NotSupportedException) { /* The default place stays. */ }
    }

    // Puts the top-left corner of a window at a point of the screen, inside the work area of the display there.
    private void PlaceAt(NeoWindow window, NeoPoint point, NeoSize? size = null)
    {
        try
        {
            var displays = DesktopWindowState.Displays(_application);
            if (displays.Count == 0) return;
            var display = _application.GetDisplayAt(point) ?? displays.FirstOrDefault(static value => value.IsPrimary) ?? displays[0];
            var current = window.Bounds;
            var wanted = size ?? (FocusedWindow is { IsClosed: false } front && !ReferenceEquals(front, window) && front.State == NeoWindowState.Normal ? front.ClientSize : current.Size);
            var bounds = Fit(new NeoRect(point.X - display.WorkArea.X, point.Y - display.WorkArea.Y, wanted.Width, wanted.Height), display.WorkArea);
            window.MoveToDisplay(display, bounds);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NeoAstraException or NotSupportedException) { /* The window stays where it is. */ }
    }

    // Bounds relative to a work area, moved and shrunk to fit in it.
    private static NeoRect Fit(NeoRect bounds, NeoRect area)
    {
        var width = Math.Clamp(bounds.Width, Math.Min(320, area.Width), area.Width);
        var height = Math.Clamp(bounds.Height, Math.Min(240, area.Height), area.Height);
        return new(Math.Clamp(bounds.X, 0, Math.Max(0, area.Width - width)), Math.Clamp(bounds.Y, 0, Math.Max(0, area.Height - height)), width, height);
    }

    private void MoveTo(NeoWindow window, string displayId)
    {
        try
        {
            if (DesktopWindowState.Displays(_application).FirstOrDefault(display => display.Id == displayId) is not { } display) return;
            if (window.Display?.Id == display.Id) return;
            if (window.State != NeoWindowState.Normal) window.State = NeoWindowState.Normal;
            window.MoveToDisplay(display);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NeoAstraException or NotSupportedException) { /* The window stays where it is. */ }
    }

    /// <summary>
    /// The displays changed: a window on a display that is gone goes to the primary one, and a window pinned to a display
    /// that came back goes to it.
    /// </summary>
    internal void OnDisplaysChanged(IReadOnlyList<NeoDisplay> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count == 0) return;
        Post(() =>
        {
            Entry[] entries;
            lock (_gate) entries = [.. _entries.Where(static entry => entry.Window is { IsClosed: false })];
            var primary = displays.FirstOrDefault(static display => display.IsPrimary) ?? displays[0];
            foreach (var entry in entries)
            {
                var window = entry.Window!;
                try
                {
                    if (entry.Record.PinnedDisplayId is { } pinned && displays.FirstOrDefault(display => display.Id == pinned) is { } back)
                    {
                        if (window.Display?.Id != back.Id) MoveTo(window, back.Id);
                        continue;
                    }
                    var frame = window.FrameBounds;
                    if (!displays.Any(display => Overlaps(frame, display.WorkArea)))
                    {
                        if (window.State != NeoWindowState.Normal) window.State = NeoWindowState.Normal;
                        window.MoveToDisplay(primary);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or NeoAstraException or NotSupportedException) { /* The OS keeps it where it put it. */ }
            }
            RaiseChanged();
        });
    }

    private static bool Overlaps(NeoRect left, NeoRect right)
        => Math.Min((long)left.X + left.Width, (long)right.X + right.Width) - Math.Max(left.X, right.X) > 32
            && Math.Min((long)left.Y + left.Height, (long)right.Y + right.Height) - Math.Max(left.Y, right.Y) > 16;

    // ---- Dragging a tab between windows ----

    /// <summary>
    /// A tab dragged in a window is at a point of its page: the window under that point, when it is another one of
    /// the application, is told so and draws where the tab would go.
    /// </summary>
    /// <param name="sourceLabel">The label of the view of the window the tab is dragged from.</param>
    /// <param name="tab">The tab.</param>
    /// <param name="spaceBound">Whether the tab goes only to a window of the same space.</param>
    /// <param name="x">The point, in the CSS pixels of the page of the source window; null when the drag ended.</param>
    /// <param name="y">See <paramref name="x"/>.</param>
    /// <returns>The window under the point, when it is another one; null over the source window or over no window.</returns>
    internal Task<string?> DragOverAsync(string sourceLabel, string tab, bool spaceBound, double? x, double? y)
        => OnWindowThreadAsync(() =>
        {
            var (target, local) = x is { } px && y is { } py ? HitTest(sourceLabel, px, py) : (null, null);
            string? previous;
            lock (_gate) { previous = _dragOver; _dragOver = target?.Id; }
            if (previous is not null && previous != target?.Id && Find(previous) is { } left) Deliver(left, new WindowsEvent("drag-over", tab), queue: false);
            if (target is null) return Task.FromResult<string?>(null);
            bool refused;
            lock (_gate) refused = spaceBound && !SameSpace(target.SpaceId, (IdOf(sourceLabel) is { } from ? Find(from) : null)?.SpaceId);
            Deliver(target, new WindowsEvent("drag-over", tab, X: Whole(local!.Value.X), Y: Whole(local.Value.Y), Refused: refused), queue: false);
            return Task.FromResult<string?>(target.Id);
        });

    /// <summary>Where a tab dragged out of a window was released: over another window of the application, or over none.</summary>
    /// <param name="sourceLabel">The label of the view of the window the tab is dragged from.</param>
    /// <param name="x">The point, in the CSS pixels of the page of the source window.</param>
    /// <param name="y">See <paramref name="x"/>.</param>
    /// <returns>
    /// The window under the point and the point in its page (<c>Over</c> is that window); the source window
    /// (<c>Over</c> is its id); or no window, with the point of the screen where a new window goes.
    /// </returns>
    internal Task<(string? Over, (double X, double Y)? Local, NeoPoint? Screen)> DragReleaseAsync(string sourceLabel, double x, double y)
        => OnWindowThreadAsync(() =>
        {
            string? previous;
            lock (_gate) { previous = _dragOver; _dragOver = null; }
            if (previous is not null && Find(previous) is { } left) Deliver(left, new WindowsEvent("drag-over"), queue: false);
            var source = IdOf(sourceLabel) is { } id ? Find(id) : null;
            if (source?.View is null) return Task.FromResult<(string?, (double, double)?, NeoPoint?)>((null, null, null));
            NeoPoint? screen;
            try { screen = source.View.ClientToScreen(new NeoViewPoint(x, y)); }
            catch (Exception exception) when (exception is InvalidOperationException or NeoAstraException or NotSupportedException) { screen = null; }
            if (screen is not { } point) return Task.FromResult<(string?, (double, double)?, NeoPoint?)>((source.Id, null, null));
            var (target, local) = HitTest(sourceLabel, x, y);
            if (target is not null) return Task.FromResult<(string?, (double, double)?, NeoPoint?)>((target.Id, local, point));
            var over = _application.GetWindowAt(point);
            // Over the source window itself, or over a window of the application that is not one of these: the page handles it.
            if (over is not null) return Task.FromResult<(string?, (double, double)?, NeoPoint?)>((source.Id, null, point));
            return Task.FromResult<(string?, (double, double)?, NeoPoint?)>((null, null, point));
        });

    // The other window of the application under a point of the page of a window, and the point in its page.
    private (Entry? Target, (double X, double Y)? Local) HitTest(string sourceLabel, double x, double y)
    {
        Entry? source;
        lock (_gate) source = IdOf(sourceLabel) is { } id ? Find(id) : null;
        if (source?.View is null || source.Window is null) return (null, null);
        try
        {
            if (source.View.ClientToScreen(new NeoViewPoint(x, y)) is not { } point) return (null, null);
            var over = _application.GetWindowAt(point);
            if (over is null || ReferenceEquals(over, source.Window)) return (null, null);
            Entry? target;
            lock (_gate) target = _entries.FirstOrDefault(entry => ReferenceEquals(entry.Window, over));
            if (target?.View is null || target.View.ScreenToClient(point) is not { } local) return (null, null);
            return (target, (local.X, local.Y));
        }
        catch (Exception exception) when (exception is InvalidOperationException or NeoAstraException or NotSupportedException) { return (null, null); }
    }

    // ---- Shared ----

    private void Follow(Entry entry)
    {
        if (entry.Window is not { } window) return;
        window.FocusChanged += (_, _) =>
        {
            if (!window.IsFocused || window.IsClosed) return;
            bool changed;
            lock (_gate) { changed = _focused != entry.Id; _focused = entry.Id; }
            if (changed) RaiseChanged();
        };
    }

    private bool Deliver(Entry entry, WindowsEvent value, bool queue = true)
    {
        Action<WindowsEvent>[] watchers;
        lock (_gate)
        {
            watchers = [.. entry.Watchers];
            if (watchers.Length == 0)
            {
                if (queue && entry.Queued.Count < 64) entry.Queued.Add(value);
                return queue;
            }
        }

        foreach (var watcher in watchers) watcher(value);
        return true;
    }

    private void RaiseChanged()
    {
        Entry[] entries;
        lock (_gate) entries = [.. _entries];
        foreach (var entry in entries) Deliver(entry, new WindowsEvent("changed"), queue: false);
        try { Changed?.Invoke(); }
        catch (Exception exception) { Warn($"A watcher of the windows failed: {exception.Message}"); }
    }

    /// <summary>Whether the main window has something of its own to keep: a name, an icon, a color or a pinned display.</summary>
    internal static bool KeepsMain(DesktopWindowRecord record)
        => record is not { Name: null, Icon: null, Color: null, PinnedDisplayId: null };

    private void Persist()
    {
        DesktopWindowsFile snapshot;
        lock (_gate)
        {
            foreach (var entry in _entries.Where(static entry => !entry.IsMain && entry.Window is not null)) Upsert(entry.Record);
            if (Find(MainId) is { } main) _file.Main = KeepsMain(main.Record) ? main.Record.Clone() : null;
            snapshot = new DesktopWindowsFile
            {
                Version = _file.Version, RestoreAtStartup = _file.RestoreAtStartup, Main = _file.Main?.Clone(),
                Windows = [.. _file.Windows.Select(static record => record.Clone())],
            };
            // While the application runs a window that is open is open; at the next start that is what comes back.
            if (!_exiting) foreach (var record in snapshot.Windows) record.Open = _entries.Any(entry => entry.Id == record.Id && entry.Window is not null);
            _writing = _writing.ContinueWith(_ => snapshot.Save(_dataRoot), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    // Keeps a record among the windows of the file, in the place it had.
    private void Upsert(DesktopWindowRecord record)
    {
        if (record.Id == MainId) return;
        var at = _file.Windows.FindIndex(value => value.Id == record.Id);
        if (at >= 0) _file.Windows[at] = record;
        else if (_file.Windows.Count < DesktopWindowsFile.MaximumWindows) _file.Windows.Add(record);
    }

    private IReadOnlyList<DesktopWindowInfo> List()
    {
        lock (_gate)
        {
            var displays = DesktopWindowState.Displays(_application);
            var list = new List<DesktopWindowInfo>();
            foreach (var entry in _entries.Where(static entry => entry.Window is not null)) list.Add(Info(entry, displays));
            foreach (var record in _file.Windows.Where(record => record.Saved && Find(record.Id) is null)) list.Add(InfoOf(record, displays));
            return list;
        }
    }

    private DesktopWindowInfo Info(Entry entry, IReadOnlyList<NeoDisplay>? displays)
    {
        var info = InfoOf(entry.Record, displays) with { Open = entry.Window is { IsClosed: false }, Focused = _focused == entry.Id, ActiveTab = entry.ActiveTab,
            SpaceId = entry.SpaceId, Tabs = [.. entry.Record.Tabs] };
        if (entry.Window is not { IsClosed: false } window || !_application.Dispatcher.CheckAccess()) return info;
        try
        {
            var display = window.Display;
            var state = window.State;
            return info with
            {
                DisplayId = display?.Id, DisplayName = display?.Name, DisplayPrimary = display?.IsPrimary == true, Visible = window.IsVisible,
                // A minimized window has no place on a monitor: the system parks it out of sight, which is not said.
                State = state.ToString().ToLowerInvariant(), Bounds = state == NeoWindowState.Minimized ? null : window.Bounds, OnCurrentDesktop = window.IsOnCurrentVirtualDesktop,
                VirtualDesktopId = window.VirtualDesktopId,
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or NeoAstraException) { return info; }
    }

    private DesktopWindowInfo InfoOf(DesktopWindowRecord record, IReadOnlyList<NeoDisplay>? displays = null) => new()
    {
        Id = record.Id, Main = record.Id == MainId, Name = NameOf(record), Icon = record.Icon, Color = record.Color, Saved = record.Saved || record.Id == MainId,
        SpaceId = record.SpaceId, PinnedDisplayId = record.PinnedDisplayId, PinnedDisplayName = record.PinnedDisplayName,
        PinnedConnected = record.PinnedDisplayId is { } pinned && (displays ?? []).Any(display => display.Id == pinned),
        VirtualDesktopId = record.VirtualDesktopId, Tabs = [.. record.Tabs], Open = false,
    };

    private string NameOf(DesktopWindowRecord record)
    {
        if (record.Name is { } name) return name;
        if (record.Id == MainId) return "Main";
        // An unnamed window is numbered after the main one, among the unnamed ones, in the order they were opened.
        var unnamed = _file.Windows.Where(static value => value.Name is null).ToList();
        var index = unnamed.FindIndex(value => value.Id == record.Id);
        return $"Window {(index < 0 ? unnamed.Count : index) + 2}";
    }

    private string TitleOf(DesktopWindowRecord record)
    {
        var application = _developer ? "CodeAlta (dev)" : "CodeAlta";
        string name;
        lock (_gate) name = NameOf(record);
        return record.Id == MainId ? application : $"{name} — {application}";
    }

    // Logging never stops the windows, also where it is not set up (tests).
    private static void Warn(string message)
    {
        try { LogManager.GetLogger("CodeAlta.Desktop").Warn(message); }
        catch (InvalidOperationException) { /* No log to write to. */ }
    }

    private static int? Whole(double? value) => value is { } number && double.IsFinite(number) ? (int)Math.Clamp(Math.Round(number), int.MinValue, int.MaxValue) : null;

    private static bool SameSpace(string? left, string? right)
        => string.Equals(left ?? CodeAlta.Catalog.SpaceDescriptor.DefaultId, right ?? CodeAlta.Catalog.SpaceDescriptor.DefaultId, StringComparison.Ordinal);

    private string NewId()
    {
        string id;
        do id = Guid.NewGuid().ToString("N")[..8];
        while (Find(id) is not null || _file.Windows.Any(record => record.Id == id));
        return id;
    }

    private Entry? Find(string id) => _entries.FirstOrDefault(entry => entry.Id == id);

    private NeoWindow? MainWindowOf()
    {
        lock (_gate) return Find(MainId)?.Window;
    }

    private IEnumerable<NeoWindow> OpenWindows()
    {
        lock (_gate) return [.. _entries.Select(static entry => entry.Window).OfType<NeoWindow>().Where(static window => !window.IsClosed)];
    }

    private IEnumerable<global::NeoAstra.NeoAstra> Views()
    {
        lock (_gate) return [.. _entries.Select(static entry => entry.View).OfType<global::NeoAstra.NeoAstra>()];
    }

    private static bool Activate(NeoWindow window)
    {
        try
        {
            window.Show();
            if (window.State == NeoWindowState.Minimized) window.Restore();
            window.Activate();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or NeoAstraException) { return false; }
    }

    private void Post(Action action)
    {
        try
        {
            if (_application.Dispatcher.CheckAccess()) action();
            else _application.Dispatcher.Post(action);
        }
        catch (ObjectDisposedException) { /* The application is ending. */ }
    }

    private async Task<T> OnWindowThreadAsync<T>(Func<Task<T>> work)
    {
        if (_application.Dispatcher.CheckAccess()) return await work().ConfigureAwait(true);
        var started = await _application.Dispatcher.InvokeAsync(work).ConfigureAwait(false);
        return await started.ConfigureAwait(false);
    }

    private async Task OnWindowThreadAsync(Func<Task> work)
    {
        if (_application.Dispatcher.CheckAccess()) { await work().ConfigureAwait(true); return; }
        var started = await _application.Dispatcher.InvokeAsync(work).ConfigureAwait(false);
        await started.ConfigureAwait(false);
    }

    private sealed class Entry(string id, string label, DesktopWindowRecord record)
    {
        internal string Id { get; } = id;
        internal string Label { get; } = label;
        internal DesktopWindowRecord Record { get; } = record;
        internal bool IsMain => Id == MainId;
        internal NeoWindow? Window { get; set; }
        internal global::NeoAstra.NeoAstra? View { get; set; }
        internal NeoRpcViewBinding? Binding { get; set; }
        internal DesktopWindowState? State { get; set; }
        internal List<Action<WindowsEvent>> Watchers { get; } = [];
        internal List<WindowsEvent> Queued { get; } = [];
        internal string? ActiveTab { get; set; }
        internal string? ShownSpace { get; set; }
        internal bool HadTabs { get; set; }
        internal bool CloseAllowed { get; set; }
        internal bool Opened { get; set; }
        internal bool Closed { get; set; }

        // The main window shows the space its page chose; a secondary one keeps the one it was created in.
        internal string? SpaceId => IsMain ? ShownSpace : Record.SpaceId ?? ShownSpace;
    }

    private sealed class Registration(DesktopWindows owner, Entry entry, Action<WindowsEvent> watcher) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) entry.Watchers.Remove(watcher);
        }
    }
}

/// <summary>A secondary window to open.</summary>
internal sealed record DesktopWindowOpening
{
    /// <summary>Gets the id of a saved window that is closed, which opens again; null for a new window.</summary>
    public string? SavedId { get; init; }
    public string? Name { get; init; }
    public string? Icon { get; init; }
    public string? Color { get; init; }
    public string? SpaceId { get; init; }
    /// <summary>Gets the display the window goes to; null for the default place.</summary>
    public string? DisplayId { get; init; }
    /// <summary>Gets where the top-left corner of the window goes on the screen; null for the default place.</summary>
    public NeoPoint? ScreenPoint { get; init; }
    /// <summary>Gets what the page opens first, as the pages describe a tab.</summary>
    public IReadOnlyList<string> Tabs { get; init; } = [];
}

/// <summary>What changes of a window; null leaves a value as it is, and an empty text takes it away.</summary>
internal sealed record DesktopWindowIdentityChange
{
    public string? Name { get; init; }
    public string? Icon { get; init; }
    public string? Color { get; init; }
    public bool? Saved { get; init; }
    public string? PinnedDisplayId { get; init; }
}

/// <summary>Where a window goes: a display, a state, bounds in the work area of its display, the current virtual desktop.</summary>
internal sealed record DesktopWindowPlacing
{
    public string? DisplayId { get; init; }
    public NeoWindowState? State { get; init; }
    public NeoRect? Bounds { get; init; }
    public bool CurrentDesktop { get; init; }
}
