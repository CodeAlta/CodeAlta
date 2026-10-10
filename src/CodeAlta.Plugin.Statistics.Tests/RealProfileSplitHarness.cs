using CodeAlta.Plugin.Statistics.Facts;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// Cuts recorded journals at record boundaries and checks that reading them in two halves gives the facts of reading them at
/// once. Skipped unless <c>CODEALTA_STATS_SESSIONS</c> names a sessions folder; it prints counts only.
/// </summary>
[TestClass]
public sealed class RealProfileSplitHarness
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void RecordedJournals_GiveTheSameFactsWhereverTheyAreCut()
    {
        var root = Environment.GetEnvironmentVariable("CODEALTA_STATS_SESSIONS");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Assert.Inconclusive("Set CODEALTA_STATS_SESSIONS to a sessions folder to run the real-profile harness.");
        }

        var candidates = Directory.EnumerateFiles(root!, "*.jsonl", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}traces{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(static path => new FileInfo(path))
            .Where(static info => info.Length is > 20_000 and < 30_000_000)
            .OrderBy(static info => info.Name, StringComparer.Ordinal)
            .ToArray();
        // About thirty sessions across the profile: the small ones are cut at every record boundary, the larger ones at some.
        var step = Math.Max(1, candidates.Length / 30);
        var checkedSessions = 0;
        var checkedSplits = 0;
        using var catchUp = new SessionCatchUp();
        for (var index = 0; index < candidates.Length; index += step)
        {
            var info = candidates[index];
            var bytes = File.ReadAllBytes(info.FullName);
            var id = Path.GetFileNameWithoutExtension(info.Name);
            var lineEnds = new List<int>();
            for (var position = 0; position < bytes.Length; position++)
            {
                if (bytes[position] == (byte)'\n')
                {
                    lineEnds.Add(position + 1);
                }
            }

            // The last line may be being written; cut only at ends of lines.
            var splits = lineEnds.Count <= 400
                ? lineEnds
                : Enumerable.Range(1, 12).Select(part => lineEnds[(int)((long)lineEnds.Count * part / 13)]).ToList();

            using var wholeStream = new MemoryStream(bytes);
            var whole = catchUp.CatchUp(id, wholeStream, null, long.MaxValue, CancellationToken.None);
            var wholeFacts = whole.Batch.ToCanonicalText();
            var wholeRuns = whole.Batch.ToCanonicalRunsText();
            var wholeState = whole.Cursor.State.ToJson();
            foreach (var split in splits)
            {
                using var headStream = new MemoryStream(bytes, 0, split);
                var first = catchUp.CatchUp(id, headStream, null, long.MaxValue, CancellationToken.None);
                using var fullStream = new MemoryStream(bytes);
                var second = catchUp.CatchUp(id, fullStream, first.Cursor, long.MaxValue, CancellationToken.None);
                var merged = new FactBatch(id);
                merged.Merge(first.Batch);
                merged.Merge(second.Batch);
                Assert.AreEqual(wholeFacts, merged.ToCanonicalText(), $"facts of {id} cut at byte {split}");
                Assert.AreEqual(wholeRuns, merged.ToCanonicalRunsText(), $"runs of {id} cut at byte {split}");
                Assert.AreEqual(wholeState, second.Cursor.State.ToJson(), $"state of {id} cut at byte {split}");
                checkedSplits++;
            }

            checkedSessions++;
        }

        TestContext.WriteLine($"sessions cut: {checkedSessions}; cuts checked: {checkedSplits}");
        Assert.IsTrue(checkedSessions > 0);
    }
}
