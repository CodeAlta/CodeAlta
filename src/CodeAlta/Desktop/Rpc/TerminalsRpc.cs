using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Terminals;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the terminals: it creates one in the folder of a project or of a session, types in it,
/// sizes it, names it and closes it, and listens to what the terminals it shows write. The terminals belong
/// to the application: they keep running when the page stops showing them, or goes away.
/// </summary>
[NeoRpcService("terminals", Version = 1)]
internal sealed class TerminalsService
{
    /// <summary>The longest text one call types in a terminal, in UTF-16 units.</summary>
    internal const int MaximumInputUnits = 1024 * 1024;

    /// <summary>The longest identifier a request may carry, in UTF-16 units.</summary>
    internal const int MaximumIdUnits = 128;

    private readonly DesktopTerminals? _terminals;
    private readonly ProjectCatalog? _projects;
    private readonly string? _epoch;
    private readonly Func<string, CancellationToken, Task<string?>>? _sessionFolder;
    private readonly Func<string, bool> _open = DesktopLinks.Open;
    private readonly ConcurrentDictionary<string, TerminalFeed> _feeds = new(StringComparer.Ordinal);

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal TerminalsService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="terminals">The terminals of the application.</param>
    /// <param name="projects">The host's project catalog, used to resolve a project id to its folder.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="sessionFolder">Gives the folder a session works in, or null when there is no such session.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal TerminalsService(DesktopTerminals terminals, ProjectCatalog projects, string epoch, Func<string, CancellationToken, Task<string?>> sessionFolder)
    {
        ArgumentNullException.ThrowIfNull(terminals);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        ArgumentNullException.ThrowIfNull(sessionFolder);
        (_terminals, _projects, _epoch, _sessionFolder) = (terminals, projects, epoch, sessionFolder);
    }

    /// <summary>Creates the service for an owned host, with what opens a link of a terminal.</summary>
    /// <param name="terminals">The terminals of the application.</param>
    /// <param name="projects">The host's project catalog.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="sessionFolder">Gives the folder a session works in.</param>
    /// <param name="open">Opens an address in the browser of the system, and tells whether it could.</param>
    internal TerminalsService(DesktopTerminals terminals, ProjectCatalog projects, string epoch, Func<string, CancellationToken, Task<string?>> sessionFolder, Func<string, bool> open)
        : this(terminals, projects, epoch, sessionFolder)
    {
        ArgumentNullException.ThrowIfNull(open);
        _open = open;
    }

    /// <summary>The command interpreters a terminal can start, the default one first, and what the page's terminal must know of the system.</summary>
    [NeoRpcMethod("profiles")]
    public Task<TerminalProfilesResponse> ProfilesAsync(TerminalsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var build = OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : 0;
        if (Refuse(request.ExpectedEpoch) is { } refused) return Task.FromResult(new TerminalProfilesResponse(refused, [], platform, build));
        // Looking for the interpreters reads the disk and the registry.
        return Task.Run(() => new TerminalProfilesResponse("ok", [.. _terminals!.Profiles.Select(static profile => new TerminalProfileOption(profile.Id, profile.Name))], platform, build), cancellationToken);
    }

    /// <summary>
    /// Creates a terminal and starts its interpreter: in the folder a session works in when the request names
    /// one, in the folder of the project otherwise, or in the home folder of the user without either.
    /// </summary>
    [NeoRpcMethod("create")]
    public async Task<TerminalCreateResponse> CreateAsync(TerminalCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!Identifier(request.ProjectId, required: false) || !Identifier(request.SessionId, required: false) || !Identifier(request.Profile, required: false)) return new("invalid", null);
        string? folder = null;
        if (request.ProjectId is not null)
        {
            var project = await SettingsProjectScope.ResolveAsync(_projects!, request.ProjectId, allowArchived: false, cancellationToken).ConfigureAwait(false);
            if (project.Status != "ok") return new(project.Status, null);
            folder = project.Root;
        }
        if (request.SessionId is not null)
        {
            string? session;
            try { session = await _sessionFolder!(request.SessionId, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException) { return new("read_failed", null); }
            if (session is null) return new("missing_session", null);
            // A session can work in a folder of its own, which is not the one of its project.
            if (Path.IsPathFullyQualified(session) && Directory.Exists(session)) folder = session;
            else if (folder is null) return new("no_folder", null);
        }
        folder ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Starting a program is not for the thread of the window.
        var (status, terminal) = await Task.Run(() => _terminals!.Create(new TerminalRequest(request.ProjectId, request.SessionId, folder, request.Profile, null, Agent: false,
            request.Columns ?? 0, request.Rows ?? 0, request.Integration ?? true)), cancellationToken).ConfigureAwait(false);
        return new(status, terminal is null ? null : Item(terminal.Describe()));
    }

