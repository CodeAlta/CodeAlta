using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// Writes one sample of every result of the questions as the plugin serializes it, and compares it with
/// <c>Golden/results.json</c>. The statistics canvas (<c>src/CodeAlta/frontend/src/statistics/golden.test.ts</c>) reads the same file and
/// checks that its TypeScript mirror of each type has exactly these properties, so a change of a shape fails a test on either side.
/// Set <c>CODEALTA_UPDATE_GOLDEN=1</c> to rewrite the file after a change on purpose.
/// </summary>
[TestClass]
public sealed class StatisticsJsonGoldenTests
{
    private static string GoldenPath([CallerFilePath] string? source = null) => Path.Combine(Path.GetDirectoryName(source)!, "Golden", "results.json");

    private static readonly QueryHeader Header = new("30d", "2026-09-10", "2026-10-09", "day", "Europe/Paris", "2026-08-11", "2026-09-09",
        new StatisticsCoverage(false, "reading", "2026-09-20"), ["origin"], ["space-membership-is-current"]);

    private static readonly IReadOnlyList<BucketInfo> Buckets = [new(0, "2026-10-08T00:00", "10-08"), new(1, "2026-10-09T00:00", "10-09")];

    private static readonly ToolRow Tool = new("shell", "shell_command", 12, 2, 2d / 12, 34567, 2400, 16410, 81269, 1200, 98000, [3, 9]);

    private static readonly SessionEntry Session = new("0123abcd-0000-0000-0000-000000000001", "Fix quoted keys", "proj-1", "CodeAlta", "claude", "claude-opus-5-5", "0123abcd-0000-0000-0000-000000000000",
        3, 123456, 456789, 21, [new CostAmount("usd", 1.25)], 1, "2026-10-09T12:40:00Z", false);

    private static readonly SummaryTile Tile = new("runs", "count", 12, 10, 0.2, [5, 7]);

    private static readonly ModelRow Model = new("claude", "claude-opus-5-5", 40, 1000, 400, 500, 100, 200, 50, 0.5, 34567, 0.4, 0.9, [new CostAmount("usd", 1.25)], [1, 2]);

