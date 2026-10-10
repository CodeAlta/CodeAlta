using System.Globalization;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>A directory of projects and spaces the tests set by hand.</summary>
internal sealed class FakeProjectDirectory : IProjectDirectory
{
    public List<ProjectInfo> Projects { get; } = [];

    public List<SpaceInfo> Spaces { get; } = [new SpaceInfo("default", "Default", true)];

    public ValueTask<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<ProjectInfo>>(Projects);

    public ValueTask<IReadOnlyList<SpaceInfo>> ListSpacesAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<SpaceInfo>>(Spaces);
}

/// <summary>A store filled with random facts of several sessions, the queries over it, and the facts kept for a naive recomputation.</summary>
internal sealed class QueryHarness : IAsyncDisposable
{
    private QueryHarness(StoreHarness store, ManualTime time)
    {
        Store = store;
        Time = time;
    }

    public StoreHarness Store { get; }

    public ManualTime Time { get; }

    public FakeProjectDirectory Directory { get; } = new();

    public StatisticsQueries Queries { get; private set; } = null!;

    public List<FactBatch> Batches { get; } = [];

    public static async Task<QueryHarness> CreateAsync(string zoneId = "UTC", int sessions = 8, int seed = 5, int entries = 150, bool chosen = true, DateTimeOffset? now = null)
    {
        var store = await StoreHarness.CreateAsync(StoreHarness.Zone(zoneId));
        var harness = new QueryHarness(store, new ManualTime(now ?? EngineHarness.Now));
        harness.Queries = new StatisticsQueries(store.Store, harness.Directory, harness.Time);
        var random = new Random(seed);
        for (var session = 0; session < sessions; session++)
        {
            var batch = SyntheticFacts.Batch(random, "session-" + session, SyntheticFacts.SpringStart + (session * 300), 200 * 96, entries);
            // A session of a sub-agent for the odd ones: its parent is the session before.
            if (session % 3 == 2)
            {
                batch.Session!.ParentSessionId = "session-" + (session - 1);
            }

            harness.Batches.Add(batch);
            await store.Store.ApplyAsync(new ApplyRequest
            {
                SessionId = batch.SessionId,
                Batch = batch,
                Cursor = new JournalCursor(100, null, new SessionFactsState()),
                FileLength = 100,
                FileStampTicks = 1,
            });
        }

        if (chosen)
        {
            await store.Store.SetMetaAsync(new Dictionary<string, string?> { ["history.choice"] = "all", ["history.done"] = "1" });
        }

        return harness;
    }

    public ValueTask DisposeAsync() => Store.DisposeAsync();

    public TimeZoneInfo TimeZone => Store.TimeZone;

    public DateTime Local(QuarterHour quarter) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UnixEpoch.AddMilliseconds(quarter.Index * QuarterHour.Milliseconds), TimeZone);

    /// <summary>The key of the bucket of a local time at a frequency, in the form the results give their buckets.</summary>
    public static string BucketKey(DateTime local, StatisticsFrequency frequency, DayOfWeek weekStart)
    {
        switch (frequency)
        {
            case StatisticsFrequency.Hour:
                return local.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
            case StatisticsFrequency.Day:
                return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case StatisticsFrequency.Week:
                var date = DateOnly.FromDateTime(local);
                return date.AddDays(-(((int)date.DayOfWeek - (int)weekStart + 7) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case StatisticsFrequency.Month:
                return local.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            default:
                return local.ToString("yyyy", CultureInfo.InvariantCulture);
        }
    }

    public static string KeyOfBucket(BucketInfo bucket, StatisticsFrequency frequency)
        => frequency switch
        {
            StatisticsFrequency.Hour => bucket.Start[..13],
            StatisticsFrequency.Day or StatisticsFrequency.Week => bucket.Start[..10],
            StatisticsFrequency.Month => bucket.Start[..7],
            _ => bucket.Start[..4],
        };

    /// <summary>The sum of a measure of the facts per bucket, computed here from the batches, with no help of the store.</summary>
    public Dictionary<string, double> Naive(
        Func<FactBatch, IEnumerable<(QuarterHour Quarter, double Value)>> values,
        StatisticsFrequency frequency,
        DateOnly from,
        DateOnly to,
        DayOfWeek weekStart,
        Func<FactBatch, bool>? sessionFilter = null)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var batch in Batches.Where(batch => sessionFilter?.Invoke(batch) ?? true))
        {
            foreach (var (quarter, value) in values(batch))
            {
                var local = Local(quarter);
                var date = DateOnly.FromDateTime(local);
                if (date < from || date > to)
                {
                    continue;
                }

                var key = BucketKey(local, frequency, weekStart);
                result[key] = result.GetValueOrDefault(key) + value;
            }
        }

        return result;
    }

    public static IEnumerable<(QuarterHour, double)> ActiveMs(FactBatch batch) => batch.Activity.Select(static pair => (pair.Key.Quarter, (double)pair.Value.ActiveMs));

    public static IEnumerable<(QuarterHour, double)> Runs(FactBatch batch) => batch.Activity.Select(static pair => (pair.Key.Quarter, (double)pair.Value.RunsStarted));

    public static IEnumerable<(QuarterHour, double)> Tokens(FactBatch batch) => batch.Usage.Select(static pair => (pair.Key.Quarter, (double)(pair.Value.InputTokens + pair.Value.OutputTokens)));

    public static IEnumerable<(QuarterHour, double)> ToolCalls(FactBatch batch) => batch.Tools.Select(static pair => (pair.Key.Quarter, (double)pair.Value.Calls));

    public static IEnumerable<(QuarterHour, double)> YourPrompts(FactBatch batch)
        => batch.Content.Where(static pair => pair.Key.Kind == ContentKind.Prompt && pair.Key.Sender == PromptSender.You).Select(static pair => (pair.Key.Quarter, (double)pair.Value.Count));

    public static IEnumerable<(QuarterHour, double)> Cost(FactBatch batch, string unit)
        => batch.Cost.Where(pair => pair.Key.Unit == unit).Select(static pair => (pair.Key.Quarter, Math.Round(pair.Value.Total * 1e6, MidpointRounding.AwayFromZero) / 1e6));

    /// <summary>The values of a result: one for each bucket, from the naive sums.</summary>
    public static double[] Expected(IReadOnlyList<BucketInfo> buckets, Dictionary<string, double> naive, StatisticsFrequency frequency)
        => [.. buckets.Select(bucket => naive.GetValueOrDefault(KeyOfBucket(bucket, frequency)))];
}
