using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A session an agent creates with <c>alta session create --no-parent</c> is not its child: stored and read back, it has
/// no parent, and its answers are not sent to the session that created it, which it still records as its creator.
/// </summary>
[TestClass]
public sealed class SessionNoParentTests
{
    private static readonly TimeSpan Hang = TestDeadlines.Hang;

    [TestMethod]
    public async Task ASessionCreatedWithoutParent_HasNoParentReadBack_AndItsAnswerIsNotSentToItsCreator()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var creator = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Creator");
        provider.Script(creator.SessionId, ScriptedTurnProvider.Reply("Creator ready."));
        await host.RuntimeService.SendAsync(creator, options, new AgentSendOptions { Input = AgentInput.Text("start") });
        await host.RuntimeService.WhenSettledAsync(creator.SessionId).WithinAsync(Hang);
        var creatorRequests = provider.RequestsOf(creator.SessionId).Length;

        // What `alta session create --no-parent` does from a turn of the creator: no parent, the creator recorded.
        var createdBy = new AltaActorProvenance { Kind = "agent", SourceSessionId = creator.SessionId, NoParent = true, CreatedAt = DateTimeOffset.UtcNow };
        var created = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Independent", parentSessionId: null, createdBy);
        created.CreatedBy = createdBy;
        await host.RuntimeService.PersistSessionLocalStateAsync(created);

        // `alta session send` reads the session back from the store before it sends.
        var independent = await host.RuntimeService.TryGetResumableSessionAsync(created.SessionId);
        Assert.IsNotNull(independent);
        Assert.IsNull(independent.ParentSessionId, "A session created without a parent is not the child of its creator.");
        Assert.AreEqual(creator.SessionId, independent.CreatedBy?.SourceSessionId, "It still records who created it.");
        Assert.IsTrue(independent.CreatedBy?.NoParent);
        provider.Script(independent.SessionId, ScriptedTurnProvider.Reply("Independent answer."));
        await host.RuntimeService.SendAsync(independent, options, new AgentSendOptions { Input = AgentInput.Text("work") });
        await host.RuntimeService.WhenSettledAsync(independent.SessionId).WithinAsync(Hang);
        await host.RuntimeService.WhenSettledAsync(creator.SessionId).WithinAsync(Hang);

        Assert.AreEqual(creatorRequests, provider.RequestsOf(creator.SessionId).Length, "The answer of the session is not sent to its creator.");
        SessionViewDescriptor? listed = null;
        await foreach (var session in host.RuntimeService.ListRecoverableSessionsAsync())
        {
            if (session.SessionId == independent.SessionId) listed = session;
        }

        Assert.IsNotNull(listed);
        Assert.IsNull(listed.ParentSessionId, "Nor is it listed under it.");
        var stored = await host.RuntimeService.TryGetResumableSessionAsync(independent.SessionId);
        Assert.IsNull(stored!.ParentSessionId, "Its runs do not make it a child either.");
    }

    [TestMethod]
    public async Task ASessionAnAgentCreatedWithoutTheMark_StaysTheChildOfItsCreator()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var creator = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Creator");

        // A session saved before the mark existed, or created without --no-parent, names its creator only.
        var createdBy = new AltaActorProvenance { Kind = "agent", SourceSessionId = creator.SessionId, CreatedAt = DateTimeOffset.UtcNow };
        var created = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Child", parentSessionId: null, createdBy);
        created.CreatedBy = createdBy;
        await host.RuntimeService.PersistSessionLocalStateAsync(created);

        var child = await host.RuntimeService.TryGetResumableSessionAsync(created.SessionId);
        Assert.AreEqual(creator.SessionId, child!.ParentSessionId, "The session an agent created is its sub-agent.");
    }
}
