using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Query;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// A session whose header records a project the catalog does not have (the transient project of <c>altacli</c> in a folder that
/// was no project) counts under the project the catalog has in its folder, as CodeAlta lists it; a session of another project of
/// the catalog stays with that project.
/// </summary>
[TestClass]
public sealed class StatisticsAdoptedSessionsTests
{
    private const string AlphaFolder = @"C:\code\alpha";
    private const string Period = "2026-04-01..2026-09-30";

    [TestMethod]
    public async Task ASessionOfAnUnknownProject_CountsUnderTheProjectOfItsFolder_AndASessionOfAnotherProjectIsNotMoved()
    {
        await using var harness = await QueryHarness.CreateAsync("Europe/Paris");
        harness.Directory.Projects.AddRange(
        [
            new ProjectInfo("project-1", "beta", "Beta", []) { Path = @"C:\code\beta" },
            new ProjectInfo("project-2", "gamma", "Gamma", []) { Path = @"C:\code\gamma" },
        ]);
        await AddAsync(harness, "adopted", 91, "transient-1", AlphaFolder);
        await AddAsync(harness, "beta-in-alpha", 92, "project-1", AlphaFolder);
        await AddAsync(harness, "stray", 93, "transient-2", @"C:\elsewhere");

        double Naive(Func<FactBatch, bool> match) => harness.Naive(QueryHarness.Tokens, StatisticsFrequency.Year, new DateOnly(2026, 4, 1), new DateOnly(2026, 9, 30), DayOfWeek.Monday, match).Values.Sum();
        async Task<double> Total(string project) => (await harness.Queries.SeriesAsync(new StatisticsRequest { Period = Period, Filter = new StatisticsFilter { Project = project } }, "tokens")).Series.Sum(static line => line.Total);
        var recordedAlpha = Naive(static batch => batch.Session!.ProjectRef == "project-0");
        var adopted = Naive(static batch => batch.SessionId == "adopted");
        Assert.IsGreaterThan(0, adopted, "The adopted session has something to count.");

        // Before Alpha is a project of the catalog, the session counts under the project it records.
        Assert.AreEqual(adopted, await Total("transient-1"), 0.001);

        // Alpha is added for the folder: its numbers have the session from the next question on.
        harness.Directory.Projects.Insert(0, new ProjectInfo("project-0", "alpha", "Alpha", []) { Path = AlphaFolder });

        Assert.AreEqual(recordedAlpha + adopted, await Total("alpha"), 0.001, "The session altacli wrote in the folder of Alpha is Alpha's.");
        Assert.AreEqual(0, await Total("transient-1"), 0.001, "Nothing stays under the transient project.");
        Assert.AreEqual(Naive(static batch => batch.Session!.ProjectRef == "project-1"), await Total("beta"), 0.001,
            "A session that records another project of the catalog stays with it, whatever its folder.");
        Assert.AreEqual(Naive(static batch => batch.SessionId == "stray"), await Total("transient-2"), 0.001, "A folder that no project has adopts nothing.");

        var byProject = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = Period }, "tokens", "project");
        Assert.AreEqual(recordedAlpha + adopted, byProject.Series.Single(static line => line.Label == "Alpha").Total, 0.001);
        Assert.IsFalse(byProject.Series.Any(static line => line.Key == "transient-1"), "No line of the transient project.");

        var projects = await harness.Queries.ProjectsAsync(new StatisticsRequest { Period = Period });
        Assert.AreEqual(recordedAlpha + adopted, projects.Rows.Single(static row => row.Project == "project-0").Tokens, 0.001);

        var sessions = await harness.Queries.SessionsAsync(new StatisticsRequest { Period = Period, Limit = 500 });
        var row = sessions.Rows.Single(static entry => entry.SessionId == "adopted");
        Assert.AreEqual("project-0", row.Project);
        Assert.AreEqual("Alpha", row.ProjectName);
        Assert.AreEqual("project-1", sessions.Rows.Single(static entry => entry.SessionId == "beta-in-alpha").Project);
    }

    private static async Task AddAsync(QueryHarness harness, string sessionId, int seed, string project, string folder)
    {
        var batch = SyntheticFacts.Batch(new Random(seed), sessionId, SyntheticFacts.SpringStart + 100, 200 * 96, 150);
        batch.Session!.ProjectRef = project;
        batch.Session.FolderKey = SessionFolders.Key(folder);
        await harness.AddAsync(batch);
    }
}
