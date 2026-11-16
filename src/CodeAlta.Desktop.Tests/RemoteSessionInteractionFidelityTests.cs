using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>What the window of this computer is told of the asks and the permission requests of a machine: all the node API says of them.</summary>
[TestClass]
public sealed class RemoteSessionInteractionFidelityTests
{
    private static RemoteSessionHost Host()
    {
        var machine = new PairedMachine("node", "device-node", default, [], DateTimeOffset.UnixEpoch, ["view", "drive"]);
        return new RemoteSessionHost(machine, _ => throw new IOException("never connected"));
    }

    private static FleetAsk Questions(string? kind, string? revision, bool immediate = false) => new("ask-1", "run-1", "pending",
        [new FleetAskQuestion("Database", "Which?", "For the cache.", [new FleetAskChoice("SQLite", null)], AllowsText: true) { FreeformTitle = "Why", FreeformPlaceholder = "Say" }])
    {
        Kind = kind, Revision = revision, Immediate = immediate,
    };

    [TestMethod]
    public void AQuestion_KeepsItsRevisionItsFreeformAndWhetherItShowsAtOnce()
    {
        var head = Host().HeadOf("session-1", Questions(FleetAsk.KindQuestions, "3", immediate: true));
        Assert.IsNotNull(head);
        Assert.AreEqual(3, head.Handle.ResponseGeneration, "The revision the node gave is the one the answer carries.");
        Assert.IsTrue(head.Immediate);
        var question = head.Request.Questions.Single();
        Assert.AreEqual(("Why", "Say"), (question.Freeform!.Title, question.Freeform.Placeholder));
        Assert.AreEqual("SQLite", question.Choices.Single().Title);
        Assert.IsNull(head.Request.Review);
    }

    [TestMethod]
    [DataRow(FleetAsk.KindReview)]
    [DataRow(null)]
    [DataRow("something-new")]
    public void AnAskThatIsNotKnownToBeAQuestion_IsNotShownToBeAnswered(string? kind)
        => Assert.IsNull(Host().HeadOf("session-1", Questions(kind, "0")), "A review request, or an ask an older machine does not describe, is answered on the machine.");

    [TestMethod]
    [DataRow("0", 0)]
    [DataRow("12", 12)]
    [DataRow(null, 0)]
    [DataRow("-1", 0)]
    [DataRow("one", 0)]
    public void TheRevisionOfAnAsk_IsReadAsTheNumberTheNodeKeepsItAs(string? revision, int expected)
        => Assert.AreEqual(expected, RemoteSessionHost.ResponseGeneration(revision));

    [TestMethod]
    public void APermissionRequest_KeepsWhatTheNodeSaysOfIt()
    {
        var attempt = new SessionPermissionHandle("session-1", "run-1", "attempt", Guid.NewGuid());
        var plain = RemoteSessionHost.ToSnapshot(attempt, new FleetPermissionRequest(Guid.NewGuid(), "run-1", "shell", "ls", "/work", null, null, DateTimeOffset.UnixEpoch));
        Assert.AreEqual((false, false, null), (plain.Shortened, plain.OutsideSandbox, plain.Skill));

        var request = new FleetPermissionRequest(Guid.NewGuid(), "run-1", "shell", "rm -rf …", "/work", null, "It cleans.", DateTimeOffset.UnixEpoch)
            { Shortened = true, OutsideSandbox = true, SkillServer = "docs", SkillName = "writer" };
        var snapshot = RemoteSessionHost.ToSnapshot(attempt, request);
        Assert.IsTrue(snapshot.Shortened, "What is allowed is longer than what is shown.");
        Assert.IsTrue(snapshot.OutsideSandbox, "No mode answers a request to leave the sandbox.");
        Assert.AreEqual(("docs", "writer", true), (snapshot.Skill!.Server, snapshot.Skill.Name, snapshot.Skill.Dynamic), "A skill the node names alone is allowed once at most.");
    }
}
