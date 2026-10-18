using System.Collections.Concurrent;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugin.Statistics;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;

namespace CodeAlta.Desktop;

/// <summary>
/// The skills the prompt of a session lists first: those used most in the last days, from the Statistics plugin. The ranking is
/// made so that provider prompt caches survive:
/// <list type="bullet">
/// <item>it is computed once per local day, from the uses of the days before today, so that a restart in the same day gives the
/// same ranking and the uses of today do not move it;</item>
/// <item>a session keeps the first ranking it got for the life of the process, also across midnight;</item>
/// <item>the same scores always give the same list: sorted by score, then by name.</item>
/// </list>
/// A ranking computed while the history is still read (the first start of a version that reads every journal again) is
/// provisional: it is computed again at most every ten minutes until the history is read, for the sessions that start then.
/// </summary>
internal sealed class DesktopSkillUsage
{
    /// <summary>How far back the uses are read.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(28);

    /// <summary>The age at which a use counts half: an exponential moving average over about a week.</summary>
    public static readonly TimeSpan HalfLife = TimeSpan.FromDays(7);

    /// <summary>How often a provisional ranking is computed again.</summary>
    public static readonly TimeSpan ProvisionalRefresh = TimeSpan.FromMinutes(10);

    /// <summary>How long the build of the instructions waits for the scores.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The score a skill needs to be preferred: about one use in the last days.</summary>
    public const double MinimumScore = 1.0;

    /// <summary>What a use in another project of the same space weighs beside a use in the project of the session.</summary>
    public const double OtherProjectWeight = 0.25;

    private readonly Func<DateTimeOffset, CancellationToken, ValueTask<IReadOnlyList<SkillUsageScore>>?> _readScores;
    private readonly Func<bool> _historySettled;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _sessions = new(StringComparer.Ordinal);
    private Snapshot? _snapshot;

    /// <summary>Ranks the skills from the Statistics plugin of a host that is created later.</summary>
    /// <param name="host">The host once it exists; null before.</param>
    public DesktopSkillUsage(Func<CodeAltaHost?> host)
        : this(
            (end, token) => Statistics(host())?.Queries.SkillUsageScoresAsync(end, Window, HalfLife, token),
            () => Statistics(host())?.Status.State is HistoryState.Done or HistoryState.StoppedHere or HistoryState.Paused,
            TimeProvider.System,
            TimeZoneInfo.Local)
    {
    }

    /// <summary>Ranks the skills from scores read by a function.</summary>
    /// <param name="readScores">Reads the scores of the uses before a time; null when there are no statistics.</param>
    /// <param name="historySettled">Tells whether the history is read, so that the ranking is not provisional.</param>
    /// <param name="time">The clock.</param>
    /// <param name="zone">The time zone of the days.</param>
    internal DesktopSkillUsage(
        Func<DateTimeOffset, CancellationToken, ValueTask<IReadOnlyList<SkillUsageScore>>?> readScores,
        Func<bool> historySettled,
        TimeProvider time,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(readScores);
        ArgumentNullException.ThrowIfNull(historySettled);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(zone);
        _readScores = readScores;
        _historySettled = historySettled;
        _time = time;
        _zone = zone;
    }

    /// <summary>The skills a session prefers, the most used first; the same for a session as long as the process runs.</summary>
    /// <param name="request">The session, its project and its space.</param>
    /// <returns>The names of the skills, at most <see cref="AgentInstructionTemplateProvider.MaxPreferredSkills"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public IReadOnlyList<string> PreferredFor(PreferredSkillsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionId.Length > 0 && _sessions.TryGetValue(request.SessionId, out var kept))
        {
            return kept;
        }

        var ranking = Rank(Current().Scores, request.Project?.Id, request.SpaceId);
        return request.SessionId.Length > 0 ? _sessions.GetOrAdd(request.SessionId, ranking) : ranking;
    }

    /// <summary>Ranks the skills for a project and a space: the uses in the project count fully, those of the other sessions of the space a quarter.</summary>
    /// <param name="scores">The scores per project and space.</param>
    /// <param name="projectId">The project of the session; null for a session of no project.</param>
    /// <param name="spaceId">The space of the session.</param>
    /// <returns>The names, the best first.</returns>
    internal static IReadOnlyList<string> Rank(IReadOnlyList<SkillUsageScore> scores, string? projectId, string spaceId)
        => [.. Totals(scores, projectId, spaceId).Where(static pair => pair.Value >= MinimumScore)
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(AgentInstructionTemplateProvider.MaxPreferredSkills)
            .Select(static pair => pair.Key)];

    /// <summary>The recent use of every skill for a project and a space, weighted as the ranking weighs it, for search.</summary>
    /// <param name="projectId">The project; null for none.</param>
    /// <param name="spaceId">The space.</param>
    /// <returns>The scores by name of skill.</returns>
    public IReadOnlyDictionary<string, double> ScoresFor(string? projectId, string spaceId) => Totals(Current().Scores, projectId, spaceId);

    private static Dictionary<string, double> Totals(IReadOnlyList<SkillUsageScore> scores, string? projectId, string spaceId)
    {
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var score in scores)
        {
            if (!string.Equals(score.SpaceId ?? "default", spaceId, StringComparison.Ordinal))
            {
                continue;
            }

            var weight = string.Equals(score.ProjectRef, projectId, StringComparison.OrdinalIgnoreCase) ? 1 : OtherProjectWeight;
            totals[score.Name] = totals.GetValueOrDefault(score.Name) + weight * score.Score;
        }

        return totals;
    }

    // The scores of today: read once a day, and again every ten minutes while they are provisional.
    private Snapshot Current()
    {
        var now = _time.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _zone).DateTime);
        lock (_gate)
        {
            if (_snapshot is { } snapshot && snapshot.Day == today && (!snapshot.Provisional || now - snapshot.ReadAt < ProvisionalRefresh))
            {
                return snapshot;
            }

            var settled = _historySettled();
            var scores = Read(StartOf(today));
            _snapshot = new Snapshot(today, now, scores ?? [], Provisional: scores is null || !settled);
            return _snapshot;
        }
    }

    private IReadOnlyList<SkillUsageScore>? Read(DateTimeOffset end)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ReadTimeout);
            var read = _readScores(end, timeout.Token);
            if (read is not { } pending)
            {
                return null;
            }

            var task = pending.AsTask();
            return task.Wait(ReadTimeout) ? task.Result : null;
        }
        catch (Exception exception) when (exception is AggregateException or OperationCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            // Statistics that cannot answer prefer nothing; the next provisional read tries again.
            return null;
        }
    }

    // The start of a local day, as an instant.
    private DateTimeOffset StartOf(DateOnly day)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, _zone.GetUtcOffset(midnight));
    }

    private static IStatisticsService? Statistics(CodeAltaHost? host)
        => host?.PluginRuntime.ActivePlugins.Select(static plugin => plugin.Instance).OfType<StatisticsPlugin>().FirstOrDefault()?.Statistics;

    private sealed record Snapshot(DateOnly Day, DateTimeOffset ReadAt, IReadOnlyList<SkillUsageScore> Scores, bool Provisional);
}
