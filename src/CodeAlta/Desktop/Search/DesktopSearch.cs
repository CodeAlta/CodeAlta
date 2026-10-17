using CodeAlta.Agent;
using CodeAlta.Catalog.Documentation;
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

    /// <summary>Gives search by meaning to the queries, and starts it.</summary>
    /// <param name="meaning">Search by meaning of this window.</param>
    public void AttachMeaning(DesktopSearchMeaning meaning)
    {
        ArgumentNullException.ThrowIfNull(meaning);
        Meaning = meaning;
        Service.Meaning = meaning;
        meaning.Start();
    }

    /// <summary>Creates the search of a host and starts its indexer, which waits a little before its first pass.</summary>
    /// <param name="host">The running host.</param>
    /// <param name="relay">The relay through which the built-in Search plugin forwards the events of the sessions.</param>
    /// <param name="journals">The journals of the sessions of the instance.</param>
    /// <param name="documentation">The user guide that ships with the application, or null.</param>
    /// <param name="profile">The profile folder of the user, for the skills and the prompts it has.</param>
    /// <param name="builtIns">The built-in plugins of the window.</param>
    /// <param name="uiTools">The tools of the window, or null when sessions do not get them.</param>
    /// <param name="log">Gets what is worth a line in the log.</param>
    /// <param name="options">The timings of the indexer; null for the defaults.</param>
    public static DesktopSearch Start(CodeAltaHost host, DesktopSearchRelay relay, ISessionJournalCatalog journals, ShippedDocumentation? documentation,
        string profile, IReadOnlyList<BuiltInPluginDefinition> builtIns, Func<IReadOnlyList<(string Name, string Description)>>? uiTools, Action<string>? log,
        SearchIndexerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(journals);
        var searchHost = new DesktopSearchHost(host);
        var sources = new DesktopSearchSources(host, searchHost, documentation, profile, builtIns, uiTools);
        var store = new SearchIndexStore(host.CatalogOptions.SearchDatabasePath);
        var indexer = new SearchIndexer(store, journals, searchHost, sources.List, TimeProvider.System, options, log);
        var service = new SearchService(store, indexer, searchHost, journals);
        var search = new DesktopSearch(host, relay, searchHost, sources, store, indexer, service);
        relay.Attach(search.OnAgentEvent);
        relay.AttachEnabled(() => searchHost.GetSettings().Enabled);
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
        _host.RuntimeService.SessionDeleted -= OnSessionDeleted;
        Service.Meaning = null;
        if (Meaning is { } meaning) await meaning.DisposeAsync().ConfigureAwait(false);
        await Indexer.DisposeAsync().ConfigureAwait(false);
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

    private Func<bool>? _enabled;

    /// <summary>Sets where the events go; null to stop.</summary>
    public void Attach(Action<AgentEvent>? target) => Volatile.Write(ref _target, target);

    /// <summary>Sets what says whether search is on; null when there is no search.</summary>
    public void AttachEnabled(Func<bool>? enabled) => Volatile.Write(ref _enabled, enabled);

    /// <summary>Gets whether search is on: false before the search exists.</summary>
    public bool Enabled() => Volatile.Read(ref _enabled)?.Invoke() == true;

    /// <summary>Forwards an event; nothing happens before the search exists.</summary>
    public void Forward(AgentEvent agentEvent) => Volatile.Read(ref _target)?.Invoke(agentEvent);
}
