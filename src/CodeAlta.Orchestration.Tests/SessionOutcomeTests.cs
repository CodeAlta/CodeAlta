using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>How the runs of a session ended, read from its journal: status, answer, failure, usage, cost and duration.</summary>
[TestClass]
public sealed class SessionOutcomeTests
{
    private static readonly ModelProviderId Provider = new("p");
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
    private static readonly AgentRunId Run1 = new("run-1");
    private static readonly AgentRunId Run2 = new("run-2");

    private static AgentContentCompletedEvent Content(AgentContentKind kind, string text, int seconds, AgentRunId run)
        => new(Provider, "s", Start.AddSeconds(seconds), run, kind, "c" + seconds, null, text);

    private static AgentSessionUpdateEvent Usage(int seconds, AgentRunId run, long input, long output, double? cost, string? unit = "USD")
        => new(Provider, "s", Start.AddSeconds(seconds), run, AgentSessionUpdateKind.UsageUpdated, null, Usage: new AgentSessionUsage(
            LastOperation: new AgentOperationUsageSnapshot(Model: "m", InputTokens: input, OutputTokens: output, CachedInputTokens: input / 2, ReasoningTokens: 3,
                Cost: cost, CostUnit: unit)));

    [TestMethod]
    public void Completed_RunsAddTheirOperationsOnce_AndTheirCostInOneUnit()
    {
        IReadOnlyList<AgentEvent> events =
        [
            Content(AgentContentKind.User, "do it", 0, Run1),
            Usage(1, Run1, 100, 10, 0.5),
            Usage(1, Run1, 100, 10, 0.5),
            Content(AgentContentKind.Assistant, "first answer", 2, Run1),
            Usage(5, Run2, 200, 20, 0.25),
            Usage(6, Run2, 50, 5, 9, "AI credits"),
            Content(AgentContentKind.Assistant, "final answer", 8, Run2),
        ];

        var outcome = SessionOutcome.From(events, isRunning: false);

        Assert.AreEqual(SessionOutcomeStatus.Completed, outcome.Status);
        Assert.AreEqual("final answer", outcome.FinalMessage);
        Assert.AreEqual(TimeSpan.FromSeconds(8), outcome.Duration);
        Assert.AreEqual(3, outcome.Usage.OperationCount, "An operation repeated by its events counts once.");
        Assert.AreEqual(350, outcome.Usage.InputTokens);
        Assert.AreEqual(35, outcome.Usage.OutputTokens);
        Assert.AreEqual(385, outcome.Usage.TotalTokens);
        Assert.AreEqual(9, outcome.Usage.ReasoningTokens);
        Assert.AreEqual(0.75, outcome.Usage.Cost!.Value, 1e-9, "Costs add up in the unit of the first: the credits are left out.");
        Assert.AreEqual("USD", outcome.Usage.CostUnit);
        Assert.AreEqual(2, outcome.RunCount);
        Assert.IsNull(outcome.FinalError);
        Assert.AreEqual(3, SessionOutcome.SelectFromRun(events, Run2).Count);
    }

    [TestMethod]
    public void Failed_Cancelled_Aborted_Running_AndNoCost()
    {
        var answered = Content(AgentContentKind.Assistant, "answer", 2, Run1);
        var failure = new AgentErrorEvent(Provider, "s", Start.AddSeconds(3), "Sign in again.", (Exception?)null, Run2) { Failure = new AgentFailureInfo(AgentFailureKind.Auth) };
        var failed = SessionOutcome.From([answered, failure], isRunning: false);
        Assert.AreEqual(SessionOutcomeStatus.Failed, failed.Status);
        Assert.AreEqual(AgentFailureKind.Auth, failed.FailureKind);
        Assert.AreEqual("Sign in again.", failed.FailureMessage);
        Assert.AreEqual("answer", failed.FinalMessage, "The last answer is kept with a failure that came after it.");
        Assert.IsNull(failed.Usage.Cost, "No provider reported a cost: none, not zero.");

        var stopped = new AgentErrorEvent(Provider, "s", Start.AddSeconds(3), "The run was stopped.", new OperationCanceledException(), Run2);
        Assert.AreEqual(SessionOutcomeStatus.Cancelled, SessionOutcome.From([answered, stopped], isRunning: false).Status);

        using var details = JsonDocument.Parse("""{"abortReason":"timeout"}""");
        var abort = new AgentSessionUpdateEvent(Provider, "s", Start.AddSeconds(4), null, AgentSessionUpdateKind.Warning, "The run was aborted: timeout.", details.RootElement.Clone());
        var aborted = SessionOutcome.From([answered, abort], isRunning: false);
        Assert.AreEqual(SessionOutcomeStatus.Cancelled, aborted.Status, "A tree aborted after its root answered did not complete.");
        Assert.AreEqual("timeout", aborted.AbortReason);

        Assert.AreEqual(SessionOutcomeStatus.Running, SessionOutcome.From([answered], isRunning: true).Status);
        Assert.AreEqual(SessionOutcomeStatus.Unknown, SessionOutcome.From([], isRunning: false).Status);
    }

    [TestMethod]
    public void SelectFromRun_KeepsThatRunAndWhatCameAfter()
    {
        var first = Content(AgentContentKind.User, "one", 0, Run1);
        var second = Content(AgentContentKind.User, "two", 5, Run2);
        var third = Content(AgentContentKind.Assistant, "after", 6, new AgentRunId("run-3"));

        CollectionAssert.AreEqual(new AgentEvent[] { second, third }, SessionOutcome.SelectFromRun([first, second, third], Run2).ToArray());
        Assert.AreEqual(0, SessionOutcome.SelectFromRun([first], Run2).Count);
        CollectionAssert.AreEqual(new AgentEvent[] { third }, SessionOutcome.SelectLastTurn([first, second, third]).ToArray());
    }

    [TestMethod]
    public async Task ReadOutcome_OfARun_ReadsItFromTheJournal()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Outcome");
        await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("first") });
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("The answer."));

        var run = await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("second") });
        var outcome = await host.RuntimeService.ReadOutcomeAsync(session, run);

        Assert.AreEqual(SessionOutcomeStatus.Completed, outcome.Status);
        Assert.AreEqual("The answer.", outcome.FinalMessage);
        Assert.AreEqual(1, outcome.UserMessageCount, "Only the run asked for and what follows it.");
        Assert.AreEqual(100, outcome.Usage.InputTokens);
        Assert.AreEqual(20, outcome.Usage.OutputTokens);
        Assert.AreEqual(40, outcome.Usage.CachedInputTokens);
        Assert.AreEqual(5, outcome.Usage.ReasoningTokens);
        Assert.AreEqual(0.25, outcome.Usage.Cost);
        Assert.AreEqual("USD", outcome.Usage.CostUnit);
        Assert.IsNotNull(outcome.Duration);
        Assert.AreEqual("The answer.", (await host.RuntimeService.ReadOutcomeAsync(session, fromRun: null)).FinalMessage);
    }
}
