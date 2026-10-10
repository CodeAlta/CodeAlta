using CodeAlta.Plugin.Statistics.Facts;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>Random facts for the tests of the store and of the queries: valid keys, small numbers, every family.</summary>
internal static class SyntheticFacts
{
    private static readonly string[] Providers = ["codex", "claude", "copilot"];
    private static readonly string[] Models = ["gpt-6.1-sol", "claude-opus-5-5", "gpt-5-mini", "sonnet-5"];
    private static readonly string[] Efforts = ["", "low", "high"];
    private static readonly string[] Tools = ["shell", "Files:read_file", "Search:grep", "Files:apply_patch", "Alta:alta"];

    /// <summary>The first quarter of 2026-03-20 UTC: a few days before the clocks of Europe and North America change.</summary>
    public static int SpringStart => QuarterHour.Of(new DateTimeOffset(2026, 3, 20, 0, 0, 0, TimeSpan.Zero)).Index;

    public static FactBatch Batch(Random random, string sessionId, int firstQuarter, int quarterSpan, int entries)
    {
        var batch = new FactBatch(sessionId);
        for (var index = 0; index < entries; index++)
        {
            var quarter = new QuarterHour(firstQuarter + random.Next(quarterSpan));
            var provider = Pick(random, Providers);
            var model = Pick(random, Models);
            var effort = Pick(random, Efforts);
            var activity = batch.ActivityFor(new ActivityKey(quarter, provider, model, effort));
            activity.ActiveMs += random.Next(1, 900_000);
            activity.RunsStarted += random.Next(0, 3);
            activity.RunsCompleted += random.Next(0, 3);
            activity.RunsFailed += random.Next(0, 2);
            activity.RunsInterrupted += random.Next(0, 2);
            activity.Errors += random.Next(0, 2);
            activity.Compactions += random.Next(0, 2);
            activity.CompactionTokensBefore += random.Next(0, 200_000);
            activity.CompactionTokensAfter += random.Next(0, 20_000);

            var usage = batch.UsageFor(new UsageKey(quarter, provider, model, effort, "default", random.Next(10) == 0 ? UsagePurpose.Compaction : UsagePurpose.Turn));
            var input = random.Next(1_000, 100_000);
            usage.Requests += random.Next(1, 6);
            usage.InputTokens += input;
            usage.FreshInputTokens += input / 2;
            usage.CacheReadTokens += input / 3;
            usage.CacheWriteTokens += input - (input / 2) - (input / 3);
            usage.OutputTokens += random.Next(10, 5_000);
            usage.ReasoningTokens += random.Next(0, 500);
            usage.ProviderDurationMs += random.Next(100, 20_000);
            usage.ContextSamples += 1;
            usage.ContextTokensSum += input;
            usage.ContextLimitSum += 200_000;
            usage.ContextFillPpmSum += input * 5L;
            usage.ContextFillPpmMax = Math.Max(usage.ContextFillPpmMax, random.Next(1, 1_000_000));

            if (random.Next(3) == 0)
            {
                var cost = batch.CostFor(new CostKey(quarter, provider, model, random.Next(4) == 0 ? CostUnits.Credits : CostUnits.Usd));
                cost.Total += random.Next(1, 5_000) / 1000d;
                cost.Records += 1;
            }

            var tool = batch.ToolFor(new ToolKey(quarter, provider, (ToolKind)random.Next(0, 8), Pick(random, Tools)));
            tool.Calls += random.Next(1, 20);
            tool.Failures += random.Next(0, 3);
            tool.Canceled += random.Next(0, 2);
            tool.DurationCount += 3;
            tool.DurationMsTotal += random.Next(100, 100_000);
            tool.DurationMsMax = Math.Max(tool.DurationMsMax, random.Next(1, 60_000));
            tool.BytesIn += random.Next(0, 10_000);
            tool.BytesOut += random.Next(0, 100_000);
            tool.FilesRead += random.Next(0, 4);
            tool.FilesChanged += random.Next(0, 4);
            tool.LinesAdded += random.Next(0, 300);
            tool.LinesRemoved += random.Next(0, 300);

            var content = batch.ContentFor(new ContentKey(quarter, (ContentKind)random.Next(0, 5), (PromptSender)random.Next(0, 6), (PromptKind)random.Next(0, 5)));
            content.Count += random.Next(1, 4);
            content.Chars += random.Next(0, 5_000);
            content.Words += random.Next(0, 800);
            content.Files += random.Next(0, 2);
            content.Images += random.Next(0, 2);

            batch.CountDetail(new DetailKey(quarter, (DetailList)random.Next(0, 8), Pick(random, ["git", "dotnet", "npm", "cs", "ts"])), random.Next(1, 5));
            batch.Observe(quarter, (HistogramMeasure)random.Next(0, 8), Pick(random, ["", "shell", "gpt-6.1-sol"]), random.Next(1, 3_000_000));
            batch.Offer(quarter, (ExtremeMeasure)random.Next(0, 5), Pick(random, ["", "shell", "gpt-6.1-sol"]), random.Next(1, 1_000_000), "run-" + random.Next(50), quarter.Start.AddSeconds(random.Next(0, 900)));
        }

        for (var index = 0; index < Math.Max(1, entries / 20); index++)
        {
            var start = new QuarterHour(firstQuarter + random.Next(quarterSpan)).Start.AddSeconds(random.Next(0, 900));
            batch.Runs["run-" + index + "-" + sessionId] = new RunRow
            {
                SessionId = sessionId,
                RunId = "run-" + index + "-" + sessionId,
                Start = start,
                End = start.AddSeconds(random.Next(1, 3000)),
                Outcome = RunOutcome.Completed,
                Sender = PromptSender.You,
                PromptKind = PromptKind.NewTurn,
                Provider = Pick(random, Providers),
                Model = Pick(random, Models),
                Requests = random.Next(1, 20),
            };
        }

        batch.Session = new SessionRow
        {
            SessionId = sessionId,
            ProjectRef = "project-" + random.Next(3),
            SessionKind = "ProjectSession",
            Title = "A title",
            Provider = Pick(random, Providers),
            FirstRecord = new QuarterHour(firstQuarter).Start,
            LastRecord = new QuarterHour(firstQuarter + quarterSpan).Start,
        };
        return batch;
    }

    public static string Pick(Random random, string[] values) => values[random.Next(values.Length)];
}
