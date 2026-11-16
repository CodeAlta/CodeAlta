using CodeAlta.Catalog.Worktrees;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Node.Tests;

[TestClass]
public sealed class NodeMobileAdmissionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PersonalPhone_CreatesAndPromptsThroughFleetAdmission_AndRetriesOnlyOnce()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await MoveFixture.StartAsync(token);
        var access = fixture.Fleet.Service.PersonalSessions;
        var request = new FleetCreateSessionParams(fixture.NodeProjectId, Title: "From the phone",
            Selection: new FleetModelSelection("scripted", ScriptedNodeProvider.Model));
        var created = await access.CreateSessionAsync(request, "phone-create", token);
        Assert.AreEqual(created, await access.CreateSessionAsync(request, "phone-create", token));
        var runtime = fixture.Node.Host.RuntimeService;
        var session = (await runtime.TryGetRecoverableSessionAsync(created.SessionId, token))!;
        Assert.IsNotNull(session.WorktreeDirectory, "Nobody sits at the node: the phone gets an isolated worktree too.");
        Assert.IsFalse(GitWorktreeService.IsWithin(session.WorktreeDirectory, fixture.NodeProject));
        Assert.IsTrue(GitWorktreeService.SameRepository(session.WorktreeDirectory, fixture.NodeProject));
        Assert.IsTrue(fixture.Fleet.Service.Work.List().Any(work => work.SessionId == session.SessionId), "Policies, budgets and seats recognize it as fleet work.");

        var send = new FleetSendParams(session.SessionId, "From the phone");
        var admission = await access.SendAsync(send, "phone-send", token);
        Assert.AreEqual(admission, await access.SendAsync(send, "phone-send", token), "A lost HTTP response never starts a second run.");
        var conflicting = await Assert.ThrowsExactlyAsync<FleetException>(() => access.SendAsync(send with { Text = "Different" }, "phone-send", token));
        Assert.AreEqual(FleetErrorCodes.InvalidRequest, conflicting.Error.Code);
    }

    [TestMethod]
    public async Task PersonalPhone_RefusesAllAccessAsSoonAsTheMachineBecomesShared()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await NodeFixture.StartAsync(token);
        var access = fixture.Fleet.Service.PersonalSessions;
        var session = await access.CreateSessionAsync(new FleetCreateSessionParams(Title: "Personal"), "create-personal", token);
        fixture.Fleet.Service.People.Set(new FleetNodePeopleChange("another-person", ["view"]));
        async Task Refused(Func<Task> action)
        {
            var refusal = await Assert.ThrowsExactlyAsync<FleetException>(action);
            Assert.AreEqual(FleetNodeErrorCodes.PolicyRefused, refusal.Error.Code);
            StringAssert.Contains(refusal.Message, "lets other people in");
        }
        await Refused(() => access.ReadSessionAsync(new("session.status", session.SessionId), token));
        await Refused(() => access.GetSessionStateAsync(session.SessionId, token));
        await Refused(() => access.CreateSessionAsync(new(), "create-shared", token));
        await Refused(() => access.SendAsync(new(session.SessionId, "No"), "send-shared", token));
        await Refused(() => access.QueueAsync(new(session.SessionId, "No"), "queue-shared", token));
        await Refused(() => access.AbortAsync(new(session.SessionId, null), "abort-shared", token));
    }
}
