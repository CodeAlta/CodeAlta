using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CodeAlta.Desktop.Terminals;

/// <summary>What a new terminal is asked for.</summary>
/// <param name="ProjectId">The project it is listed under, or null.</param>
/// <param name="SessionId">The session it is opened from, or null.</param>
/// <param name="Folder">The folder it starts in.</param>
/// <param name="Profile">The identifier of the interpreter it starts, or null for the default one.</param>
/// <param name="Title">A title, or null.</param>
/// <param name="Agent">Whether a session asks for it: it then stays once its program has ended, so that it can still be read.</param>
/// <param name="Columns">The width of its screen, or 0 for the usual one.</param>
/// <param name="Rows">The height of its screen, or 0 for the usual one.</param>
/// <param name="Integration">Whether its shell is started with the script that makes it report its prompts and commands.</param>
internal sealed record TerminalRequest(string? ProjectId, string? SessionId, string Folder, string? Profile, string? Title, bool Agent, int Columns = 0, int Rows = 0,
    bool Integration = true);

/// <summary>What a window is told about the terminals.</summary>
internal enum TerminalNewsKind
{
    /// <summary>The terminals there are now.</summary>
    List,
    /// <summary>What follows for a terminal starts from an empty screen of a given size, in given modes.</summary>
    Start,
    /// <summary>What the program of a terminal wrote.</summary>
    Data,
    /// <summary>The window is asked to show the tab of a terminal.</summary>
    Show,
}

/// <summary>One thing a window is told about the terminals.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Id">The terminal it is about, for what is about one.</param>
/// <param name="Terminals">The terminals there are, for a list.</param>
/// <param name="Data">What the program wrote, or the sequences of the modes for a start.</param>
/// <param name="Columns">The width the data was written for, when it is not the one of the data before; 0 otherwise.</param>
/// <param name="Rows">The height the data was written for, when it is not the one of the data before; 0 otherwise.</param>
/// <param name="Replayed">Whether the window now has everything that was written before it asked for the terminal.</param>
internal sealed record TerminalNews(TerminalNewsKind Kind, string? Id = null, IReadOnlyList<TerminalInfo>? Terminals = null, string? Data = null, int Columns = 0, int Rows = 0, bool Replayed = false);

/// <summary>
/// The terminals of the application. They belong to the application, not to a window: a terminal runs with or
/// without a tab that shows it, and a window that starts later is given what each one wrote.
/// </summary>
internal sealed class DesktopTerminals
{
    /// <summary>How many terminals can run at once.</summary>
    internal const int MaximumTerminals = 64;
    /// <summary>The size of a terminal that no window has sized yet.</summary>
    internal const int DefaultColumns = 120, DefaultRows = 30;
    // How long the programs have to end when the application exits.
    private static readonly TimeSpan ExitPatience = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();
    private readonly List<DesktopTerminal> _terminals = [];
    private readonly List<TerminalFeed> _feeds = [];
    private readonly TerminalProfiles _profiles;
    private readonly Func<PseudoTerminalStart, PseudoTerminal> _start;
    private readonly TimeProvider _time;
    private readonly string _version;
    private readonly string? _scripts;
    private bool _closed;

    /// <summary>Creates the terminals of an application.</summary>
    /// <param name="version">The version of the application, for the programs that ask which terminal they run in.</param>
    /// <param name="scripts">The folder the scripts of the shells are written in; null to start the shells that need one as they are.</param>
    internal DesktopTerminals(string version, string? scripts = null)
        : this(version, new TerminalProfiles(), PseudoTerminal.Start, TimeProvider.System, scripts)
    {
    }

    /// <summary>Creates the terminals of an application from their parts.</summary>
    /// <param name="version">The version of the application.</param>
    /// <param name="profiles">Finds the command interpreters.</param>
    /// <param name="start">Starts a program behind a pseudo-terminal.</param>
    /// <param name="time">The clock.</param>
    /// <param name="scripts">The folder the scripts of the shells are written in, or null.</param>
    internal DesktopTerminals(string version, TerminalProfiles profiles, Func<PseudoTerminalStart, PseudoTerminal> start, TimeProvider time, string? scripts = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(time);
        (_version, _profiles, _start, _time, _scripts) = (version, profiles, start, time, scripts);
    }

