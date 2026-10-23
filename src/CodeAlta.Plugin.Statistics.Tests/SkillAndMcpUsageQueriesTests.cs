using CodeAlta.Plugin.Statistics.Query;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>The questions of the skills and the MCP tools, and the recent use of skills the prompt ranks them by.</summary>
[TestClass]
public sealed class SkillAndMcpUsageQueriesTests
{
    // The harness says it is 2026-10-09 12:00 UTC.
    private static readonly DateTimeOffset Yesterday = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastWeek = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Today = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);
    private const string Alpha = "project-alpha";
    private const string Beta = "project-beta";

    private static async Task<QueryHarness> HarnessAsync()
    {
        var harness = await QueryHarness.CreateAsync(sessions: 0);
        harness.Directory.Projects.AddRange([new ProjectInfo(Alpha, "alpha", "Alpha", []), new ProjectInfo(Beta, "beta", "Beta", ["work"])]);
        harness.Directory.Spaces.Add(new SpaceInfo("work", "Work", false));

        var alpha = new JournalBuilder("aaaaaaaa-0000-0000-0000-000000000001");
        alpha.Header(Yesterday, projectRef: Alpha, spaceId: "default")
            .ModelChanged(Yesterday, "r1", "codex", "m", "Low")
            .User(Yesterday.AddSeconds(1), "r1", "release it", skills: ["release-notes"])
            .ToolDone(Yesterday.AddSeconds(2), "r1", "a", "alta", argsJson: "{\"args\":[\"skill\",\"activate\",\"release-notes\"]}", resultText: JournalBuilder.ActivationResult("release-notes", "UserAlta"))
            .ToolDone(Yesterday.AddSeconds(3), "r1", "b", "alta", argsJson: "{\"args\":[\"skill\",\"activate\",\"release-notes\"]}", resultText: JournalBuilder.ActivationResult("release-notes", "UserAlta"))
            .ToolDone(Yesterday.AddSeconds(4), "r1", "c", "read_file", readFiles: ["/repo/alpha/.alta/skills/dotnet-test-fix/SKILL.md"])
            .ToolDone(Yesterday.AddSeconds(5), "r1", "d", "alta", argsJson: "{\"args\":[\"mcp\",\"tool\",\"call\",\"--server\",\"memory\",\"--tool\",\"echo\"]}")
            .ToolDone(Yesterday.AddSeconds(6), "r1", "e", "mcp__github__issue_read")
            .ToolDone(Yesterday.AddSeconds(7), "r1", "f", "mcp__github__issue_read")
            .Idle(Yesterday.AddSeconds(8), "r1");
        var beta = new JournalBuilder("bbbbbbbb-0000-0000-0000-000000000002");
        beta.Header(LastWeek, projectRef: Beta, spaceId: "work")
            .ModelChanged(LastWeek, "r1", "codex", "m", "Low")
            .ToolDone(LastWeek.AddSeconds(1), "r1", "a", "Skill", argsJson: "{\"skill\":\"release-notes\"}")
            .Idle(LastWeek.AddSeconds(2), "r1")
            .ModelChanged(Today, "r2", "codex", "m", "Low")
            .ToolDone(Today.AddSeconds(1), "r2", "b", "Skill", argsJson: "{\"skill\":\"release-notes\"}")
            .Idle(Today.AddSeconds(2), "r2");
        await harness.AddAsync(SessionFactsReducerTests.Reduce(alpha));
        await harness.AddAsync(SessionFactsReducerTests.Reduce(beta));
        return harness;
    }

    [TestMethod]
    public async Task TheSkills_AreRankedByTheirUses_WithWhereTheyComeFrom_HowAndWhereTheyWereUsed()
    {
        await using var harness = await HarnessAsync();

        var all = await harness.Queries.SkillsAsync(new StatisticsRequest { Period = "30d" });

        // The skill of the user and the skill of Claude Code that have the same name are two skills.
        Assert.AreEqual(3, all.Rows.Count);
        var release = all.Rows[0];
        Assert.AreEqual(("release-notes", "user"), (release.Name, release.Source));
        Assert.AreEqual(3, release.Uses);
        Assert.AreEqual(2, release.Activated);
        Assert.AreEqual(1, release.Attached);
        Assert.AreEqual(0, release.Read);
        Assert.AreEqual(1, release.Sessions);
        CollectionAssert.AreEqual(new[] { "Alpha" }, release.Projects.ToArray());
        CollectionAssert.AreEqual(new[] { "default" }, release.Spaces.ToArray());
        Assert.AreEqual(all.Buckets.Count, release.Spark.Count);
        Assert.AreEqual(3, release.Spark.Sum());
        var claude = all.Rows[1];
        Assert.AreEqual(("release-notes", "claude-code", 2L, 2L), (claude.Name, claude.Source, claude.Uses, claude.Activated));
        Assert.AreEqual("2026-10-09T09:00Z", claude.LastUsed);
        CollectionAssert.AreEqual(new[] { "Beta" }, claude.Projects.ToArray());
        CollectionAssert.AreEqual(new[] { "work" }, claude.Spaces.ToArray());
        var read = all.Rows[2];
        Assert.AreEqual(("dotnet-test-fix", "project", 1L, 1L, 1), (read.Name, read.Source, read.Uses, read.Read, read.Sessions));
        Assert.AreEqual(6, all.Total);

        var alpha = await harness.Queries.SkillsAsync(new StatisticsRequest { Period = "30d", Filter = new StatisticsFilter { Project = "alpha" } });
        Assert.AreEqual(3, alpha.Rows.Single(static row => row.Name == "release-notes").Uses);
        var work = await harness.Queries.SkillsAsync(new StatisticsRequest { Period = "30d", Filter = new StatisticsFilter { Space = "work" } });
        Assert.AreEqual(("claude-code", 2L), (work.Rows.Single().Source, work.Rows.Single().Uses));
        var limited = await harness.Queries.SkillsAsync(new StatisticsRequest { Period = "30d", Limit = 1 });
        Assert.IsTrue(limited.Truncated);
        Assert.AreEqual(3, limited.TotalRows);
        var ignored = await harness.Queries.SkillsAsync(new StatisticsRequest { Period = "30d", Filter = new StatisticsFilter { Provider = "codex" } });
        CollectionAssert.Contains(ignored.Query.IgnoredFilters.ToArray(), "provider");
    }

    [TestMethod]
    public async Task ASkillOfAnMcpServer_IsListedUnderThatServer()
    {
        await using var harness = await QueryHarness.CreateAsync(sessions: 0);
        var journal = new JournalBuilder("cccccccc-0000-0000-0000-000000000003");
        journal.Header(Yesterday, projectRef: Alpha, spaceId: "default")
            .ModelChanged(Yesterday, "r1", "codex", "m", "Low")
            .ToolDone(Yesterday.AddSeconds(1), "r1", "a", "alta", argsJson: "{\"args\":[\"mcp\",\"skill\",\"activate\",\"--server\",\"acme\",\"skill://acme/review/code-review/SKILL.md\"]}")
            .ToolDone(Yesterday.AddSeconds(2), "r1", "b", "mcp__codealta__alta", argsJson: "{\"args\":[\"mcp\",\"skill\",\"activate\",\"--server=acme\",\"code-review\"]}")
            .Idle(Yesterday.AddSeconds(3), "r1");
        await harness.AddAsync(SessionFactsReducerTests.Reduce(journal));

        var result = await harness.Queries.SkillsAsync(new StatisticsRequest { Period = "30d" });

        var row = result.Rows.Single();
        Assert.AreEqual(("code-review", "mcp:acme", 2L, 2L), (row.Name, row.Source, row.Uses, row.Activated));
    }

    [TestMethod]
    public async Task TheMcpTools_CountTheCallsThroughAlta_AndAddUpTheirServers()
    {
        await using var harness = await HarnessAsync();

        var result = await harness.Queries.McpToolsAsync(new StatisticsRequest { Period = "30d" });

        Assert.AreEqual(2, result.Rows.Count);
        Assert.AreEqual(("github", "issue_read", 2L, 1), (result.Rows[0].Server, result.Rows[0].Tool, result.Rows[0].Calls, result.Rows[0].Sessions));
        Assert.AreEqual(("memory", "echo", 1L), (result.Rows[1].Server, result.Rows[1].Tool, result.Rows[1].Calls));
        CollectionAssert.AreEqual(new[] { "github", "memory" }, result.Servers.Select(static server => server.Name).ToArray());
        Assert.AreEqual(3, result.Total);
        Assert.AreEqual(1d, result.Servers.Sum(static server => server.Share), 1e-9);
        var json = StatisticsJson.Serialize(result);
        StringAssert.Contains(json, "\"server\":\"github\"");
    }

    [TestMethod]
    public async Task TheRecentUseOfSkills_DecaysWithAge_AndLeavesTodayOut()
    {
        await using var harness = await HarnessAsync();
        var midnight = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

        var scores = await harness.Queries.SkillUsageScoresAsync(midnight, TimeSpan.FromDays(28), TimeSpan.FromDays(7));
        var again = await harness.Queries.SkillUsageScoresAsync(midnight, TimeSpan.FromDays(28), TimeSpan.FromDays(7));

        CollectionAssert.AreEqual(scores.ToArray(), again.ToArray());
        // The score is of the name of a skill, whatever its source: the prompt lists names.
        var alpha = scores.Single(static score => score is { ProjectRef: Alpha, Name: "release-notes" });
        Assert.AreEqual("default", alpha.SpaceId);
        Assert.AreEqual(3, alpha.Uses);
        // Three uses of yesterday, 15 hours old.
        Assert.AreEqual(3 * Math.Pow(0.5, (15 - 0.125) / 24 / 7), alpha.Score, 1e-3);
        // The use of today is not counted; the one of last week weighs about half.
        var beta = scores.Single(static score => score is { ProjectRef: Beta, Name: "release-notes" });
        Assert.AreEqual(("work", 1L), (beta.SpaceId, beta.Uses));
        Assert.AreEqual(Math.Pow(0.5, (7 * 24 + 15 - 0.125) / 24 / 7), beta.Score, 1e-3);
        Assert.AreEqual(1, scores.Single(static score => score.Name == "dotnet-test-fix").Uses);
        // Out of the window, nothing.
        Assert.AreEqual(0, (await harness.Queries.SkillUsageScoresAsync(midnight.AddDays(60), TimeSpan.FromDays(28), TimeSpan.FromDays(7))).Count);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => harness.Queries.SkillUsageScoresAsync(midnight, TimeSpan.Zero, TimeSpan.FromDays(7)));
    }
}
