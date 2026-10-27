using System.Text.Json.Nodes;
using CodeAlta.Agent;

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
        await phone.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await late.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void AQuietSession_HasTheShapeOfALiveOne()
    {
        var live = new MobileLive();
        var quiet = live.Quiet("s2", "p2", running: true);
        CollectionAssert.AreEquivalent(new[] { "id", "projectId", "live", "kind", "line", "at", "activity", "canSteer", "asking" }, quiet.Select(static property => property.Key).ToArray());
        Assert.AreEqual(("running", true, false), ((string?)quiet["live"], (bool)quiet["canSteer"]!, (bool)quiet["asking"]!));
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

        live.Flush(["s1", "s-unseen"]);
        Assert.IsTrue(phone.Reader.TryRead(out var asking));
        Assert.IsTrue((bool)Data(asking)["asking"]!);
        Assert.AreEqual("s1", (string?)Data(asking)["id"]);
        Assert.IsFalse(phone.Reader.TryRead(out _), "A session the plugin has not seen run is not told of on the stream.");
        Assert.IsTrue((bool)live.Quiet("s-unseen", "p1", running: false)["asking"]!, "The list says it of a session it shows.");

        live.Flush(["s1"]);
        live.Flush();
        Assert.IsFalse(phone.Reader.TryRead(out _), "Nothing changed: nothing is pushed.");
        live.Flush([]);
        Assert.IsTrue(phone.Reader.TryRead(out var answered));
        Assert.IsFalse((bool)Data(answered)["asking"]!);
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
