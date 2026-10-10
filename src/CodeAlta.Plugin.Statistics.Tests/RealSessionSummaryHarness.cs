using CodeAlta.Plugin.Statistics.Facts;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// Prints the totals of the facts of some real sessions, to compare them with <c>alta session metrics</c>. Skipped unless
/// <c>CODEALTA_STATS_SESSIONS</c> names a sessions folder and <c>CODEALTA_STATS_SESSION_IDS</c> lists session ids (comma
/// separated); it prints counts only.
/// </summary>
[TestClass]
public sealed class RealSessionSummaryHarness
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SummarizeSomeSessions()
    {
        var root = Environment.GetEnvironmentVariable("CODEALTA_STATS_SESSIONS");
        var ids = Environment.GetEnvironmentVariable("CODEALTA_STATS_SESSION_IDS");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(ids) || !Directory.Exists(root))
        {
            Assert.Inconclusive("Set CODEALTA_STATS_SESSIONS and CODEALTA_STATS_SESSION_IDS to run this harness.");
        }

        using var catchUp = new SessionCatchUp();
        foreach (var id in ids!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = Directory.EnumerateFiles(root!, id + ".jsonl", SearchOption.AllDirectories).FirstOrDefault();
            if (path is null)
            {
                TestContext.WriteLine($"{id}: not found");
                continue;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var batch = catchUp.CatchUp(id, stream, null, long.MaxValue, CancellationToken.None).Batch;
            var turn = batch.Usage.Where(static pair => pair.Key.Purpose == UsagePurpose.Turn).Select(static pair => pair.Value).ToArray();
            var rows = batch.Runs.Values.OrderBy(static row => row.Start).ToArray();
            TestContext.WriteLine(
                $"{id}: runs={rows.Length} (completed {rows.Count(static r => r.Outcome == RunOutcome.Completed)}, failed {rows.Count(static r => r.Outcome == RunOutcome.Failed)}, interrupted {rows.Count(static r => r.Outcome == RunOutcome.Interrupted)}, open {rows.Count(static r => r.Outcome == RunOutcome.Running)}) "
                + $"activeSeconds={batch.Activity.Sum(static pair => pair.Value.ActiveMs) / 1000.0:F1} "
                + $"firstRunStart={rows.FirstOrDefault()?.Start:O} lastRunEnd={rows.LastOrDefault()?.End:O} "
                + $"firstRecord={batch.Session?.FirstRecord:O} lastRecord={batch.Session?.LastRecord:O} "
                + $"toolCallsStarted={batch.Tools.Sum(static pair => pair.Value.Calls)} requests={turn.Sum(static m => m.Requests)} "
                + $"input={turn.Sum(static m => m.InputTokens)} fresh={turn.Sum(static m => m.FreshInputTokens)} cacheRead={turn.Sum(static m => m.CacheReadTokens)} cacheWrite={turn.Sum(static m => m.CacheWriteTokens)} "
                + $"output={turn.Sum(static m => m.OutputTokens)} reasoning={turn.Sum(static m => m.ReasoningTokens)} "
                + $"prompts={batch.Content.Where(static pair => pair.Key.Kind == ContentKind.Prompt).Sum(static pair => pair.Value.Count)} answers={batch.Content.Where(static pair => pair.Key.Kind == ContentKind.Answer).Sum(static pair => pair.Value.Count)} "
                + $"cost={string.Join('+', batch.Cost.Select(static pair => $"{pair.Value.Total:F4} {pair.Key.Unit}"))}");
        }
    }
}