    /// <summary>The command interpreters a terminal can start, the default one first.</summary>
    public IReadOnlyList<TerminalProfile> Profiles => _profiles.List();

    /// <summary>The terminals, in the order they were created.</summary>
    public IReadOnlyList<TerminalInfo> List()
    {
        DesktopTerminal[] terminals;
        lock (_gate) terminals = [.. _terminals];
        return [.. terminals.Select(static terminal => terminal.Describe())];
    }

    /// <summary>How many terminals have a program that runs.</summary>
    public int Running => List().Count(static terminal => terminal.Running);

    /// <summary>How many terminals have a shell that runs a command, as far as the shells say: what an exit would interrupt.</summary>
    public int Busy => List().Count(static terminal => terminal.Busy);

    /// <summary>The terminal with an identifier, or null.</summary>
    public DesktopTerminal? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate) return _terminals.FirstOrDefault(terminal => string.Equals(terminal.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Creates a terminal and starts its program.</summary>
    /// <returns>
    /// The terminal with the status <c>ok</c>; or no terminal with <c>closed</c> (the application is exiting),
    /// <c>limit</c> (too many terminals), <c>no_folder</c> (the folder does not exist), <c>no_shell</c> (no command
    /// interpreter was found) or <c>failed</c> (the program could not be started).
    /// </returns>
    public (string Status, DesktopTerminal? Terminal) Create(TerminalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_closed) return ("closed", null);
            if (_terminals.Count >= MaximumTerminals) return ("limit", null);
        }
        if (!Path.IsPathFullyQualified(request.Folder) || !Directory.Exists(request.Folder)) return ("no_folder", null);
        if (_profiles.Choose(request.Profile) is not { } profile) return ("no_shell", null);
        var id = Guid.CreateVersion7(_time.GetUtcNow()).ToString("D");
        var description = new TerminalDescription(id, request.ProjectId, request.SessionId, Path.GetFullPath(request.Folder), profile, request.Title,
            Keep: request.Agent, request.Agent, request.Columns > 0 ? request.Columns : DefaultColumns, request.Rows > 0 ? request.Rows : DefaultRows);
        PseudoTerminal program;
        try
        {
            var environment = TerminalEnvironment.Create(id, _version);
            var arguments = profile.Arguments;
            if (request.Integration)
            {
                // The shell is started with the script that makes it say where its prompt is and what it runs.
                var launch = TerminalIntegration.Launch(profile, _scripts, environment);
                arguments = launch.Arguments;
                foreach (var (name, value) in launch.Variables)
                {
                    if (value is null) environment.Remove(name);
                    else environment[name] = value;
                }
            }
            program = _start(new PseudoTerminalStart(profile.FileName, arguments, description.Folder, environment, description.Columns, description.Rows));
        }
        catch (Exception exception) when (exception is PseudoTerminalException or PlatformNotSupportedException)
        {
            return ("failed", null);
        }
        var terminal = new DesktopTerminal(description, program, Changed, _time);
        lock (_gate)
        {
            if (_closed)
            {
                terminal.Dispose();
                return ("closed", null);
            }
            _terminals.Add(terminal);
        }
        terminal.Start();
        Tell(list: true);
        return ("ok", terminal);
    }

    /// <summary>Asks the windows to show the tab of a terminal to the user.</summary>
    /// <returns>False when no window listens.</returns>
    public bool Reveal(DesktopTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        TerminalFeed[] feeds;
        lock (_gate) feeds = [.. _feeds];
        foreach (var feed in feeds) feed.Reveal(terminal.Id);
        return feeds.Length > 0;
    }

    /// <summary>Starts telling a window about the terminals. The window disposes of the feed when it stops listening.</summary>
    public TerminalFeed Open()
    {
        var feed = new TerminalFeed(this);
        lock (_gate) _feeds.Add(feed);
        return feed;
    }

    /// <summary>Ends every terminal, when the application exits. No terminal is created afterwards.</summary>
    public async Task CloseAsync()
    {
        DesktopTerminal[] terminals;
        TerminalFeed[] feeds;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            terminals = [.. _terminals];
            feeds = [.. _feeds];
        }
        foreach (var terminal in terminals) terminal.Close();
        await Task.WhenAny(Task.WhenAll(terminals.Select(static terminal => terminal.Ended)), Task.Delay(ExitPatience, _time)).ConfigureAwait(false);
        foreach (var terminal in terminals) terminal.Dispose();
        lock (_gate) _terminals.Clear();
        foreach (var feed in feeds) feed.Dispose();
    }

    internal void Forget(TerminalFeed feed)
    {
        lock (_gate) _feeds.Remove(feed);
    }

    // A terminal has something new. One whose program has ended goes away, unless it is kept to be read: a
    // terminal a session created, or a shell that could not start.
    private void Changed(DesktopTerminal terminal, bool list)
    {
        if (terminal.Ended.IsCompleted && (terminal.Closing || !terminal.Keep && !terminal.FailedToStart))
        {
            bool removed;
            lock (_gate) removed = _terminals.Remove(terminal);
            if (removed)
            {
                terminal.Dispose();
                list = true;
            }
        }
        Tell(list);
    }

    private void Tell(bool list)
    {
        TerminalFeed[] feeds;
        lock (_gate) feeds = [.. _feeds];
        foreach (var feed in feeds) feed.Wake(list);
    }
}

