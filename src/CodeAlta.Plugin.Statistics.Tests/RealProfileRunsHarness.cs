using CodeAlta.Plugin.Statistics.Facts;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// Counts, over a real profile, what the runs say about the sessions that run at once: how many runs there are, how many cannot
/// be placed in time (a record far from the others), how long the longest ones are, and the most sessions with a run going at the
/// same moment. Skipped unless <c>CODEALTA_STATS_SESSIONS</c> names a sessions folder; it prints counts only.
/// </summary>
[TestClass]
public sealed class RealProfileRunsHarness
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TheRunsOfAProfile_SayHowManySessionsRanAtOnce()
    {
        var root = Environment.GetEnvironmentVariable("CODEALTA_STATS_SESSIONS");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Assert.Inconclusive("Set CODEALTA_STATS_SESSIONS to a sessions folder to run the real-profile harness.");
        }

        var runs = new List<RunRow>();
        var parents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var catchUp = new SessionCatchUp();
        foreach (var path in Directory.EnumerateFiles(root!, "*.jsonl", SearchOption.AllDirectories)
                     .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}traces{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var result = catchUp.CatchUp(id, stream, null, long.MaxValue, CancellationToken.None);
            runs.AddRange(result.Batch.Runs.Values);
            parents[id] = result.Batch.Session?.ParentSessionId;
        }

        var placed = runs.Where(static run => run.SkippedMs == 0).ToList();
        var moments = new List<(DateTimeOffset At, int Change, string Session)>();
        foreach (var run in placed)
        {
            moments.Add((run.Start, 1, run.SessionId));
            moments.Add((run.End > run.Start ? run.End : run.Start.AddMilliseconds(1), -1, run.SessionId));
        }

        moments.Sort(static (left, right) => left.At != right.At ? left.At.CompareTo(right.At) : left.Change.CompareTo(right.Change));
        var going = new Dictionary<string, int>(StringComparer.Ordinal);
        var most = 0;
        foreach (var (_, change, session) in moments)
        {
            var count = going.GetValueOrDefault(session) + change;
            if (count > 0)
            {
                going[session] = count;
            }
            else
            {
                going.Remove(session);
            }

            most = Math.Max(most, going.Count);
        }

        var withParent = parents.Count(static pair => !string.IsNullOrEmpty(pair.Value));
        var unknownParent = parents.Count(pair => !string.IsNullOrEmpty(pair.Value) && !parents.ContainsKey(pair.Value!));
        TestContext.WriteLine($"sessions: {parents.Count}; with a parent: {withParent}; whose parent has no journal: {unknownParent}");
        TestContext.WriteLine($"runs: {runs.Count}; not placed (a record more than seven days from the others): {runs.Count - placed.Count}; still open: {runs.Count(static run => run.Outcome == RunOutcome.Running)}");
        TestContext.WriteLine($"runs longer than an hour: {placed.Count(static run => run.Duration > TimeSpan.FromHours(1))}; than a day: {placed.Count(static run => run.Duration > TimeSpan.FromDays(1))}");
        TestContext.WriteLine($"most sessions with a run going at the same moment: {most}");
        Assert.IsTrue(runs.Count > 0);
    }
}
