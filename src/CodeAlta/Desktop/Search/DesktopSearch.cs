using CodeAlta.Agent;
using CodeAlta.Catalog.Documentation;
using CodeAlta.Catalog.Search;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Plugins;
using CodeAlta.Search;

namespace CodeAlta.Desktop.Search;

/// <summary>
/// The search of the window: its index (<c>search.sqlite3</c> of the instance), the indexer that is its one writer,
/// and the service that answers the queries of the window and of <c>alta search</c>. Created once the host runs,
/// closed in the close flow of the window.
/// </summary>
internal sealed class DesktopSearch : IAsyncDisposable
{
    private readonly CodeAltaHost _host;
    private readonly DesktopSearchRelay _relay;
    private int _disposed;

    private DesktopSearch(CodeAltaHost host, DesktopSearchRelay relay, DesktopSearchHost searchHost, DesktopSearchSources sources,
        SearchIndexStore store, SearchIndexer indexer, SearchService service)
    {
        (_host, _relay, Host, Sources, Store, Indexer, Service) = (host, relay, searchHost, sources, store, indexer, service);
    }

    /// <summary>Gets what the window tells search: settings, projects and sessions.</summary>
    public DesktopSearchHost Host { get; }

    /// <summary>Gets the catalog sources.</summary>
    public DesktopSearchSources Sources { get; }

    /// <summary>Gets the index.</summary>
    public SearchIndexStore Store { get; }

    /// <summary>Gets the indexer.</summary>
    public SearchIndexer Indexer { get; }

    /// <summary>Gets the query service.</summary>
    public SearchService Service { get; }

    private SearchActivityRecorder? _activity;

    /// <summary>Gets search by meaning, or null where the window has none.</summary>
    public DesktopSearchMeaning? Meaning { get; private set; }

    /// <summary>
    /// Gets how the prompt of a session lists the skills: the setting, or the full list when search is off or the
    /// project or the space is kept out of it, since the short forms send agents to <c>alta search</c>.
    /// </summary>
    public string SkillPromptCatalogFor(CodeAlta.Catalog.ProjectDescriptor? project, string? spaceId)
    {
        var settings = Host.GetSettings();
        if (!settings.Enabled || settings.IsExcluded(project?.Id, spaceId)) return CodeAlta.Catalog.Search.SearchSettings.PromptCatalogFull;
        if (project is { ProjectPath.Length: > 0 } && Host.IsProjectExcludedByItself(new SearchProjectInfo(project.Id, project.Name, project.ProjectPath, project.Archived)))
            return CodeAlta.Catalog.Search.SearchSettings.PromptCatalogFull;
        return settings.PromptCatalog;
    }

    /// <summary>Raised when something asks the window to open the dialog of search by meaning (the landing card, <c>/search_meaning</c>).</summary>
    public event Action? MeaningDialogRequested;

    /// <summary>Asks the window to open the dialog of search by meaning.</summary>
    /// <returns>False when no window listens.</returns>
    public bool RequestMeaningDialog()
    {
        var requested = MeaningDialogRequested;
        if (requested is null) return false;
        requested();
        return true;
    }

