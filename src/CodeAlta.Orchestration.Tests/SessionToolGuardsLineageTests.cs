using CodeAlta.Agent;
using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The guard a session takes from the run it comes from, up its lineage: a deep lineage still finds it, and one that cannot be
/// followed to its start (deeper than what is followed, or a cycle) is taken to be guarded, never to come from no run.
/// </summary>
[TestClass]
public sealed class SessionToolGuardsLineageTests
{
    private const string RunCreator = "the-run";

    // Deeper than the lineage that is followed (SessionToolGuards.MaximumLineageDepth).
    private const int TooDeep = 300;

    [TestMethod]
    public async Task ADeepDescendantOfARun_TakesTheGuardOfTheRun()
    {
        var (guards, run) = Chain(40);
        Assert.AreSame(run, await guards.FindAsync("session-40", default), "40 sessions below the run, the guard still holds");
    }

    [TestMethod]
    public async Task ALineageThatCannotBeFollowedToItsStart_IsGuarded_AndRefusesEveryCall()
    {
        // Deeper than what is followed: the run, if any, is beyond.
        var (deep, run) = Chain(TooDeep);
        var guard = await deep.FindAsync($"session-{TooDeep}", default);
        Assert.IsNotNull(guard, "a lineage too deep is not taken to come from no run");
        Assert.AreNotSame(run, guard);
        var decision = await guard.BeforeToolCallAsync(Call(), default);
        StringAssert.Contains(decision.BlockMessage, "cannot be followed");

        // A cycle of links.
        var cycle = new SessionToolGuards();
        var sessions = new Dictionary<string, SessionViewDescriptor>
        {
            ["a"] = Session("a", "b", null), ["b"] = Session("b", "c", null), ["c"] = Session("c", "a", null),
        };
        cycle.ReadSession = (id, _) => Task.FromResult(sessions.GetValueOrDefault(id));
        Assert.IsNotNull(await cycle.FindAsync("a", default), "a cycle is not taken to come from no run");

        // A lineage that ends (a session without a parent, or a parent this machine does not have) gives no guard.
        var plain = new SessionToolGuards();
        var ends = new Dictionary<string, SessionViewDescriptor> { ["x"] = Session("x", "y", null), ["y"] = Session("y", "on-another-machine", null) };
        plain.ReadSession = (id, _) => Task.FromResult(ends.GetValueOrDefault(id));
        Assert.IsNull(await plain.FindAsync("x", default));
    }

    // A run (session-0, created by an automation) and its descendants session-1..n, each the child of the one before.
    private static (SessionToolGuards Guards, AgentToolInterceptor Run) Chain(int depth)
    {
        var run = new Blocking();
        var guards = new SessionToolGuards();
        guards.AddOrigin((_, createdBy) => createdBy.CorrelationId == RunCreator ? run : null);
        var sessions = new Dictionary<string, SessionViewDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["session-0"] = Session("session-0", null, new AltaActorProvenance { Kind = AltaActorProvenance.AutomationKind, AutomationId = "automation", CorrelationId = RunCreator, CreatedAt = DateTimeOffset.UnixEpoch }),
        };
        for (var i = 1; i <= depth; i++) sessions[$"session-{i}"] = Session($"session-{i}", $"session-{i - 1}", null);
        guards.ReadSession = (id, _) => Task.FromResult(sessions.GetValueOrDefault(id));
        return (guards, run);
    }

    private static SessionViewDescriptor Session(string id, string? parent, AltaActorProvenance? createdBy) => new()
    {
        SessionId = id, ParentSessionId = parent, CreatedBy = createdBy, ProviderId = "p", ProviderKey = "p", Kind = SessionViewKind.GlobalSession,
        Status = SessionViewStatus.Active, Title = id, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch, LastActiveAt = DateTimeOffset.UnixEpoch,
    };

    private static AgentToolHookCall Call()
    {
        using var schema = System.Text.Json.JsonDocument.Parse("{}");
        var spec = new AgentToolSpec("read_file", "Reads.", schema.RootElement.Clone());
        return AgentToolHookCall.Create(new AgentToolInvocation(new ModelProviderId("p"), "s", "c", spec.Name, schema.RootElement.Clone()), spec, null, AgentToolHookCapabilities.Block);
    }

    private sealed class Blocking : AgentToolInterceptor;
}
