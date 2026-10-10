using CodeAlta.Desktop;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopRemoteControlTests
{
    [TestMethod]
    public async Task Reconnect_TurnsOnEachSessionInTurn_AndForgetsOnlyThoseThatAreUnavailable()
    {
        var asked = new List<string>();
        var forgotten = new List<string>();
        var statuses = new Dictionary<string, string> { ["ok"] = "ok", ["gone"] = "unavailable", ["busy"] = "busy", ["last"] = "ok" };

        await DesktopRemoteControl.ReconnectAsync(["ok", "gone", "busy", "last"],
            sessionId => { asked.Add(sessionId); return Task.FromResult(statuses[sessionId]); }, forgotten.Add, Unexpected);

        CollectionAssert.AreEqual(new[] { "ok", "gone", "busy", "last" }, asked, "One after the other, the last turned on first.");
        CollectionAssert.AreEqual(new[] { "gone" }, forgotten, "A session that is busy stays remembered for the next start.");
    }

    [TestMethod]
    public async Task Reconnect_GoesOnAfterASessionThatFails()
    {
        var asked = new List<string>();
        var forgotten = new List<string>();
        var warned = new List<(string, string)>();

        await DesktopRemoteControl.ReconnectAsync(["broken", "next"], sessionId =>
        {
            asked.Add(sessionId);
            return sessionId == "broken" ? Task.FromException<string>(new IOException("The CLI could not start.")) : Task.FromResult("ok");
        }, forgotten.Add, (sessionId, exception) => warned.Add((sessionId, exception.Message)));

        CollectionAssert.AreEqual(new[] { "broken", "next" }, asked);
        Assert.IsEmpty(forgotten, "A session that failed stays remembered.");
        CollectionAssert.AreEqual(new[] { ("broken", "The CLI could not start.") }, warned, "and the log says why.");
    }

    [TestMethod]
    public async Task Reconnect_StopsOnceTheHostIsClosed()
    {
        var asked = new List<string>();
        var forgotten = new List<string>();

        await DesktopRemoteControl.ReconnectAsync(["first", "closing", "never"],
            sessionId => { asked.Add(sessionId); return Task.FromResult(sessionId == "closing" ? "closed" : "ok"); }, forgotten.Add, Unexpected);

        CollectionAssert.AreEqual(new[] { "first", "closing" }, asked, "CodeAlta exits: nothing more is asked.");
        Assert.IsEmpty(forgotten, "The sessions are kept for the next start.");
    }

    private static void Unexpected(string sessionId, Exception exception) => Assert.Fail($"{sessionId}: {exception}");
}
