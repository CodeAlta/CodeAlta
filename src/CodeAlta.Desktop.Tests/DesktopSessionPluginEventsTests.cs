using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>Literal journal pages only; the service runs the real built-in Statistics projection over them.</summary>
[TestClass]
public sealed class DesktopSessionPluginEventsTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Read_ReturnsOneStatisticsCardPerFinishedTurnOfTheWindow()
    {
        // Two finished runs and one still running, two records per page, newest page first.
        var journal = Turn("first", 0).Concat(Turn("second", 100)).Concat(Turn("third", 200, finished: false)).ToArray();
        var reads = 0;
        var service = Service(Pages(journal, 2, () => reads++));

        // The window starts in the middle of the second run: its card needs the records before the window.
        var response = await service.ReadAsync(Request(Start.AddSeconds(102)), CancellationToken.None);

        Assert.AreEqual("ok", response.Status);
        Assert.AreEqual("session", response.SessionId);
        var card = response.Events.Single();
        Assert.AreEqual(SessionPluginEventsService.StatisticsPluginId, card.PluginId);
        Assert.AreEqual(Start.AddSeconds(104), card.Timestamp);
        StringAssert.StartsWith(card.Markdown, "**Turn statistics** · 4.0s");
        StringAssert.Contains(card.EventId, "run-second");
        StringAssert.Contains(card.Details.Single().Markdown, "| Prompt |");
        Assert.IsTrue(reads < journal.Length / 2 + 1, "the walk stops at the first older run instead of reading the whole journal");

        var all = await service.ReadAsync(Request(Start), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { Start.AddSeconds(4), Start.AddSeconds(104) }, all.Events.Select(static item => item.Timestamp).ToArray());

        var running = await service.ReadAsync(Request(Start.AddSeconds(200)), CancellationToken.None);
        Assert.AreEqual("ok", running.Status);
        Assert.AreEqual(0, running.Events.Length, "an unfinished turn has no card");
    }

    [TestMethod]
    public async Task ReadTurns_LeavesOutARunWhoseBeginningIsBeyondThePageLimit()
    {
        // One record per page: the old run is longer than the page limit, the new one fits.
        var old = Enumerable.Range(0, SessionPluginEventsService.MaximumPages + 8)
            .Select(index => (AgentEvent)Text("old", AgentContentKind.Assistant, index)).ToArray();
        var journal = old.Concat(Turn("new", 1000)).ToArray();

        var events = await SessionPluginEventsService.ReadTurnsAsync(Pages(journal, 1), "session", Start, CancellationToken.None);

        Assert.IsTrue(events.Count > 0);
        Assert.IsTrue(events.All(static item => item.RunId?.Value == "new"));
    }

    [TestMethod]
    public async Task Read_RefusesWithoutReadingAndHonoursThePluginSwitch()
    {
        var reads = 0;
        var journal = Turn("run", 0).ToArray();
        var disabled = new SessionPluginEventsService(Pages(journal, 10, () => reads++), static (_, _) => Task.FromResult(false), Epoch);
        var off = await disabled.ReadAsync(Request(Start), CancellationToken.None);
        Assert.AreEqual("ok", off.Status);
        Assert.AreEqual(0, off.Events.Length);

        var service = Service(Pages(journal, 10, () => reads++));
        Assert.AreEqual("stale_epoch", (await service.ReadAsync(Request(Start) with { ExpectedHostEpoch = "22222222-2222-4222-8222-222222222222" }, CancellationToken.None)).Status);
        Assert.AreEqual("invalid_request", (await service.ReadAsync(Request(Start) with { SessionId = " session" }, CancellationToken.None)).Status);
        Assert.AreEqual("invalid_request", (await service.ReadAsync(Request(Start) with { ProjectId = "" }, CancellationToken.None)).Status);
        Assert.AreEqual("unavailable", (await new SessionPluginEventsService().ReadAsync(Request(Start), CancellationToken.None)).Status);
        Assert.AreEqual(0, reads);
    }

    [TestMethod]
    public async Task Read_ReportsAJournalThatChangedOrIsMissingByCodeOnly()
    {
        static SessionPluginEventsService Failing(Exception failure) => Service((_, _, _) => Task.FromException<AgentSessionHistoryPage>(failure));
        Assert.AreEqual("history_changed", (await Failing(new AgentSessionHistoryException("history_changed")).ReadAsync(Request(Start), CancellationToken.None)).Status);
        Assert.AreEqual("missing_session", (await Failing(new FileNotFoundException("C:\\secret\\path")).ReadAsync(Request(Start), CancellationToken.None)).Status);
        var failed = await Failing(new InvalidOperationException("provider detail")).ReadAsync(Request(Start), CancellationToken.None);
        Assert.AreEqual("read_failed", failed.Status);
        Assert.AreEqual(0, failed.Events.Length);
    }

    [TestMethod]
    public async Task StatisticsEnablement_FollowsTheConfiguredSwitch()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-plugin-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var options = new CatalogOptions { GlobalRoot = root };
            var enabled = SessionPluginEventsService.StatisticsEnablement(new ProjectCatalog(options));
            Assert.IsTrue(await enabled(null, CancellationToken.None), "enabled unless configured otherwise");

            var store = new CodeAltaConfigStore(options);
            store.EnsureGlobalConfigExists();
            store.SaveGlobalPluginEnabled(SessionPluginEventsService.StatisticsPluginId, false);
            Assert.IsFalse(await enabled(null, CancellationToken.None));
            store.SaveGlobalPluginEnabled(SessionPluginEventsService.StatisticsPluginId, true);
            Assert.IsTrue(await enabled("unknown-project", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static SessionPluginEventsService Service(Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>> read)
        => new(read, static (_, _) => Task.FromResult(true), Epoch);

    private static SessionPluginEventsRequest Request(DateTimeOffset notBefore) => new(Epoch, "session", null, notBefore);

    // A prompt, an answer and, when finished, the idle update that ends the turn.
    private static IEnumerable<AgentEvent> Turn(string run, int second, bool finished = true)
    {
        yield return Text(run, AgentContentKind.User, second);
        yield return Text(run, AgentContentKind.Assistant, second + 2);
        if (finished) yield return new AgentSessionUpdateEvent(new("provider"), "session", Start.AddSeconds(second + 4), new(run), AgentSessionUpdateKind.Idle, null);
    }

    private static AgentContentCompletedEvent Text(string run, AgentContentKind kind, int second)
        => new(new("provider"), "session", Start.AddSeconds(second), new(run), kind, $"{run}-{kind}-{second}", null, "text");

    // Serves the journal backwards: the cursor offset is the index of the first record already returned.
    private static Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>> Pages(
        AgentEvent[] journal, int size, Action? onRead = null)
        => (sessionId, cursor, _) =>
        {
            onRead?.Invoke();
            var end = cursor is null ? journal.Length : (int)cursor.Offset;
            var start = Math.Max(0, end - size);
            var entries = Enumerable.Range(start, end - start).Select(index => new AgentSessionHistoryEntry(index + 1, journal[index])).ToArray();
            return Task.FromResult(new AgentSessionHistoryPage(entries, start == 0 ? null : new(sessionId, journal.Length, 1, start), false));
        };
}
