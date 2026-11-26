using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile.Tests;

[TestClass]
public sealed class MobileLiveTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ASession_IsFollowedFromItsTurnToItsEnd_AndPushedToThePhones()
    {
        var live = new MobileLive();
        var phone = live.Subscribe();

        live.Observe(Activity(AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-1"), "p1");
        Assert.AreEqual(("running", "p1"), State(live, "s1"));
        live.Observe(Activity(AgentActivityKind.ToolCall, AgentActivityPhase.Started, "tool-1", "shell_command"));
        Assert.AreEqual("tools", (string?)live.Describe("s1")!["live"]);
        live.Observe(Activity(AgentActivityKind.ToolCall, AgentActivityPhase.Completed, "tool-1", "shell_command"));
        live.Observe(new AgentContentCompletedEvent(Provider, "s1", At, null, AgentContentKind.Assistant, "answer-1", null, "## Done\nThe build passes."));
        Assert.AreEqual("Done", (string?)live.Describe("s1")!["line"]);
        live.Observe(Activity(AgentActivityKind.Turn, AgentActivityPhase.Completed, "turn-1"), "p1");
        Assert.AreEqual(("idle", "p1"), State(live, "s1"));

        live.Flush();
        Assert.IsTrue(phone.Reader.TryRead(out var frame));
        StringAssert.StartsWith(frame, "event: session\ndata: ");
        var data = JsonNode.Parse(frame["event: session\ndata: ".Length..].Trim())!;
        Assert.AreEqual(("s1", "p1", "idle"), ((string?)data["id"], (string?)data["projectId"], (string?)data["live"]));
        Assert.IsFalse(phone.Reader.TryRead(out _), "What did not change since is not pushed again.");

        // A phone that connects gets the state of every session seen; a reset of the token ends the streams.
        var late = live.Subscribe();
        Assert.IsTrue(late.Reader.TryRead(out _));
        live.CloseListeners();
        await phone.Reader.Completion.WithinAsync(TestTime.Seconds(5));
        await late.Reader.Completion.WithinAsync(TestTime.Seconds(5));
    }

    [TestMethod]
    public void AQuietSession_HasTheShapeOfALiveOne()
    {
        var live = new MobileLive();
        var quiet = live.Quiet("s2", "p2", running: true);
        CollectionAssert.AreEquivalent(new[] { "id", "projectId", "live", "kind", "line", "at", "activity", "canSteer", "asking", "approving", "suggestion" }, quiet.Select(static property => property.Key).ToArray());
        Assert.AreEqual(("running", true), ((string?)quiet["live"], (bool)quiet["canSteer"]!));
        Assert.IsNull(quiet["asking"]);
        live.Observe(Activity(AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-1"), "p1");
        CollectionAssert.AreEquivalent(quiet.Select(static property => property.Key).ToArray(), live.Describe("s1")!.Select(static property => property.Key).ToArray());
    }

    [TestMethod]
    public void ASessionThatAsksTheUser_IsPushedWhenItStartsAndWhenItEndsToAsk()
    {
        var live = new MobileLive();
        live.Observe(Activity(AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-1"), "p1");
        live.Observe(Activity(AgentActivityKind.Turn, AgentActivityPhase.Completed, "turn-1"), "p1");
        live.Flush([]);
        var phone = live.Subscribe();
        Assert.IsTrue(phone.Reader.TryRead(out _), "the state of now");

        live.Flush([Asking("s1"), Asking("s-unseen")]);
        Assert.IsTrue(phone.Reader.TryRead(out var asking));
        Assert.AreEqual(("ask-1", "0", "pending", false), Mark(asking));
        Assert.AreEqual("s1", (string?)Data(asking)["id"]);
        Assert.IsFalse(phone.Reader.TryRead(out _), "A session the plugin has not seen run is not told of on the stream.");
        Assert.AreEqual("ask-1", (string?)live.Quiet("s-unseen", "p1", running: false)["asking"]!["id"], "The list says it of a session it shows.");

        live.Flush([Asking("s1")]);
        live.Flush();
        Assert.IsFalse(phone.Reader.TryRead(out _), "Nothing changed: nothing is pushed.");

        // An answer goes, the session does not take it, and the ask waits again with a new revision: each is pushed.
        live.Flush([Asking("s1", state: "submitting")]);
        Assert.IsTrue(phone.Reader.TryRead(out var submitting));
        Assert.AreEqual(("ask-1", "0", "submitting", false), Mark(submitting));
        live.Flush([Asking("s1", revision: "1")]);
        Assert.IsTrue(phone.Reader.TryRead(out var again));
        Assert.AreEqual(("ask-1", "1", "pending", false), Mark(again));
        live.Flush([Asking("s1", revision: "1") with { Immediate = true }]);
        Assert.IsTrue(phone.Reader.TryRead(out var immediate));
        Assert.IsTrue((bool)Data(immediate)["asking"]!["immediate"]!);

        live.Flush([]);
        Assert.IsTrue(phone.Reader.TryRead(out var answered));
        Assert.IsNull(Data(answered)["asking"]);
    }

    [TestMethod]
    public void ThePermissionRequestsOfASession_ArePushed_WhenTheyChange()
    {
        var live = new MobileLive();
        live.Observe(Activity(AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-1"), "p1");
        var phone = live.Subscribe();
        live.Flush();
        while (phone.Reader.TryRead(out _))
        {
        }

        live.Flush(null, [new PluginPermissionSummary("s1", "r1", 1)]);
        Assert.IsTrue(phone.Reader.TryRead(out var came));
        Assert.AreEqual(("r1", 1), ((string?)Data(came)["approving"]!["id"], (int)Data(came)["approving"]!["count"]!));

        live.Flush(null, [new PluginPermissionSummary("s1", "r1", 1)]);
        Assert.IsFalse(phone.Reader.TryRead(out _), "nothing changed");

        live.Flush(null, [new PluginPermissionSummary("s1", "r2", 2)]);
        Assert.IsTrue(phone.Reader.TryRead(out var next));
        Assert.AreEqual("r2", (string?)Data(next)["approving"]!["id"]);

        live.Flush();
        Assert.IsFalse(phone.Reader.TryRead(out _), "a host that could not say keeps what it read before");

        live.Flush(null, []);
        Assert.IsTrue(phone.Reader.TryRead(out var gone));
        Assert.IsNull(Data(gone)["approving"]);
        Assert.IsNull(live.Quiet("s1", null, false)["approving"]);
        live.Flush(null, [new PluginPermissionSummary("s2", "r3", 1)]);
        Assert.AreEqual("r3", (string?)live.Quiet("s2", null, true)["approving"]!["id"], "A session not seen run says it when it is listed.");
    }

    [TestMethod]
    public void TheSuggestedNextPrompt_IsPushedAfterTheTurn_AndEndsWithTheNextOne()
    {
        var live = new MobileLive();
        live.Observe(Activity(AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-1"), "p1");
        live.Observe(Suggested("too early"));
        Assert.IsNull(Suggestion(live), "A suggestion that comes while a turn runs answers nothing.");
        live.Observe(new AgentSessionUpdateEvent(Provider, "s1", At, new AgentRunId("run-1"), AgentSessionUpdateKind.Idle, null));
        var phone = live.Subscribe();
        live.Flush();
        while (phone.Reader.TryRead(out _))
        {
        }

        live.Observe(Suggested("  go ahead "));
        live.Flush();
        Assert.IsTrue(phone.Reader.TryRead(out var suggested));
        Assert.AreEqual("go ahead", (string?)Data(suggested)["suggestion"]);
        live.Observe(Suggested("go ahead"));
        live.Flush();
        Assert.IsFalse(phone.Reader.TryRead(out _), "The same suggestion is not pushed again.");
        live.Observe(Suggested("two\nlines"));
        Assert.AreEqual("go ahead", Suggestion(live), "What is not one line is not a suggestion.");

        live.Observe(Activity(AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-2"), "p1");
        Assert.IsNull(Suggestion(live), "The next turn ends it.");
        live.Observe(new AgentSessionUpdateEvent(Provider, "s1", At, new AgentRunId("run-2"), AgentSessionUpdateKind.Idle, null));
        live.Observe(Suggested("run the tests"));
        Assert.AreEqual("run the tests", Suggestion(live));
        live.Observe(new AgentContentCompletedEvent(Provider, "s1", At, null, AgentContentKind.User, "prompt-3", null, "run the tests"));
        Assert.IsNull(Suggestion(live), "A prompt of the user ends it.");

        live.Observe(new AgentSessionUpdateEvent(Provider, "s1", At, new AgentRunId("run-3"), AgentSessionUpdateKind.Idle, null));
        live.Observe(Suggested("commit"));
        live.Observe(new AgentErrorEvent(Provider, "s1", At, "failed"));
        Assert.IsNull(Suggestion(live), "An error ends it.");
        live.Observe(new AgentSessionUpdateEvent(Provider, "s1", At, new AgentRunId("run-3"), AgentSessionUpdateKind.Idle, null));
        live.Observe(Suggested("commit"));
        live.Flush();
        while (phone.Reader.TryRead(out _))
        {
        }

        live.Observe(new AgentSessionUpdateEvent(Provider, "s1", At, null, AgentSessionUpdateKind.Shutdown, null));
        live.Flush();
        Assert.IsFalse(phone.Reader.TryRead(out _), "A session the host detached once idle goes on suggesting: its journal keeps it.");
        Assert.AreEqual("commit", Suggestion(live));
    }

    [TestMethod]
    public void TheSuggestionOfAStatus_IsTakenByAnIdleSessionThatToldNone()
    {
        var live = new MobileLive();
        var phone = live.Subscribe();
        live.Adopt("s1", "p1", "two\nlines");
        Assert.IsNull(live.Describe("s1"), "What is not one line is not a suggestion.");

        live.Adopt("s1", "p1", "  go ahead ");
        Assert.AreEqual("go ahead", Suggestion(live), "A session the plugin never saw suggests what its status says.");
        live.Flush();
        Assert.IsTrue(phone.Reader.TryRead(out var pushed));
        Assert.AreEqual("go ahead", (string?)Data(pushed)["suggestion"]);

        // The status is all the plugin knows of a session it does not follow: another suggestion replaces it, none ends it.
        live.Adopt("s1", "p1", "run the tests");
        Assert.AreEqual("run the tests", Suggestion(live));
        live.Adopt("s1", "p1", null);
        Assert.IsNull(Suggestion(live), "Another process ran the session: its status suggests nothing any more.");
        live.Flush();
        Assert.IsTrue(phone.Reader.TryRead(out var ended));
        Assert.IsNull(Data(ended)["suggestion"]);
        live.Adopt("s1", "p1", null);
        live.Flush();
        Assert.IsFalse(phone.Reader.TryRead(out _), "Nothing changed: nothing is pushed.");

        live.Observe(Activity(AgentActivityKind.Turn, AgentActivityPhase.Started, "turn-2"), "p1");
        live.Adopt("s1", "p1", "late");
        Assert.IsNull(Suggestion(live), "A session seen running suggests nothing.");

        // A session the plugin follows keeps what its events said: a status neither replaces nor ends it.
        live.Observe(Activity(AgentActivityKind.Turn, AgentActivityPhase.Completed, "turn-2"), "p1");
        live.Observe(Suggested("commit"));
        live.Adopt("s1", "p1", "older");
        live.Adopt("s1", "p1", null);
        Assert.AreEqual("commit", Suggestion(live));
        // Detached, it keeps it until a status says otherwise.
        live.Observe(new AgentSessionUpdateEvent(Provider, "s1", At, null, AgentSessionUpdateKind.Shutdown, null));
        Assert.AreEqual("commit", Suggestion(live));
        live.Adopt("s1", "p1", null);
        Assert.IsNull(Suggestion(live));
    }

    [TestMethod]
    public async Task ReadingTheStatusOfASession_GivesItsSuggestionToTheLiveState_WhichAListCannot()
    {
        // The phone opens a session of its list after the desktop started again: nothing was seen told, the list says no
        // suggestion, and the status the plugin reads for the session does.
        var alta = new FakeAlta();
        var status = FakeAlta.Status("s1", "p1", "work");
        status["promptSuggestion"] = "commit it";
        alta.Answer("session status s1", status);
        var hidden = FakeAlta.Status("s2", "p1", "personal");
        hidden["promptSuggestion"] = "not for this phone";
        alta.Answer("session status s2", hidden);
        var live = new MobileLive();
        var phone = live.Subscribe();
        var sessions = new MobileSessions(alta, new FakeSpaces("work"), live);

        Assert.IsNotNull(await sessions.FindAsync("s1", CancellationToken.None));
        Assert.AreEqual("commit it", Suggestion(live));
        live.Flush();
        Assert.IsTrue(phone.Reader.TryRead(out var pushed), "The phone is told, as for a suggestion the plugin saw told.");
        Assert.AreEqual("commit it", (string?)Data(pushed)["suggestion"]);

        // A session of a space the phone does not see gives nothing.
        Assert.IsNull(await sessions.FindAsync("s2", CancellationToken.None));
        Assert.IsNull(live.Describe("s2"));
    }

    private static AgentPromptSuggestionEvent Suggested(string text) => new(Provider, "s1", At, text);

    private static string? Suggestion(MobileLive live) => (string?)live.Describe("s1")!["suggestion"];

    [TestMethod]
    public void TheSameAsks_AreKept_NotBuiltAgain()
    {
        var live = new MobileLive();
        live.Flush([Asking("s1")]);
        var before = live.Asking;
        live.Flush([Asking("s1")]);
        Assert.AreSame(before, live.Asking, "nothing changed: the asks of the last flush are kept");
        live.Flush([Asking("s1", revision: "1")]);
        Assert.AreNotSame(before, live.Asking);
    }

    private static PluginAskSummary Asking(string sessionId, string revision = "0", string state = "pending") => new(sessionId, "ask-1", revision, state, false);

    private static (string?, string?, string?, bool) Mark(string frame)
    {
        var ask = Data(frame)["asking"]!;
        return ((string?)ask["id"], (string?)ask["revision"], (string?)ask["state"], (bool)ask["immediate"]!);
    }

    private static JsonNode Data(string frame) => JsonNode.Parse(frame["event: session\ndata: ".Length..].Trim())!;

    [TestMethod]
    public void TheLine_IsTheFirstLineOfATextWithoutItsMarks()
    {
        Assert.AreEqual("Title", MobileLive.Concise("# Title\n\nBody"));
        Assert.AreEqual("A list item", MobileLive.Concise("- A list item\n- Another"));
        Assert.AreEqual(160, MobileLive.Concise(new string('a', 400)).Length);
        Assert.AreEqual("", MobileLive.Concise("   "));
    }

    private static readonly ModelProviderId Provider = new("codex");

    private static AgentActivityEvent Activity(AgentActivityKind kind, AgentActivityPhase phase, string id, string? name = null)
        => new(Provider, "s1", At, null, kind, phase, id, null, name, null);

    private static (string?, string?) State(MobileLive live, string id)
    {
        var state = live.Describe(id)!;
        return ((string?)state["live"], (string?)state["projectId"]);
    }
}
