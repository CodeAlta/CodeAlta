using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugin.Statistics.Store;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Statistics;

public sealed partial class StatisticsPlugin
{
    private readonly ISessionJournalCatalog? _journals;
    private readonly StatisticsArchiveStore? _archives;
    private readonly TimeSpan? _startDelay;
    private readonly TimeSpan? _flowDebounce;

    // The clock of the engine and of the events of a canvas; null for the one of the system. Tests give one they move by hand.
    private readonly TimeProvider? _time;
    private StatisticsEngine? _engine;
    private PluginTaskHandle? _job;

    private StatisticsPlugin(ISessionJournalCatalog journals)
    {
        ArgumentNullException.ThrowIfNull(journals);
        _journals = journals;
    }

    /// <summary>
    /// Creates the Statistics plugin of CodeAlta Desktop: besides the cards of the timeline it keeps the statistics of every session
    /// in the application database, reading the journals of <paramref name="journals"/>.
    /// </summary>
    /// <param name="journals">The journals of the session store of this instance of the application.</param>
    /// <returns>The plugin.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="journals"/> is null.</exception>
    /// <remarks>
    /// The reading runs only in CodeAlta Desktop, once the plugin is activated and the host has a database; the user is first asked
    /// how much of the history to read. A plugin made without journals (<c>new StatisticsPlugin()</c>), or in CodeAlta TUI, keeps the
    /// cards and the commands that read the numbers, and reads nothing.
    /// </remarks>
    public static StatisticsPlugin CreateForDesktop(ISessionJournalCatalog journals) => new(journals);

    /// <summary>Creates the desktop plugin with the owning StateRoot's durable archive. The archive is independent of plugin unload/reset.</summary>
    /// <param name="journals">The owning instance's journal catalog.</param>
    /// <param name="archives">The dedicated archive supplied by host composition.</param>
    /// <returns>The plugin with optional retirement preservation capability while activated.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static StatisticsPlugin CreateForDesktop(ISessionJournalCatalog journals, StatisticsArchiveStore archives)
        => new(journals, archives);

    private StatisticsPlugin(ISessionJournalCatalog journals, StatisticsArchiveStore archives)
        : this(journals)
    {
        ArgumentNullException.ThrowIfNull(archives);
        _archives = archives;
    }

    internal StatisticsPlugin(ISessionJournalCatalog journals, TimeSpan startDelay, TimeSpan flowDebounce, TimeProvider? time = null)
        : this(journals)
    {
        _startDelay = startDelay;
        _flowDebounce = flowDebounce;
        _time = time;
    }

    /// <summary>
    /// Gets what the plugin offers to the application while it keeps the statistics up to date: the state of the reading, its controls
    /// and the questions about the numbers; null when the plugin does not read the sessions (CodeAlta TUI, a host without a database, a plugin that is not active).
    /// </summary>
    public IStatisticsService? Statistics => _engine;

    /// <summary>Gets the active engine's source-bound archive capability; null when disabled, unloaded or not composed with durable storage.</summary>
    public ISessionStatisticsRetirementArchive? RetirementArchive => _archives is null ? null : _engine;

    /// <inheritdoc />
    public override ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
    {
        if (_journals is null || Context.Host.Frontend != PluginFrontends.Desktop || !Services.Database.HasDatabase)
        {
            return ValueTask.CompletedTask;
        }

        var directory = new AltaProjectDirectory(Services.Alta);
        _directory = directory;
        var store = new StatisticsStore(Services.Database, new LocalDays(TimeZoneInfo.Local), _archives);
        var engine = new StatisticsEngine(store, _journals, new StatisticsEngineOptions
        {
            Logger = Logger,
            Time = _time ?? TimeProvider.System,
            StartDelay = _startDelay ?? TimeSpan.FromSeconds(5),
            FlowDebounce = _flowDebounce ?? TimeSpan.FromSeconds(1),
            ProjectDirectory = directory,
            ResolveProjectNames = async token => (await directory.ListProjectsAsync(token).ConfigureAwait(false))
                .ToDictionary(static project => project.Id, static project => project.Name, StringComparer.OrdinalIgnoreCase),
        });
        _engine = engine;
        engine.StatusChanged += OnButtonStatusChanged;
        engine.StatusChanged += OnLandingStatusChanged;
        _job = Tasks.Run(
            "statistics-history",
            token => new ValueTask(engine.RunAsync(token)),
            // Reads tolerate disappearing journals; history/flow only write the Statistics projection/archive,
            // never schedule or mutate a session. Archive preparation serializes with this reader in the engine.
            new PluginTaskOptions { LongRunning = true, SessionRetentionIndependent = true, Description = "Keeps the statistics of the sessions up to date." });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override async ValueTask OnDeactivatingAsync(CancellationToken cancellationToken = default)
    {
        await StopEngineAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await StopEngineAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>The numbers are read from the journal, which records no delta of streaming content: deltas are not needed.</remarks>
    public override bool ObservesContentDeltas => false;

    /// <inheritdoc />
    public override ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        // The event is a signal, nothing more: the numbers are read from the journal a moment later, and the callback returns at once,
        // since a slow callback holds the event pipeline of its session and callbacks beyond the plugin's budget are dropped.
        _engine?.Signal(context.Event.SessionId);
        return ValueTask.CompletedTask;
    }

    private async ValueTask StopEngineAsync()
    {
        var engine = Interlocked.Exchange(ref _engine, null);
        var job = Interlocked.Exchange(ref _job, null);
        if (engine is null)
        {
            return;
        }

        engine.StatusChanged -= OnButtonStatusChanged;
        engine.StatusChanged -= OnLandingStatusChanged;
        job?.RequestCancellation();
        if (job is not null)
        {
            try
            {
                await job.Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                Logger.Warn("The statistics did not stop in time.");
            }
        }

        await engine.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Gets the questions about the numbers: the ones of the running engine, or ones over the database of the host for a process that reads nothing.</summary>
    internal async ValueTask<StatisticsQueries?> GetQueriesAsync(PluginAltaCommandContext context, CancellationToken cancellationToken)
    {
        if (_engine is { } engine)
        {
            // The engine waits a few seconds after the start of the application before it prepares its tables: a question asked
            // before that prepares them, as a question of the canvas does.
            await engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return engine.Queries;
        }

        var store = await OpenStoreAsync(context, cancellationToken).ConfigureAwait(false);
        return store is null ? null : new StatisticsQueries(store, new AltaProjectDirectory(context.Services.Alta));
    }

    /// <summary>Opens the store over the database the host gives the plugin; null when the host has none.</summary>
    internal async ValueTask<StatisticsStore?> OpenStoreAsync(PluginAltaCommandContext context, CancellationToken cancellationToken)
    {
        if (_engine is { } engine)
        {
            await engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return engine.Store;
        }

        var database = context.Services.Database;
        if (!database.HasDatabase)
        {
            return null;
        }

        var store = new StatisticsStore(database, new LocalDays(TimeZoneInfo.Local), _archives);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }
}