    /// <summary>Types text in a terminal.</summary>
    [NeoRpcMethod("input")]
    public TerminalReply Input(TerminalInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (request.Data is not { Length: <= MaximumInputUnits } data) return new("invalid");
        if (_terminals!.Find(request.Id) is not { } terminal) return new("not_found");
        return new(terminal.Write(data) ? "ok" : "refused");
    }

    /// <summary>Gives the screen of a terminal another size.</summary>
    [NeoRpcMethod("resize")]
    public TerminalReply Resize(TerminalResizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (request.Columns < PseudoTerminal.MinimumSize || request.Rows < PseudoTerminal.MinimumSize
            || request.Columns > PseudoTerminal.MaximumSize || request.Rows > PseudoTerminal.MaximumSize) return new("invalid");
        if (_terminals!.Find(request.Id) is not { } terminal) return new("not_found");
        terminal.Resize(request.Columns, request.Rows);
        return new("ok");
    }

    /// <summary>Gives a terminal a title, or with an empty one the title it has by itself.</summary>
    [NeoRpcMethod("rename")]
    public TerminalReply Rename(TerminalRenameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (request.Title is { Length: > DesktopTerminal.MaximumTitleLength }) return new("invalid");
        if (_terminals!.Find(request.Id) is not { } terminal) return new("not_found");
        terminal.Rename(request.Title);
        return new("ok");
    }

    /// <summary>Closes a terminal: its program is ended, and the terminal is gone once the program is.</summary>
    [NeoRpcMethod("close")]
    public TerminalReply Close(TerminalIdRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (_terminals!.Find(request.Id) is not { } terminal) return new("not_found");
        terminal.Close();
        return new("ok");
    }

    /// <summary>
    /// Opens a link of a terminal in the browser of the system: an <c>http</c> or <c>https</c> address, nothing
    /// else. What a program prints is not trusted with more.
    /// </summary>
    [NeoRpcMethod("open")]
    public TerminalReply Open(TerminalLinkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        return new(DesktopLinks.WebAddress(request.Address) is { } address ? _open(address) ? "ok" : "failed" : "invalid");
    }

    /// <summary>
    /// The page shows a terminal in a tab: its <see cref="Watch"/> is first given what the terminal wrote
    /// before, then what it writes as it comes.
    /// </summary>
    [NeoRpcMethod("attach")]
    public TerminalReply Attach(TerminalViewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (Feed(request.Feed) is not { } feed) return new("no_feed");
        return new(feed.Attach(request.Id) ? "ok" : "not_found");
    }

    /// <summary>The page no longer shows a terminal. The terminal keeps running.</summary>
    [NeoRpcMethod("detach")]
    public TerminalReply Detach(TerminalViewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (Feed(request.Feed) is not { } feed) return new("no_feed");
        feed.Detach(request.Id);
        return new("ok");
    }

    /// <summary>The tab of a terminal is the one the page shows, or no longer is.</summary>
    [NeoRpcMethod("show")]
    public TerminalReply Show(TerminalShowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (Feed(request.Feed) is not { } feed) return new("no_feed");
        feed.Show(request.Id, request.Visible);
        return new("ok");
    }

    /// <summary>
    /// The page took in characters of a terminal it shows. A terminal is sent no more than
    /// <see cref="TerminalFeed.Window"/> characters beyond what the page said it took in.
    /// </summary>
    [NeoRpcMethod("acknowledge")]
    public TerminalReply Acknowledge(TerminalAcknowledgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (Feed(request.Feed) is not { } feed) return new("no_feed");
        feed.Acknowledge(request.Id, request.Characters);
        return new("ok");
    }

