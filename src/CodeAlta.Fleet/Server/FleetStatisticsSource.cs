using CodeAlta.Fleet.Protocol;
using CodeAlta.Plugin.Statistics.History;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The statistics facts an instance has to publish (<c>doc/fleet/statistics.md</c>): a revision per session that changes when
/// its facts change, and the rows of a session, read again only then.
/// </summary>
public interface IFleetStatisticsSource
{
    /// <summary>Raised when facts were saved; the connector reads the revisions again soon after.</summary>
    event Action? Changed;

    /// <summary>Reads the revision of the facts of every session counted.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The revisions by session; empty while the statistics are not ready.</returns>
    ValueTask<IReadOnlyDictionary<string, string>> ReadRevisionsAsync(CancellationToken cancellationToken);

    /// <summary>Reads the rows of a session from a quarter hour on.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="fromQuarter">The first quarter hour (<see cref="FleetStatisticsFamilies.QuarterOf"/>).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rows.</returns>
    ValueTask<IReadOnlyList<FleetStatisticsRow>> ReadRowsAsync(string sessionId, long fromQuarter, CancellationToken cancellationToken);
}

/// <summary>
/// The statistics facts of a running instance, read from its Statistics plugin (<see cref="IStatisticsPublication"/>): the
/// families a fleet sums (time and runs, requests and tokens, costs, tool calls), with their provider and model taken out
/// of their keys. Nothing is read while the plugin does not run.
/// </summary>
public sealed class FleetHostStatisticsSource : IFleetStatisticsSource, IDisposable
{
    private readonly Func<(IStatisticsPublication? Publication, IStatisticsService? Service)> _resolve;
    private readonly Lock _gate = new();
    private IStatisticsService? _observed;

    /// <summary>Initializes a new instance of the <see cref="FleetHostStatisticsSource"/> class.</summary>
    /// <param name="resolve">Gives the statistics of the instance as they are now (the plugin may start, stop or reload); nulls when none run.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resolve"/> is null.</exception>
    public FleetHostStatisticsSource(Func<(IStatisticsPublication? Publication, IStatisticsService? Service)> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolve = resolve;
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>Makes the source of the statistics of a running instance: the Statistics plugin among its active plugins, when it runs.</summary>
    /// <param name="host">The runtime.</param>
    /// <returns>The source; dispose it with the connections.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
    public static FleetHostStatisticsSource ForHost(CodeAlta.Orchestration.Hosting.CodeAltaHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new FleetHostStatisticsSource(() =>
        {
            var plugin = host.PluginRuntime.ActivePlugins.Select(static active => active.Instance).OfType<CodeAlta.Plugin.Statistics.StatisticsPlugin>().FirstOrDefault();
            return (plugin?.Publication, plugin?.Statistics);
        });
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, string>> ReadRevisionsAsync(CancellationToken cancellationToken)
    {
        if (Resolve() is not { } publication) return new Dictionary<string, string>(StringComparer.Ordinal);
        return await publication.ReadRevisionsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<FleetStatisticsRow>> ReadRowsAsync(string sessionId, long fromQuarter, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (Resolve() is not { } publication) return [];
        var rows = await publication.ReadRowsAsync(sessionId, fromQuarter, cancellationToken).ConfigureAwait(false);
        var result = new List<FleetStatisticsRow>(rows.Count);
        foreach (var row in rows)
        {
            if (!FleetStatisticsFamilies.All.Contains(row.Family)) continue;
            string? provider = null, model = null;
            var keys = new List<string>(row.Keys.Count);
            foreach (var (name, value) in row.Keys)
            {
                switch (name)
                {
                    case "provider": provider = Bounded(value); break;
                    case "model": model = Bounded(value); break;
                    default: keys.Add(Key(value)); break;
                }
            }

            var measures = new Dictionary<string, long>(row.Measures.Count, StringComparer.Ordinal);
            foreach (var (name, value) in row.Measures)
            {
                if (value != 0) measures[name] = value;
            }

            result.Add(new FleetStatisticsRow(row.Family, row.Quarter, provider, model, keys.Count == 0 ? null : keys, measures));
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_observed is { } observed) observed.DataChanged -= OnDataChanged;
            _observed = null;
        }
    }

    // The engine of the moment, followed for its saved catch-ups: a reloaded plugin has a new one.
    private IStatisticsPublication? Resolve()
    {
        var (publication, service) = _resolve();
        lock (_gate)
        {
            if (!ReferenceEquals(service, _observed))
            {
                if (_observed is { } old) old.DataChanged -= OnDataChanged;
                _observed = service;
                if (service is not null) service.DataChanged += OnDataChanged;
            }
        }

        return publication;
    }

    private void OnDataChanged(StatisticsDataChange change) => Changed?.Invoke();

    // Names are kept as the store has them; one that is not a short name without control characters is not published.
    private static string? Bounded(string value) => string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(char.IsControl) ? null : value;

    // Another key (an effort, a tool) as the server takes it: at most 128 characters, without control characters.
    private static string Key(string value)
    {
        var kept = value.Length > 128 ? value[..128] : value;
        return kept.Any(char.IsControl) ? new string([.. kept.Select(static c => char.IsControl(c) ? ' ' : c)]) : kept;
    }

    /// <summary>Gives the quarter hours from which the facts of a server link are published: those of the day the link was made, in UTC.</summary>
    /// <param name="linkedAt">When the link was made.</param>
    /// <returns>The first quarter hour.</returns>
    public static long FirstQuarter(DateTimeOffset linkedAt) => FleetStatisticsFamilies.QuarterOf(new DateTimeOffset(linkedAt.UtcDateTime.Date, TimeSpan.Zero));
}