    private static IReadOnlyDictionary<string, string> Samples()
    {
        var samples = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["SeriesResult"] = StatisticsJson.Serialize(new SeriesResult(Header, "tokens", "tokens", "provider", Buckets,
                [new SeriesLine("claude", "claude", [10, 20], [8, 9], 30, 17), new SeriesLine("other", "other", [1, 2], null, 3, null)])),
            ["SummaryResult"] = StatisticsJson.Serialize(new SummaryResult(Header, Buckets, [Tile, Tile with { Previous = null, Change = null }], [new SummaryTile("usd", "usd", 3.5, null, null, [1, 2.5])])),
            ["TopResult"] = StatisticsJson.Serialize(new TopResult(Header, "tools", "calls", "count", Buckets,
                [new RankedRow("shell_command", "shell_command", "shell", 12, 0.4, 0, 34567, 12, 0, 2, [3, 9]), new RankedRow("a/b", "b", null, 5, 0.1, 100, 0, 0, 4, null, [1])], 9, true)),
            ["DistributionResult"] = StatisticsJson.Serialize(new DistributionResult(Header, "run-duration", null, "ms", 10, 90463, 387472,
                [new DistributionStep(1000, 1190, 4), new DistributionStep(1190, null, 6)])),
            ["CalendarResult"] = StatisticsJson.Serialize(new CalendarResult(Header, [new CalendarDay("2026-10-09", 123456, 4, 3)], 123456)),
            ["WeekHourResult"] = StatisticsJson.Serialize(new WeekHourResult(Header, ["Sunday", "Monday"], [[0, 1.5], [2, 3]], [[0, 1], [2, 3]])),
            ["SessionsResult"] = StatisticsJson.Serialize(new SessionsResult(Header, "recent", [Session, Session with { Title = null, Project = null, ProjectName = null, Provider = null, Model = null, ParentSessionId = null, Costs = [], LastActivity = null, Deleted = true }], 2, false)),
            ["ToolsResult"] = StatisticsJson.Serialize(new ToolsResult(Header, Buckets, [Tool, Tool with { P50Ms = null, P90Ms = null }], [new KeyValuePair<string, long>("shell", 12), new KeyValuePair<string, long>("files", 7)], 2, false)),
            ["ModelsResult"] = StatisticsJson.Serialize(new ModelsResult(Header, Buckets, [Model, Model with { AverageContextFill = null, Costs = [] }], [new EffortRow("claude", "claude-opus-5-5", "high", 10, 5000, 0.4)], 2, false)),
            ["ProjectsResult"] = StatisticsJson.Serialize(new ProjectsResult(Header, Buckets, [new ProjectRow("proj-1", "CodeAlta", 4, 12, 123456, 456789, 21, [new CostAmount("AI credits", 340)], [1, 2])], 1, false)),
            ["RecordsResult"] = StatisticsJson.Serialize(new RecordsResult(Header, [new RecordEntry("longestRun", "", 6028868, "ms", "0123abcd", "run-1", "2026-07-15T17:00:00Z"), new RecordEntry("busiestDay", "", 35285529, "ms", null, null, "2026-08-11")])),
            ["HealthResult"] = StatisticsJson.Serialize(new HealthResult(Header, Buckets, [1, 0], [0, 1], 12, 0.083, [Tool], [new KeyValuePair<string, long>("threshold", 3)], [1, 2], 172000, 58000, [new ContextFillRow("claude", "claude-opus-5-5", 0.4, 0.9, 40), new ContextFillRow("codex", "gpt-6.1", null, 0.2, 3)])),
            ["SessionDetailResult"] = StatisticsJson.Serialize(new SessionDetailResult(Header, Session, [Session], [Tile], [new RunEntry("run-1", "2026-10-09T12:00:00Z", 90463, "completed", "you", 12, 20, 1000, 200, "claude-opus-5-5")], [Model], [Tool])),
            ["DetailsResult"] = StatisticsJson.Serialize(new DetailsResult(Header, "shell-program", [new NameCount("git", 1944, 0.34)], 5718, 10, true)),
            ["RunsResult"] = StatisticsJson.Serialize(new RunsResult(Header, "recent", [new RunSample("0123abcd", "run-1", "2026-10-09T12:00:00Z", 90463, "completed", "you", "newturn", 250, 49, 12, 20, 2, 1000, 200, 500, 80, "claude", "claude-opus-5-5", "high")], 1, false)),
            ["StatisticsStatus"] = StatisticsJson.Serialize(new StatisticsStatus
            {
                State = HistoryState.StoppedHere, Reason = "extended", Choice = "days:90", FloorDay = 20260714, SessionsTotal = 906, SessionsDone = 312, BytesTotal = 10_840_000_000, BytesDone = 3_000_000_000,
                OldestDateReached = 20260714, CompleteFromDay = 20260714, BytesPerSecond = 480_000_000, EtaSeconds = 40, CurrentSessionId = "0123abcd", SkippedCount = 1,
                Skipped = [new SkippedSession("0123abcd", "The journal is damaged.")], PendingFlow = 2, Revision = 7, Error = null,
            }),
            ["StatisticsStatus.needsChoice"] = StatisticsJson.Serialize(new StatisticsStatus { State = HistoryState.NeedsChoice }),
            ["StatisticsRequest"] = StatisticsJson.Serialize(new StatisticsRequest
            {
                Period = "7d", Frequency = StatisticsFrequency.Week, Comparison = StatisticsComparison.PreviousPeriod, WeekStart = DayOfWeek.Monday, Limit = 5,
                Filter = new StatisticsFilter { Project = "CodeAlta", Origin = "you" },
            }),
        };
        return samples;
    }

    [TestMethod]
    public void TheCanvasAndThePluginAgreeOnTheShapesOfTheResults()
    {
        var path = GoldenPath();
        var text = new StringBuilder("{\n");
        var samples = Samples();
        var index = 0;
        foreach (var (name, json) in samples)
        {
            using var document = JsonDocument.Parse(json);
            text.Append("  ").Append(JsonSerializer.Serialize(name)).Append(": ").Append(document.RootElement.GetRawText()).Append(++index < samples.Count ? ",\n" : "\n");
        }

        text.Append("}\n");
        if (Environment.GetEnvironmentVariable("CODEALTA_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.ToString());
        }

        Assert.IsTrue(File.Exists(path), "Run the test with CODEALTA_UPDATE_GOLDEN=1 to write Golden/results.json.");
        Assert.AreEqual(text.ToString().ReplaceLineEndings("\n"), File.ReadAllText(path).ReplaceLineEndings("\n"), "A result changed its JSON: rewrite Golden/results.json and update the TypeScript types of the canvas.");
    }
}