    /// <summary>
    /// What the page is told about the terminals: first the name of this feed, which the page gives back with
    /// <see cref="Attach"/> and the calls like it, then the terminals there are each time one changes, and what
    /// the terminals the page shows write.
    /// </summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<TerminalEvent> Watch(TerminalsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.TerminalEvent);
    }

    internal async IAsyncEnumerable<TerminalEvent> WatchAsync(TerminalsRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request.ExpectedEpoch) is not null) yield break;
        using var feed = _terminals!.Open();
        _feeds[feed.Id] = feed;
        try
        {
            yield return new TerminalEvent("feed", feed.Id, null, null, null, 0, 0, false);
            await foreach (var news in feed.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return news.Kind switch
                {
                    TerminalNewsKind.List => new TerminalEvent("list", null, null, [.. news.Terminals!.Select(Item)], null, 0, 0, false),
                    TerminalNewsKind.Start => new TerminalEvent("start", null, news.Id, null, news.Data, news.Columns, news.Rows, news.Replayed),
                    TerminalNewsKind.Show => new TerminalEvent("show", null, news.Id, null, null, 0, 0, false),
                    _ => new TerminalEvent("data", null, news.Id, null, news.Data, news.Columns, news.Rows, news.Replayed),
                };
            }
        }
        finally { _feeds.TryRemove(feed.Id, out _); }
    }

    private string? Refuse(string? expectedEpoch)
        => _terminals is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private TerminalFeed? Feed(string? id) => id is not null && _feeds.TryGetValue(id, out var feed) ? feed : null;

    private static bool Identifier(string? value, bool required)
        => value is null ? !required : value.Length is > 0 and <= MaximumIdUnits && !value.Any(char.IsControl);

    private static TerminalItem Item(TerminalInfo terminal)
        => new(terminal.Id, terminal.ProjectId, terminal.SessionId, terminal.Title, terminal.Titled, terminal.Folder, terminal.Profile, terminal.ProfileName,
            terminal.ProgramTitle, terminal.Running, terminal.ExitCode, terminal.Integrated, terminal.Busy, terminal.Command, terminal.LastExitCode,
            terminal.Columns, terminal.Rows, terminal.Created, terminal.Open, terminal.Attention, terminal.Agent);
}

/// <summary>A request about the terminals as a whole.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
internal sealed record TerminalsRequest(string? ExpectedEpoch);

/// <summary>A command interpreter a terminal can start.</summary>
/// <param name="Id">Its identifier.</param>
/// <param name="Name">The name shown to the user.</param>
internal sealed record TerminalProfileOption(string Id, string Name);

/// <summary>The interpreters of this system and what the page's terminal must know of the system.</summary>
/// <param name="Status"><c>ok</c>, <c>unavailable</c> or <c>stale_epoch</c>.</param>
/// <param name="Profiles">The interpreters, the default one first.</param>
/// <param name="Platform"><c>windows</c>, <c>macos</c> or <c>linux</c>.</param>
/// <param name="Build">The build of Windows, which tells how its pseudoconsole wraps lines; 0 elsewhere.</param>
internal sealed record TerminalProfilesResponse(string Status, TerminalProfileOption[] Profiles, string Platform, int Build);

/// <summary>A terminal to create.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="ProjectId">The project the terminal is listed under, or null.</param>
/// <param name="SessionId">The session whose folder the terminal starts in, or null.</param>
/// <param name="Profile">The interpreter to start, or null for the default one.</param>
/// <param name="Columns">The width of the screen, when the page knows it.</param>
/// <param name="Rows">The height of the screen, when the page knows it.</param>
/// <param name="Integration">Whether the shell is started with the script that makes it report its prompts and commands; null for yes.</param>
internal sealed record TerminalCreateRequest(string? ExpectedEpoch, string? ProjectId, string? SessionId, string? Profile, int? Columns, int? Rows, bool? Integration = null);

/// <summary>The terminal that was created.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>unavailable</c>, <c>stale_epoch</c> or <c>invalid</c>; a status of the project (<c>unknown_project</c>,
/// <c>archived_project</c>, <c>project_unavailable</c>, <c>read_failed</c>); <c>missing_session</c>; <c>no_folder</c>;
/// <c>no_shell</c> when no interpreter was found; <c>limit</c> when too many terminals run; <c>failed</c> when the
/// interpreter could not be started; <c>closed</c> while the application exits.
/// </param>
/// <param name="Terminal">The terminal, with <c>ok</c>.</param>
internal sealed record TerminalCreateResponse(string Status, TerminalItem? Terminal);

/// <summary>What the page knows of a terminal.</summary>
/// <param name="Id">Its identifier.</param>
/// <param name="ProjectId">The project it is listed under, or null.</param>
/// <param name="SessionId">The session it was opened from, or null.</param>
/// <param name="Title">Its title: the one it was given, or the folder it is in.</param>
/// <param name="Titled">Whether the title was given to it.</param>
/// <param name="Folder">The folder its shell is in, as far as the shell said, or the one it started in.</param>
/// <param name="Profile">The identifier of its interpreter.</param>
/// <param name="ProfileName">The name of its interpreter.</param>
/// <param name="ProgramTitle">The title its program gave it, or null.</param>
/// <param name="Running">Whether its program is running.</param>
/// <param name="ExitCode">The exit code of its program once it has ended.</param>
/// <param name="Integrated">Whether its shell reports its prompts and its commands.</param>
/// <param name="Busy">Whether its shell runs a command, as far as it reports it.</param>
/// <param name="Command">The command line that runs, when the shell reported it.</param>
/// <param name="LastExitCode">The exit code of the last command that ended, when the shell reported one.</param>
/// <param name="Columns">The width of its screen.</param>
/// <param name="Rows">The height of its screen.</param>
/// <param name="Created">When it was created.</param>
/// <param name="Open">Whether a page shows it in a tab.</param>
/// <param name="Attention">Whether its program rang the bell since its tab was last the one shown.</param>
/// <param name="Agent">Whether a session created it.</param>
internal sealed record TerminalItem(string Id, string? ProjectId, string? SessionId, string Title, bool Titled, string Folder, string Profile, string ProfileName,
    string? ProgramTitle, bool Running, int? ExitCode, bool Integrated, bool Busy, string? Command, int? LastExitCode,
    int Columns, int Rows, DateTimeOffset Created, bool Open, bool Attention, bool Agent);