/// <summary>
/// What one window is told about the terminals: which there are, and what the programs of those it shows
/// wrote. A terminal the window shows is first given what was written before, then what is written as it
/// comes. The window says how much it has taken in: a terminal it has not caught up with waits, and never
/// holds the others back; one too far behind is started again from what the terminal still has.
/// </summary>
internal sealed class TerminalFeed : IDisposable
{
    /// <summary>The most characters of one terminal in one piece of news.</summary>
    internal const int MaximumPiece = 64 * 1024;
    /// <summary>How many characters a window can have been sent without saying it took them in.</summary>
    internal const int Window = 512 * 1024;
    // The least time between two pieces of news of a program that writes without pause.
    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(4);

    private sealed class View(DesktopTerminal terminal)
    {
        public readonly DesktopTerminal Terminal = terminal;
        public long Offset, ReplayEnd, Sent, Acknowledged;
        public int Columns, Rows;
        public bool Starts = true, Replayed, Visible;
    }

    private readonly Lock _gate = new();
    private readonly DesktopTerminals _owner;
    private readonly Dictionary<string, View> _views = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly List<string> _reveals = [];
    private bool _list = true;
    private bool _disposed;

    internal TerminalFeed(DesktopTerminals owner) => _owner = owner;

    /// <summary>The name the window gives back with what it asks about this feed.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The window shows a terminal in a tab: it is given what the terminal wrote, from the oldest it still has.</summary>
    /// <returns>False when there is no such terminal.</returns>
    public bool Attach(string? id)
    {
        if (_owner.Find(id) is not { } terminal) return false;
        lock (_gate)
        {
            if (_disposed || _views.ContainsKey(terminal.Id)) return !_disposed;
            var (start, end, _, _) = terminal.Position();
            _views.Add(terminal.Id, new View(terminal) { Offset = start, ReplayEnd = end });
        }
        terminal.Viewed(true);
        Wake(false);
        return true;
    }

    /// <summary>The window no longer shows a terminal.</summary>
    public void Detach(string? id)
    {
        View? view;
        lock (_gate)
        {
            if (id is null || !_views.Remove(id, out view)) return;
        }
        if (view.Visible) view.Terminal.Shown(false);
        view.Terminal.Viewed(false);
    }

    /// <summary>The tab of a terminal is the one shown, or no longer is.</summary>
    public void Show(string? id, bool visible)
    {
        View? view;
        lock (_gate)
        {
            if (id is null || !_views.TryGetValue(id, out view) || view.Visible == visible) return;
            view.Visible = visible;
        }
        view.Terminal.Shown(visible);
    }