    /// <summary>Answers the offer of search by meaning: <c>later</c> (asked again in 30 days) or <c>never</c>.</summary>
    /// <param name="answer"><c>later</c> or <c>never</c>.</param>
    /// <param name="today">The day it is answered.</param>
    /// <returns>The settings saved.</returns>
    /// <exception cref="ArgumentException">The answer is neither.</exception>
    public SearchSettings AnswerMeaningOffer(string answer, DateOnly today)
    {
        if (answer is not ("later" or "never")) throw new ArgumentException("The answer is 'later' or 'never'.", nameof(answer));
        return Host.UpdateSettings(current => current with
        {
            MeaningOffer = answer == "never" ? SearchSettings.MeaningOfferNever : "later:" + today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    /// <summary>Gets whether there is enough to search by meaning to offer it: ten sessions, or three days since the first turn indexed.</summary>
    /// <param name="counts">What the index holds.</param>
    /// <param name="today">The day.</param>
    /// <returns>True when it is worth offering.</returns>
    internal static bool IsMeaningWorthOffering(SearchIndexCounts counts, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Sessions >= 10) return true;
        return counts.Days.Count > 0 && today.DayNumber - counts.Days.Min(static day => day.Day).DayNumber >= 3;
    }

    /// <summary>Gets whether search by meaning is offered now: this window has it, it is off, the offer was not refused nor put off, and there is enough to search.</summary>
    /// <param name="today">The day.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>True when it is offered.</returns>
    public async Task<bool> IsMeaningOfferedAsync(DateOnly today, CancellationToken cancellationToken = default)
    {
        if (Meaning is null || !Host.GetSettings().MayOfferMeaning(today)) return false;
        return IsMeaningWorthOffering(await Store.CountAsync(cancellationToken).ConfigureAwait(false), today);
    }

    /// <summary>Gives search by meaning to the queries, and starts it: its first step waits, unless a query by meaning comes first.</summary>
    /// <param name="meaning">Search by meaning of this window.</param>
    public void AttachMeaning(DesktopSearchMeaning meaning)
    {
        ArgumentNullException.ThrowIfNull(meaning);
        Meaning = meaning;
        Service.Meaning = meaning;
        // The landing card follows search by meaning too: its download, its indexing.
        meaning.Changed += _relay.RaiseChanged;
        meaning.Start();
    }

    /// <summary>
    /// Creates the search of a host and starts its indexer, which reads nothing in its first seconds but what a query
    /// needs (<see cref="SearchIndexerOptions.QuietStart"/>), and waits a little more before its first pass.
    /// </summary>
    /// <param name="host">The running host.</param>
    /// <param name="relay">The relay through which the built-in Search plugin forwards the events of the sessions.</param>
    /// <param name="journals">The journals of the sessions of the instance.</param>
    /// <param name="documentation">The user guide that ships with the application, or null.</param>
    /// <param name="profile">The profile folder of the user, for the skills and the prompts it has.</param>
    /// <param name="builtIns">The built-in plugins of the window.</param>
    /// <param name="uiTools">The tools of the window, or null when sessions do not get them.</param>
    /// <param name="log">Gets what is worth a line in the log.</param>
    /// <param name="options">The timings of the indexer; null for the defaults.</param>
    /// <param name="skillUsage">The recent use of the skills, which ranks the skills used most a little first; null for none.</param>
    public static DesktopSearch Start(CodeAltaHost host, DesktopSearchRelay relay, ISessionJournalCatalog journals, ShippedDocumentation? documentation,
        string profile, IReadOnlyList<BuiltInPluginDefinition> builtIns, Func<IReadOnlyList<(string Name, string Description)>>? uiTools, Action<string>? log,
        SearchIndexerOptions? options = null, DesktopSkillUsage? skillUsage = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(journals);
        var searchHost = new DesktopSearchHost(host, skillUsage);
        var sources = new DesktopSearchSources(host, searchHost, documentation, profile, builtIns, uiTools);
        var store = new SearchIndexStore(host.CatalogOptions.SearchDatabasePath);
        var indexer = new SearchIndexer(store, journals, searchHost, sources.List, TimeProvider.System, options, log);
        var service = new SearchService(store, indexer, searchHost, journals);
        var search = new DesktopSearch(host, relay, searchHost, sources, store, indexer, service);
        // The searches of the window and of agents go to the activity, never their text.
        search._activity = new SearchActivityRecorder(store, TimeProvider.System, SearchActivityRecorder.DefaultSettle, log);
        service.Recorded = search._activity.RecordAsync;
        relay.Attach(search.OnAgentEvent);
        relay.AttachEnabled(() => searchHost.GetSettings().Enabled);
        indexer.StatusChanged += relay.RaiseChanged;
        searchHost.SettingsChanged += (_, _) => relay.RaiseChanged();
        relay.Connect(search);
        host.RuntimeService.SessionDeleted += search.OnSessionDeleted;
        // Turned on or off, or a project or a space kept out: the next pass applies it at once.
        searchHost.SettingsChanged += (_, _) => indexer.SignalPass();
        // A plugin built, loaded or stopped changes the tools, the plugins and the skills.
        host.PluginRuntime.Changed += (_, _) =>
        {
            indexer.Signal("tools");
            indexer.Signal("plugins");
        };
        indexer.Start();
        return search;
    }

    /// <summary>Gives the dispatcher of the <c>alta</c> commands, whose help is indexed with the tools.</summary>
    /// <param name="commands">The dispatcher of the window.</param>
    public void AttachCommands(LiveTool.AltaCommandDispatcher commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        Sources.Dispatcher = commands;
        Indexer.Signal("tools");
    }

    private void OnAgentEvent(AgentEvent agentEvent)
    {
        switch (agentEvent)
        {
            // A prompt is indexed as soon as it is written, the final answer when the turn ends.
            case AgentContentCompletedEvent { Kind: AgentContentKind.User }:
            case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }:
                Host.InvalidateSessions();
                Indexer.SignalJournal(agentEvent.SessionId);
                break;
        }
    }

    private void OnSessionDeleted(string sessionId)
    {
        Host.InvalidateSessions();
        _ = ForgetAsync(sessionId);
    }

    private async Task ForgetAsync(string sessionId)
    {
        try { await Indexer.ForgetSessionAsync(sessionId).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
        {
            // The pass that follows removes what is left of a session whose journal is gone.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _relay.Attach(null);
        _relay.AttachEnabled(null);
        _relay.Connect(null);
        Indexer.StatusChanged -= _relay.RaiseChanged;
        _host.RuntimeService.SessionDeleted -= OnSessionDeleted;
        Service.Meaning = null;
        if (Meaning is { } meaning)
        {
            meaning.Changed -= _relay.RaiseChanged;
            await meaning.DisposeAsync().ConfigureAwait(false);
        }
        await Indexer.DisposeAsync().ConfigureAwait(false);
        Service.Recorded = null;
        if (_activity is { } activity) await activity.DisposeAsync().ConfigureAwait(false);
        await Store.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Where the built-in Search plugin, which exists before the host, forwards the events of the sessions to the search
/// that the window creates once the host runs.
/// </summary>
internal sealed class DesktopSearchRelay
{
    private Action<AgentEvent>? _target;
    private DesktopSearch? _search;

    /// <summary>Raised when the search is created or closed, when its indexer changes state, and when its settings change.</summary>
    public event Action? Changed;

    /// <summary>Gets the search of the window, or null before it exists and once it is closed.</summary>
    public DesktopSearch? Search => Volatile.Read(ref _search);

    private Func<bool>? _enabled;

    /// <summary>Sets where the events go; null to stop.</summary>
    public void Attach(Action<AgentEvent>? target) => Volatile.Write(ref _target, target);

    /// <summary>Sets what says whether search is on; null when there is no search.</summary>
    public void AttachEnabled(Func<bool>? enabled) => Volatile.Write(ref _enabled, enabled);

    /// <summary>Gets whether search is on: false before the search exists.</summary>
    public bool Enabled() => Volatile.Read(ref _enabled)?.Invoke() == true;
    /// <summary>Sets the search the plugin reads its state from; null once it is closed.</summary>
    /// <param name="search">The search.</param>
    public void Connect(DesktopSearch? search)
    {
        Volatile.Write(ref _search, search);
        RaiseChanged();
    }

    /// <summary>Forwards an event; nothing happens before the search exists.</summary>
    public void Forward(AgentEvent agentEvent) => Volatile.Read(ref _target)?.Invoke(agentEvent);

    /// <summary>Tells the plugin that the state of search changed.</summary>
    public void RaiseChanged() => Changed?.Invoke();
}