/// <summary>Text to type in a terminal.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="Id">The terminal.</param>
/// <param name="Data">What its keyboard sends.</param>
internal sealed record TerminalInputRequest(string? ExpectedEpoch, string? Id, string? Data);

/// <summary>Another size for the screen of a terminal.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="Id">The terminal.</param>
/// <param name="Columns">The width, in characters.</param>
/// <param name="Rows">The height, in lines.</param>
internal sealed record TerminalResizeRequest(string? ExpectedEpoch, string? Id, int Columns, int Rows);

/// <summary>A title for a terminal.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="Id">The terminal.</param>
/// <param name="Title">The title; null or empty for the title the terminal has by itself.</param>
internal sealed record TerminalRenameRequest(string? ExpectedEpoch, string? Id, string? Title);

/// <summary>A request about one terminal.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="Id">The terminal.</param>
internal sealed record TerminalIdRequest(string? ExpectedEpoch, string? Id);

/// <summary>A link of a terminal to open.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="Address">The address: <c>http</c> or <c>https</c>.</param>
internal sealed record TerminalLinkRequest(string? ExpectedEpoch, string? Address);

/// <summary>A terminal the page starts or stops showing.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="Feed">The name the page was given first by its watch.</param>
/// <param name="Id">The terminal.</param>
internal sealed record TerminalViewRequest(string? ExpectedEpoch, string? Feed, string? Id);

/// <summary>Whether the tab of a terminal is the one shown.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="Feed">The name the page was given first by its watch.</param>
/// <param name="Id">The terminal.</param>
/// <param name="Visible">Whether its tab is the one shown.</param>
internal sealed record TerminalShowRequest(string? ExpectedEpoch, string? Feed, string? Id, bool Visible);

/// <summary>Characters of a terminal the page took in.</summary>
/// <param name="ExpectedEpoch">The host epoch the page read.</param>
/// <param name="Feed">The name the page was given first by its watch.</param>
/// <param name="Id">The terminal.</param>
/// <param name="Characters">How many more characters the page took in, in UTF-16 units.</param>
internal sealed record TerminalAcknowledgeRequest(string? ExpectedEpoch, string? Feed, string? Id, int Characters);

/// <summary>The outcome of a request that returns nothing else.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>unavailable</c> or <c>stale_epoch</c>; <c>invalid</c>; <c>not_found</c> when there is no such terminal;
/// <c>no_feed</c> when the watch of the page is gone; <c>refused</c> when a terminal takes no more input;
/// <c>failed</c> when a link could not be opened.
/// </param>
internal sealed record TerminalReply(string Status);

/// <summary>One thing the page is told about the terminals.</summary>
/// <param name="Kind">
/// <c>feed</c>: the name of the feed, first. <c>list</c>: the terminals there are. <c>start</c>: what follows for a
/// terminal starts from an empty screen of the given size, after the sequences in <paramref name="Data"/>.
/// <c>data</c>: what the program of a terminal wrote. <c>show</c>: the page is asked to show the tab of a terminal.
/// </param>
/// <param name="Feed">The name of the feed, with <c>feed</c>.</param>
/// <param name="Id">The terminal, with <c>start</c>, <c>data</c> and <c>show</c>.</param>
/// <param name="Terminals">The terminals, with <c>list</c>.</param>
/// <param name="Data">What the program wrote, or with <c>start</c> the sequences that set the modes of the terminal.</param>
/// <param name="Columns">The width the data was written for, when it is not the one of the data before; 0 otherwise.</param>
/// <param name="Rows">The height the data was written for, when it is not the one of the data before; 0 otherwise.</param>
/// <param name="Replayed">Whether the page now has everything that was written before it asked for the terminal.</param>
internal sealed record TerminalEvent(string Kind, string? Feed, string? Id, TerminalItem[]? Terminals, string? Data, int Columns, int Rows, bool Replayed);
