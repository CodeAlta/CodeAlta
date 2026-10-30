using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mobile.Tests;

[TestClass]
public sealed class MobileJobsTests
{
    [TestMethod]
    public async Task Jobs_KeepRunningCommandsAfterTheTurnEnds_AndRecentOutcomes()
    {
        var alta = new FakeAlta().Answer("session status a", FakeAlta.Status("a", "p", "work", running: false))
            .Answer("job list", Job("job-running", "a"), Job("job-recent", "a", "succeeded", DateTimeOffset.UtcNow),
                Job("job-old", "a", "failed", DateTimeOffset.UtcNow.AddMinutes(-11)), Job("job-other", "b"));
        var api = Api(alta);
        var result = await api.JobsAsync("a", default);
        CollectionAssert.AreEqual(new[] { "job-running", "job-recent" }, result!["jobs"]!.AsArray().Select(job => (string)job!["id"]!).ToArray());
        CollectionAssert.AreEqual(new[] { "job", "list", "--session", "a" }, alta.CallsOf("job list").Single());
    }

    [TestMethod]
    public async Task JobOutputAndStop_CheckSessionVisibilityAndJobOwnership()
    {
        var alta = new FakeAlta().Answer("session status a", FakeAlta.Status("a", "p", "work"))
            .Answer("session status hidden", FakeAlta.Status("hidden", "p", "private"))
            .Answer("job status job-a", Status("job-a", "a"))
            .Answer("job status job-b", Status("job-b", "b"))
            .Answer("job output", new JsonObject { ["type"] = "alta.job.output", ["text"] = "<script>output</script>", ["truncated"] = true });
        var api = Api(alta);
        Assert.IsNull(await api.JobsAsync("hidden", default));
        Assert.AreEqual(0, alta.CallsOf("job list").Length);
        foreach (var stop in new[] { false, true })
        {
            Assert.IsNull(await api.JobAsync("hidden", "job-a", stop, default));
            Assert.IsNull(await api.JobAsync("missing", "job-a", stop, default));
            Assert.IsNull(await api.JobAsync("a", "job-b", stop, default));
            Assert.IsNull(await api.JobAsync("a", "--all", stop, default));
        }
        Assert.AreEqual(0, alta.CallsOf("job output").Length);
        Assert.AreEqual(0, alta.CallsOf("job cancel").Length);
        var output = await api.JobAsync("a", "job-a", false, default);
        Assert.AreEqual("<script>output</script>", (string?)output!["text"]);
        Assert.IsTrue((bool)output["truncated"]!);
        CollectionAssert.AreEqual(new[] { "job", "output", "job-a", "--lines", "200" }, alta.CallsOf("job output").Single());
        Assert.IsNull((await api.JobAsync("a", "job-a", true, default))!["error"]);
        CollectionAssert.AreEqual(new[] { "job", "cancel", "job-a" }, alta.CallsOf("job cancel").Single());
    }

    private static MobileApi Api(FakeAlta alta) => new(alta, new MobileSessions(alta, new FakeSpaces("work"), new MobileLive()));

    private static JsonObject Job(string id, string session, string state = "running", DateTimeOffset? ended = null) => new()
    {
        ["type"] = "alta.job", ["id"] = id, ["sessionId"] = session, ["state"] = state,
        ["command"] = "dotnet test", ["folder"] = "/project", ["startedAt"] = DateTimeOffset.UtcNow.AddMinutes(-1), ["endedAt"] = ended,
    };

    private static JsonObject Status(string id, string session)
    {
        var job = Job(id, session);
        job["type"] = "alta.job.status";
        return job;
    }
}