    /// <summary>The window took in characters of a terminal.</summary>
    public void Acknowledge(string? id, long characters)
    {
        lock (_gate)
        {
            if (id is null || characters <= 0 || !_views.TryGetValue(id, out var view)) return;
            view.Acknowledged = Math.Min(view.Sent, view.Acknowledged + characters);
        }
        Wake(false);
    }

    /// <summary>What the window is told, as it happens, until it stops listening.</summary>
    public async IAsyncEnumerable<TerminalNews> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var news = new List<TerminalNews>();
        while (!cancellationToken.IsCancellationRequested)
        {
            news.Clear();
            var more = Collect(news);
            foreach (var item in news) yield return item;
            if (more) await Task.Delay(Pace, cancellationToken).ConfigureAwait(false);
            else if (!await _wake.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) yield break;
            else while (_wake.Reader.TryRead(out _)) { }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        View[] views;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            views = [.. _views.Values];
            _views.Clear();
        }
        foreach (var view in views)
        {
            if (view.Visible) view.Terminal.Shown(false);
            view.Terminal.Viewed(false);
        }
        _owner.Forget(this);
        _wake.Writer.TryComplete();
    }

    // The window is asked to show the tab of a terminal, after it was told the terminals there are.
    internal void Reveal(string id)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _reveals.Add(id);
        }
        Wake(false);
    }

    internal void Wake(bool list)
    {
        if (list)
        {
            lock (_gate) _list = true;
        }
        _wake.Writer.TryWrite(true);
    }

    // What there is to tell now. Returns whether a terminal has more than what one piece holds.
    private bool Collect(List<TerminalNews> news)
    {
        bool list;
        View[] views;
        string[] reveals;
        lock (_gate)
        {
            if (_disposed) return false;
            (list, _list) = (_list, false);
            views = [.. _views.Values];
            reveals = [.. _reveals];
            _reveals.Clear();
        }
        if (list)
        {
            var terminals = _owner.List();
            news.Add(new TerminalNews(TerminalNewsKind.List, Terminals: terminals));
            // A terminal that is gone is no longer shown.
            foreach (var view in views)
            {
                if (terminals.All(terminal => terminal.Id != view.Terminal.Id)) Detach(view.Terminal.Id);
            }
        }
        foreach (var id in reveals) news.Add(new TerminalNews(TerminalNewsKind.Show, id));
        var more = false;
        foreach (var view in views)
        {
            lock (_gate)
            {
                if (!_views.ContainsKey(view.Terminal.Id) || view.Sent - view.Acknowledged >= Window) continue;
            }
            var piece = view.Terminal.Piece(view.Offset, MaximumPiece, out _, out var end);
            if (view.Starts || piece is { } moved && moved.Offset != view.Offset)
            {
                // The window starts from an empty screen: at first, and again when it fell so far behind that what it was to read next is gone.
                var (_, _, columns, rows) = view.Terminal.Position();
                if (!view.Starts) (view.ReplayEnd, view.Replayed) = (end, false);
                (view.Columns, view.Rows) = (piece?.Columns ?? columns, piece?.Rows ?? rows);
                view.Replayed = piece is null;
                news.Add(new TerminalNews(TerminalNewsKind.Start, view.Terminal.Id, Data: piece?.Modes ?? string.Empty, Columns: view.Columns, Rows: view.Rows, Replayed: view.Replayed));
                view.Starts = false;
                view.Offset = piece?.Offset ?? end;
                lock (_gate) (view.Sent, view.Acknowledged) = (0, 0);
            }
            if (piece is not { } data) continue;
            var resized = data.Columns != view.Columns || data.Rows != view.Rows;
            (view.Columns, view.Rows) = (data.Columns, data.Rows);
            view.Offset = data.Offset + data.Text.Length;
            lock (_gate) view.Sent += data.Text.Length;
            var replayed = !view.Replayed && view.Offset >= view.ReplayEnd;
            if (replayed) view.Replayed = true;
            news.Add(new TerminalNews(TerminalNewsKind.Data, view.Terminal.Id, Data: data.Text, Columns: resized ? data.Columns : 0, Rows: resized ? data.Rows : 0, Replayed: replayed));
            more |= view.Offset < end;
        }
        return more;
    }
}
